using Microsoft.Extensions.Options;

namespace BaronDeskAgent.ServiceCore.Configuration;

/// <summary>
/// Fails startup on unsafe or unusable configuration, instead of retrying a broken connection forever
/// or silently running without certificate validation.
/// </summary>
public sealed class AgentOptionsValidator : IValidateOptions<AgentOptions>
{
    private readonly IHostEnvironment _environment;

    public AgentOptionsValidator(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, AgentOptions options)
    {
        var errors = new List<string>();
        var isDevelopment = _environment.IsDevelopment();

        if (!Uri.TryCreate(options.ServerUrl, UriKind.Absolute, out var serverUri))
        {
            errors.Add($"ServerUrl '{options.ServerUrl}' is not an absolute URI.");
        }
        else if (serverUri.Scheme != "wss" && !(isDevelopment && serverUri.Scheme == "ws"))
        {
            errors.Add("ServerUrl must use wss:// (ws:// is only allowed in Development).");
        }

        try
        {
            var pin = options.GetPinnedCertificateHash();
            if (pin is null && !isDevelopment)
            {
                errors.Add("PinnedCertificateHash is required outside Development: the LAN server certificate must be pinned.");
            }
        }
        catch (FormatException ex)
        {
            errors.Add($"PinnedCertificateHash is invalid: {ex.Message}");
        }

        if (options.AllowUntrustedCertificate && !isDevelopment)
        {
            errors.Add("AllowUntrustedCertificate disables TLS validation and is only allowed in Development.");
        }

        if (!string.IsNullOrWhiteSpace(options.StationToken) && !isDevelopment)
        {
            errors.Add(
                "StationToken must not be stored in configuration outside Development (plain text). " +
                "Provision it with 'BaronDeskAgent.ServiceCore.exe --set-station-token' from an elevated prompt.");
        }

        if (!string.IsNullOrWhiteSpace(options.EnrollmentToken) && !isDevelopment)
        {
            errors.Add(
                "EnrollmentToken must not be stored in configuration outside Development (plain text). " +
                "Provision it with 'BaronDeskAgent.ServiceCore.exe --set-enrollment-token' from an elevated prompt.");
        }

        if (serverUri is not null || !string.IsNullOrWhiteSpace(options.EnrollmentUrl))
        {
            try
            {
                var enrollmentUri = options.ResolveEnrollmentUri();
                if (enrollmentUri.Scheme != Uri.UriSchemeHttps && !(isDevelopment && enrollmentUri.Scheme == Uri.UriSchemeHttp))
                {
                    errors.Add("EnrollmentUrl must use https:// (http:// is only allowed in Development).");
                }
            }
            catch (UriFormatException)
            {
                errors.Add($"EnrollmentUrl '{options.EnrollmentUrl}' is not an absolute URI.");
            }
        }

        if (options.EnrollmentPollSeconds is not (>= 1 and <= 3600))
        {
            errors.Add("EnrollmentPollSeconds must be in [1, 3600].");
        }

        if (options.ReconnectBaseDelaySeconds is not (> 0 and <= 300))
        {
            errors.Add("ReconnectBaseDelaySeconds must be in (0, 300].");
        }

        if (options.ReconnectMaxDelaySeconds < options.ReconnectBaseDelaySeconds || options.ReconnectMaxDelaySeconds > 3600)
        {
            errors.Add("ReconnectMaxDelaySeconds must be between ReconnectBaseDelaySeconds and 3600.");
        }

        if (options.KeepAliveIntervalSeconds is not (> 0 and <= 600))
        {
            errors.Add("KeepAliveIntervalSeconds must be in (0, 600].");
        }

        if (options.MaxTimestampDriftSeconds is not (> 0 and <= 3600))
        {
            errors.Add("MaxTimestampDriftSeconds must be in (0, 3600].");
        }

        if (!string.IsNullOrWhiteSpace(options.LockUiExecutablePath) && !Path.IsPathFullyQualified(options.LockUiExecutablePath))
        {
            errors.Add("LockUiExecutablePath must be a fully qualified path.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
