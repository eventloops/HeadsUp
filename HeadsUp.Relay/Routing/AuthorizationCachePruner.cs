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
    private readonly EventEnricher _enricher;
    private readonly ILogger<AuthorizationCachePruner> _logger;

    public AuthorizationCachePruner(
        CachingEventAuthorizationResolver cache,
        EventEnricher enricher,
        ILogger<AuthorizationCachePruner> logger)
    {
        _cache = cache;
        _enricher = enricher;
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

            // Both caches: the name cache would otherwise retain every id the relay has ever
            // seen, which on a long-running server is a slow leak rather than a cache.
            var removed = _cache.Prune() + _enricher.Prune();
            if (removed > 0)
                _logger.LogDebug("Pruned {Count} expired cache entries.", removed);
        }
    }
}
