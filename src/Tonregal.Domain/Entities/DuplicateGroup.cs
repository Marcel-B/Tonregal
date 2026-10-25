// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Domain.Entities;

/// <summary>Ein Dublettenfund samt Entscheidung, welche Datei bleibt.</summary>
public class DuplicateGroup
{
    public int Id { get; set; }
    public DuplicateKind Kind { get; set; }
    public List<AudioFile> Files { get; set; } = [];
    public int? KeeperFileId { get; set; }
    public AudioFile? KeeperFile { get; set; }
    public DuplicateDecision Decision { get; set; } = DuplicateDecision.Open;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}
