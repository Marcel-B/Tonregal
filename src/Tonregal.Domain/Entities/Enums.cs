// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Domain.Entities;

public enum AlbumKind
{
    Album,
    Ep,
    Single,
    Compilation,
}

public enum AudioFormat
{
    Unknown,
    Mp3,
    Flac,
    Aac,
    Alac,
    Ogg,
    Opus,
    Wav,
}

/// <summary>Woher eine Datei stammt.</summary>
public enum FileSource
{
    Inbox,
    Purchase,
    Legacy,
    Api,
}

/// <summary>Zustand eines Datei-Durchlaufs durch die Import-Pipeline.</summary>
public enum ImportState
{
    New,
    Fingerprinted,
    Matched,
    NeedsReview,
    Imported,
    Rejected,
}

public enum DuplicateKind
{
    ExactHash,
    Fingerprint,
    Metadata,
}

public enum DuplicateDecision
{
    Open,
    Resolved,
}

public enum BackupStatus
{
    Running,
    Succeeded,
    Failed,
}
