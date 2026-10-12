using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChatGptVergeLauncher
{
    // Only the destination of the existing CopyToAsync is decorated. Do not own
    // the stream, buffer payloads, flush, retry, add deadlines, or log per write.
    internal sealed class RelayWriteMeter : Stream
    {
        private readonly Stream inner;
        private long bytes;
        internal Exception WriteError;
        internal RelayWriteMeter(Stream inner) { this.inner = inner; }
        internal long Bytes { get { return Interlocked.Read(ref bytes); } }
        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return inner.CanWrite; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Flush() { inner.Flush(); }
        public override void Write(byte[] buffer, int offset, int count)
        {
            try { inner.Write(buffer, offset, count); Interlocked.Add(ref bytes, count); }
            catch (Exception error) { WriteError = error; throw; }
        }
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            try
            {
                await inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref bytes, count);
            }
            catch (Exception error) { WriteError = error; throw; }
        }
    }

    // Snapshots contain connection metadata and numeric counters, never bytes,
    // exception messages, CONNECT headers, TLS plaintext, or credentials.
    internal sealed class RelayDiagnostics
    {
        internal readonly int Id;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long connectedAt = -1;
        internal string Phase = "resolve";
        internal string Client = "unknown";
        internal string Listener = "unknown";
        internal string UpstreamLocal = "unknown";
        internal string Target = "unresolved";
        internal int ConnectStatus;
        internal RelayWriteMeter UploadMeter;
        internal RelayWriteMeter DownloadMeter;
        internal Task Upload;
        internal Task Download;
        private string cleanupReason = "none";
        private string firstObserved = "none";
        private string uploadAtClose = "not_observed";
        private string downloadAtClose = "not_observed";
        private long closeObservedAt = -1;

        internal RelayDiagnostics(int id) { Id = id; }
        internal void Connected() { Interlocked.Exchange(ref connectedAt, clock.ElapsedMilliseconds); }
        internal void ObserveCompletion(Task selected)
        {
            // Both tasks can already be complete when WhenAny resumes. Do not
            // invent an ordering or attribute cleanup-induced errors to a peer.
            uploadAtClose = Outcome(Upload);
            downloadAtClose = Outcome(Download);
            firstObserved = uploadAtClose != "pending" && downloadAtClose != "pending" ? "both" :
                Object.ReferenceEquals(selected, Upload) ? "local_to_proxy" : "proxy_to_local";
            Interlocked.Exchange(ref closeObservedAt, clock.ElapsedMilliseconds);
            BeginCleanup("transfer_completed");
        }
        internal void BeginCleanup(string reason) { Interlocked.CompareExchange(ref cleanupReason, reason, "none"); }
        internal string ConnectionFields()
        {
            return "client=" + Client + " listener=" + Listener + " upstreamLocal=" + UpstreamLocal +
                " connectStatus=" + ConnectStatus + " connectMs=" + Math.Max(0, Interlocked.Read(ref connectedAt));
        }
        internal string Snapshot()
        {
            try
            {
                long elapsed = clock.ElapsedMilliseconds, connected = Interlocked.Read(ref connectedAt);
                return "phase=" + Phase + " target=" + Target + " " + ConnectionFields() +
                    " elapsedMs=" + elapsed + " connectedMs=" + (connected < 0 ? 0 : elapsed - connected) +
                    " cleanupReason=" + cleanupReason + " firstObserved=" + firstObserved +
                    " closeObservedMs=" + Interlocked.Read(ref closeObservedAt) +
                    " upAtClose=" + uploadAtClose + " downAtClose=" + downloadAtClose +
                    " upBytes=" + (UploadMeter == null ? 0 : UploadMeter.Bytes) +
                    " downBytes=" + (DownloadMeter == null ? 0 : DownloadMeter.Bytes) +
                    TransferFields("up", Upload, UploadMeter) + TransferFields("down", Download, DownloadMeter);
            }
            catch (Exception error) { return "diagnosticError=" + error.GetType().Name; }
        }
        private static string TransferFields(string prefix, Task task, RelayWriteMeter meter)
        {
            string outcome = Outcome(task);
            Exception error = task != null && task.IsFaulted ? task.Exception.InnerExceptions[0] : null;
            return " " + prefix + "Outcome=" + outcome + " " + prefix + "ErrorOperation=" +
                (error == null ? "none" : meter != null && meter.WriteError != null ? "write" : "read_or_copy") +
                " " + ErrorFields(prefix, error);
        }
        internal static string Outcome(Task task)
        {
            if (task == null) return "not_started";
            // Sample status once: separate property reads could mislabel a task
            // that faults between IsFaulted and IsCompleted as successful EOF.
            TaskStatus status = task.Status;
            if (status == TaskStatus.Faulted) return "faulted";
            if (status == TaskStatus.Canceled) return "canceled";
            return status == TaskStatus.RanToCompletion ? "eof" : "pending";
        }
        internal static string ErrorFields(string prefix, Exception error)
        {
            if (error == null) return prefix + "ErrorType=none";
            var text = new StringBuilder(prefix + "ErrorType=" + error.GetType().Name +
                " " + prefix + "HResult=0x" + error.HResult.ToString("X8"));
            int depth = 0;
            for (Exception current = error; current != null && depth < 8; current = current.InnerException, depth++)
            {
                if (depth != 0) text.Append(" " + prefix + "Inner" + depth + "=" + current.GetType().Name);
                var socket = current as SocketException;
                if (socket != null)
                    text.Append(" " + prefix + "SocketError=" + socket.SocketErrorCode +
                        " " + prefix + "NativeError=" + socket.NativeErrorCode);
            }
            return text.ToString();
        }
        internal static string Endpoint(TcpClient client, bool remote)
        {
            try
            {
                var endpoint = remote ? client.Client.RemoteEndPoint : client.Client.LocalEndPoint;
                return endpoint == null ? "unknown" : endpoint.ToString();
            }
            catch (Exception) { return "unavailable"; }
        }
    }
}
