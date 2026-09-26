using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Configuration;
using BaronDeskAgent.ServiceCore.Connection;
using Microsoft.Extensions.Options;

namespace BaronDeskAgent.ServiceCore.Enrollment;

/// <summary>
/// <c>POST /enrollment/request</c> over HTTPS with the same certificate pin as the WSS link: on a pin mismatch the
/// TLS handshake fails and the one-time token is never sent.
/// </summary>
public sealed class HttpEnrollmentClient : IEnrollmentClient, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;

    public HttpEnrollmentClient(IOptions<AgentOptions> options, ILogger<HttpEnrollmentClient> logger)
    {
        Endpoint = options.Value.ResolveEnrollmentUri();
        _http = PinnedHttpClient.Create(options.Value, logger, RequestTimeout, maxResponseBytes: 64 * 1024);
    }

    public Uri Endpoint { get; }

    public async Task<EnrollmentResponse> RequestAsync(EnrollmentRequest request, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(Endpoint, request, AgentJsonContext.Default.EnrollmentRequest, cancellationToken);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
        {
            var refusal = await TryReadAsync(response, cancellationToken);
            return new EnrollmentResponse
            {
                Status = EnrollmentStatuses.Rejected,
                Reason = refusal?.Reason ?? $"HTTP {(int)response.StatusCode}"
            };
        }

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync(AgentJsonContext.Default.EnrollmentResponse, cancellationToken)
            ?? throw new JsonException("The enrollment response is empty.");
    }

    public void Dispose() => _http.Dispose();

    private static async Task<EnrollmentResponse?> TryReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(AgentJsonContext.Default.EnrollmentResponse, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
