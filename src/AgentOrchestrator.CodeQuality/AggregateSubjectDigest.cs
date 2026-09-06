using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// One derived unit below an aggregate, as the caller already knows it from the hierarchy.
/// The runner never re-derives the hierarchy for a review; the planner passes what it built.
/// </summary>
public sealed record ReviewSubjectGroup(
    ReviewLevel Level,
    string Name,
    string Path,
    IReadOnlyList<string> Members);

public sealed record AggregateDigestRequest(
    string RepositoryRoot,
    string AnchorPath,
    string DisplayName,
    ReviewLevel Level,
    string Kind,
    IReadOnlyList<string> MemberPaths,
    IReadOnlyList<ScopeExclusion> Exclusions,
    IReadOnlyList<ReviewSubjectGroup> Groups,
    int BudgetCharacters = AggregateSubjectDigest.DefaultBudgetCharacters);

/// <summary>
/// The rendered digest plus the member-file finding fingerprints it printed. A module or project
/// review may cite those fingerprints; the runner verifies a citation against this map instead of
/// trusting the agent.
/// </summary>
public sealed record AggregateDigest(
    string Text,
    IReadOnlyDictionary<string, string> MemberFindings);

/// <summary>
/// Builds the subject a module or project review sees. Concatenating every member source answers
/// the file question N times over and does not fit; the digest answers the aggregate question
/// instead: who the members are, what their own reviews already said, how the hierarchy groups
/// them, where the repository's boundaries are, and a size-proportional sample of real source.
/// Source lines keep their original one-based numbers so a cited range is a range in the file.
/// </summary>
public static partial class AggregateSubjectDigest
{
    /// <summary>Character ceiling for the whole digest. Roughly 20k tokens at four characters each.</summary>
    public const int DefaultBudgetCharacters = 80_000;

    private const int StructurePercent = 12;
    private const int MemberFindingPercent = 22;
    private const int BoundaryPercent = 18;
    private const int MinimumOutlinePercent = 25;
    private const int MinimumOutlinePerMember = 320;
    private const int MinimumHeaderCharacters = 160;
    private const int MaximumSourceLineLength = 400;
    private const int MaximumMemberRows = 400;

    private static readonly JsonSerializerOptions BoundaryJsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<AggregateDigest> BuildAsync(
        AggregateDigestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var members = new List<MemberSource>(request.MemberPaths.Count);
        foreach (var path in request.MemberPaths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var absolute = Path.Combine(request.RepositoryRoot, path.Replace('/', Path.DirectorySeparatorChar));
            var text = NormalizeLineEndings(await File.ReadAllTextAsync(absolute, cancellationToken).ConfigureAwait(false));
            members.Add(new MemberSource(path, text, text.Split('\n').Length));
        }

        var reviews = LoadMemberReviews(request.RepositoryRoot, request.Kind, members.Select(member => member.Path));
        var budget = Math.Max(4_000, request.BudgetCharacters);
        var builder = new StringBuilder(budget);
        AppendHeader(builder, request, members, reviews);
        AppendMembers(builder, request, members, reviews);
        AppendExclusions(builder, request);
        Append(builder, Structure(request), Share(budget, StructurePercent));
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        Append(builder, MemberFindings(reviews, fingerprints), Share(budget, MemberFindingPercent));
        if (string.Equals(request.Kind, "security", StringComparison.Ordinal))
        {
            Append(builder, BoundaryInventorySection(request, members), Share(budget, BoundaryPercent));
        }

        var remaining = Math.Max(Share(budget, MinimumOutlinePercent), budget - builder.Length);
        AppendSource(builder, members, remaining);
        // AppendLine writes the host's line separator; the digest must read the same on every host.
        return new AggregateDigest(NormalizeLineEndings(builder.ToString()), fingerprints);
    }

    private static int Share(int budget, int percent) => Math.Max(500, budget * percent / 100);

    private static void AppendHeader(
        StringBuilder builder,
        AggregateDigestRequest request,
        IReadOnlyList<MemberSource> members,
        IReadOnlyDictionary<string, ReviewMetaDocument> reviews)
    {
        var level = request.Level.ToString().ToLowerInvariant();
        builder.Append("# ").Append(level).Append(" review subject digest: ").AppendLine(request.DisplayName);
        builder.AppendLine();
        builder.Append("Anchor path: ").AppendLine(request.AnchorPath);
        builder.Append("Review kind: ").AppendLine(request.Kind);
        builder.Append("Members: ").Append(Number(members.Count)).Append(" file(s), ")
            .Append(Number(members.Sum(member => member.LineCount))).Append(" line(s), ")
            .Append(Number(members.Sum(member => member.Text.Length))).AppendLine(" character(s)");
        builder.Append("Members with a current ").Append(request.Kind).Append(" review: ")
            .Append(Number(reviews.Count)).Append(" of ").AppendLine(Number(members.Count));
        builder.AppendLine();
        builder.AppendLine(
            "This digest replaces the member sources. It is derived, not authored: sizes and outlines come from the working tree, grades and findings from the member sidecars.");
    }

