using Microsoft.Extensions.Hosting;

namespace ConX.Services;

public class TerminalTimeoutService : BackgroundService
{
    private readonly TerminalManager _tm;
    private readonly ILogger<TerminalTimeoutService> _logger;
    public TerminalTimeoutService(TerminalManager tm, ILogger<TerminalTimeoutService> logger)
    {
        _tm = tm; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TerminalTimeoutService started");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                // Placeholder: actual cleanup logic should iterate tabs and remove timed-out ones
                _logger.LogInformation("TerminalTimeoutService tick");
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TerminalTimeoutService encountered an error");
            throw;
        }
        finally
        {
            _logger.LogInformation("TerminalTimeoutService stopping");
        }
    }
}
