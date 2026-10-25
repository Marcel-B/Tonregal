// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Tests;

/// <summary>
/// Temporärer MusicRoot mit Mount-Marker und einer kleinen Library aus den Testdateien:
/// ein Album "Testband – Testalbum" (MP3 mit eingebettetem Cover, FLAC, M4A, cover.jpg im Ordner)
/// und eine Compilation "Sampler" (Opus).
/// </summary>
public sealed class TestLibrary : IDisposable
{
    public static string TestData => Path.Combine(AppContext.BaseDirectory, "TestData");

    public string MusicRoot { get; } = Directory.CreateTempSubdirectory("tonregal-").FullName;
    public string Library => Path.Combine(MusicRoot, "Library");
    public string AlbumFolder => Path.Combine(Library, "Testband", "2024 - Testalbum");

    public TestLibrary(bool populate = true, bool mounted = true)
    {
        if (mounted)
            File.WriteAllText(Path.Combine(MusicRoot, ".tonregal-mount"), "");
        if (!populate)
            return;

        Copy("song1.mp3", AlbumFolder, "01 - Erster Song.mp3");
        Copy("song2.flac", AlbumFolder, "02 - Zweiter Song.flac");
        Copy("song3.m4a", AlbumFolder, "03 - Dritter Song.m4a");
        Copy("cover.jpg", AlbumFolder, "cover.jpg");
        Copy("song4.opus", Path.Combine(Library, "Various Artists", "2020 - Sampler"), "01 - Sampler-Stück.opus");
        // Wird ignoriert: Papierkorb und macOS-Ressourcendateien.
        Copy("song1.mp3", Path.Combine(Library, ".trash"), "alt.mp3");
        Copy("song1.mp3", AlbumFolder, "._01 - Erster Song.mp3");
    }

    public string Copy(string testFile, string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, name);
        File.Copy(Path.Combine(TestData, testFile), target, overwrite: true);
        return target;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(MusicRoot, recursive: true);
    }
}
