namespace AgentOrchestrator.CodeQuality;

/// <summary>One line of a diff excerpt; <see cref="Anchor"/> marks the line a finding cites.</summary>
public sealed record ChangeReviewDiffLine(string Kind, int? BaseLine, int? HeadLine, string Text, bool Anchor);

/// <summary>Where a stored finding points: a changed line on the base or head side of the range.</summary>
public sealed record ChangeReviewAnchor(string Fingerprint, string Path, string Side, int Line);

/// <summary>
/// Read-only facts about a commit range on this host: whether its commits exist in the checkout,
/// the diff lines around each finding, and which findings a later round touched.
/// </summary>
public static class ChangeReviewRange
{
    private const int MaximumLineCharacters = 400;

    /// <summary>A full, lowercase 40-character commit SHA - the only range form the store accepts.</summary>
    public static bool IsCommitSha(string? value) => ChangeReviewVerdictService.FullSha(value);

    /// <summary>
    /// False when the checkout does not hold the commit, for example a delivery that was never fetched
    /// to this host. A verdict for that range can still be stored and shown; its diff cannot.
    /// </summary>
    public static async Task<bool> CommitPresentAsync(string root, string sha, CancellationToken cancellationToken)
    {
        if (!ChangeReviewVerdictService.FullSha(sha)) return false;
        try
        {
            await GitPlumbing.RunAsync(root, ["cat-file", "-e", sha + "^{commit}"], cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ChangeReviewException)
        {
            return false;
        }
    }

    /// <summary>The same two-dot diff the reviewer judged.</summary>
    public static Task<string> DiffAsync(string root, string baseSha, string headSha, CancellationToken cancellationToken) =>
        GitPlumbing.RunAsync(root, ["diff", "--no-ext-diff", "--no-textconv", "--unified=3", baseSha, headSha, "--"],
            cancellationToken);

    /// <summary>For each anchor found in the diff, its hunk lines within <paramref name="context"/> lines of it.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ChangeReviewDiffLine>> Excerpts(string diff,
        IEnumerable<ChangeReviewAnchor> anchors, int context = 3)
    {
        var hunks = ParseHunks(diff);
        var result = new Dictionary<string, IReadOnlyList<ChangeReviewDiffLine>>(StringComparer.Ordinal);
        foreach (var anchor in anchors)
        {
            if (result.ContainsKey(anchor.Fingerprint)) continue;
            foreach (var hunk in hunks.Where(hunk => (anchor.Side == "head" ? hunk.HeadPath : hunk.BasePath) == anchor.Path))
            {
                var index = hunk.Lines.FindIndex(line => anchor.Side == "head"
                    ? line.Kind == "added" && line.HeadLine == anchor.Line
                    : line.Kind == "removed" && line.BaseLine == anchor.Line);
                if (index < 0) continue;
                var start = Math.Max(0, index - context);
                var end = Math.Min(hunk.Lines.Count - 1, index + context);
                result[anchor.Fingerprint] = hunk.Lines.Skip(start).Take(end - start + 1)
                    .Select((line, offset) => line with { Anchor = start + offset == index })
                    .ToArray();
                break;
            }
        }
        return result;
    }

    /// <summary>
    /// The anchors a later round changed: a head-side finding whose line that round added, or a
    /// base-side finding in a file from which that round removed lines. This is the "this round
    /// only" view of a cumulative merge-base range.
    /// </summary>
    public static IReadOnlySet<string> ChangedInRound(string roundDiff, IEnumerable<ChangeReviewAnchor> anchors)
    {
        var changed = ChangeReviewVerdictService.ChangedLines.Parse(roundDiff);
        return anchors.Where(anchor => anchor.Side == "head"
                ? changed.TryGetValue((anchor.Path, "head"), out var lines) && lines.ContainsKey(anchor.Line)
                : changed.Any(entry => entry.Key.Side == "base" && entry.Value.Count > 0 &&
                    (entry.Key.Path == anchor.Path)))
            .Select(anchor => anchor.Fingerprint)
            .ToHashSet(StringComparer.Ordinal);
    }

    private sealed record Hunk(string? BasePath, string? HeadPath, List<ChangeReviewDiffLine> Lines);

    private static List<Hunk> ParseHunks(string diff)
    {
        var hunks = new List<Hunk>();
        string? basePath = null;
        string? headPath = null;
        Hunk? current = null;
        int baseLine = 0, headLine = 0;
        foreach (var raw in diff.Split('\n'))
        {
            var text = raw.TrimEnd('\r');
            if (text.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                current = null;
                basePath = headPath = null;
            }
            else if (current is null && text.StartsWith("--- ", StringComparison.Ordinal))
                basePath = ChangeReviewVerdictService.ChangedLines.DiffPath(text[4..], "a/");
            else if (current is null && text.StartsWith("+++ ", StringComparison.Ordinal))
                headPath = ChangeReviewVerdictService.ChangedLines.DiffPath(text[4..], "b/");
            else if (text.StartsWith("@@ ", StringComparison.Ordinal))
            {
                baseLine = ChangeReviewVerdictService.ChangedLines.Start(text, '-');
                headLine = ChangeReviewVerdictService.ChangedLines.Start(text, '+');
                current = new Hunk(basePath, headPath, []);
                hunks.Add(current);
            }
            else if (current is not null && text.StartsWith('+'))
                current.Lines.Add(new("added", null, headLine++, Clip(text[1..]), false));
            else if (current is not null && text.StartsWith('-'))
                current.Lines.Add(new("removed", baseLine++, null, Clip(text[1..]), false));
            else if (current is not null && text.StartsWith(' '))
                current.Lines.Add(new("context", baseLine++, headLine++, Clip(text[1..]), false));
        }
        return hunks;
    }

    private static string Clip(string text) =>
        text.Length <= MaximumLineCharacters ? text : text[..MaximumLineCharacters] + "…";
}