    private static void AppendMembers(
        StringBuilder builder,
        AggregateDigestRequest request,
        IReadOnlyList<MemberSource> members,
        IReadOnlyDictionary<string, ReviewMetaDocument> reviews)
    {
        // A file can be aliased under several derived units. The table names the first owner in
        // the same order the structure section prints, and the structure section shows the rest.
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in OrderedGroups(request))
        {
            foreach (var member in group.Members) owners.TryAdd(member, group.Path);
        }

        builder.AppendLine();
        builder.AppendLine("## Members and their file-level review state");
        builder.AppendLine();
        builder.AppendLine("| path | lines | characters | owning unit | " + request.Kind + " review | findings |");
        builder.AppendLine("| --- | ---: | ---: | --- | --- | --- |");
        foreach (var member in members.Take(MaximumMemberRows))
        {
            reviews.TryGetValue(member.Path, out var review);
            builder.Append("| ").Append(member.Path)
                .Append(" | ").Append(Number(member.LineCount))
                .Append(" | ").Append(Number(member.Text.Length))
                .Append(" | ").Append(owners.GetValueOrDefault(member.Path, request.AnchorPath))
                .Append(" | ").Append(review is null
                    ? "not reviewed"
                    : $"{review.Grade.Band}/{review.Grade.Score.ToString(CultureInfo.InvariantCulture)}")
                .Append(" | ").Append(review is null ? "-" : SeveritySummary(review.Findings))
                .AppendLine(" |");
        }

