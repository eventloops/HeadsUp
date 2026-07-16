namespace SlackHuddleNotifier.Configuration;

public sealed class NotificationOptions
{
    public const string SectionName = "Notification";

    public bool PlaySound { get; set; } = true;

    /// <summary>Full path to a .wav file. Leave empty to use the system default.</summary>
    public string SoundFilePath { get; set; } = string.Empty;

    public int ToastDurationSeconds { get; set; } = 15;
}
