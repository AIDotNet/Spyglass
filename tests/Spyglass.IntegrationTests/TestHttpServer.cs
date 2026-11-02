using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Spyglass.IntegrationTests;

/// <summary>Local HTTP server the test app calls (never the Spyglass hub, whose traffic is excluded from capture).</summary>
public sealed class TestHttpServer : IDisposable
{
    readonly HttpListener _listener = new();
    public int Port { get; }
    public Uri BaseUri => new($"http://127.0.0.1:{Port}/");

    public TestHttpServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add(BaseUri.ToString());
        _listener.Start();
        _ = Loop();
    }

    async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext c;
            try { c = await _listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(async () =>
            {
                var r = c.Response;
                try
                {
                    switch (c.Request.Url!.AbsolutePath)
                    {
                        case "/api/user":
                            r.AddHeader("Set-Cookie", "theme=dark; Path=/; HttpOnly");
                            await Write(r, 200, "application/json", """{"id":1,"fullName":"Ada Lovelace"}""");
                            break;
                        case "/api/login":
                            using (var reader = new StreamReader(c.Request.InputStream)) await reader.ReadToEndAsync();
                            await Write(r, 401, "application/json", """{"error":"invalid_credentials"}""");
                            break;
                        case "/api/old":
                            r.StatusCode = 302;
                            r.RedirectLocation = "/api/user";
                            r.Close();
                            break;
                        default:
                            await Write(r, 404, "text/plain", "not found");
                            break;
                    }
                }
                catch { }
            });
        }
    }

    static async Task Write(HttpListenerResponse r, int status, string type, string body)
    {
        var b = Encoding.UTF8.GetBytes(body);
        r.StatusCode = status;
        r.ContentType = type;
        r.ContentLength64 = b.Length;
        await r.OutputStream.WriteAsync(b);
        r.Close();
    }

    public void Dispose() => _listener.Close();
}
