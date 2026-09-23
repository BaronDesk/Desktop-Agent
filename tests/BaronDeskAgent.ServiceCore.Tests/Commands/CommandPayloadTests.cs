using System.Text.Json;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Commands;

namespace BaronDeskAgent.ServiceCore.Tests.Commands;

public sealed class CommandPayloadTests
{
    [Fact]
    public void Missing_payload_yields_an_empty_instance()
    {
        Assert.True(CommandPayload.TryParse(default, AgentJsonContext.Default.LockPayload, out var payload, out _));
        Assert.Null(payload.Reason);
    }

    [Fact]
    public void Non_object_payloads_are_rejected()
    {
        var payload = JsonDocument.Parse("\"restart\"").RootElement;

        Assert.False(CommandPayload.TryParse(payload, AgentJsonContext.Default.ShutdownPayload, out _, out _));
    }

    [Fact]
    public void Policy_update_rejects_unknown_fields_instead_of_ignoring_a_typo()
    {
        var payload = JsonDocument.Parse("""{ "heartbeatIntervalSecs": 10 }""").RootElement;

        Assert.False(CommandPayload.TryParse(payload, AgentJsonContext.Default.PolicyUpdatePayload, out _, out _));
    }

    [Fact]
    public void Numbers_are_never_parsed_from_culture_dependent_strings()
    {
        // "2.5" as a string failed to parse on fr-FR machines and was silently dropped before.
        var payload = JsonDocument.Parse("""{ "telemetryCadenceSeconds": "2.5" }""").RootElement;

        Assert.False(CommandPayload.TryParse(payload, AgentJsonContext.Default.PolicyUpdatePayload, out _, out _));
    }

    [Fact]
    public void Policy_update_reads_camel_case_numbers()
    {
        var payload = JsonDocument.Parse("""{ "telemetryCadenceSeconds": 2.5, "stopGameOnLock": false }""").RootElement;

        Assert.True(CommandPayload.TryParse(payload, AgentJsonContext.Default.PolicyUpdatePayload, out var update, out _));
        Assert.Equal(2.5, update.TelemetryCadenceSeconds);
        Assert.False(update.StopGameOnLock);
        Assert.True(update.HasChanges);
    }

    [Fact]
    public void Required_payloads_must_be_present()
    {
        Assert.False(CommandPayload.TryParseRequired(default, AgentJsonContext.Default.LoginResultPayload, out _, out _));
    }
}
