// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using Microsoft.Net.Http.Headers;

namespace Tonregal.Web.Api;

/// <summary>
/// Schreibt ein Album als ZIP direkt in die Antwort, ohne Zwischendatei: die interne SSD von oi-o-mat ist klein,
/// und ein Album in FLAC hat schnell ein Gigabyte. Audio ist schon komprimiert, deshalb wird nur gespeichert.
/// Im ZIP liegt ein Ordner mit dem Namen des Albums, darin die Dateien mit ihren Pfaden ab dem gemeinsamen Ordner
/// (bei Alben mit mehreren Discs also z. B. „CD1/…“).
/// </summary>
public sealed class AlbumZipResult(string name, IReadOnlyList<string> files) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        context.Response.ContentType = "application/zip";
        context.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileName = Ascii(name) + ".zip",
            FileNameStar = name + ".zip",
        }.ToString();

        var ct = context.RequestAborted;
        try
        {
            await using var body = new SyncTailStream(context.Response.Body);
            await using var zip = await ZipArchive.CreateAsync(body, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, ct);
            foreach (var (file, entryName) in EntryNames(name, files))
            {
                var entry = zip.CreateEntry(entryName, CompressionLevel.NoCompression);
                entry.LastWriteTime = File.GetLastWriteTime(file);
                await using var target = await entry.OpenAsync(ct);
                await using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await source.CopyToAsync(target, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Download im Browser abgebrochen.
        }
    }

    public static IEnumerable<(string File, string Entry)> EntryNames(string folder, IReadOnlyList<string> files)
    {
        var common = CommonDirectory(files);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(common, file).Replace(Path.DirectorySeparatorChar, '/');
            var entry = $"{folder}/{relative}";
            for (var i = 2; !used.Add(entry); i++)
                entry = $"{folder}/{Path.ChangeExtension(relative, null)} ({i}){Path.GetExtension(relative)}";
            yield return (file, entry);
        }
    }

    private static string CommonDirectory(IReadOnlyList<string> files)
    {
        var common = Path.GetDirectoryName(files[0])!;
        while (!files.All(f => f.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            common = Path.GetDirectoryName(common)!;
        return common;
    }

    /// <summary>
    /// Kestrel verbietet synchrone Schreibzugriffe, aber .NET 10 schreibt den Data Descriptor eines Eintrags auch beim
    /// asynchronen Schließen synchron. Diese paar Bytes werden gesammelt und mit dem nächsten asynchronen Schreiben verschickt.
    /// </summary>
    private sealed class SyncTailStream(Stream inner) : Stream
    {
        private readonly MemoryStream _pending = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => _pending.Write(buffer, offset, count);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await SendPendingAsync(cancellationToken);
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await SendPendingAsync(cancellationToken);
            await inner.FlushAsync(cancellationToken);
        }

        public override void Flush()
        {
        }

        public override async ValueTask DisposeAsync()
        {
            await SendPendingAsync(CancellationToken.None);
            await base.DisposeAsync();
        }

        private async Task SendPendingAsync(CancellationToken ct)
        {
            if (_pending.Length == 0)
                return;
            await inner.WriteAsync(_pending.GetBuffer().AsMemory(0, (int)_pending.Length), ct);
            _pending.SetLength(0);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static string Ascii(string text) => new(text.Select(c => c is >= ' ' and <= '~' && c != '"' ? c : '_').ToArray());
}
