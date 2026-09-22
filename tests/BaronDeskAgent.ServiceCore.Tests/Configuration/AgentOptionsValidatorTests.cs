using BaronDeskAgent.ServiceCore.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace BaronDeskAgent.ServiceCore.Tests.Configuration;

public sealed class AgentOptionsValidatorTests
{
    private const string Pin = "AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89";

    [Fact]
    public void A_pinned_wss_configuration_is_valid_in_production()
    {
        Assert.True(Validate(Environments.Production, new AgentOptions { PinnedCertificateHash = Pin }).Succeeded);
    }

    [Fact]
    public void Production_requires_a_pinned_certificate()
    {
        Assert.True(Validate(Environments.Production, new AgentOptions()).Failed);
    }

    [Fact]
    public void Production_rejects_disabled_tls_validation_and_plain_ws()
    {
        Assert.True(Validate(Environments.Production, new AgentOptions { PinnedCertificateHash = Pin, AllowUntrustedCertificate = true }).Failed);
        Assert.True(Validate(Environments.Production, new AgentOptions { PinnedCertificateHash = Pin, ServerUrl = "ws://10.0.0.1/agent-ws" }).Failed);
    }

    [Fact]
    public void A_plain_text_station_token_is_development_only()
    {
        Assert.True(Validate(Environments.Production, new AgentOptions { PinnedCertificateHash = Pin, StationToken = "secret" }).Failed);
        Assert.True(Validate(Environments.Development, new AgentOptions { StationToken = "secret" }).Succeeded);
    }

    [Fact]
    public void A_malformed_pin_is_rejected_at_startup()
    {
        Assert.True(Validate(Environments.Development, new AgentOptions { PinnedCertificateHash = "not-hex" }).Failed);
        Assert.True(Validate(Environments.Development, new AgentOptions { PinnedCertificateHash = "ABCD" }).Failed);
    }

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(string environment, AgentOptions options) =>
        new AgentOptionsValidator(new HostingEnvironment { EnvironmentName = environment }).Validate(null, options);
}
