namespace QualityStudio.Api;

public sealed record ScopeRuleMutationRequest(
    string Action,
    string Pattern,
    string? Reason = null,
    bool ConfirmExpansion = false);

public sealed record ScopeRuleView(
    int Index,
    string Action,
    string Pattern,
    string? Reason,
    IReadOnlyList<string> MatchedFiles,
    bool WiderPattern);

public sealed record ScopeRulesResponse(string Schema, IReadOnlyList<ScopeRuleView> Rules);
