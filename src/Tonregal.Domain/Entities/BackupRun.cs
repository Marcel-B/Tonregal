// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Domain.Entities;

public class BackupRun
{
    public int Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int FilesSynced { get; set; }
    public long BytesSynced { get; set; }
    public BackupStatus Status { get; set; } = BackupStatus.Running;
    public string? Log { get; set; }
}
