using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public enum SecurityScanMode
{
    Repository,
    Range,
    Staged,
}

public enum SecurityVerdict
{
    Pass,
    Warn,
    Block,
    Unavailable,
}

public sealed record SecurityScanRequest(
    string RepositoryRoot,
    SecurityScanMode Mode = SecurityScanMode.Repository,
    string? Range = null,
    string? ConfigPath = null,
    string? BaselinePath = null,
    bool PersistMetadata = true);

public sealed record SecurityScanReport(
    SecurityVerdict Verdict,
    bool Available,
    string Scanner,
    string Version,
    string Mode,
    string? Range,
    string? ConfigPath,
    string? BaselinePath,
    string? ScannedAt,
    int FilesScanned,
    int NewFindings,
    int AcceptedFindings,
    int BlockFindings,
    int WarnFindings,
    int CleanFiles,
    string? UnavailableReason,
    IReadOnlyList<SecurityFindingRecord> Findings);

public sealed record SecurityFindingRecord(
    string Id,
    string Aspect,
    FindingSeverity Severity,
    string Title,
    string Description,
    string Recommendation,
    IReadOnlyList<FindingLocation> Locations,
    string Fingerprint,
    string RuleId,
    string? Evidence,
    string Path,
    bool Accepted);

public sealed record SecurityScanProvenance(
    string Scanner,
    string Version,
    string Mode,
    string? Range,
    string? ConfigPath,
    string? BaselinePath,
    string ScannedAt);

public sealed record SecurityScanCounts(
    int FilesScanned,
    int NewFindings,
    int AcceptedFindings,
    int BlockFindings,
    int WarnFindings,
    int CleanFiles);

public sealed record SecurityScanResult(
    SecurityScanReport Report,
    SecurityScanProvenance Provenance,
    SecurityScanCounts Counts,
    IReadOnlyList<SecurityFindingRecord> Findings);

public sealed class SecurityScannerUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed record SecurityScanOptions(
    string? ConfigPath = null,
    string? BaselinePath = null,
    string? GitleaksPath = null,
    string? GitleaksVersion = null,
    string? CacheDirectory = null);

public sealed record SecurityReviewMetadataBundle(
    ReviewMetaDocument Document,
    string MetaPath);

public sealed record SecurityReviewSnapshot(
    string RelativePath,
    string ContentHash,
    IReadOnlyList<SecurityFindingRecord> Findings,
    IReadOnlyList<SecurityFindingRecord> AcceptedFindings,
    SecurityVerdict Verdict);