        if (members.Count > MaximumMemberRows)
        {
            builder.Append("| (").Append(Number(members.Count - MaximumMemberRows))
                .AppendLine(" further members omitted from this table) | | | | | |");
        }
    }

    private static string SeveritySummary(IReadOnlyList<ReviewFinding> findings)
    {
        if (findings.Count == 0) return "none";
        var counts = findings.GroupBy(finding => finding.Severity)
            .OrderBy(group => group.Key)
            .Select(group => $"{Number(group.Count())} {group.Key.ToString().ToLowerInvariant()}");
        return string.Join(", ", counts);
    }

    private static void AppendExclusions(StringBuilder builder, AggregateDigestRequest request)
    {
        if (request.Exclusions.Count == 0) return;
        builder.AppendLine();
        builder.AppendLine("## Paths excluded from this aggregate");
        builder.AppendLine();
        foreach (var exclusion in request.Exclusions
                     .DistinctBy(exclusion => (exclusion.Path, exclusion.Reason))
                     .OrderBy(exclusion => exclusion.Path, StringComparer.Ordinal)
                     .ThenBy(exclusion => exclusion.Reason, StringComparer.Ordinal))
        {
            builder.Append("- ").Append(exclusion.Path).Append(" - ").AppendLine(exclusion.Reason);
        }
    }

    private static string Structure(AggregateDigestRequest request)
    {
        if (request.Groups.Count == 0) return string.Empty;
        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine("## Derived structure");
        builder.AppendLine();
        foreach (var group in OrderedGroups(request))
        {
            builder.Append("- ").Append(group.Level.ToString().ToLowerInvariant()).Append(' ').Append(group.Name)
                .Append(" (").Append(group.Path).Append(") - ")
                .Append(Number(group.Members.Count)).AppendLine(" direct file(s)");
            foreach (var member in group.Members.Order(StringComparer.Ordinal))
            {
                builder.Append("  - ").AppendLine(member);
            }
        }

        return builder.ToString();
    }

    private static string MemberFindings(
        IReadOnlyDictionary<string, ReviewMetaDocument> reviews,
        Dictionary<string, string> fingerprints)
    {
        var rows = reviews.Values
            .SelectMany(review => review.Findings.Select(finding => (Review: review, Finding: finding)))
            .OrderBy(row => row.Finding.Severity)
            .ThenBy(row => row.Review.Unit.Path, StringComparer.Ordinal)
            .ThenBy(row => row.Finding.Fingerprint, StringComparer.Ordinal)
            .ToArray();
        foreach (var row in rows) fingerprints[row.Finding.Fingerprint] = row.Review.Unit.Path;
        if (rows.Length == 0) return string.Empty;

        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine("## Findings already recorded for these members");
        builder.AppendLine();
        builder.AppendLine(
            "Cite a fingerprint in `relatedFindings` when an aggregate finding generalises one of these. Do not restate a member finding as your own.");
        builder.AppendLine();
        foreach (var row in rows)
        {
            var location = row.Finding.Locations.FirstOrDefault();
            var line = location?.Range?.Start.Line;
            builder.Append("- ").Append(row.Finding.Fingerprint)
                .Append(" | ").Append(location?.Path ?? row.Review.Unit.Path)
                .Append(line is null ? string.Empty : ":" + Number(line.Value))
                .Append(" | ").Append(row.Finding.Severity.ToString().ToLowerInvariant())
                .Append(" | ").Append(row.Finding.RuleId)
                .Append(" | ").AppendLine(SingleLine(row.Finding.Title));
        }

        return builder.ToString();
    }

    private static string BoundaryInventorySection(
        AggregateDigestRequest request,
        IReadOnlyList<MemberSource> members)
    {
        var path = QualityDataRoot.PathFor(request.RepositoryRoot, BoundaryInventorySensor.InventoryRelativePath);
        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine("## Derived boundary inventory");
        builder.AppendLine();
        if (!File.Exists(path))
        {
            builder.AppendLine(
                "No boundary inventory has been scanned for this repository. Treat the entry-point list as missing evidence, not as an absence of entry points.");
            return builder.ToString();
        }

        BoundaryInventory? inventory;
        try
        {
            inventory = JsonSerializer.Deserialize<BoundaryInventory>(File.ReadAllText(path), BoundaryJsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            builder.Append("The stored boundary inventory could not be read (").Append(exception.GetType().Name)
                .AppendLine("). Treat the entry-point list as missing evidence.");
            return builder.ToString();
        }

        if (inventory is null)
        {
            builder.AppendLine("The stored boundary inventory is empty. Treat the entry-point list as missing evidence.");
            return builder.ToString();
        }

        var memberPaths = members.Select(member => member.Path).ToHashSet(StringComparer.Ordinal);
        var entries = (request.Level == ReviewLevel.Project
                ? inventory.Entries
                : inventory.Entries.Where(entry => memberPaths.Contains(entry.Location.Path)).ToArray())
            .OrderBy(entry => entry.Location.Path, StringComparer.Ordinal)
            .ThenBy(entry => entry.Location.Line)
            .ToArray();
        builder.Append("Sensor ").Append(inventory.Sensor).Append(' ').Append(inventory.SensorVersion)
            .Append(" derived ").Append(Number(entries.Length)).Append(" entry point(s) in scope and ")
            .Append(Number(inventory.Findings.Count)).AppendLine(" mechanical finding(s) for the repository.");
        builder.AppendLine("A fact of `unknown` means the analyzer could not prove it from source.");
        builder.AppendLine();
        foreach (var entry in entries)
        {
            builder.Append("- ").Append(entry.Location.Path).Append(':').Append(Number(entry.Location.Line))
                .Append(" | ").Append(entry.Kind).Append('/').Append(entry.Direction)
                .Append(" | ").Append(SingleLine(entry.Name))
                .Append(" | transport=").Append(entry.Transport)
                .Append(" | reachability=").Append(entry.Reachability.Value)
                .Append(" | authentication=").Append(entry.Authentication.Value)
                .Append(" | authorization=").Append(entry.Authorization.Value)
                .Append(" | rateLimit=").Append(entry.RateLimit.Value)
                .Append(" | sizeLimit=").Append(entry.SizeLimit.Value)
                .Append(" | inputs=").Append(Number(entry.Inputs.Count))
                .Append(" | sideEffects=")
                .AppendLine(entry.SideEffects.Count == 0 ? "none" : string.Join(",", entry.SideEffects));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Spends the remaining budget on real source, proportionally to member size and never below a
    /// floor, so a monolith cannot crowd out the small members that surround it. A member whose
    /// whole text fits its allocation is included in full; a larger one is reduced to its
    /// declaration lines, which is what an aggregate question needs from it.
    /// </summary>
    private static void AppendSource(StringBuilder builder, IReadOnlyList<MemberSource> members, int budget)
    {
        builder.AppendLine();
        builder.AppendLine("## Member source");
        builder.AppendLine();
        builder.AppendLine(
            "Line numbers are the real one-based numbers in each file. A member listed as an outline shows its declaration lines only; the omitted lines are bodies, not other declarations.");
        var ordered = members
            .OrderByDescending(member => member.Text.Length)
            .ThenBy(member => member.Path, StringComparer.Ordinal)
            .ToArray();
        // Every member keeps a reserve before size decides the rest, so the tail of small members
        // is not silently spent by the head of large ones. The reserve shrinks with the budget
        // rather than dropping members, and only a budget too small for a header omits source.
        var reserve = Math.Clamp(budget / Math.Max(1, ordered.Length), 0, MinimumOutlinePerMember);
        var remainingBudget = budget;
        var remainingSize = ordered.Sum(member => (long)member.Text.Length);
        var remainingMembers = ordered.Length;
        foreach (var member in ordered)
        {
            remainingMembers--;
            var available = Math.Max(reserve, remainingBudget - remainingMembers * reserve);
            var proportional = remainingSize <= 0
                ? available
                : (int)Math.Min(available, remainingBudget * (long)member.Text.Length / remainingSize);
            var allocation = Math.Clamp(Math.Max(proportional, reserve), 0, Math.Max(0, remainingBudget));
            remainingSize -= member.Text.Length;
            if (allocation < MinimumHeaderCharacters)
            {
                builder.AppendLine();
                builder.Append("### ").Append(member.Path)
                    .AppendLine(" (source omitted: the digest budget is too small to show every member)");
                continue;
            }

            var rendered = Render(member, allocation);
            remainingBudget = Math.Max(0, remainingBudget - rendered.Length);
            builder.Append(rendered);
        }
    }

    private static string Render(MemberSource member, int allocation)
    {
        var builder = new StringBuilder();
        var lines = member.Text.Split('\n');
        var full = member.Text.Length <= allocation;
        builder.AppendLine();
        builder.Append("### ").Append(member.Path).Append(" (").Append(Number(member.LineCount))
            .Append(" lines, ").Append(Number(member.Text.Length)).Append(" characters, ")
            .Append(full ? "complete" : "declaration outline").AppendLine(")");
        builder.AppendLine();
        var emitted = 0;
        var truncated = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd();
            if (!full)
            {
                if (line.Length == 0 || !IsDeclaration(line)) continue;
            }
            else if (line.Length == 0 && emitted == 0)
            {
                continue;
            }

            if (line.Length > MaximumSourceLineLength) line = line[..MaximumSourceLineLength] + " ...";
            var row = (index + 1).ToString(CultureInfo.InvariantCulture).PadLeft(6) + " | " + line;
            if (builder.Length + row.Length + 1 > allocation)
            {
                truncated = true;
                break;
            }

            builder.AppendLine(row);
            emitted++;
        }

        builder.Append("(").Append(Number(emitted)).Append(" of ").Append(Number(lines.Length))
            .Append(" lines shown").Append(truncated ? ", cut off by the digest budget" : string.Empty)
            .AppendLine(")");
        return builder.ToString();
    }

    private static bool IsDeclaration(string line)
    {
        try
        {
            return DeclarationLine().IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the file-level sidecars of the members through the typed contract. A sidecar that
    /// cannot be loaded is left out of the digest rather than reported as an absent review of
    /// unknown quality, and it never fails the aggregate review.
    /// </summary>
    private static IReadOnlyDictionary<string, ReviewMetaDocument> LoadMemberReviews(
        string root, string kind, IEnumerable<string> memberPaths)
    {
        var members = memberPaths.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, ReviewMetaDocument>(StringComparer.Ordinal);
        var lane = Path.Combine(QualityDataRoot.Resolve(root), "reviews", "files");
        if (!Directory.Exists(lane)) return result;
        foreach (var sidecar in Directory.EnumerateFiles(lane, "*.review-meta." + kind + ".json"))
        {
            ReviewMetaDocument document;
            try
            {
                document = ReviewMetaJson.Deserialize(File.ReadAllText(sidecar));
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (document.Unit.Level == ReviewLevel.File && members.Contains(document.Unit.Path))
            {
                result[document.Unit.Path] = document;
            }
        }

        return result;
    }

    private static void Append(StringBuilder builder, string section, int limit)
    {
        if (section.Length == 0) return;
        if (section.Length <= limit)
        {
            builder.Append(section);
            return;
        }

        var cut = section.LastIndexOf('\n', limit - 1);
        builder.Append(section[..(cut > 0 ? cut + 1 : limit)]);
        builder.AppendLine("(this section was cut off by the digest budget)");
    }

    private static string SingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/');

    private static IEnumerable<ReviewSubjectGroup> OrderedGroups(AggregateDigestRequest request) =>
        request.Groups
            .Where(group => group.Level != ReviewLevel.File)
            .OrderBy(group => group.Level)
            .ThenBy(group => group.Path, StringComparer.Ordinal);

    private static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>
    /// A deliberately coarse declaration filter across C#, TypeScript, JavaScript and their
    /// relatives: attributes, decorators, and lines that open with a modifier, a type keyword, or
    /// an import. It over-includes rather than hiding a signature, and it is anchored and bounded
    /// so repository content cannot make it expensive.
    /// </summary>
    [GeneratedRegex(
        @"^\s{0,120}(\[|@|(public|internal|protected|private|export|abstract|sealed|static|partial|virtual|override|async|readonly|const|namespace|module|using|import|from|class|struct|interface|record|enum|delegate|function|def|type|implicit|explicit|operator|extern|unsafe|required|new)\b)",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex DeclarationLine();

    private sealed record MemberSource(string Path, string Text, int LineCount);
}
