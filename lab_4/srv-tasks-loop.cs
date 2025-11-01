// Minimal HTTP multi-file downloader using Task wrappers + ContinueWith chaining.
// Usage: dotnet run --project . http://example.com/a http://example.com/b
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: downloader <http://url1> <http://url2> ...");
            return;
        }

        Directory.CreateDirectory("downloads");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var tasks = new Task[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            var url = new Uri(args[i]);
            var outPath = MakeOutputPath(url, i);
            tasks[i] = DownloadWithContinuations(url, outPath);
        }

        Task.WhenAll(tasks).Wait(); // allowed in Main
        sw.Stop();
        Console.WriteLine($"All done in {sw.ElapsedMilliseconds} ms");
    }

    static string MakeOutputPath(Uri u, int idx)
    {
        string file = Path.GetFileName(u.AbsolutePath);
        if (string.IsNullOrEmpty(file)) file = "index.html";
        foreach (var c in Path.GetInvalidFileNameChars()) file = file.Replace(c, '_');
        return Path.Combine("downloads", $"{idx:00}-{file}");
    }

    // ---- Task-based wrappers over Begin/End ----
    static Task ConnectAsync(Socket s, EndPoint ep)
    {
        var tcs = new TaskCompletionSource<bool>();
        s.BeginConnect(ep, ar => {
            try { s.EndConnect(ar); tcs.SetResult(true); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }
    static Task<int> SendAsync(Socket s, byte[] buf, int off, int cnt)
    {
        var tcs = new TaskCompletionSource<int>();
        s.BeginSend(buf, off, cnt, SocketFlags.None, ar => {
            try { tcs.SetResult(s.EndSend(ar)); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }
    static Task<int> ReceiveAsync(Socket s, byte[] buf, int off, int cnt)
    {
        var tcs = new TaskCompletionSource<int>();
        s.BeginReceive(buf, off, cnt, SocketFlags.None, ar => {
            try { tcs.SetResult(s.EndReceive(ar)); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }

    // ---- The continuation-style pipeline ----
    static Task DownloadWithContinuations(Uri url, string outPath)
    {
        Console.WriteLine($"[TASKS] {url} -> {outPath}");
        var tcs = new TaskCompletionSource<bool>();

        // Resolve DNS (sync ok for the lab; could use Dns.GetHostAddressesAsync)
        IPAddress ip = null;
        var hostEntry = Dns.GetHostEntry(url.Host);
        foreach (var a in hostEntry.AddressList)
            if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
        ip ??= hostEntry.AddressList[0];
        var ep = new IPEndPoint(ip, url.IsDefaultPort ? 80 : url.Port);

        var sock = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        var req = BuildRequest(url);
        var reqBytes = Encoding.ASCII.GetBytes(req);

        var recvBuf = new byte[8192];
        var headerAccum = new MemoryStream();
        int headerEnd = -1;
        long contentLen = -1;
        long haveBody = 0;
        FileStream file = null;

        // Helpers to chain receive until a condition
        Func<Task> readHeadersLoop = null;
        readHeadersLoop = () =>
            ReceiveAsync(sock, recvBuf, 0, recvBuf.Length).ContinueWith(t =>
            {
                int n = t.Result;
                if (n == 0) throw new Exception("EOF before headers");
                headerAccum.Write(recvBuf, 0, n);
                headerEnd = FindHeaderEnd(headerAccum.GetBuffer(), (int)headerAccum.Length);
                if (headerEnd < 0) return readHeadersLoop();
                ParseHeaders(headerAccum.GetBuffer(), headerEnd + 4, (int)headerAccum.Length,
                             out contentLen, out int bodyOffset, out int bodyCount);
                if (contentLen < 0) throw new Exception("Content-Length not found");
                file = File.Create(outPath);
                if (bodyCount > 0)
                {
                    file.Write(headerAccum.GetBuffer(), bodyOffset, bodyCount);
                    haveBody += bodyCount;
                }
                return Task.CompletedTask;
            }).Unwrap();

        Func<Task> readBodyLoop = null;
        readBodyLoop = () =>
            ReceiveAsync(sock, recvBuf, 0, recvBuf.Length).ContinueWith(t =>
            {
                int n = t.Result;
                if (n == 0) throw new Exception("EOF before full body");
                file.Write(recvBuf, 0, n);
                haveBody += n;
                if (haveBody < contentLen) return readBodyLoop();
                return Task.CompletedTask;
            }).Unwrap();

        // Pipeline: connect → send all → read headers → read body
        ConnectAsync(sock, ep)
        .ContinueWith(_ => SendAll(sock, reqBytes))
        .ContinueWith(_ => readHeadersLoop()).Unwrap()
        .ContinueWith(_ => readBodyLoop()).Unwrap()
        .ContinueWith(t =>
        {
            try
            {
                if (t.IsFaulted) throw t.Exception.InnerException;
                file?.Dispose();
                sock.Shutdown(SocketShutdown.Both);
                sock.Close();
                Console.WriteLine($"[OK] {outPath} ({haveBody} bytes)");
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                try { file?.Dispose(); sock?.Close(); } catch { }
                Console.WriteLine($"[ERR] {url}: {ex.Message}");
                tcs.SetException(ex);
            }
        });

        return tcs.Task;
    }

    static Task SendAll(Socket s, byte[] data)
    {
        int sent = 0;
        Task loop() => (sent < data.Length)
            ? SendAsync(s, data, sent, data.Length - sent).ContinueWith(t => { sent += t.Result; return loop(); }).Unwrap()
            : Task.CompletedTask;
        return loop();
    }

    static string BuildRequest(Uri u)
    {
        string path = string.IsNullOrEmpty(u.PathAndQuery) ? "/" : u.PathAndQuery;
        return $"GET {path} HTTP/1.1\r\nHost: {u.Host}\r\nConnection: close\r\nUser-Agent: MinimalDownloader/1.0\r\nAccept: */*\r\n\r\n";
    }
    static int FindHeaderEnd(byte[] buf, int len)
    {
        for (int i = 3; i < len; i++)
            if (buf[i - 3] == (byte)'\r' && buf[i - 2] == (byte)'\n' && buf[i - 1] == (byte)'\r' && buf[i] == (byte)'\n')
                return i - 3;
        return -1;
    }
    static void ParseHeaders(byte[] buf, int headerLen, int totalLen,
                             out long contentLength, out int bodyOffset, out int bodyCount)
    {
        contentLength = -1;
        bodyOffset = headerLen;
        bodyCount = Math.Max(0, totalLen - headerLen);
        var headers = Encoding.ASCII.GetString(buf, 0, headerLen);
        foreach (var line in headers.Split(new[] { "\r\n" }, StringSplitOptions.None))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(line.Substring(15).Trim(), out var v))
                contentLength = v;
    }
}
