using Microsoft.AspNetCore.Components.Server.Circuits;
using ConX.Services;

namespace ConX.Infrastructure;

public class TerminalCircuitHandler : CircuitHandler
{
    private readonly TerminalManager _tm;
    private readonly ILogger<TerminalCircuitHandler> _logger;
    public TerminalCircuitHandler(TerminalManager tm, ILogger<TerminalCircuitHandler> logger)
    {
        _tm = tm; _logger = logger;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _logger.LogInformation($"Circuit opened: {circuit.Id}");
        // initialize session if needed
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _logger.LogInformation($"Circuit closed: {circuit.Id}");
        // mark tabs as disconnected; TerminalManager can handle wait/reconnect
        return Task.CompletedTask;
    }
}
