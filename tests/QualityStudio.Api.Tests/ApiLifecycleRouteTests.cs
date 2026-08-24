using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;
using CodingAgentRunner.Events;
using CodingAgentRunner.Quota;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>
/// Direct HTTP proof for the registry, guideline, scope, risk, thread and review-lifecycle
/// routes. Every route is exercised over the real pipeline so the status code, the problem
/// title produced by the exception middleware, and the response shape are all observed the
/// way a client observes them — a handler-level unit test cannot see any of those three.
/// </summary>
/// <remarks>
/// The default security mode is <c>Local</c>, so no authentication headers are required.
/// The agent-backed surfaces (review execution and guideline dry-run impact) are driven by
/// deterministic in-process doubles: the portable lane must never launch a coding agent CLI.
/// </remarks>
public sealed class ApiLifecycleRouteTests : IAsyncLifetime
{
    private const string SampleSource =
        "namespace Sample;\npublic static class Greeter\n{\n    public static string Hello() => \"hello\";\n}\n";

    /// <summary>Marker text that only the draft guideline carries, so the stub agent can react to it.</summary>
    private const string ImpactMarker = "GUIDELINE-IMPACT-MARKER";

    private RepositoryFixture? fixture;
    private TestApplication? application;
    private GitTestRepository? updateTarget;
    private GitTestRepository? archiveTarget;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string RepositoryRoot => fixture!.Repository.Root;

    // ---------------------------------------------------------------- repositories

