using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeadsUp.Relay.Routing;

/// <summary>
/// Drops expired authorization cache entries periodically.
///
/// Expiry is checked on read, so stale entries are never *used* — but a channel that goes quiet
/// keeps its entry forever without this, and a long-lived relay in a workspace with many
/// channels would accumulate them indefinitely.
/// </summary>
public sealed class AuthorizationCachePruner : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly CachingEventAuthorizationResolver _cache;
    private readonly ILogger<AuthorizationCachePruner> _logger;

    public AuthorizationCachePruner(
        CachingEventAuthorizationResolver cache, ILogger<AuthorizationCachePruner> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var removed = _cache.Prune();
            if (removed > 0)
                _logger.LogDebug("Pruned {Count} expired authorization cache entries.", removed);
        }
    }
}
