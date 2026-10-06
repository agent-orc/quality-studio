namespace AgentOrchestrator.CodeQuality;

public sealed record RuleEffectivenessDay(string Day, int Hits, int Accepted, int Dismissed,
    int FalsePositives, int Resolved, decimal Cost);

public sealed record RuleEffectivenessRow(string RuleId, int Hits, int Accepted, int Dismissed,
    int FalsePositives, int Resolved, decimal Cost, int UnpricedRuns,
    IReadOnlyList<RuleEffectivenessDay> Trend)
{
    public decimal? FalsePositiveRate => Hits == 0 ? null : (decimal)FalsePositives / Hits;
}

public sealed record RuleEffectivenessReport(DateTimeOffset GeneratedAt, string CostCurrency,
    IReadOnlyList<RuleEffectivenessRow> Rules, IReadOnlyList<RuleEffectivenessRow> WorstOffenders,
    decimal UnattributedCost, int UnpricedRuns);

/// <summary>
/// A fingerprint is one hit. The state ledger retains its latest disposition, so trend buckets
/// describe first observations or latest disposition dates, not a complete transition history.
/// A priced review's cost is shared equally by the named rules included in its prompt. Older
/// ledger entries have no rule list and remain unattributed instead of being guessed.
/// </summary>
public static class RuleEffectiveness
{
    public static RuleEffectivenessReport Aggregate(
        IEnumerable<FindingStateRecord> findings, IEnumerable<ReviewUsageEntry> usage,
        DateTimeOffset generatedAt, IEnumerable<string>? catalogueRuleIds = null)
    {
        var rows = new Dictionary<string, MutableRow>(StringComparer.Ordinal);
        MutableRow Row(string id)
        {
            if (!rows.TryGetValue(id, out var row)) rows[id] = row = new MutableRow(id);
            return row;
        }

        foreach (var id in catalogueRuleIds ?? [])
            if (!string.IsNullOrWhiteSpace(id)) Row(id);

        foreach (var finding in findings.DistinctBy(item => item.Fingerprint, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(finding.RuleId)) continue;
            var row = Row(finding.RuleId);
            row.AddFinding(finding.State, finding.Timestamp.UtcDateTime.ToString("yyyy-MM-dd"));
        }

        decimal unattributed = 0;
        var unpriced = 0;
        foreach (var entry in usage)
        {
            var ruleIds = (entry.RuleIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (entry.Cost?.Total is not decimal cost)
            {
                unpriced++;
                foreach (var id in ruleIds) Row(id).UnpricedRuns++;
                continue;
            }
            if (ruleIds.Length == 0)
            {
                unattributed += cost;
                continue;
            }
            var share = cost / ruleIds.Length;
            var day = entry.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");
            foreach (var id in ruleIds) Row(id).AddCost(share, day);
        }

        var result = rows.Values.Select(row => row.Freeze()).OrderBy(row => row.RuleId, StringComparer.Ordinal).ToArray();
        var worst = result.Where(row => row.Hits > 0)
            .OrderByDescending(row => row.FalsePositives + row.Dismissed)
            .ThenByDescending(row => row.FalsePositiveRate)
            .ThenByDescending(row => row.Cost)
            .ThenBy(row => row.RuleId, StringComparer.Ordinal).Take(10).ToArray();
        return new RuleEffectivenessReport(generatedAt, "USD", result, worst, unattributed, unpriced);
    }

    private sealed class MutableRow(string ruleId)
    {
        private readonly Dictionary<string, MutableDay> days = new(StringComparer.Ordinal);
        public int Hits, Accepted, Dismissed, FalsePositives, Resolved, UnpricedRuns;
        public decimal Cost;
        private MutableDay Day(string day)
        {
            if (!days.TryGetValue(day, out var value)) days[day] = value = new MutableDay();
            return value;
        }
        public void AddFinding(FindingState state, string day)
        {
            Hits++; Day(day).Hits++;
            switch (state)
            {
                case FindingState.Accepted: Accepted++; Day(day).Accepted++; break;
                case FindingState.Waived: Dismissed++; Day(day).Dismissed++; break;
                case FindingState.FalsePositive: FalsePositives++; Day(day).FalsePositives++; break;
                case FindingState.Resolved: Resolved++; Day(day).Resolved++; break;
            }
        }
        public void AddCost(decimal share, string day) { Cost += share; Day(day).Cost += share; }
        public RuleEffectivenessRow Freeze() => new(ruleId, Hits, Accepted, Dismissed, FalsePositives,
            Resolved, Cost, UnpricedRuns, days.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new RuleEffectivenessDay(pair.Key, pair.Value.Hits, pair.Value.Accepted,
                    pair.Value.Dismissed, pair.Value.FalsePositives, pair.Value.Resolved, pair.Value.Cost)).ToArray());
        private sealed class MutableDay
        {
            public int Hits, Accepted, Dismissed, FalsePositives, Resolved;
            public decimal Cost;
        }
    }
}
