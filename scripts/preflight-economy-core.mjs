export function buildEconomyReport(matches, generatedAt = new Date().toISOString()) {
  const accepted = matches.filter((match) =>
    match.snapshotMatched === true &&
    match.routeMatched === true &&
    match.falseClean === false &&
    match.staleReuse === false &&
    Number.isInteger(match.operations) &&
    match.operations > 0 &&
    hasUsage(match.before) &&
    hasUsage(match.after));
  const matchedOperations = accepted.reduce((total, match) => total + match.operations, 0);
  const before = sumUsage(accepted.map((match) => match.before));
  const after = sumUsage(accepted.map((match) => match.after));
  const beforeTokens = before.inputTokens + before.outputTokens;
  const afterTokens = after.inputTokens + after.outputTokens;
  const evidenceReady = matchedOperations >= 30 && beforeTokens > 0;
  return {
    schemaVersion: 1,
    generatedAt,
    status: evidenceReady ? 'measured' : 'insufficient-evidence',
    matchedRuns: accepted.length,
    matchedOperations,
    minimumMatchedOperations: 30,
    before,
    after,
    avoidedModelCalls: accepted.reduce((total, match) => total + (match.avoidedModelCalls ?? 0), 0),
    staticDurationMs: accepted.reduce((total, match) => total + (match.staticDurationMs ?? 0), 0),
    savingsPercent: evidenceReady
      ? Number((((beforeTokens - afterTokens) / beforeTokens) * 100).toFixed(2))
      : null,
    rejectedMatches: matches.length - accepted.length,
    guardrails: {
      zeroFalseCleanResults: accepted.length > 0 ? accepted.every((match) => match.falseClean !== true) : null,
      zeroStaleResultReuse: accepted.length > 0 ? accepted.every((match) => match.staleReuse !== true) : null,
      routesUnchanged: accepted.length > 0 ? accepted.every((match) => match.routeMatched === true) : null,
    },
  };
}

function hasUsage(usage) {
  return usage && Number.isFinite(usage.inputTokens) && Number.isFinite(usage.outputTokens);
}

function sumUsage(usages) {
  return usages.reduce((total, usage) => ({
    inputTokens: total.inputTokens + usage.inputTokens,
    outputTokens: total.outputTokens + usage.outputTokens,
    cachedInputTokens: total.cachedInputTokens + (usage.cachedInputTokens ?? 0),
    reasoningOutputTokens: total.reasoningOutputTokens + (usage.reasoningOutputTokens ?? 0),
  }), { inputTokens: 0, outputTokens: 0, cachedInputTokens: 0, reasoningOutputTokens: 0 });
}
