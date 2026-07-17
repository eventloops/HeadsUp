using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HeadsUp.Models;

namespace HeadsUp.Services;

/// <summary>
/// Live, observable snapshot of what the app is doing right now, aggregated across every
/// connector. Each source has its own <see cref="SourceStatus"/> in <see cref="Sources"/>
/// (a single instance is shared as a singleton); this type rolls those up into the
/// convenience properties the tray, About window, and welcome hand-off still bind to as
/// scalars, so single-connector UI keeps working while the dashboard can show per-source
/// detail.
///
/// The <see cref="Sources"/> collection is seeded once at startup (on the UI thread) and
/// not mutated afterwards — sources only update their own <see cref="SourceStatus"/>
/// scalars, which WPF marshals safely from background threads.
/// </summary>
public sealed partial class AppStatus : ObservableObject
{
    /// <summary>Per-connector status, one entry per registered source.</summary>
    public ObservableCollection<SourceStatus> Sources { get; } = new();

    /// <summary>
    /// Returns the status for <paramref name="sourceId"/>, creating and adding it if absent.
    /// Prefer seeding sources at startup (see <see cref="Seed"/>) so this only reads.
    /// </summary>
    public SourceStatus GetOrAdd(SourceId sourceId, string displayName)
    {
        var existing = Sources.FirstOrDefault(s => s.SourceId == sourceId);
        if (existing is not null)
            return existing;

        var status = new SourceStatus(sourceId, displayName);
        status.PropertyChanged += OnSourceChanged;
        Sources.Add(status);
        RaiseAggregates();
        return status;
    }

    /// <summary>Pre-registers a source (at startup) with its initial mode, so its card
    /// shows immediately in a "Starting…" state before it connects.</summary>
    public SourceStatus Seed(SourceId sourceId, string displayName, string mode)
    {
        var status = GetOrAdd(sourceId, displayName);
        status.Mode = mode;
        return status;
    }

    private SourceStatus? Primary => Sources.FirstOrDefault();

    /// <summary>True when any source has a live connection.</summary>
    public bool AnyConnected => Sources.Any(s => s.IsConnected);

    /// <summary>Alias of <see cref="AnyConnected"/> for the single-dot tray/sidebar bindings.</summary>
    public bool IsConnected => AnyConnected;

    /// <summary>True while any source's embedded sign-in window is up.</summary>
    public bool NeedsSignIn => Sources.Any(s => s.NeedsSignIn);

    /// <summary>A one-line roll-up for the tray/sidebar: the sole source's message, or a count.</summary>
    public string StatusMessage
    {
        get
        {
            if (Sources.Count == 0)
                return "Starting…";
            if (Sources.Count == 1)
                return Sources[0].StatusMessage;
            var connected = Sources.Count(s => s.IsConnected);
            return $"{connected}/{Sources.Count} sources connected";
        }
    }

    // Primary-source scalars, for UI that predates multi-source (About window, etc.).
    public string? Account => Primary?.Account;
    public string? Team => Primary?.Team;
    public string Mode => Primary?.Mode ?? string.Empty;
    public string CredentialSource => Primary?.CredentialSource ?? string.Empty;

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e) => RaiseAggregates();

    private void RaiseAggregates()
    {
        OnPropertyChanged(nameof(AnyConnected));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(NeedsSignIn));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(Account));
        OnPropertyChanged(nameof(Team));
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(CredentialSource));
    }
}
