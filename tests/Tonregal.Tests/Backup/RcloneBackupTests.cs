// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Backup;
using Tonregal.Infrastructure.Persistence;

namespace Tonregal.Tests.Backup;

/// <summary>Gegen ein Fake-rclone (Shell-Script), das einen lokalen Ordner als Nextcloud benutzt.</summary>
public sealed class RcloneBackupTests : IDisposable
{
    private readonly TestLibrary _library = new();
    private readonly TonregalDbContext _db;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Remote => Path.Combine(_library.MusicRoot, "remote");
    private string CallLog => Path.Combine(_library.MusicRoot, "rclone-calls.txt");
    private static string Today => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public RcloneBackupTests()
    {
        var database = Path.Combine(_library.MusicRoot, "app", "tonregal.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        _db = new TonregalDbContext(new DbContextOptionsBuilder<TonregalDbContext>().UseSqlite($"Data Source={database}").Options);
        _db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        _library.Dispose();
    }

    private string FakeRclone(string remotes = "nextcloud:", int syncExit = 0, int syncSeconds = 0, string syncLog = """{"level":"info","msg":"Transferred","stats":{"bytes":1234,"transfers":3,"deletes":1}}""")
    {
        var script = Path.Combine(_library.MusicRoot, "fake-rclone.sh");
        File.WriteAllText(script, $$"""
            #!/bin/bash
            echo "$@" >> '{{CallLog}}'
            map() { echo "${1/nextcloud:Musik/{{Remote}}}"; }
            case "$1" in
              listremotes) echo '{{remotes}}' ;;
              sync) echo '{{syncLog}}' >&2; sleep {{syncSeconds}}; exit {{syncExit}} ;;
              copy) mkdir -p "$(map "$3")" && cp -R "$2"/. "$(map "$3")" ;;
              lsf) ls -1 "$(map "$3")" | sed 's|$|/|' ;;
              purge) rm -rf "$(map "$2")" ;;
            esac
            """.Replace("\r", ""));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private RcloneBackup Backup(string rclone) => new(
        _db, new LibraryWriteLock(),
        Options.Create(new BackupOptions { RclonePath = rclone }),
        Options.Create(new TonregalOptions { MusicRoot = _library.MusicRoot }),
        TimeProvider.System, NullLogger<RcloneBackup>.Instance);

    [Fact]
    public async Task Spiegelt_die_Library_und_legt_den_Datenbank_Stand_ab()
    {
        Directory.CreateDirectory(Path.Combine(Remote, "_state", "2000-01-01"));
        var yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(Path.Combine(Remote, "_state", yesterday));

        var run = await Backup(FakeRclone()).RunAsync(null, Ct);

        Assert.Equal(BackupStatus.Succeeded, run.Status);
        Assert.Equal((3, 1234L), (run.FilesSynced, run.BytesSynced));
        Assert.NotNull(run.FinishedAt);
        Assert.True(File.Exists(Path.Combine(Remote, "_state", Today, "tonregal.db")));
        Assert.False(Directory.Exists(Path.Combine(Remote, "_state", "2000-01-01")));
        Assert.True(Directory.Exists(Path.Combine(Remote, "_state", yesterday)));
        Assert.Contains("navidrome.db nicht gefunden", run.Log);
        Assert.False(Directory.Exists(Path.Combine(_library.MusicRoot, ".backup-state")));

        var sync = File.ReadAllLines(CallLog).Single(l => l.StartsWith("sync "));
        Assert.StartsWith($"sync {_library.Library} nextcloud:Musik/Library --exclude .* --exclude .*/** --max-delete 500", sync);
        Assert.Equal(1, await _db.BackupRuns.CountAsync(Ct));
    }

    [Fact]
    public async Task Ohne_eingerichteten_Remote_schlaegt_der_Lauf_fehl()
    {
        var run = await Backup(FakeRclone(remotes: "")).RunAsync(null, Ct);

        Assert.Equal(BackupStatus.Failed, run.Status);
        Assert.Contains("rclone config", run.Log);
        Assert.DoesNotContain(File.ReadAllLines(CallLog), l => l.StartsWith("sync "));
    }

    [Fact]
    public async Task Fehler_von_rclone_landen_im_Log()
    {
        var error = """{"level":"error","msg":"Failed to sync: too many deletes","object":"","time":"x"}""";

        var run = await Backup(FakeRclone(syncExit: 1, syncLog: error)).RunAsync(null, Ct);

        Assert.Equal(BackupStatus.Failed, run.Status);
        Assert.Contains("too many deletes", run.Log);
        Assert.False(Directory.Exists(Path.Combine(Remote, "_state")));
    }

    [Fact]
    public async Task Ohne_rclone_schlaegt_der_Lauf_fehl()
    {
        var run = await Backup("/gibt/es/nicht/rclone").RunAsync(null, Ct);

        Assert.Equal(BackupStatus.Failed, run.Status);
        Assert.Contains("brew install rclone", run.Log);
    }

    [Fact]
    public void Liest_die_letzte_Statistik_und_Fehler()
    {
        var summary = RcloneBackup.ParseLog("""
            {"level":"info","msg":"a.mp3: Copied (new)","object":"a.mp3"}
            {"level":"notice","msg":"stats","stats":{"bytes":10,"transfers":1,"deletes":0}}
            {"level":"error","msg":"Failed to copy: 507 Insufficient Storage","object":"b.flac"}
            {"level":"notice","msg":"stats","stats":{"bytes":2048,"transfers":2,"deletes":5}}
            """);

        Assert.Equal((2, 5, 2048L), (summary.Transfers, summary.Deletes, summary.Bytes));
        Assert.Equal(["b.flac: Failed to copy: 507 Insufficient Storage"], summary.Errors);
    }

    [Fact]
    public async Task Abbrechen_beendet_rclone_und_speichert_den_Lauf()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var progress = new List<string>();
        cancel.CancelAfter(TimeSpan.FromSeconds(2));

        var run = await Backup(FakeRclone(syncSeconds: 30)).RunAsync(new SyncProgress(progress.Add), cancel.Token);

        Assert.Equal(BackupStatus.Failed, run.Status);
        Assert.Equal("Abgebrochen", run.Log);
        Assert.Contains("Lädt hoch: 3 Dateien, 0,0 GB", progress);
        Assert.Equal(BackupStatus.Failed, (await _db.BackupRuns.AsNoTracking().SingleAsync(Ct)).Status);
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    [Fact]
    public void Fortschritt_aus_der_Statistik()
    {
        Assert.Equal("Lädt hoch: 120 von 4.800 Dateien, 1,2 von 38,5 GB", RcloneBackup.ProgressText(
            """{"level":"notice","msg":"stats","stats":{"bytes":1234000000,"totalBytes":38500000000,"transfers":120,"totalTransfers":4800}}"""));
        Assert.Null(RcloneBackup.ProgressText("""{"level":"info","msg":"a.mp3: Copied (new)"}"""));
        Assert.Null(RcloneBackup.ProgressText("kein JSON"));
    }
}
