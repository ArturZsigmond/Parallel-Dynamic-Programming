// Minimal HTTP multi-file downloader using pure Begin/End callbacks.
// Usage: dotnet run --project . http://example.com/index.html http://example.com/pic.jpg
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: downloader <http://url1> <http://url2> ...");
            return;
        }

        Directory.CreateDirectory("downloads");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var allDone = new CountdownEvent(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            var url = new Uri(args[i]);
            var outPath = MakeOutputPath(url, i);
            new Session(url, outPath, () => allDone.Signal()).Start();
        }

        allDone.Wait(); // allowed (only in Main)
        sw.Stop();
        Console.WriteLine($"All done in {sw.ElapsedMilliseconds} ms");
    }

    static string MakeOutputPath(Uri u, int idx)
    {
        string file = Path.GetFileName(u.AbsolutePath);
        if (string.IsNullOrEmpty(file)) file = "index.html";
        return Path.Combine("downloads", $"{idx:00}-{Sanitize(file)}");
    }
    static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    sealed class Session
    {
        readonly Uri _url;
        readonly string _outPath;
        readonly Action _onDone;

        Socket _sock;
        byte[] _sendBuf;
        int _sent;

        const int BufSize = 8192;
        readonly byte[] _recvBuf = new byte[BufSize];
        readonly MemoryStream _headerAccum = new MemoryStream();
        int _headerEnd = -1;

        FileStream _file;
        long _contentLen = -1;
        long _haveBody = 0;

        public Session(Uri url, string outPath, Action onDone)
        {
            _url = url;
            _outPath = outPath;
            _onDone = onDone;
        }

        public void Start()
        {
            try
            {
                Console.WriteLine($"[BEGIN/END] { _url } -> { _outPath }");
                var hostEntry = Dns.GetHostEntry(_url.Host);
                IPAddress ip = null;
                foreach (var a in hostEntry.AddressList)
                    if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
                ip ??= hostEntry.AddressList[0];

                var ep = new IPEndPoint(ip, _url.IsDefaultPort ? 80 : _url.Port);
                _sock = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                _sock.BeginConnect(ep, OnConnected, null);
            }
            catch (Exception ex) { Fail(ex); }
        }

        void OnConnected(IAsyncResult ar)
        {
            try
            {
                _sock.EndConnect(ar);
                var req = BuildRequest(_url);
                _sendBuf = Encoding.ASCII.GetBytes(req);
                _sent = 0;
                BeginSendMore();
            }
            catch (Exception ex) { Fail(ex); }
        }

        void BeginSendMore()
        {
            int remaining = _sendBuf.Length - _sent;
            if (remaining <= 0) { BeginReceive(); return; }
            _sock.BeginSend(_sendBuf, _sent, remaining, SocketFlags.None, OnSent, null);
        }

        void OnSent(IAsyncResult ar)
        {
            try
            {
                int n = _sock.EndSend(ar);
                _sent += n;
                BeginSendMore();
            }
            catch (Exception ex) { Fail(ex); }
        }

        void BeginReceive() =>
            _sock.BeginReceive(_recvBuf, 0, _recvBuf.Length, SocketFlags.None, OnRecv, null);

        void OnRecv(IAsyncResult ar)
        {
            try
            {
                int n = _sock.EndReceive(ar);
                if (n == 0)
                {
                    // EOF
                    if (_contentLen >= 0 && _haveBody == _contentLen)
                        Complete();
                    else
                        Fail(new Exception("EOF before full body"));
                    return;
                }

                if (_headerEnd < 0)
                {
                    _headerAccum.Write(_recvBuf, 0, n);
                    _headerEnd = FindHeaderEnd(_headerAccum.GetBuffer(), (int)_headerAccum.Length);
                    if (_headerEnd >= 0)
                    {
                        int headerLen = _headerEnd + 4;
                        ParseHeaders(_headerAccum.GetBuffer(), headerLen, (int)_headerAccum.Length,
                                     out _contentLen, out int bodyOffset, out int bodyCount);
                        if (_contentLen < 0) throw new Exception("Content-Length not found");
                        _file = File.Create(_outPath);
                        if (bodyCount > 0)
                        {
                            _file.Write(_headerAccum.GetBuffer(), bodyOffset, bodyCount);
                            _haveBody += bodyCount;
                        }
                    }
                }
                else
                {
                    _file.Write(_recvBuf, 0, n);
                    _haveBody += n;
                }

                if (_contentLen >= 0 && _haveBody >= _contentLen)
                    Complete();
                else
                    BeginReceive();
            }
            catch (Exception ex) { Fail(ex); }
        }

        void Complete()
        {
            try { _file?.Dispose(); _sock?.Shutdown(SocketShutdown.Both); _sock?.Close(); }
            catch { /* ignore */ }
            Console.WriteLine($"[OK] {_outPath} ({_haveBody} bytes)");
            _onDone();
        }

        void Fail(Exception ex)
        {
            try { _file?.Dispose(); _sock?.Close(); } catch { }
            Console.WriteLine($"[ERR] {_url}: {ex.Message}");
            _onDone();
        }

        static string BuildRequest(Uri u)
        {
            string path = string.IsNullOrEmpty(u.PathAndQuery) ? "/" : u.PathAndQuery;
            return $"GET {path} HTTP/1.1\r\n" +
                   $"Host: {u.Host}\r\n" +
                   $"Connection: close\r\n" +
                   $"User-Agent: MinimalDownloader/1.0\r\n" +
                   $"Accept: */*\r\n\r\n";
        }

        static int FindHeaderEnd(byte[] buf, int len)
        {
            for (int i = 3; i < len; i++)
                if (buf[i - 3] == (byte)'\r' && buf[i - 2] == (byte)'\n' &&
                    buf[i - 1] == (byte)'\r' && buf[i] == (byte)'\n')
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
            var lines = headers.Split(new[] { "\r\n" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    if (long.TryParse(line.Substring(15).Trim(), out var v))
                        contentLength = v;
                }
            }
        }
    }
}