    [Fact]
    public async Task Repository_update_rewrites_the_registration_and_reports_unknown_and_invalid_targets()
    {
        using var client = application!.CreateClient();
        using (var created = await client.PostAsJsonAsync("/api/repos", new
        {
            id = "update-target",
            displayName = "Update target",
            rootPath = updateTarget!.Root,
            inputBudgetCharacters = 9000,
            enabledReviewKinds = new[] { "code" },
        }, Token))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using var updated = await client.PutAsJsonAsync("/api/repos/update-target", new
        {
            displayName = "Renamed target",
            rootPath = updateTarget.Root,
            inputBudgetCharacters = 12000,
            enabledReviewKinds = new[] { "code", "security" },
        }, Token);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var registration = await updated.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal("update-target", registration.GetProperty("id").GetString());
        Assert.Equal("Renamed target", registration.GetProperty("displayName").GetString());
        Assert.Equal(12000, registration.GetProperty("inputBudgetCharacters").GetInt32());
        Assert.False(registration.GetProperty("archived").GetBoolean());
        Assert.Equal(
            ["code", "security"],
            registration.GetProperty("enabledReviewKinds").EnumerateArray().Select(kind => kind.GetString()!).ToArray());
        // Sensors are omitted by the request, so the update must preserve the stored selection.
        Assert.NotEmpty(registration.GetProperty("sensors").EnumerateArray());
        Assert.Contains("Renamed target", await File.ReadAllTextAsync(
            Path.Combine(fixture!.HostRoot, ".quality-studio", "repositories.json"), Token));

        using var unknown = await client.PutAsJsonAsync("/api/repos/repository-that-was-never-registered", new
        {
            displayName = "Ghost",
            rootPath = updateTarget.Root,
        }, Token);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("Repository not found", await TitleAsync(unknown));

        using var invalid = await client.PutAsJsonAsync("/api/repos/update-target", new
        {
            displayName = "Renamed target",
            rootPath = Path.Combine(fixture.BaseRoot, "directory-that-does-not-exist"),
        }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("Repository path does not exist", await TitleAsync(invalid));
    }

    [Fact]
    public async Task Repository_archive_hides_the_registration_and_refuses_the_default_repository()
    {
        using var client = application!.CreateClient();
        using (var created = await client.PostAsJsonAsync("/api/repos", new
        {
            id = "archive-target",
            displayName = "Archive target",
            rootPath = archiveTarget!.Root,
            inputBudgetCharacters = 9000,
            enabledReviewKinds = new[] { "code" },
        }, Token))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using var archived = await client.DeleteAsync("/api/repos/archive-target", Token);

        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        var registration = await archived.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal("archive-target", registration.GetProperty("id").GetString());
        Assert.True(registration.GetProperty("archived").GetBoolean());

        var active = await client.GetFromJsonAsync<JsonElement>("/api/repos", Token);
        Assert.DoesNotContain(active.GetProperty("repositories").EnumerateArray(),
            entry => entry.GetProperty("id").GetString() == "archive-target");
        var all = await client.GetFromJsonAsync<JsonElement>("/api/repos?includeArchived=true", Token);
        Assert.Contains(all.GetProperty("repositories").EnumerateArray(),
            entry => entry.GetProperty("id").GetString() == "archive-target");

        // Archiving is idempotent: a second delete returns the already-archived registration.
        using var again = await client.DeleteAsync("/api/repos/archive-target", Token);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        using var refused = await client.DeleteAsync("/api/repos/default", Token);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("Invalid repository configuration", await TitleAsync(refused));

        using var unknown = await client.DeleteAsync("/api/repos/repository-that-was-never-registered", Token);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("Repository not found", await TitleAsync(unknown));
    }

    // ------------------------------------------------------------------------ risk

    [Fact]
    public async Task Risk_projects_files_with_an_explicit_window_and_rejects_windows_outside_one_to_3650_days()
    {
        using var client = application!.CreateClient();

        var defaultWindow = await client.GetFromJsonAsync<JsonElement>("/api/risk", Token);
        Assert.Equal(90, defaultWindow.GetProperty("days").GetInt32());
        var head = defaultWindow.GetProperty("currentCommit").GetString();
        Assert.NotNull(head);
        Assert.Equal(40, head.Length);

        var scoped = await client.GetFromJsonAsync<JsonElement>("/api/risk?days=30", Token);
        Assert.Equal(30, scoped.GetProperty("days").GetInt32());
        var sample = Assert.Single(scoped.GetProperty("rows").EnumerateArray(),
            row => row.GetProperty("path").GetString() == "Sample.cs");
        Assert.Equal("Sample.cs", sample.GetProperty("name").GetString());
        // No coverage report exists, so the composite score is deliberately withheld rather than guessed.
        Assert.Equal(JsonValueKind.Null, sample.GetProperty("coverage").GetProperty("linePercent").ValueKind);
        Assert.Equal(JsonValueKind.Null, sample.GetProperty("riskScore").ValueKind);
        Assert.All(scoped.GetProperty("matrix").EnumerateArray(),
            cell => Assert.Equal("unknown", cell.GetProperty("coverage").GetString()));
        Assert.Equal(
            scoped.GetProperty("rows").GetArrayLength(),
            scoped.GetProperty("matrix").EnumerateArray().Sum(cell => cell.GetProperty("files").GetInt32()));

        foreach (var window in new[] { 0, -1, 3651 })
        {
            using var rejected = await client.GetAsync($"/api/risk?days={window}", Token);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Equal("Invalid repository path", await TitleAsync(rejected));
        }
    }

    // ------------------------------------------------------------------ guidelines

    [Fact]
    public async Task Guideline_listing_returns_the_builtin_catalogue_and_surfaces_a_malformed_input_file()
    {
        using var client = application!.CreateClient();
        var listing = await client.GetFromJsonAsync<JsonElement>("/api/guidelines", Token);

        Assert.Equal(JsonValueKind.Array, listing.GetProperty("guidelines").ValueKind);
        Assert.Equal(JsonValueKind.Array, listing.GetProperty("traces").ValueKind);
        var catalogue = listing.GetProperty("catalogue").EnumerateArray().ToArray();
        Assert.Equal(GuidelineStore.Catalogue.Count, catalogue.Length);
        var dotnet = Assert.Single(catalogue, entry => entry.GetProperty("id").GetString() == "dotnet-api-safety");
        Assert.Equal(".NET API safety", dotnet.GetProperty("title").GetString());
        Assert.Equal(".NET", dotnet.GetProperty("technology").GetString());
        var draft = dotnet.GetProperty("guideline");
        Assert.Equal(80, draft.GetProperty("priority").GetInt32());
        Assert.True(draft.GetProperty("enabled").GetBoolean());
        Assert.Equal(["code"], draft.GetProperty("kinds").EnumerateArray().Select(kind => kind.GetString()!).ToArray());
        Assert.Equal(["file"], draft.GetProperty("levels").EnumerateArray().Select(level => level.GetString()!).ToArray());

        var malformed = GuidelinePath("malformed-guideline.md");
        Directory.CreateDirectory(Path.GetDirectoryName(malformed)!);
        await File.WriteAllTextAsync(malformed, "no frontmatter at all\n", Token);
        try
        {
            using var response = await client.GetAsync("/api/guidelines", Token);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("Review input is invalid", await TitleAsync(response));
        }
        finally
        {
            File.Delete(malformed);
        }
    }

    [Fact]
    public async Task Guideline_update_rewrites_the_stored_file_and_rejects_unknown_ids_and_invalid_drafts()
    {
        using var client = application!.CreateClient();
        try
        {
            using (var created = await client.PostAsJsonAsync("/api/guidelines",
                       Draft("update-guideline", priority: 30, content: "Original guidance."), Token))
            {
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            }

            using var updated = await client.PutAsJsonAsync("/api/guidelines/update-guideline", new
            {
                id = "update-guideline",
                enabled = false,
                priority = 55,
                kinds = new[] { "code", "security" },
                levels = new[] { "file" },
                content = "Rewritten guidance.",
            }, Token);

            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            var definition = await updated.Content.ReadFromJsonAsync<JsonElement>(Token);
            Assert.Equal("update-guideline", definition.GetProperty("id").GetString());
            Assert.Equal("update-guideline.md", definition.GetProperty("fileName").GetString());
            Assert.False(definition.GetProperty("enabled").GetBoolean());
            Assert.Equal(55, definition.GetProperty("priority").GetInt32());
            Assert.Equal("Rewritten guidance.", definition.GetProperty("content").GetString());
            Assert.Contains("Rewritten guidance.", await File.ReadAllTextAsync(GuidelinePath("update-guideline.md"), Token));

            var listing = await client.GetFromJsonAsync<JsonElement>("/api/guidelines", Token);
            var listed = Assert.Single(listing.GetProperty("guidelines").EnumerateArray(),
                entry => entry.GetProperty("id").GetString() == "update-guideline");
            Assert.Equal(55, listed.GetProperty("priority").GetInt32());

            using var unknown = await client.PutAsJsonAsync("/api/guidelines/guideline-that-was-never-created",
                Draft("guideline-that-was-never-created", priority: 10, content: "Nothing here."), Token);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Equal("Repository not found", await TitleAsync(unknown));

            using var invalid = await client.PutAsJsonAsync("/api/guidelines/update-guideline", new
            {
                id = "update-guideline",
                enabled = true,
                priority = 55,
                kinds = new[] { "nonsense" },
                levels = new[] { "file" },
                content = "Rewritten guidance.",
            }, Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("Invalid repository path", await TitleAsync(invalid));
        }
        finally
        {
            File.Delete(GuidelinePath("update-guideline.md"));
        }
    }

    [Fact]
    public async Task Guideline_delete_removes_the_stored_file_and_reports_a_second_delete_as_missing()
    {
        using var client = application!.CreateClient();
        try
        {
            using (var created = await client.PostAsJsonAsync("/api/guidelines",
                       Draft("delete-guideline", priority: 20, content: "Temporary guidance."), Token))
            {
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            }

            Assert.True(File.Exists(GuidelinePath("delete-guideline.md")));

            using var deleted = await client.DeleteAsync("/api/guidelines/delete-guideline", Token);

            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.Equal(0, deleted.Content.Headers.ContentLength ?? 0);
            Assert.False(File.Exists(GuidelinePath("delete-guideline.md")));
            var listing = await client.GetFromJsonAsync<JsonElement>("/api/guidelines", Token);
            Assert.DoesNotContain(listing.GetProperty("guidelines").EnumerateArray(),
                entry => entry.GetProperty("id").GetString() == "delete-guideline");

            using var again = await client.DeleteAsync("/api/guidelines/delete-guideline", Token);
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
            Assert.Equal("Repository not found", await TitleAsync(again));
        }
        finally
        {
            File.Delete(GuidelinePath("delete-guideline.md"));
        }
    }

    [Fact]
    public async Task Guideline_install_copies_a_catalogue_entry_once_and_reports_unknown_and_duplicate_ids()
    {
        using var client = application!.CreateClient();
        try
        {
            using var installed = await client.PostAsync("/api/guidelines/catalog/testing-confidence/install", null, Token);

            Assert.Equal(HttpStatusCode.Created, installed.StatusCode);
            Assert.Equal("/api/guidelines/testing-confidence", installed.Headers.Location?.ToString());
            var definition = await installed.Content.ReadFromJsonAsync<JsonElement>(Token);
            Assert.Equal("testing-confidence", definition.GetProperty("id").GetString());
            Assert.Equal("testing-confidence.md", definition.GetProperty("fileName").GetString());
            Assert.Equal(70, definition.GetProperty("priority").GetInt32());
            Assert.True(definition.GetProperty("enabled").GetBoolean());
            Assert.Contains("deterministic", definition.GetProperty("content").GetString()!, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(GuidelinePath("testing-confidence.md")));

            using var duplicate = await client.PostAsync("/api/guidelines/catalog/testing-confidence/install", null, Token);
            Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
            Assert.Equal("Invalid repository path", await TitleAsync(duplicate));

            using var unknown = await client.PostAsync("/api/guidelines/catalog/not-in-the-catalogue/install", null, Token);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Equal("Repository not found", await TitleAsync(unknown));
        }
        finally
        {
            File.Delete(GuidelinePath("testing-confidence.md"));
        }
    }

    [Fact]
    public async Task Guideline_impact_diffs_the_draft_policy_and_rejects_empty_and_off_repository_samples()
    {
        using var client = application!.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/guidelines/impact", new
        {
            guideline = Draft("impact-draft", priority: 90, content: $"Report the {ImpactMarker} rule."),
            samplePaths = new[] { "Sample.cs" },
            kind = "code",
        }, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var impact = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal("impact-draft", impact.GetProperty("guidelineId").GetString());
        Assert.Equal("code", impact.GetProperty("kind").GetString());
        Assert.True(impact.GetProperty("changed").GetBoolean());
        Assert.Equal(1, impact.GetProperty("addedCount").GetInt32());
        Assert.Equal(0, impact.GetProperty("removedCount").GetInt32());
        var file = Assert.Single(impact.GetProperty("files").EnumerateArray());
        Assert.Equal("Sample.cs", file.GetProperty("path").GetString());
        Assert.Equal(1, file.GetProperty("before").GetArrayLength());
        Assert.Equal(2, file.GetProperty("after").GetArrayLength());
        var added = Assert.Single(file.GetProperty("added").EnumerateArray());
        Assert.Equal("guideline.draft", added.GetProperty("ruleId").GetString());
        Assert.Equal("Sample.cs", added.GetProperty("path").GetString());
        Assert.Empty(file.GetProperty("removed").EnumerateArray());
        // The dry run must never persist policy or review metadata.
        Assert.False(File.Exists(GuidelinePath("impact-draft.md")));

        using var empty = await client.PostAsJsonAsync("/api/guidelines/impact", new
        {
            guideline = Draft("impact-draft", priority: 90, content: "Anything."),
            samplePaths = Array.Empty<string>(),
            kind = "code",
        }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("Invalid repository path", await TitleAsync(empty));

        using var missing = await client.PostAsJsonAsync("/api/guidelines/impact", new
        {
            guideline = Draft("impact-draft", priority: 90, content: "Anything."),
            samplePaths = new[] { "NotInTheRepository.cs" },
            kind = "code",
        }, Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("File not found", await TitleAsync(missing));
    }

    // ----------------------------------------------------------------- scope rules

    [Fact]
    public async Task Scope_rules_read_the_stored_configuration_and_reject_a_malformed_file()
    {
        using var client = application!.CreateClient();
        var empty = await client.GetFromJsonAsync<JsonElement>("/api/scope/rules", Token);
        Assert.Equal(RepositoryScopeConfigurationStore.Schema, empty.GetProperty("schema").GetString());
        Assert.Empty(empty.GetProperty("rules").EnumerateArray());

        var scopePath = Path.Combine(RepositoryRoot, ".quality", "scope.json");
        Directory.CreateDirectory(Path.GetDirectoryName(scopePath)!);
        try
        {
            await File.WriteAllTextAsync(scopePath, new JsonObject
            {
                ["$schema"] = RepositoryScopeConfigurationStore.Schema,
                ["rules"] = new JsonArray(new JsonObject { ["action"] = "include", ["pattern"] = "Sample.cs" }),
            }.ToJsonString(), Token);

            var configured = await client.GetFromJsonAsync<JsonElement>("/api/scope/rules", Token);
            var rule = Assert.Single(configured.GetProperty("rules").EnumerateArray());
            Assert.Equal(0, rule.GetProperty("index").GetInt32());
            Assert.Equal("include", rule.GetProperty("action").GetString());
            Assert.Equal("Sample.cs", rule.GetProperty("pattern").GetString());
            Assert.Equal(JsonValueKind.Null, rule.GetProperty("reason").ValueKind);
            Assert.False(rule.GetProperty("widerPattern").GetBoolean());
            Assert.Equal(["Sample.cs"],
                rule.GetProperty("matchedFiles").EnumerateArray().Select(path => path.GetString()!).ToArray());

            await File.WriteAllTextAsync(scopePath, "{\"rules\":\"not-an-array\"}", Token);
            using var malformed = await client.GetAsync("/api/scope/rules", Token);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, malformed.StatusCode);
            Assert.Equal("Stored report is invalid", await TitleAsync(malformed));
        }
        finally
        {
            File.Delete(scopePath);
        }
    }

    // -------------------------------------------------------------------- handover

    [Fact]
    public async Task Handover_configuration_reports_the_agent_studio_target_and_404s_for_an_unknown_repository()
    {
        using var client = application!.CreateClient();
        using var response = await client.GetAsync("/api/handover", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var configuration = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.True(configuration.GetProperty("targetConfigured").GetBoolean());
        Assert.True(configuration.GetProperty("dryRun").GetBoolean());
        Assert.Equal("QS", configuration.GetProperty("project").GetString());

        using var unknown = await client.GetAsync("/api/repos/repository-that-was-never-registered/handover", Token);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("Repository not found", await TitleAsync(unknown));
    }

    // --------------------------------------------------------------------- threads

    [Fact]
    public async Task Thread_mutation_opens_a_thread_appends_a_reply_and_resolves_it()
    {
        using var client = application!.CreateClient();

        using var opened = await client.PostAsJsonAsync("/api/threads", new
        {
            path = "Sample.cs",
            kind = "code",
            body = "  Please rename this helper.  ",
            humanName = "  Ada  ",
            line = 2,
        }, Token);

        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        var thread = await opened.Content.ReadFromJsonAsync<JsonElement>(Token);
        var threadId = thread.GetProperty("id").GetString()!;
        Assert.StartsWith("thread-", threadId, StringComparison.Ordinal);
        Assert.Equal("open", thread.GetProperty("status").GetString());
        Assert.Equal("anchored", thread.GetProperty("anchorState").GetString());
        var anchor = thread.GetProperty("anchor");
        Assert.Equal("Sample.cs", anchor.GetProperty("path").GetString());
        var fingerprint = anchor.GetProperty("fingerprint").GetString()!;
        Assert.StartsWith("sha256:", fingerprint, StringComparison.Ordinal);
        Assert.Equal(71, fingerprint.Length);
        Assert.Equal(2, anchor.GetProperty("lastKnownRange").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(2, anchor.GetProperty("lastKnownRange").GetProperty("end").GetProperty("line").GetInt32());
        var first = Assert.Single(thread.GetProperty("entries").EnumerateArray());
        Assert.Equal("Please rename this helper.", first.GetProperty("body").GetString());
        Assert.Equal("human", first.GetProperty("author").GetProperty("kind").GetString());
        Assert.Equal("Ada", first.GetProperty("author").GetProperty("name").GetString());
        Assert.False(first.TryGetProperty("replyTo", out _));
        var firstEntryId = first.GetProperty("id").GetString()!;

        using var replied = await client.PostAsJsonAsync("/api/threads", new
        {
            path = "Sample.cs",
            kind = "code",
            threadId,
            body = "Agreed, renaming it.",
            replyTo = firstEntryId,
        }, Token);

        Assert.Equal(HttpStatusCode.OK, replied.StatusCode);
        var withReply = await replied.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(threadId, withReply.GetProperty("id").GetString());
        var entries = withReply.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(firstEntryId, entries[1].GetProperty("replyTo").GetString());
        // An omitted reviewer name falls back to the neutral default rather than failing.
        Assert.Equal("Reviewer", entries[1].GetProperty("author").GetProperty("name").GetString());

        using var resolved = await client.PostAsJsonAsync("/api/threads", new
        {
            path = "Sample.cs",
            kind = "code",
            threadId,
            status = "resolved",
        }, Token);

        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var closed = await resolved.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal("resolved", closed.GetProperty("status").GetString());
        Assert.Equal(2, closed.GetProperty("entries").GetArrayLength());

        using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(fixture!.SidecarPath, Token));
        var stored = Assert.Single(persisted.RootElement.GetProperty("threads").EnumerateArray(),
            candidate => candidate.GetProperty("id").GetString() == threadId);
        Assert.Equal("resolved", stored.GetProperty("status").GetString());
        Assert.Equal(2, stored.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Thread_mutation_rejects_empty_out_of_bounds_and_unknown_requests()
    {
        using var client = application!.CreateClient();

        using var noChange = await client.PostAsJsonAsync("/api/threads",
            new { path = "Sample.cs", kind = "code" }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, noChange.StatusCode);
        Assert.Equal("Invalid repository path", await TitleAsync(noChange));

        using var noLine = await client.PostAsJsonAsync("/api/threads",
            new { path = "Sample.cs", kind = "code", body = "A new thread without an anchor." }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, noLine.StatusCode);

        using var zeroLine = await client.PostAsJsonAsync("/api/threads",
            new { path = "Sample.cs", kind = "code", body = "Line zero.", line = 0 }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, zeroLine.StatusCode);

        using var pastEndOfFile = await client.PostAsJsonAsync("/api/threads",
            new { path = "Sample.cs", kind = "code", body = "Past the end.", line = 99 }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, pastEndOfFile.StatusCode);

        using var badStatus = await client.PostAsJsonAsync("/api/threads",
            new { path = "Sample.cs", kind = "code", body = "Closing.", line = 1, status = "closed" }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);

        using var oversizedBody = await client.PostAsJsonAsync("/api/threads",
            new { path = "Sample.cs", kind = "code", body = new string('x', 20001), line = 1 }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, oversizedBody.StatusCode);

        using var unknownThread = await client.PostAsJsonAsync("/api/threads",
            new { path = "Sample.cs", kind = "code", threadId = "thread-that-was-never-opened", body = "Hello." }, Token);
        Assert.Equal(HttpStatusCode.NotFound, unknownThread.StatusCode);
        Assert.Equal("Repository not found", await TitleAsync(unknownThread));

        using var unknownKind = await client.PostAsJsonAsync("/api/threads",
            new { path = "Sample.cs", kind = "performance", body = "No sidecar for this kind.", line = 1 }, Token);
        Assert.Equal(HttpStatusCode.NotFound, unknownKind.StatusCode);
        Assert.Equal("File not found", await TitleAsync(unknownKind));
    }

    // ------------------------------------------------------------ review lifecycle

    [Fact]
    public async Task Pause_stops_a_running_review_and_reports_an_unknown_run_as_missing()
    {
        using var lifecycle = RepositoryFixture.Create("quality-studio-lifecycle-pause");
        var executors = new GatedExecutorFactory();
        await using var host = lifecycle.CreateApplication(executors);
        using var client = host.CreateClient();
        var id = await StartReviewAsync(client, executors);

        using var paused = await client.PostAsync($"/api/review/runs/{id}/pause", null, Token);

        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        var run = await paused.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(id, run.GetProperty("id").GetString());
        Assert.Equal("paused", run.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("finishedAt").ValueKind);
        Assert.Equal("paused", ReadStatusState(lifecycle, id));

        using var unknown = await client.PostAsync("/api/review/runs/review-that-was-never-started/pause", null, Token);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("Repository not found", await TitleAsync(unknown));
    }

    [Fact]
    public async Task Cancel_terminates_a_running_review_and_blocks_a_later_pause()
    {
        using var lifecycle = RepositoryFixture.Create("quality-studio-lifecycle-cancel");
        var executors = new GatedExecutorFactory();
        await using var host = lifecycle.CreateApplication(executors);
        using var client = host.CreateClient();
        var id = await StartReviewAsync(client, executors);

        using var cancelled = await client.DeleteAsync($"/api/review/runs/{id}", Token);

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var run = await cancelled.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(id, run.GetProperty("id").GetString());
        Assert.Equal("cancelled", run.GetProperty("state").GetString());
        Assert.NotEqual(JsonValueKind.Null, run.GetProperty("finishedAt").ValueKind);
        Assert.All(run.GetProperty("files").EnumerateArray(),
            file => Assert.Equal("cancelled", file.GetProperty("state").GetString()));
        Assert.Equal("cancelled", ReadStatusState(lifecycle, id));

        // The meaningful contract is the status code: a run that exists but is terminal is a
        // 400, a run that never existed is a 404. The titles are asserted as documentation of
        // current behaviour only - both come from a blanket exception map in Program.cs and
        // name "repository" even when the missing or invalid thing is the run.
        using var pauseTerminal = await client.PostAsync($"/api/review/runs/{id}/pause", null, Token);
        Assert.Equal(HttpStatusCode.BadRequest, pauseTerminal.StatusCode);
        Assert.Equal("Invalid repository path", await TitleAsync(pauseTerminal));

        using var unknown = await client.DeleteAsync("/api/review/runs/review-that-was-never-started", Token);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("Repository not found", await TitleAsync(unknown));
    }

    // ----------------------------------------------------------------- test set-up

    public ValueTask InitializeAsync()
    {
        fixture = RepositoryFixture.Create("quality-studio-lifecycle-tests");
        updateTarget = GitTestRepository.CreateIn(Path.Combine(fixture.BaseRoot, "update-target"));
        archiveTarget = GitTestRepository.CreateIn(Path.Combine(fixture.BaseRoot, "archive-target"));
        application = fixture.CreateApplication(executorFactory: null);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null) await application.DisposeAsync();
        updateTarget?.Dispose();
        archiveTarget?.Dispose();
        fixture?.Dispose();
    }

    /// <summary>Starts a review and waits until the injected executor has actually been entered.</summary>
    private static async Task<string> StartReviewAsync(HttpClient client, GatedExecutorFactory executors)
    {
        using var accepted = await client.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs",
            kind = "code",
            cliType = "test-agent",
        }, Token);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var run = await accepted.Content.ReadFromJsonAsync<JsonElement>(Token);
        // Waiting on the executor's own signal keeps the test free of polling and wall-clock delays.
        await executors.Entered.WaitAsync(Token);
        return run.GetProperty("id").GetString()!;
    }

    private static string ReadStatusState(RepositoryFixture lifecycle, string runId)
    {
        using var status = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            lifecycle.Repository.Root, ".quality", "runs", runId, "status.json")));
        return status.RootElement.GetProperty("state").GetString()!;
    }

