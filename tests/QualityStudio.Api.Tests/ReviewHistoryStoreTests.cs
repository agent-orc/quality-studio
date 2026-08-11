using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewHistoryStoreTests
{
    [Fact]
    public void TerminalHistory_IsCreateOnlyIdempotentPortableAndIndependentOfActiveState()
    {
        var root = Directory.CreateTempSubdirectory("review-history-");
        var clone = Directory.CreateTempSubdirectory("review-history-clone-");
        try
        {
            var (manifest, status, progress) = TerminalRun(root.FullName, "portable");
            var store = new ReviewHistoryStore(root.FullName);
            var first = store.Commit(manifest, status, progress, []);
            var second = store.Commit(manifest, status, progress, []);
            Assert.Equal(first.ContentHash, second.ContentHash);

            var historyFile = Path.Combine(store.HistoryPath, manifest.RunId + ".json");
            var content = File.ReadAllText(historyFile);
            Assert.DoesNotContain(root.FullName, content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<repository>", Assert.Single(first.Run.Errors), StringComparison.Ordinal);

            Directory.CreateDirectory(Path.Combine(clone.FullName, ".quality", "review-history"));
            Directory.Move(Path.Combine(root.FullName, ".quality", "review-history"),
                Path.Combine(clone.FullName, ".quality", "review-history", "copied"));
            Directory.Move(Path.Combine(clone.FullName, ".quality", "review-history", "copied", "runs"),
                Path.Combine(clone.FullName, ".quality", "review-history", "runs"));
            Directory.Delete(Path.Combine(clone.FullName, ".quality", "review-history", "copied"));
            var fromClone = Assert.Single(new ReviewHistoryStore(clone.FullName).LoadAll());
            Assert.Equal(manifest.RunId, fromClone.Run.RunId);
            Assert.Equal("done", fromClone.Run.State);
        }
        finally
        {
            root.Delete(true);
            clone.Delete(true);
        }
    }

    [Fact]
    public void TerminalHistory_RejectsConflictingPayloadForTheSameRunId()
    {
        var root = Directory.CreateTempSubdirectory("review-history-conflict-");
        try
        {
            var (manifest, status, progress) = TerminalRun(root.FullName, "conflict");
            var store = new ReviewHistoryStore(root.FullName);
            store.Commit(manifest, status, progress, []);

            Assert.Throws<ReviewHistoryConflictException>(() => store.Commit(manifest,
                status with { UsageOperations = 1, Usage = new TokenUsage(10, 2, 0, 0, 100) }, progress, []));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void TerminalHistory_DetectsTamperingInsteadOfSilentlyReplacingIt()
    {
        var root = Directory.CreateTempSubdirectory("review-history-tamper-");
        try
        {
            var (manifest, status, progress) = TerminalRun(root.FullName, "tamper");
            var store = new ReviewHistoryStore(root.FullName);
            store.Commit(manifest, status, progress, []);
            var path = Path.Combine(store.HistoryPath, manifest.RunId + ".json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var tampered = document.RootElement.GetRawText().Replace("\"state\": \"done\"", "\"state\": \"failed\"", StringComparison.Ordinal);
            File.WriteAllText(path, tampered);

            Assert.Empty(store.LoadAll());
            Assert.Throws<ReviewHistoryConflictException>(() => store.Commit(manifest, status, progress, []));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void ActiveDirectoryCanBeDeletedWithoutRemovingCommittedHistory()
    {
        var root = Directory.CreateTempSubdirectory("review-history-active-delete-");
        try
        {
            var (manifest, status, _) = TerminalRun(root.FullName, "active-delete");
            var active = new ReviewRunStore(root.FullName);
            active.Create(manifest, status);
            Directory.Delete(active.RunsPath, recursive: true);

            var history = Assert.Single(new ReviewHistoryStore(root.FullName).LoadAll());
            Assert.Equal(manifest.RunId, history.Run.RunId);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void OperationEvidenceCapturesOnlyTypedReferencesHashesAndRoutes()
    {
        var root = Directory.CreateTempSubdirectory("review-operation-evidence-");
        try
        {
            var metadata = Path.Combine(root.FullName, "Subject.review-meta.code.json");
            var fingerprint = "sha256:" + new string('d', 64);
            File.WriteAllText(metadata, $$"""
                {
                  "reviewer": {
                    "runId": "operation-1",
                    "requested": { "model": null, "thinkingLevel": "medium" },
                    "executed": { "cli": "codex", "model": "gpt-5.1-codex", "thinkingLevel": "high" }
                  },
                  "findings": [{
                    "fingerprint": "{{fingerprint}}",
                    "aspect": "correctness",
                    "evidence": [{ "class": "source-span" }],
                    "reproduction": { "status": "specified" },
                    "origin": { "operationRunId": "operation-1" }
                  }]
                }
                """);
            var review = new ReviewResult(metadata, "sha256:" + new string('a', 64), "operation-1",
                new ResolvedInputs("code", "file", 1, 0, [], []),
                new ReviewUsageEntry("operation-1", DateTimeOffset.UtcNow, "gpt-5.1-codex", "codex",
                    new TokenUsage(1, 1, 0, 0, 1), "code", "file", "Subject.cs"));

            var captured = new ReviewRunStore(root.FullName).CaptureOperationEvidence("Subject.cs", "done", review);
            Assert.Equal("Subject.review-meta.code.json", captured.MetaReference);
            Assert.StartsWith("sha256:", captured.MetaHash, StringComparison.Ordinal);
            Assert.Equal("gpt-5.1-codex", captured.Executed?.Model);
            Assert.Equal([fingerprint], captured.FindingFingerprintsByAspect["correctness"]);
            Assert.Equal(1, captured.EvidenceClasses["source-span"]);
            Assert.Equal(1, captured.ReproductionStatuses["specified"]);
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static (ReviewRunManifest Manifest, ReviewRunStatus Status, ReviewRunFileTransition[] Progress)
        TerminalRun(string root, string suffix)
    {
        var runId = "review-" + suffix;
        var now = new DateTimeOffset(2026, 8, 11, 9, 30, 0, TimeSpan.Zero);
        var manifest = new ReviewRunManifest(runId, "default",
            new ReviewRunPlanNode("file-a", "A.cs", "src/A.cs"), "file", "code", "gpt-5.1-codex",
            "codex", now, [new ReviewRunPlanTarget("file-a", "A.cs", "src/A.cs", "sha256:" + new string('a', 64))],
            null, RequestedModel: null, RequestedThinkingLevel: "high", RequestedCliType: "codex");
        var progress = new[] { new ReviewRunFileTransition("src/A.cs", "done", now, now.AddSeconds(2), runId, null) };
        var status = new ReviewRunStatus(runId, "done", 1, 1, 0, 1, now, now, now.AddSeconds(2),
            [$"{root}/private diagnostic was removed"], 0, new TokenUsage(0, 0, 0, 0, 2_000));
        return (manifest, status, progress);
    }
}
