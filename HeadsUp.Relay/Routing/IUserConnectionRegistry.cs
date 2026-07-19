namespace HeadsUp.Relay.Routing;

/// <summary>
/// One connected desktop client, identified by the Slack user it was authenticated as.
/// </summary>
public interface IUserConnection
{
    /// <summary>The Slack user id this connection was authenticated as. Never client-supplied.</summary>
    string UserId { get; }

    /// <summary>Sends one serialized event payload. Failures are the registry's problem, not the router's.</summary>
    Task SendAsync(string payload, CancellationToken ct);
}

/// <summary>
/// Tracks connected desktops so events can be delivered to a <b>specific</b> user.
///
/// There is deliberately no "send to all" / "broadcast" member on this interface, and there
/// must never be one. Every delivery path in the relay is therefore forced to name the user it
/// is delivering to, which is what structurally prevents one person's messages reaching
/// another's machine. A missing recipient is a dropped alert; a wrong recipient would be a
/// disclosure, so the type system is pointed squarely at the second.
/// </summary>
public interface IUserConnectionRegistry
{
    /// <summary>Registers a connection. A user may have several (multiple machines).</summary>
    IDisposable Add(IUserConnection connection);

    /// <summary>
    /// Every live connection for one user, or empty when they have none. Empty is normal —
    /// the user simply isn't running Heads-Up right now.
    /// </summary>
    IReadOnlyList<IUserConnection> For(string userId);
}