    private static async Task<string?> TitleAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        return problem.GetProperty("title").GetString();
    }

    private static object Draft(string id, int priority, string content) => new
    {
        id,
        enabled = true,
        priority,
        kinds = new[] { "code" },
        levels = new[] { "file" },
        content,
    };

    private string GuidelinePath(string fileName) =>
        Path.Combine(RepositoryRoot, ".quality", "inputs", fileName);

    /// <summary>A git repository, an isolated content root, and the API host that serves them.</summary>
    private sealed class RepositoryFixture : IDisposable
    {
        private RepositoryFixture(string baseRoot, GitTestRepository repository)
        {
            BaseRoot = baseRoot;
            Repository = repository;
            HostRoot = Path.Combine(baseRoot, "host");
        }

        public string BaseRoot { get; }

        public string HostRoot { get; }

        public GitTestRepository Repository { get; }

        /// <summary>The pre-seeded <c>code</c> review sidecar that anchors the thread routes.</summary>
        public string SidecarPath => Path.Combine(
            Repository.Root, ".quality", "reviews", "files", "sample.review-meta.code.json");

        public static RepositoryFixture Create(string prefix)
        {
            var baseRoot = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
            var repository = GitTestRepository.CreateIn(Path.Combine(baseRoot, "repository"));
            repository
                .Write("Sample.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />")
                .Write("Sample.cs", SampleSource)
                .Write("Scratch.cs", "namespace Sample;\ninternal sealed class Scratch { }\n")
                .Write(".gitignore", "bin/\n")
                .CommitAll("fixture");
            var fixture = new RepositoryFixture(baseRoot, repository);
            Directory.CreateDirectory(fixture.HostRoot);
            // The sidecar has to exist before the host builds its review-metadata index, otherwise
            // discovery would depend on a filesystem-watcher notification and stop being deterministic.
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.SidecarPath)!);
            File.WriteAllText(fixture.SidecarPath, new JsonObject
            {
                ["unit"] = new JsonObject { ["path"] = "Sample.cs" },
                ["kind"] = "code",
                ["reviewedAt"] = "2026-07-22T09:00:00.000Z",
                ["reviewer"] = new JsonObject { ["agent"] = "test", ["model"] = "test" },
                ["grade"] = new JsonObject { ["score"] = 84, ["band"] = "B", ["rationale"] = "Fixture review." },
                ["summary"] = "Fixture review.",
                ["findings"] = new JsonArray(),
                ["threads"] = new JsonArray(),
            }.ToJsonString());
            return fixture;
        }

        public TestApplication CreateApplication(IReviewExecutorFactory? executorFactory) =>
            new(Repository.Root, HostRoot, Repository.AllowedRoot, executorFactory);

        public void Dispose()
        {
            Repository.Dispose();
            TestDirectory.Delete(BaseRoot);
        }
    }

    private sealed class TestApplication(
        string repositoryRoot,
        string contentRoot,
        string allowedRoot,
        IReviewExecutorFactory? executorFactory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = repositoryRoot,
                    ["QualityStudio:AllowedRoots:0"] = allowedRoot,
                    ["AgentStudio:BaseUrl"] = "http://agent-studio.test",
                    ["AgentStudio:ClientId"] = "quality-studio-test",
                    ["AgentStudio:Project"] = "QS",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<QuotaService>();
                services.AddSingleton(new QuotaService([]));
                services.RemoveAll<GuidelineImpactAnalyzer>();
                services.AddTransient(_ => new GuidelineImpactAnalyzer(new StubReviewAgent()));
                if (executorFactory is not null)
                {
                    services.RemoveAll<IReviewExecutorFactory>();
                    services.AddSingleton(executorFactory);
                }
            });
        }
    }

    /// <summary>
    /// A review agent that answers from a fixed script and reports one extra finding whenever the
    /// draft guideline reached the prompt, which is what makes the impact diff observable.
    /// </summary>
    private sealed class StubReviewAgent : IReviewAgent
    {
        public string AgentName => "stub";

        public string? Model => "stub-model";

        public Task<ReviewAgentResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            var findings = new JsonArray(Finding("baseline", "guideline.baseline", "Baseline finding", 1));
            if (prompt.Contains(ImpactMarker, StringComparison.Ordinal))
                findings.Add(Finding("draft", "guideline.draft", "Draft-only finding", 2));
            var response = new JsonObject
            {
                ["grade"] = Grade(),
                ["summary"] = "Stub review.",
                ["aspects"] = new JsonArray(new JsonObject
                {
                    ["id"] = "correctness",
                    ["title"] = "Correctness",
                    ["grade"] = Grade(),
                }),
                ["findings"] = findings,
            };
            return Task.FromResult(new ReviewAgentResult("stub-run", response.ToJsonString()));
        }

        private static JsonObject Grade() => new()
        {
            ["score"] = 84,
            ["band"] = "B",
            ["rationale"] = "Stub rationale.",
        };

        private static JsonObject Finding(string id, string ruleId, string title, int line) => new()
        {
            ["id"] = id,
            ["ruleId"] = ruleId,
            ["aspect"] = "correctness",
            ["severity"] = "medium",
            ["title"] = title,
            ["description"] = "Stubbed for the dry-run impact route.",
            ["recommendation"] = "Nothing to do.",
            ["locations"] = new JsonArray(new JsonObject
            {
                ["path"] = "Sample.cs",
                ["range"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = line, ["column"] = 1 },
                    ["end"] = new JsonObject { ["line"] = line, ["column"] = 1 },
                },
            }),
        };
    }

    /// <summary>
    /// An executor that announces the moment a review attempt starts and then blocks until the
    /// attempt is cancelled, so pause and cancel act on a genuinely running review without any
    /// polling loop or timing assumption.
    /// </summary>
    private sealed class GatedExecutorFactory : IReviewExecutorFactory
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => entered.Task;

        public IReviewExecutor Create(string cliType, string? model, string? thinkingLevel,
            Action<string, CliRunEvent> eventObserver, Action<ReviewUsageEntry> usageRecorded) =>
            new GatedExecutor(entered);

        private sealed class GatedExecutor(TaskCompletionSource entered) : IReviewExecutor
        {
            public async Task<ReviewExecutionResult> ReviewIfNeededAsync(
                ReviewRequest request,
                bool force,
                CancellationToken cancellationToken)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }
}
