export const dotnetProjects = Object.freeze([
  Object.freeze({
    id: 'core',
    path: 'tests/AgentOrchestrator.CodeQuality.Tests/AgentOrchestrator.CodeQuality.Tests.csproj',
  }),
  Object.freeze({
    id: 'api',
    path: 'tests/QualityStudio.Api.Tests/QualityStudio.Api.Tests.csproj',
  }),
]);

export const testLanes = Object.freeze({
  portable: Object.freeze({
    filter: 'Category!=ToolBound&Category!=MachineBound&Category!=ExternalLive',
    purpose: 'deterministic tests without a real tool, host timing, or external service boundary',
  }),
  'tool-bound': Object.freeze({
    filter: 'Category=ToolBound&Category!=MachineBound&Category!=ExternalLive',
    purpose: 'controlled Git, .NET, browser, or native-tool integration',
  }),
  'non-machine': Object.freeze({
    filter: 'Category!=MachineBound&Category!=ExternalLive',
    purpose: 'coverage across the required portable and controlled tool-bound lanes',
  }),
  machine: Object.freeze({
    filter: 'Category=MachineBound',
    purpose: 'host timing and performance checks on the labeled canary host',
  }),
  'external-live': Object.freeze({
    filter: 'Category=ExternalLive',
    purpose: 'explicitly approved checks that call an external service',
    projects: Object.freeze(['core']),
  }),
});
