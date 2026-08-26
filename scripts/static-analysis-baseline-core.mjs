export function compareObservations(baseline, current, selectedTools = null) {
  const previousObservations = selectedTools
    ? baseline.filter((observation) => selectedTools.has(observation.tool))
    : baseline;
  const currentObservations = selectedTools
    ? current.filter((observation) => selectedTools.has(observation.tool))
    : current;
  const previous = new Map(previousObservations.map((observation) => [observation.fingerprint, observation]));
  const latest = new Map(currentObservations.map((observation) => [observation.fingerprint, observation]));
  const added = currentObservations.filter((observation) => !previous.has(observation.fingerprint));
  const resolved = previousObservations.filter((observation) => !latest.has(observation.fingerprint));
  return {
    current: currentObservations,
    existing: currentObservations.filter((observation) => previous.has(observation.fingerprint)),
    added,
    resolved,
  };
}
