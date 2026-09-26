using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Configuration;
using BaronDeskAgent.ServiceCore.Connection;
using BaronDeskAgent.ServiceCore.Credentials;
using Microsoft.Extensions.Options;

namespace BaronDeskAgent.ServiceCore.Games;

public interface IGameCatalogClient
{
    /// <exception cref="HttpRequestException">The request failed or the server refused it.</exception>
    /// <exception cref="JsonException">The body is not a catalog.</exception>
    /// <exception cref="InvalidOperationException">No station credential is provisioned.</exception>
    Task<GameCatalogResponse> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// <c>GET /stations/me/games</c> with the station credential, over HTTPS pinned like the WSS link. Pulled over REST
/// because a full catalog can exceed the WebSocket's 64 KB inbound frame limit.
/// </summary>
public sealed class HttpGameCatalogClient : IGameCatalogClient, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private const int MaxResponseBytes = 1024 * 1024;

    private readonly HttpClient _http;
    private readonly IStationCredentialStore _credentials;
    private readonly AgentOptions _options;

    public HttpGameCatalogClient(IOptions<AgentOptions> options, IStationCredentialStore credentials, ILogger<HttpGameCatalogClient> logger)
    {
        _options = options.Value;
        _credentials = credentials;
        Endpoint = _options.ResolveGameCatalogUri();
        _http = PinnedHttpClient.Create(_options, logger, RequestTimeout, MaxResponseBytes);
    }

    public Uri Endpoint { get; }

    public async Task<GameCatalogResponse> FetchAsync(CancellationToken cancellationToken)
    {
        // Same resolution as the WSS upgrade: DPAPI store first, configuration fallback in Development only.
        var token = _credentials.TryGetToken() ?? _options.StationToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("No station credential is provisioned.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GET {Endpoint.AbsolutePath} returned HTTP {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync(AgentJsonContext.Default.GameCatalogResponse, cancellationToken)
            ?? throw new JsonException("The catalog response is empty.");
    }

    public void Dispose() => _http.Dispose();
}
