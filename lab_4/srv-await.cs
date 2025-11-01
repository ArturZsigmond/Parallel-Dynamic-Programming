// Minimal HTTP multi-file downloader using async/await over Task-wrapped Begin/End.
// Usage: dotnet run --project . http://example.com/a http://example.com/b
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: downloader <http://url1> <http://url2> ...");
            return 1;
        }

        Directory.CreateDirectory("downloads");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var tasks = new Task[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            var url = new Uri(args[i]);
            var outPath = MakeOutputPath(url, i);
            tasks[i] = DownloadOneAsync(url, outPath);
        }

        // Note: requirement allows waiting only in Main. We await here because async Main is still "Main".
        await Task.WhenAll(tasks);

        sw.Stop();
        Console.WriteLine($"All done in {sw.ElapsedMilliseconds} ms");
        return 0;
    }

    static string MakeOutputPath(Uri u, int idx)
    {
        string file = Path.GetFileName(u.AbsolutePath);
        if (string.IsNullOrEmpty(file)) file = "index.html";
        foreach (var c in Path.GetInvalidFileNameChars()) file = file.Replace(c, '_');
        return Path.Combine("downloads", $"{idx:00}-{file}");
    }

    static async Task DownloadOneAsync(Uri url, string outPath)
    {
        Console.WriteLine($"[AWAIT] {url} -> {outPath}");

        // DNS resolve (prefer IPv4 when present)
        var addrs = await Dns.GetHostAddressesAsync(url.Host);
        IPAddress ip = null;
        foreach (var a in addrs)
            if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
        ip ??= addrs[0];
        var ep = new IPEndPoint(ip, url.IsDefaultPort ? 80 : url.Port);

        using var sock = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        await ConnectAsync(sock, ep);

        var req = BuildRequest(url);
        var reqBytes = Encoding.ASCII.GetBytes(req);

        int sent = 0;
        while (sent < reqBytes.Length)
            sent += await SendAsync(sock, reqBytes, sent, reqBytes.Length - sent);

        var recvBuf = new byte[8192];
        var headerAccum = new MemoryStream();
        int headerEnd = -1;

        // read headers loop
        while (headerEnd < 0)
        {
            int n = await ReceiveAsync(sock, recvBuf, 0, recvBuf.Length);
            if (n == 0) throw new Exception("EOF before headers");
            headerAccum.Write(recvBuf, 0, n);
            headerEnd = FindHeaderEnd(headerAccum.GetBuffer(), (int)headerAccum.Length);
        }

        ParseHeaders(headerAccum.GetBuffer(), headerEnd + 4, (int)headerAccum.Length,
                     out long contentLen, out int bodyOffset, out int bodyCount);
        if (contentLen < 0) throw new Exception("Content-Length not found");

        using var fs = File.Create(outPath);
        long have = 0;
        if (bodyCount > 0)
        {
            fs.Write(headerAccum.GetBuffer(), bodyOffset, bodyCount);
            have += bodyCount;
        }

        // body loop
        while (have < contentLen)
        {
            int n = await ReceiveAsync(sock, recvBuf, 0, recvBuf.Length);
            if (n == 0) throw new Exception("EOF before full body");
            fs.Write(recvBuf, 0, n);
            have += n;
        }

        Console.WriteLine($"[OK] {outPath} ({have} bytes)");
    }

    // ---- Socket Task wrappers (Begin/End under the hood) ----
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

    // ---- HTTP helpers ----
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
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(line.Substring(15).Trim(), out var v))
                    contentLength = v;
            }
        }
    }
}
