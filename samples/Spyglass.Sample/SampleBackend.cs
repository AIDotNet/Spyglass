using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace Spyglass.Sample;

/// <summary>
/// A tiny local API so the Network panel has something to show: the sample calls it every few seconds
/// (JSON GET, a POST that fails with 401, a redirect, an image).
/// </summary>
internal sealed class SampleBackend : IDisposable
{
    // 1×1 PNG
    static readonly byte[] Avatar = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    readonly HttpListener _listener = new();
    readonly HttpClient _http = new();
    readonly Timer _timer;
    public Uri BaseUri { get; }

    public SampleBackend()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(BaseUri.ToString());
        _listener.Start();
        _ = Serve();
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
    }

    async Task TickAsync()
    {
        try
        {
            await new UserService(_http, BaseUri).LoadUserAsync();
            await new UserService(_http, BaseUri).SignInAsync("ada", "wrong-password");
            await _http.GetStringAsync(new Uri(BaseUri, "api/profile-old"));
            await _http.GetByteArrayAsync(new Uri(BaseUri, "img/avatar.png"));
        }
        catch { /* demo traffic */ }
    }

    async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext c;
            try { c = await _listener.GetContextAsync(); } catch { return; }
            var r = c.Response;
            try
            {
                switch (c.Request.Url!.AbsolutePath)
                {
                    case "/api/user":
                        await Write(r, 200, "application/json", """{"id":42,"fullName":"Ada Lovelace","roles":["admin","author"],"settings":{"theme":"dark","notifications":true}}""");
                        break;
                    case "/api/login":
                        using (var reader = new StreamReader(c.Request.InputStream)) await reader.ReadToEndAsync();
                        await Write(r, 401, "application/json", """{"error":"invalid_credentials","message":"Wrong user name or password."}""");
                        break;
                    case "/api/profile-old":
                        r.StatusCode = 302; r.RedirectLocation = "/api/user"; r.Close();
                        break;
                    case "/img/avatar.png":
                        r.ContentType = "image/png"; r.ContentLength64 = Avatar.Length; await r.OutputStream.WriteAsync(Avatar); r.Close();
                        break;
                    default:
                        await Write(r, 404, "text/plain", "not found");
                        break;
                }
            }
            catch { }
        }
    }

    static async Task Write(HttpListenerResponse r, int status, string type, string body)
    {
        var b = Encoding.UTF8.GetBytes(body);
        r.StatusCode = status; r.ContentType = type; r.ContentLength64 = b.Length;
        await r.OutputStream.WriteAsync(b);
        r.Close();
    }

    public void Dispose()
    {
        _timer.Dispose();
        _listener.Close();
    }
}

/// <summary>Shows up as the "Initiator" of the requests.</summary>
internal sealed class UserService(HttpClient http, Uri baseUri)
{
    public Task<string> LoadUserAsync() => http.GetStringAsync(new Uri(baseUri, "api/user?include=settings"));

    public async Task SignInAsync(string user, string password)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/login"))
        {
            Content = new StringContent($$"""{"user":"{{user}}","password":"{{password}}"}""", Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "demo-token-123");
        using var _ = await http.SendAsync(req);
    }
}
