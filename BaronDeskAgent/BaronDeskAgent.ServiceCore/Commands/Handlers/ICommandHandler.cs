using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

public interface ICommandHandler
{
    string CommandType { get; }

    Task<CommandResponse> HandleAsync(
        CommandRequest command,
        CancellationToken cancellationToken = default);
}