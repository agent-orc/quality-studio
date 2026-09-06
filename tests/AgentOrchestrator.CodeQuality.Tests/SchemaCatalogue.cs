using System.Collections.Concurrent;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// JsonSchema.Net registers each schema's $id in a process-wide registry and refuses a second
/// registration, so every test that validates against a schema file shares one parsed instance.
/// </summary>
internal static class SchemaCatalogue
{
    private static readonly ConcurrentDictionary<string, Lazy<JsonSchema>> Schemas = new(StringComparer.Ordinal);

    public static JsonSchema Get(string fileName) =>
        Schemas.GetOrAdd(fileName, name => new Lazy<JsonSchema>(
            () => JsonSchema.FromText(File.ReadAllText(
                Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "schemas", name))),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
}
