// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Application;

/// <summary>Pfade und Grundeinstellungen; Abschnitt <c>Tonregal</c> in appsettings.json.</summary>
public sealed class TonregalOptions
{
    public const string SectionName = "Tonregal";

    /// <summary>Wurzel auf der externen SSD, z. B. <c>/Volumes/T7/Musik</c>.</summary>
    public string MusicRoot { get; set; } = "/Volumes/T7/Musik";

    public string InboxPath { get; set; } = "";
    public string LibraryPath { get; set; } = "";
    public string DatabasePath { get; set; } = "";
    public string LogPath { get; set; } = "";

    /// <summary>Datei, deren Existenz anzeigt, dass die SSD gemountet ist.</summary>
    public string MountMarkerFile { get; set; } = ".tonregal-mount";

    public string Inbox => Resolve(InboxPath, "Inbox");
    public string Library => Resolve(LibraryPath, "Library");
    public string Database => Resolve(DatabasePath, Path.Combine("app", "tonregal.db"));
    public string Logs => Resolve(LogPath, "logs");
    public string Trash => Path.Combine(Library, ".trash");
    public string MountMarker => Path.Combine(MusicRoot, MountMarkerFile);

    private string Resolve(string configured, string defaultRelative) =>
        string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(MusicRoot, defaultRelative)
            : Path.IsPathRooted(configured) ? configured : Path.Combine(MusicRoot, configured);
}
