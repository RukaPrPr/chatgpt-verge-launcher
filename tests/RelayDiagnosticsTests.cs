using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ChatGptVergeLauncher;

internal static class RelayDiagnosticsTests
{
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
    private sealed class ControlledStream : MemoryStream
    {
        internal readonly TaskCompletionSource<int> WriteGate = new TaskCompletionSource<int>();
        internal CancellationToken SeenToken;
        internal int FlushCount;
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            SeenToken = token;
            return WriteGate.Task;
        }
        public override void Flush() { FlushCount++; base.Flush(); }
    }
    private static int Main()
    {
        try
        {
            byte[] bytes = new byte[262157];
            new Random(16).NextBytes(bytes);
            var destination = new MemoryStream();
            using (var meter = new RelayWriteMeter(destination))
            {
                new MemoryStream(bytes).CopyToAsync(meter).GetAwaiter().GetResult();
                Check(meter.Bytes == bytes.Length && Convert.ToBase64String(bytes) == Convert.ToBase64String(destination.ToArray()), "meter preserves opaque bytes and successful byte count");
            }
            Check(destination.CanWrite, "meter disposal leaves network stream ownership unchanged");
            var pending = new ControlledStream();
            var pendingMeter = new RelayWriteMeter(pending);
            using (var cancellation = new CancellationTokenSource())
            {
                Task write = pendingMeter.WriteAsync(bytes, 0, 7, cancellation.Token);
                Check(pendingMeter.Bytes == 0 && !write.IsCompleted, "pending writes are not counted as forwarded bytes");
                Check(pending.SeenToken == cancellation.Token && pending.FlushCount == 0, "meter passes cancellation token without adding flushes");
                pending.WriteGate.SetResult(0);
                write.GetAwaiter().GetResult();
                Check(pendingMeter.Bytes == 7, "byte count advances only after successful write completion");
            }
            var failing = new ControlledStream();
            var failingMeter = new RelayWriteMeter(failing);
            var expected = new IOException("DO_NOT_LOG_SECRET", new SocketException(10054));
            Task failedWrite = failingMeter.WriteAsync(bytes, 0, 8, CancellationToken.None);
            failing.WriteGate.SetException(expected);
            Exception actual = null;
            try { failedWrite.GetAwaiter().GetResult(); } catch (Exception error) { actual = error; }
            Check(Object.ReferenceEquals(expected, actual) && Object.ReferenceEquals(expected, failingMeter.WriteError) && failingMeter.Bytes == 0, "write failures preserve exception and do not overcount bytes");
            string fields = RelayDiagnostics.ErrorFields("test", actual);
            Check(fields.Contains("testErrorType=IOException") && fields.Contains("testSocketError=ConnectionReset") && fields.Contains("testNativeError=10054") && fields.Contains("testHResult=0x"), "exception diagnostics preserve wrapper type and socket error code");
            Check(!fields.Contains("DO_NOT_LOG_SECRET"), "exception messages and secrets are excluded");
            var upload = Task.FromResult(0);
            var download = new TaskCompletionSource<int>();
            var diagnostic = new RelayDiagnostics(1) { Upload = upload, Download = download.Task, ConnectStatus = 200, UploadMeter = pendingMeter, DownloadMeter = failingMeter };
            diagnostic.ObserveCompletion(upload);
            download.SetException(new ObjectDisposedException("cleanup"));
            string snapshot = diagnostic.Snapshot();
            Check(snapshot.Contains("firstObserved=local_to_proxy") && snapshot.Contains("downAtClose=pending") && snapshot.Contains("downOutcome=faulted"), "cleanup-induced failure stays distinct from first observed completion");
            Check(snapshot.Contains("connectStatus=200") && snapshot.Contains("upBytes=7") && snapshot.Contains("downBytes=0"), "snapshot retains CONNECT status and only completed write bytes");
            var both = new RelayDiagnostics(2) { Upload = upload, Download = Task.FromResult(0) };
            both.ObserveCompletion(upload);
            Check(both.Snapshot().Contains("firstObserved=both"), "simultaneous completions do not invent a first peer");
            var canceled = new TaskCompletionSource<int>(); canceled.SetCanceled();
            Check(RelayDiagnostics.Outcome(canceled.Task) == "canceled", "cancellation is distinct from EOF");
            var shutdown = new RelayDiagnostics(3);
            shutdown.BeginCleanup("relay_shutdown");
            shutdown.BeginCleanup("failure");
            Check(shutdown.Snapshot().Contains("cleanupReason=relay_shutdown") && shutdown.Snapshot().Contains("upOutcome=not_started"), "early shutdown snapshot preserves initiating cleanup category");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
