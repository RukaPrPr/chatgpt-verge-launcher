using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChatGptVergeLauncher;

[assembly: System.Reflection.AssemblyVersion("1.0.6.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.6.0")]

// A fixed-destination, loopback-only byte relay. TLS remains end to end.
internal static class DurableRelay
{
    private const string Domain = "codex-cloud-backend.chatgpt.com";
    private static readonly object Gate = new object();
    private static readonly HashSet<TcpClient> Clients = new HashSet<TcpClient>();
    private static readonly Dictionary<int, RelayDiagnostics> ActiveDiagnostics = new Dictionary<int, RelayDiagnostics>();
    private static LaunchLog log;
    private static int sequence;
    private static readonly SemaphoreSlim DnsGate = new SemaphoreSlim(1, 1);
    private static IPAddress cachedAddress;
    private static DateTime dnsExpires;
    private static IPAddress diagnosticAddress;

    [DataContract]
    private sealed class DnsAnswer
    {
        [DataMember] public int type { get; set; }
        [DataMember] public int TTL { get; set; }
        [DataMember] public string data { get; set; }
    }
    [DataContract]
    private sealed class DnsResult
    {
        [DataMember(IsRequired = true)] public int Status { get; set; }
        [DataMember] public DnsAnswer[] Answer { get; set; }
    }

