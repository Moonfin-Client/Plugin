using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Closes socket relays whose device stopped polling, everything when the relay is
/// turned off, and everything on server shutdown so no upstream sockets linger.
/// </summary>
public sealed class SocketRelaySweepService : IHostedService, IDisposable
{
    private readonly SocketRelayManager _manager;
    private readonly ILogger<SocketRelaySweepService> _logger;
    private readonly CancellationTokenSource _cts = new();

    public SocketRelaySweepService(SocketRelayManager manager, ILogger<SocketRelaySweepService> logger)
    {
        _manager = manager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
                {
                    try
                    {
                        if (MoonfinPlugin.Instance?.Configuration?.EnableSocketRelay == true)
                        {
                            _manager.Sweep();
                        }
                        else
                        {
                            _manager.CloseAll();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Moonfin SocketRelay sweep failed");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        _manager.CloseAll();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts.Dispose();
    }
}
