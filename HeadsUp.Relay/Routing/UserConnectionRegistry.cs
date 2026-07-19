using System.Collections.Concurrent;

namespace HeadsUp.Relay.Routing;

/// <summary>
/// In-memory registry of connected desktops, keyed by Slack user id.
///
/// In-memory is the right scope: connections are inherently per-process, and the relay holds no
/// durable state at all — it forwards and forgets. Restarting it drops every client, which each
/// reconnects on its own.
/// </summary>
public sealed class UserConnectionRegistry : IUserConnectionRegistry
{
    // One user may run Heads-Up on several machines, so each id maps to a set.
    private readonly ConcurrentDictionary<string, ImmutableConnectionSet> _byUser = new(StringComparer.Ordinal);

    public IDisposable Add(IUserConnection connection)
    {
        _byUser.AddOrUpdate(
            connection.UserId,
            _ => new ImmutableConnectionSet([connection]),
            (_, existing) => existing.With(connection));

        return new Registration(this, connection);
    }

    public IReadOnlyList<IUserConnection> For(string userId) =>
        _byUser.TryGetValue(userId, out var set) ? set.Connections : [];

    private void Remove(IUserConnection connection)
    {
        while (_byUser.TryGetValue(connection.UserId, out var existing))
        {
            var reduced = existing.Without(connection);

            if (reduced.Connections.Count == 0)
            {
                // Remove the key only if it still holds exactly what we just read, so a
                // reconnect racing a disconnect can't have its new connection dropped.
                if (((ICollection<KeyValuePair<string, ImmutableConnectionSet>>)_byUser)
                    .Remove(new KeyValuePair<string, ImmutableConnectionSet>(connection.UserId, existing)))
                    return;
            }
            else if (_byUser.TryUpdate(connection.UserId, reduced, existing))
            {
                return;
            }
        }
    }

    /// <summary>Copy-on-write set, so <see cref="For"/> never hands out a mutating collection.</summary>
    private sealed class ImmutableConnectionSet(IReadOnlyList<IUserConnection> connections)
    {
        public IReadOnlyList<IUserConnection> Connections { get; } = connections;

        public ImmutableConnectionSet With(IUserConnection connection) =>
            new([.. Connections, connection]);

        public ImmutableConnectionSet Without(IUserConnection connection) =>
            new([.. Connections.Where(c => !ReferenceEquals(c, connection))]);
    }

    private sealed class Registration(UserConnectionRegistry registry, IUserConnection connection) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                registry.Remove(connection);
        }
    }
}
