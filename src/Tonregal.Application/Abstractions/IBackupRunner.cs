// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Domain.Entities;

namespace Tonregal.Application.Abstractions;

/// <summary>Ein Backup-Lauf: Library nach Nextcloud spiegeln, Datenbank-Stände ablegen, alte Stände aufräumen.</summary>
public interface IBackupRunner
{
    /// <summary>Führt einen Lauf aus und speichert ihn als <see cref="BackupRun"/>, auch wenn er fehlschlägt.</summary>
    /// <param name="progress">Kurzer Text zum aktuellen Schritt.</param>
    Task<BackupRun> RunAsync(IProgress<string>? progress, CancellationToken ct);
}
