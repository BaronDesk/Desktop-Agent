using BaronDeskAgent.ServiceCore.Configuration;

namespace BaronDeskAgent.ServiceCore.Connection;

/// <summary>
/// HTTPS client for the agent's REST calls (enrollment, game catalog), locked to the same pinned server as the WSS link.
/// </summary>
internal static class PinnedHttpClient
{
    public static HttpClient Create(AgentOptions options, ILogger logger, TimeSpan timeout, int maxResponseBytes)
    {
        var handler = new SocketsHttpHandler
        {
            // LAN only: never route a credential through a proxy or follow a redirect away from the pinned server.
            UseProxy = false,
            AllowAutoRedirect = false,
            SslOptions = { RemoteCertificateValidationCallback = CertificatePinning.CreateCallback(options, logger) }
        };

        return new HttpClient(handler)
        {
            Timeout = timeout,
            MaxResponseContentBufferSize = maxResponseBytes
        };
    }
}
