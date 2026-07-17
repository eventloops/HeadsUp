namespace HeadsUp.Models;

// String-backed identifier value objects. Equality is case-insensitive (ordinal),
// centralizing the OrdinalIgnoreCase comparison that identifiers are matched with
// throughout the app so call sites stop repeating it. All immutable.

/// <summary>Stable identity of a connector, e.g. "slack".</summary>
public sealed record SourceId(string Value)
{
    public static readonly SourceId Empty = new(string.Empty);
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public bool Equals(SourceId? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public override string ToString() => Value;
}

/// <summary>A detection route/connector type, e.g. "SlackClientApi".</summary>
public sealed record ConnectorType(string Value)
{
    public bool Equals(ConnectorType? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public override string ToString() => Value;
}

/// <summary>A conversation/channel identifier within a connector.</summary>
public sealed record ChannelId(string Value)
{
    public bool Equals(ChannelId? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public override string ToString() => Value;
}

/// <summary>A user identifier within a connector.</summary>
public sealed record UserId(string Value)
{
    public bool Equals(UserId? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public override string ToString() => Value;
}

/// <summary>A watch-word/phrase, matched case-insensitively.</summary>
public sealed record Keyword(string Value)
{
    public bool Equals(Keyword? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public override string ToString() => Value;
}
