using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;
using VibeOCR.App.Features.Update;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class VelopackTransportTests
{
    [Fact]
    public void InvalidSettingsEntriesAreSkippedWithoutBlockingStartup()
    {
        string configFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(configFile, JsonSerializer.Serialize(new
            {
                update = new
                {
                    sources = new object[]
                    {
                        new { kind = "future_source", url = "https://unknown.invalid/" },
                        new { kind = "forward_proxy", url = "file:///C:/proxy" },
                        new { kind = "url_prefix", url = "https://mirror.invalid/prefix/" },
                    },
                },
            }));

            VelopackFeedEndpoint endpoint = Assert.Single(
                VelopackFeedFactory.FromSettings(configFile));

            Assert.Equal(
                "https://mirror.invalid/prefix/" + VelopackFeedFactory.DirectBaseUrl,
                endpoint.BaseUri.AbsoluteUri);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public void SettingsWithNoUsableEntriesFallBackToDirect()
    {
        string configFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                configFile,
                "{\"update\":{\"sources\":[{\"kind\":\"forward_proxy\",\"url\":\"ftp://proxy.invalid/\"}]}}");

            VelopackFeedEndpoint endpoint = Assert.Single(
                VelopackFeedFactory.FromSettings(configFile));

            Assert.Equal(VelopackFeedFactory.DirectBaseUrl, endpoint.BaseUri.AbsoluteUri);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public async Task DirectSourceUsesSameOriginForFeedAndFullPackage()
    {
        await using var server = await LoopbackServer.StartAsync();
        var source = new SimpleWebSource(server.BaseUri);

        await DownloadFeedAndPackageAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "/releases.win.json?arch=x64&os=win&rid=win-x64",
                "/VibeOCRNext-0.4.0-full.nupkg",
            ],
            server.Paths);
    }

    [Fact]
    public async Task UrlPrefixSourcePreservesCompleteOriginForFeedAndFullPackage()
    {
        await using var server = await LoopbackServer.StartAsync();
        string origin = "https://github.com/FelixJI/vibeocr-next/releases/latest/download/";
        var source = new SimpleWebSource(new Uri(server.BaseUri.AbsoluteUri + origin));

        await DownloadFeedAndPackageAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                $"/{origin}releases.win.json?arch=x64&os=win&rid=win-x64",
                $"/{origin}VibeOCRNext-0.4.0-full.nupkg",
            ],
            server.Paths);
    }

    [Fact]
    public async Task ForwardProxyCarriesFeedAndFullPackageWithoutRewritingOrigin()
    {
        await using var proxy = await LoopbackServer.StartAsync();
        var source = new SimpleWebSource(
            "http://updates.invalid/releases/",
            new ProxyFileDownloader(proxy.BaseUri));

        await DownloadFeedAndPackageAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "http://updates.invalid/releases/releases.win.json?arch=x64&os=win&rid=win-x64",
                "http://updates.invalid/releases/VibeOCRNext-0.4.0-full.nupkg",
            ],
            proxy.Paths);
    }

    [Fact]
    public async Task HttpsForwardProxyTunnelsFeedAndFullPackageThroughConnect()
    {
        await using var origin = await TlsOriginServer.StartAsync();
        await using var proxy = await ConnectProxy.StartAsync();
        var source = new SimpleWebSource(
            origin.BaseUri,
            new ProxyFileDownloader(proxy.BaseUri, (_, _, _, _) => true));

        try
        {
            await DownloadFeedAndPackageAsync(source, TestContext.Current.CancellationToken);
        }
        catch (Exception error)
        {
            Assert.Fail(
                $"CONNECT authorities={string.Join(',', proxy.Authorities)}; " +
                $"origin paths={string.Join(',', origin.Paths)}; " +
                $"proxy errors={string.Join(" | ", proxy.Errors)}; " +
                $"origin errors={string.Join(" | ", origin.Errors)}; error={error}");
        }

        Assert.Equal(
            [
                "/releases.win.json?arch=x64&os=win&rid=win-x64",
                "/VibeOCRNext-0.4.0-full.nupkg",
            ],
            origin.Paths);
        string authority = $"{origin.BaseUri.Host}:{origin.BaseUri.Port}";
        Assert.Equal([authority, authority], proxy.Authorities);
    }

    private static async Task DownloadFeedAndPackageAsync(
        SimpleWebSource source,
        CancellationToken cancellationToken)
    {
        VelopackAssetFeed feed = await source.GetReleaseFeed(
            NullVelopackLogger.Instance,
            VelopackUpdateCoordinator.PackId,
            VelopackUpdateCoordinator.Channel);
        VelopackAsset asset = Assert.Single(feed.Assets);
        string destination = Path.Combine(
            Path.GetTempPath(),
            $"vibeocr-transport-{Guid.NewGuid():N}.nupkg");
        try
        {
            await source.DownloadReleaseEntry(
                NullVelopackLogger.Instance,
                asset,
                destination,
                _ => { },
                cancellationToken);
            Assert.Equal("full-package", await File.ReadAllTextAsync(destination, cancellationToken));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _serve;
        private readonly List<string> _paths = [];

        private LoopbackServer(HttpListener listener, Uri baseUri)
        {
            _listener = listener;
            BaseUri = baseUri;
            _serve = ServeAsync();
        }

        public Uri BaseUri { get; }
        public IReadOnlyList<string> Paths
        {
            get
            {
                lock (_paths) return _paths.ToArray();
            }
        }

        public static Task<LoopbackServer> StartAsync()
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var baseUri = new Uri($"http://127.0.0.1:{port}/");
            var listener = new HttpListener();
            listener.Prefixes.Add(baseUri.AbsoluteUri);
            listener.Start();
            return Task.FromResult(new LoopbackServer(listener, baseUri));
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Stop();
            await _serve;
            _listener.Close();
            _shutdown.Dispose();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    HttpListenerContext context = await _listener.GetContextAsync();
                    string path = context.Request.RawUrl ?? string.Empty;
                    lock (_paths) _paths.Add(path);
                    byte[] body = path.Contains("releases.win.json", StringComparison.Ordinal)
                        ? Encoding.UTF8.GetBytes(FeedJson)
                        : Encoding.UTF8.GetBytes("full-package");
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
            }
            catch (Exception error) when (
                _shutdown.IsCancellationRequested &&
                error is HttpListenerException or ObjectDisposedException)
            {
            }
        }

        private const string FeedJson = """
            {"Assets":[{"PackageId":"VibeOCRNext","Version":"0.4.0","Type":"Full","FileName":"VibeOCRNext-0.4.0-full.nupkg","SHA256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","Size":12}]}
            """;
    }

    private sealed class TlsOriginServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _certificate;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly ConcurrentBag<TcpClient> _clients = [];
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly List<string> _paths = [];
        private readonly ConcurrentBag<string> _errors = [];
        private readonly Task _serve;

        private TlsOriginServer(
            TcpListener listener,
            X509Certificate2 certificate,
            Uri baseUri)
        {
            _listener = listener;
            _certificate = certificate;
            BaseUri = baseUri;
            _serve = ServeAsync();
        }

        public Uri BaseUri { get; }
        public IReadOnlyList<string> Paths
        {
            get
            {
                lock (_paths) return _paths.ToArray();
            }
        }
        public IReadOnlyCollection<string> Errors => _errors;

        public static Task<TlsOriginServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            RSA rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=localhost",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using X509Certificate2 generated = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddHours(1));
            X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(
                generated.Export(X509ContentType.Pfx),
                password: null,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            rsa.Dispose();
            return Task.FromResult(new TlsOriginServer(
                listener,
                certificate,
                new Uri($"https://127.0.0.1:{port}/")));
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Stop();
            foreach (TcpClient client in _clients) client.Dispose();
            await _serve;
            await IgnoreShutdownAsync(Task.WhenAll(_connections));
            _certificate.Dispose();
            _shutdown.Dispose();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                    _clients.Add(client);
                    _connections.Add(HandleAsync(client));
                }
            }
            catch (Exception error) when (
                _shutdown.IsCancellationRequested &&
                error is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                using (client)
                using (var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        EnabledSslProtocols = SslProtocols.Tls12,
                    }, _shutdown.Token);
                    using var reader = new StreamReader(
                        tls,
                        Encoding.ASCII,
                        detectEncodingFromByteOrderMarks: false,
                        leaveOpen: true);
                    while (!_shutdown.IsCancellationRequested)
                    {
                        string? requestLine = await reader.ReadLineAsync(_shutdown.Token);
                        if (requestLine is null) return;
                        string path = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_shutdown.Token))) { }
                        lock (_paths) _paths.Add(path);
                        byte[] body = path.Contains("releases.win.json", StringComparison.Ordinal)
                            ? Encoding.UTF8.GetBytes(FeedJson)
                            : Encoding.UTF8.GetBytes("full-package");
                        byte[] headers = Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: keep-alive\r\n\r\n");
                        await tls.WriteAsync(headers, _shutdown.Token);
                        await tls.WriteAsync(body, _shutdown.Token);
                        await tls.FlushAsync(_shutdown.Token);
                    }
                }
            }
            catch (Exception error)
            {
                _errors.Add(error.ToString());
            }
        }
    }

    private sealed class ConnectProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly ConcurrentBag<TcpClient> _clients = [];
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly List<string> _authorities = [];
        private readonly ConcurrentBag<string> _errors = [];
        private readonly Task _serve;

        private ConnectProxy(TcpListener listener, Uri baseUri)
        {
            _listener = listener;
            BaseUri = baseUri;
            _serve = ServeAsync();
        }

        public Uri BaseUri { get; }
        public IReadOnlyList<string> Authorities
        {
            get
            {
                lock (_authorities) return _authorities.ToArray();
            }
        }
        public IReadOnlyCollection<string> Errors => _errors;

        public static Task<ConnectProxy> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return Task.FromResult(new ConnectProxy(
                listener,
                new Uri($"http://127.0.0.1:{port}/")));
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Stop();
            foreach (TcpClient client in _clients) client.Dispose();
            await _serve;
            await IgnoreShutdownAsync(Task.WhenAll(_connections));
            _shutdown.Dispose();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                    _clients.Add(client);
                    _connections.Add(HandleAsync(client));
                }
            }
            catch (Exception error) when (
                _shutdown.IsCancellationRequested &&
                error is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    NetworkStream downstream = client.GetStream();
                    string header = await ReadHeaderAsync(downstream, _shutdown.Token);
                    string requestLine = header.Split("\r\n", StringSplitOptions.None)[0];
                    string[] parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    Assert.Equal("CONNECT", parts[0]);
                    string authority = parts[1];
                    lock (_authorities) _authorities.Add(authority);
                    int separator = authority.LastIndexOf(':');
                    string host = authority[..separator];
                    int port = int.Parse(authority[(separator + 1)..]);
                    using var upstreamClient = new TcpClient();
                    await upstreamClient.ConnectAsync(host, port, _shutdown.Token);
                    NetworkStream upstream = upstreamClient.GetStream();
                    await downstream.WriteAsync(
                        "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(),
                        _shutdown.Token);
                    Task upload = downstream.CopyToAsync(upstream, _shutdown.Token);
                    Task download = upstream.CopyToAsync(downstream, _shutdown.Token);
                    Task completed = await Task.WhenAny(upload, download);
                    await completed;
                }
            }
            catch (Exception error)
            {
                _errors.Add(error.ToString());
            }
        }

        private static async Task<string> ReadHeaderAsync(
            Stream stream,
            CancellationToken cancellationToken)
        {
            var bytes = new List<byte>();
            while (bytes.Count < 16 * 1024)
            {
                byte[] next = new byte[1];
                if (await stream.ReadAsync(next, cancellationToken) == 0) break;
                bytes.Add(next[0]);
                int count = bytes.Count;
                if (count >= 4 && bytes[count - 4] == '\r' && bytes[count - 3] == '\n' &&
                    bytes[count - 2] == '\r' && bytes[count - 1] == '\n')
                {
                    return Encoding.ASCII.GetString(bytes.ToArray());
                }
            }
            throw new InvalidDataException("CONNECT request header is incomplete.");
        }
    }

    private static async Task IgnoreShutdownAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception error) when (
            error is OperationCanceledException or IOException or SocketException or
            ObjectDisposedException or AuthenticationException)
        {
        }
    }

    private const string FeedJson = """
        {"Assets":[{"PackageId":"VibeOCRNext","Version":"0.4.0","Type":"Full","FileName":"VibeOCRNext-0.4.0-full.nupkg","SHA256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","Size":12}]}
        """;
}
