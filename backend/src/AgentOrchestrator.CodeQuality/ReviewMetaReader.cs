using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

public enum ReviewMetaReadFailure
{
    /// <summary>No file exists at the path.</summary>
    Missing,

    /// <summary>The file exists but could not be opened or decoded.</summary>
    Unreadable,

    /// <summary>The content is not the review metadata contract.</summary>
    Malformed,

    /// <summary>The document names a schema version this build does not accept.</summary>
    UnsupportedVersion,

    /// <summary>The document parses but omits a field the contract requires.</summary>
    IncompleteContract,
}

/// <summary>Why one review-meta sidecar could not be trusted, and which one it was.</summary>
public sealed record ReviewMetaReadError(string Source, ReviewMetaReadFailure Failure, string Reason)
{
    public override string ToString() => $"{Source}: {Reason}";
}

public sealed class ReviewMetaReadException(ReviewMetaReadError error)
    : Exception(error.ToString())
{
    public ReviewMetaReadError Error { get; } = error;
}

/// <summary>A review-meta sidecar as both the typed contract and the exact text it was read from.</summary>
public sealed record ReviewMetaSidecar(string Source, ReviewMetaDocument Document, string Json);

/// <summary>
/// The single entry point for reading a review-meta sidecar. Every consumer — staleness scanning,
/// the finding lifecycle, thread healing, hierarchy discovery, reporting, change reviews, and the
/// secret scanner — loads through here, so a sidecar that cannot be trusted is reported once, with
/// its path and cause, instead of being reinterpreted as "no findings", "no threads", or "stale" by
/// each reader's own property access.
/// </summary>
public static class ReviewMetaReader
{
    public static bool TryLoad(
        string path,
        [NotNullWhen(true)] out ReviewMetaSidecar? sidecar,
        [NotNullWhen(false)] out ReviewMetaReadError? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        sidecar = null;
        string json;
        try
        {
            if (!File.Exists(path))
            {
                error = new ReviewMetaReadError(path, ReviewMetaReadFailure.Missing, "No review metadata exists here.");
                return false;
            }

            json = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = Report(new ReviewMetaReadError(path, ReviewMetaReadFailure.Unreadable, exception.Message));
            return false;
        }

        return TryParse(json, path, out sidecar, out error);
    }

    public static bool TryParse(
        string json,
        string source,
        [NotNullWhen(true)] out ReviewMetaSidecar? sidecar,
        [NotNullWhen(false)] out ReviewMetaReadError? error)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        sidecar = null;
        ReviewMetaDocument document;
        try
        {
            document = ReviewMetaJson.Deserialize(json);
        }
        catch (JsonException exception)
        {
            error = Report(new ReviewMetaReadError(
                source,
                exception.Message.Contains("schemaVersion", StringComparison.Ordinal)
                    ? ReviewMetaReadFailure.UnsupportedVersion
                    : ReviewMetaReadFailure.Malformed,
                exception.Message));
            return false;
        }
        catch (NotSupportedException exception)
        {
            error = Report(new ReviewMetaReadError(source, ReviewMetaReadFailure.Malformed, exception.Message));
            return false;
        }

        // Required members guarantee the property is present, not that its value carries the
        // fields the schema demands of it; nested records fall back to null instead of failing.
        var missing = FirstMissingField(document);
        if (missing is not null)
        {
            error = Report(new ReviewMetaReadError(
                source, ReviewMetaReadFailure.IncompleteContract, $"Review metadata is missing '{missing}'."));
            return false;
        }

        sidecar = new ReviewMetaSidecar(source, document, json);
        error = null;
        return true;
    }

    /// <summary>Loads a sidecar that the caller requires; the read error becomes the exception.</summary>
    public static ReviewMetaSidecar Load(string path) =>
        TryLoad(path, out var sidecar, out var error) ? sidecar : throw new ReviewMetaReadException(error);

    // A sidecar nobody can read is a fault, not an absence, so it leaves a trace even where the
    // caller chooses to carry on without it.
    private static ReviewMetaReadError Report(ReviewMetaReadError error)
    {
        QualityStudioEventSource.Log.ReviewMetaUnreadable(error.Source, error.Failure.ToString(), error.Reason);
        return error;
    }

    private static string? FirstMissingField(ReviewMetaDocument document)
    {
        if (document.Unit is null) return "unit";
        if (string.IsNullOrWhiteSpace(document.Unit.Id)) return "unit.id";
        if (string.IsNullOrWhiteSpace(document.Unit.Path)) return "unit.path";
        if (string.IsNullOrWhiteSpace(document.Unit.DisplayName)) return "unit.displayName";
        if (document.Reviewer is null) return "reviewer";
        if (string.IsNullOrWhiteSpace(document.Reviewer.Agent)) return "reviewer.agent";
        if (string.IsNullOrWhiteSpace(document.Reviewer.Model)) return "reviewer.model";
        if (document.ReviewedHash is null) return "reviewedHash";
        if (string.IsNullOrWhiteSpace(document.ReviewedHash.Value)) return "reviewedHash.value";
        if (document.SubjectInputs is null or { Count: 0 }) return "subjectInputs";
        if (document.SubjectInputs.Any(input =>
                string.IsNullOrWhiteSpace(input.Path) || string.IsNullOrWhiteSpace(input.Selector) ||
                string.IsNullOrWhiteSpace(input.ContentHash)))
            return "subjectInputs[].contentHash";
        if (document.ReviewInputs is null) return "reviewInputs";
        if (document.ReviewInputs.EffectiveHash is null ||
            string.IsNullOrWhiteSpace(document.ReviewInputs.EffectiveHash.Value))
            return "reviewInputs.effectiveHash.value";
        if (document.ReviewInputs.Prompt is null) return "reviewInputs.prompt";
        if (document.Grade is null) return "grade";
        if (document.Summary is null) return "summary";
        if (document.Aspects is null) return "aspects";
        if (document.Findings is null) return "findings";
        return document.Findings.Any(finding =>
            string.IsNullOrWhiteSpace(finding.Id) || string.IsNullOrWhiteSpace(finding.Fingerprint) ||
            string.IsNullOrWhiteSpace(finding.RuleId) || finding.Locations is null)
            ? "findings[].fingerprint"
            : null;
    }
}
