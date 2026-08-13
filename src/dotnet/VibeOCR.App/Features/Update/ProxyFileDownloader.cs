using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Velopack.Sources;

namespace VibeOCR.App.Features.Update;

internal sealed class ProxyFileDownloader(
    Uri proxyUri,
    Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>?
        certificateValidator = null) : HttpClientFileDownloader
{
    private readonly Uri _proxyUri = proxyUri ?? throw new ArgumentNullException(nameof(proxyUri));
    private readonly Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>?
        _certificateValidator = certificateValidator;

    protected override HttpClientHandler CreateHttpClientHandler() => new()
    {
        Proxy = new WebProxy(_proxyUri)
        {
            BypassProxyOnLocal = false,
        },
        UseProxy = true,
        ServerCertificateCustomValidationCallback = _certificateValidator,
    };
}