    private static bool PublicV4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = ip.GetAddressBytes();
        return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224 &&
            !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) &&
            !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] >= 64 && b[1] <= 127) &&
            !(b[0] == 198 && (b[1] == 18 || b[1] == 19));
    }

    private static async Task<IPAddress> ResolveTarget(int proxyPort)
    {
        if (diagnosticAddress != null) return diagnosticAddress;
        await DnsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (cachedAddress != null && DateTime.UtcNow < dnsExpires) return cachedAddress;
            return await Task.Run(delegate
            {
                var request = (HttpWebRequest)WebRequest.Create("https://cloudflare-dns.com/dns-query?name=" + Domain + "&type=A");
                request.Proxy = new WebProxy("http://127.0.0.1:" + proxyPort, false);
                request.Accept = "application/dns-json";
                request.AllowAutoRedirect = false;
                request.KeepAlive = false;
                request.Timeout = 5000;
                request.ReadWriteTimeout = 5000;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var body = response.GetResponseStream())
                using (var memory = new MemoryStream())
                {
                    if (response.StatusCode != HttpStatusCode.OK) throw new IOException("DoH request failed");
                    byte[] chunk = new byte[4096]; int n;
                    while ((n = body.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (memory.Length + n > 65536) throw new IOException("DoH response too large");
                        memory.Write(chunk, 0, n);
                    }
                    memory.Position = 0;
                    var result = (DnsResult)new DataContractJsonSerializer(typeof(DnsResult)).ReadObject(memory);
                    if (result.Status != 0 || result.Answer == null) throw new IOException("DoH did not resolve target");
                    foreach (var answer in result.Answer)
                    {
                        IPAddress ip;
                        if (answer.type != 1 || !IPAddress.TryParse(answer.data, out ip) || !PublicV4(ip)) continue;
                        cachedAddress = ip;
                        dnsExpires = DateTime.UtcNow.AddSeconds(Math.Max(0, Math.Min(answer.TTL, 300)));
                        Log("RESOLVED via=DoH_over_proxy address=" + ip);
                        return ip;
                    }
                    throw new IOException("DoH returned no public IPv4 address");
                }
            }).ConfigureAwait(false);
        }
        finally { DnsGate.Release(); }
    }

    private static string Option(string[] args, string name, string fallback)
    {
        for (int i = 0; i < args.Length; i++)
            if (args[i] == name && i + 1 < args.Length) return args[i + 1];
        return fallback;
    }

    private static void Log(string message)
    {
        if (log != null) log.Write(message);
        else Console.Error.WriteLine("RELAY_LOG_UNAVAILABLE " + message);
    }

    private static async Task Deadline(Task operation, int milliseconds, TcpClient client)
    {
        if (await Task.WhenAny(operation, Task.Delay(milliseconds)).ConfigureAwait(false) != operation)
        {
            client.Close();
            try { await operation.ConfigureAwait(false); } catch { }
            throw new TimeoutException("Proxy handshake deadline exceeded");
        }
        await operation.ConfigureAwait(false);
    }

    private static async Task ReadConnectResponse(NetworkStream stream, RelayDiagnostics diagnostics)
    {
        // Read exactly through CRLFCRLF, without consuming tunneled TLS bytes.
        var bytes = new List<byte>();
        byte[] one = new byte[1];
        while (bytes.Count < 16384)
        {
            if (await stream.ReadAsync(one, 0, 1).ConfigureAwait(false) == 0)
                throw new IOException("Proxy closed during CONNECT");
            bytes.Add(one[0]);
            int n = bytes.Count;
            if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10)
            {
                string line = Encoding.ASCII.GetString(bytes.ToArray()).Split('\r')[0];
                string[] fields = line.Split(' ');
                int status;
                if (fields.Length >= 2 && Int32.TryParse(fields[1], out status)) diagnostics.ConnectStatus = status;
                if (fields.Length < 2 || (fields[0] != "HTTP/1.1" && fields[0] != "HTTP/1.0") || fields[1] != "200")
                    throw new IOException("Proxy rejected CONNECT");
                return;
            }
        }
        throw new IOException("CONNECT response headers too large");
    }

    private static async Task Serve(TcpClient incoming, int proxyPort, SemaphoreSlim slots)
    {
        int id = Interlocked.Increment(ref sequence);
        var upstream = new TcpClient(AddressFamily.InterNetwork);
        var diagnostics = new RelayDiagnostics(id);
        diagnostics.Client = RelayDiagnostics.Endpoint(incoming, true);
        diagnostics.Listener = RelayDiagnostics.Endpoint(incoming, false);
        lock (Gate) { Clients.Add(incoming); Clients.Add(upstream); ActiveDiagnostics.Add(id, diagnostics); }
        try
        {
            IPAddress targetAddress = await ResolveTarget(proxyPort).ConfigureAwait(false);
            string target = targetAddress + ":443";
            diagnostics.Target = target;
            incoming.NoDelay = true;
            upstream.NoDelay = true;
            diagnostics.Phase = "proxy_connect";
            await Deadline(upstream.ConnectAsync(IPAddress.Loopback, proxyPort), 5000, upstream).ConfigureAwait(false);
            diagnostics.UpstreamLocal = RelayDiagnostics.Endpoint(upstream, false);
            NetworkStream proxy = upstream.GetStream();
            byte[] request = Encoding.ASCII.GetBytes("CONNECT " + target + " HTTP/1.1\r\nHost: " + target + "\r\n\r\n");
            diagnostics.Phase = "connect_write";
            await Deadline(proxy.WriteAsync(request, 0, request.Length), 5000, upstream).ConfigureAwait(false);
            diagnostics.Phase = "connect_response";
            await Deadline(ReadConnectResponse(proxy, diagnostics), 5000, upstream).ConfigureAwait(false);
            diagnostics.Connected();
            Log("tunnel=" + id + " CONNECT_OK proxy=127.0.0.1:" + proxyPort + " target=" + target + " " + diagnostics.ConnectionFields());
            NetworkStream local = incoming.GetStream();
            diagnostics.Phase = "forward";
            diagnostics.UploadMeter = new RelayWriteMeter(proxy);
            diagnostics.DownloadMeter = new RelayWriteMeter(local);
            Task upload = local.CopyToAsync(diagnostics.UploadMeter);
            Task download = proxy.CopyToAsync(diagnostics.DownloadMeter);
            diagnostics.Upload = upload;
            diagnostics.Download = download;
            Task first = await Task.WhenAny(upload, download).ConfigureAwait(false);
            diagnostics.ObserveCompletion(first);
            incoming.Close();
            upstream.Close();
            try { await Task.WhenAll(upload, download).ConfigureAwait(false); } catch (IOException) { } catch (ObjectDisposedException) { }
            Log("tunnel=" + id + " CLOSED " + diagnostics.Snapshot());
        }
        catch (Exception ex)
        {
            diagnostics.BeginCleanup("failure");
            Log("tunnel=" + id + " FAILED type=" + ex.GetType().Name + " " + diagnostics.Snapshot() + " " + RelayDiagnostics.ErrorFields("failure", ex));
        }
        finally
        {
            incoming.Close();
            upstream.Close();
            lock (Gate) { Clients.Remove(incoming); Clients.Remove(upstream); ActiveDiagnostics.Remove(id); }
            slots.Release();
        }
    }

    private static int Main(string[] args)
    {
        string instance = Option(args, "--instance", null);
        Guid parsed;
        if (!Guid.TryParse(instance, out parsed)) { Console.Error.WriteLine("A GUID --instance is required."); return 2; }
        string eventName = @"Local\ChatGPT-DurableRelay-" + parsed.ToString("N");
        if (Array.IndexOf(args, "--stop") >= 0)
        {
            try { using (var stop = EventWaitHandle.OpenExisting(eventName)) stop.Set(); return 0; }
            catch (WaitHandleCannotBeOpenedException) { return 3; }
        }
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        string diagnosticIp = Option(args, "--connect-ip", null);
        if (diagnosticIp != null && (!IPAddress.TryParse(diagnosticIp, out diagnosticAddress) || !PublicV4(diagnosticAddress))) return 2;
        string readyPath = Option(args, "--ready-file", null);
        int port, proxyPort;
        if (!Int32.TryParse(Option(args, "--listen-port", "443"), out port) || port < 0 || port > 65535 ||
            !Int32.TryParse(Option(args, "--proxy-port", "7896"), out proxyPort) || proxyPort < 1 || proxyPort > 65535 || port == proxyPort)
            return 2;
        var listener = new TcpListener(IPAddress.Loopback, port);
        var slots = new SemaphoreSlim(32, 32);
        string stopReason = "startup_exit";
        try
        {
            string explicitLog = Option(args, "--log-file", null);
            log = explicitLog == null ? LaunchLog.Create(parsed.ToString("N"), "relay") : LaunchLog.CreateAt(explicitLog, "relay");
            bool created;
            using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset, eventName, out created))
            {
                if (!created) return 4;
                listener.Start(32);
                int boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                string ready = "{\"pid\":" + Process.GetCurrentProcess().Id + ",\"port\":" + boundPort + ",\"instance\":\"" + parsed + "\"}";
                if (readyPath != null) File.WriteAllText(readyPath, ready, new UTF8Encoding(false));
                Log("LISTEN 127.0.0.1:" + boundPort + " proxy=127.0.0.1:" + proxyPort);
                Console.WriteLine(ready);
                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; stop.Set(); };
                while (!stop.WaitOne(50))
                {
                    if (!listener.Pending()) continue;
                    TcpClient incoming = listener.AcceptTcpClient();
                    if (!slots.Wait(0)) { incoming.Close(); continue; }
                    // Serve contains its own exception handling; active clients are closed at shutdown.
#pragma warning disable 4014
                    Serve(incoming, proxyPort, slots);
#pragma warning restore 4014
                }
                stopReason = "stop_event";
            }
            return 0;
        }
        catch (Exception ex) { stopReason = "main_failure"; Log("START_FAILED type=" + ex.GetType().Name); Console.Error.WriteLine(ex.Message); return 1; }
        finally
        {
            listener.Stop();
            var snapshots = new List<string>();
            lock (Gate)
            {
                foreach (RelayDiagnostics diagnostics in ActiveDiagnostics.Values)
                {
                    diagnostics.BeginCleanup("relay_shutdown");
                    snapshots.Add("tunnel=" + diagnostics.Id + " SHUTDOWN_SNAPSHOT stopReason=" + stopReason + " " + diagnostics.Snapshot());
                }
                foreach (TcpClient client in Clients) client.Close();
            }
            foreach (string snapshot in snapshots) Log(snapshot);
            Log("STOPPED");
        }
    }
}
