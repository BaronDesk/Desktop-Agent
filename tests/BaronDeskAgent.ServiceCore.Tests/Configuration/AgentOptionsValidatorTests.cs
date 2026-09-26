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
    public void A_plain_text_enrollment_token_is_development_only()
    {
        Assert.True(Validate(Environments.Production, new AgentOptions { PinnedCertificateHash = Pin, EnrollmentToken = "enroll-1" }).Failed);
        Assert.True(Validate(Environments.Development, new AgentOptions { EnrollmentToken = "enroll-1" }).Succeeded);
    }

    [Fact]
    public void Production_rejects_plain_http_enrollment()
    {
        Assert.True(Validate(Environments.Production, new AgentOptions { PinnedCertificateHash = Pin, EnrollmentUrl = "http://10.0.0.1/enrollment/request" }).Failed);
        Assert.True(Validate(Environments.Production, new AgentOptions { PinnedCertificateHash = Pin, EnrollmentUrl = "https://10.0.0.1/enrollment/request" }).Succeeded);
    }

    [Theory]
    [InlineData("wss://192.168.1.100:8443/agent-ws", "https://192.168.1.100:8443/stations/me/games")]
    [InlineData("wss://server.local/agent-ws", "https://server.local/stations/me/games")]
    [InlineData("ws://127.0.0.1:8443/agent-ws", "http://127.0.0.1:8443/stations/me/games")]
    public void The_game_catalog_endpoint_defaults_to_the_server_host(string serverUrl, string expected)
    {
        Assert.Equal(new Uri(expected), new AgentOptions { ServerUrl = serverUrl }.ResolveGameCatalogUri());
    }

    [Fact]
    public void Production_rejects_a_plain_http_game_catalog()
    {
        Assert.True(Validate(Environments.Production, new AgentOptions { PinnedCertificateHash = Pin, GameCatalogUrl = "http://10.0.0.1/stations/me/games" }).Failed);
        Assert.True(Validate(Environments.Production, new AgentOptions { PinnedCertificateHash = Pin, GameCatalogUrl = "https://10.0.0.1/stations/me/games" }).Succeeded);
    }

    [Fact]
    public void Allowed_game_directories_must_be_fully_qualified()
    {
        Assert.True(Validate(Environments.Production, new AgentOptions { PinnedCertificateHash = Pin, AllowedGameDirectories = [@"D:\Games", @"\\nas\games"] }).Succeeded);
        Assert.True(Validate(Environments.Production, new AgentOptions { PinnedCertificateHash = Pin, AllowedGameDirectories = ["Games"] }).Failed);
        Assert.True(Validate(Environments.Production, new AgentOptions { PinnedCertificateHash = Pin, AllowedGameDirectories = [""] }).Failed);
    }

    [Theory]
    [InlineData("wss://192.168.1.100:8443/agent-ws", "https://192.168.1.100:8443/enrollment/request")]
    [InlineData("wss://server.local/agent-ws", "https://server.local/enrollment/request")]
    [InlineData("ws://127.0.0.1:8443/agent-ws", "http://127.0.0.1:8443/enrollment/request")]
    public void The_enrollment_endpoint_defaults_to_the_server_host(string serverUrl, string expected)
    {
        Assert.Equal(new Uri(expected), new AgentOptions { ServerUrl = serverUrl }.ResolveEnrollmentUri());
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
