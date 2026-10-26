// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Application;

/// <summary>
/// Gemeinsames Lock für alles, was Dateien in der Library schreibt (Import, Analyse) oder sie liest,
/// während andere schreiben dürften (Backup). So landen nie halbe Dateien in Nextcloud.
/// </summary>
public sealed class LibraryWriteLock
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>Wer das Lock gerade hält, z. B. „Backup“; <c>null</c>, wenn es frei ist.</summary>
    public string? Holder { get; private set; }

    public async Task<IDisposable> AcquireAsync(string holder, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        Holder = holder;
        return new Release(this);
    }

    private sealed class Release(LibraryWriteLock owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 1)
                return;
            owner.Holder = null;
            owner._semaphore.Release();
        }
    }
}
