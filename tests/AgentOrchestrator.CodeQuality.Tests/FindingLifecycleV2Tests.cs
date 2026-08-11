using System.Text.Json;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class FindingLifecycleV2Tests
{
    [Fact]
    public async Task Model_omission_does_not_resolve_and_explicit_policy_resolution_can_reopen()
    {
        var root = Directory.CreateTempSubdirectory("finding-lifecycle-v2-");
        var now = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
        try
        {
            var store = new FindingLifecycleStore(root.FullName, () => now);
            var observed = WithFinding(QualityTaxonomyContractTests.CreateObservation(new string('1', 64)), 'a');
            await store.ObserveAsync(observed, TestContext.Current.CancellationToken);

            var omittedByOtherModel = QualityTaxonomyContractTests.CreateObservation(new string('2', 64)) with
            {
                ObservedAt = now.AddMinutes(1),
                Producer = QualityTaxonomyContractTests.CreateObservation().Producer with
                {
                    EffectiveModel = "model-b",
                    RunId = "model-b-run",
                },
            };
            await store.ObserveAsync(omittedByOtherModel, TestContext.Current.CancellationToken);
            var afterOmission = await store.ReadAsync(now.AddMinutes(1), TestContext.Current.CancellationToken);
            Assert.Equal(QualityLifecycleState.Open, Assert.Single(afterOmission.ByIssueId).Value.State);

            now = now.AddMinutes(2);
            var resolved = await store.TransitionAsync(
                observed.Findings[0].IssueId,
                QualityLifecycleState.Resolved,
                "reconciler",
                "The versioned reconciliation policy established absence.",
                basisObservationIds: [observed.ObservationId, omittedByOtherModel.ObservationId],
                policyRef: "finding-reconciliation@1",
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(QualityLifecycleState.Resolved, resolved.State);

            var reappeared = WithFinding(QualityTaxonomyContractTests.CreateObservation(new string('3', 64)) with
            {
                ObservedAt = now.AddMinutes(1),
            }, 'a');
            var originalFinding = observed.Findings[0];
            var newOccurrence = "sha256:" + new string('d', 64);
            reappeared = reappeared with
            {
                Findings = [reappeared.Findings[0] with
                {
                    OccurrenceFingerprint = newOccurrence,
                    FingerprintAliases = ["sha256:" + new string('e', 64)],
                }],
            };
            await store.ObserveAsync(reappeared, TestContext.Current.CancellationToken);
            var reopened = await store.ReadAsync(now.AddMinutes(1), TestContext.Current.CancellationToken);
            var reopenedIssue = reopened.ByIssueId[originalFinding.IssueId];
            Assert.Equal(QualityLifecycleState.Open, reopenedIssue.State);
            Assert.Contains(originalFinding.OccurrenceFingerprint, reopenedIssue.FingerprintAliases);
            Assert.Contains(newOccurrence, reopenedIssue.FingerprintAliases);

            var legacy = await new FindingStateStore(root.FullName, () => now.AddMinutes(1))
                .ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(FindingState.Open, legacy[originalFinding.FingerprintAliases[0]].State);
            Assert.Equal(FindingState.Open, legacy[originalFinding.OccurrenceFingerprint].State);
            Assert.Equal(FindingState.Open, legacy[newOccurrence].State);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task Human_state_expiry_is_projected_open_and_event_replay_is_idempotent()
    {
        var root = Directory.CreateTempSubdirectory("finding-lifecycle-expiry-");
        var now = new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);
        try
        {
            var store = new FindingLifecycleStore(root.FullName, () => now);
            var observation = WithFinding(QualityTaxonomyContractTests.CreateObservation(new string('4', 64)), 'b');
            await store.ObserveAsync(observation, TestContext.Current.CancellationToken);
            var accepted = await store.TransitionAsync(
                observation.Findings[0].IssueId,
                QualityLifecycleState.AcceptedRisk,
                "Ada",
                "Accepted for the release window.",
                expiresAt: now.AddDays(1),
                eventId: "lifecycle-human-acceptance",
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(await store.AppendAsync(accepted, TestContext.Current.CancellationToken));
            var before = await store.ReadAsync(now.AddHours(12), TestContext.Current.CancellationToken);
            var after = await store.ReadAsync(now.AddDays(2), TestContext.Current.CancellationToken);

            Assert.Equal(QualityLifecycleState.AcceptedRisk, before.ByIssueId[accepted.IssueId].State);
            Assert.Equal(QualityLifecycleState.Open, after.ByIssueId[accepted.IssueId].State);
            Assert.Contains("expired", after.ByIssueId[accepted.IssueId].Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, File.ReadLines(store.Path).Count(line => !string.IsNullOrWhiteSpace(line)));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void Agent_sensor_and_human_sources_and_evidence_references_round_trip()
    {
        var baseObservation = QualityTaxonomyContractTests.CreateObservation(new string('5', 64));
        var evidence = new QualityEvidence("ev-1", QualityEvidenceKind.SourceCode,
            new QualityEvidenceLocator("src/A.cs", StartLine: 1, EndLine: 1),
            "Evidence for all producer forms.", "sha256:" + new string('e', 64), null, null,
            QualityObservationJson.NoExtensions);
        var findings = new[]
        {
            Finding('a', QualityProducerKind.Agent, "self"),
            Finding('b', QualityProducerKind.DeterministicSensor, "gitleaks"),
            Finding('c', QualityProducerKind.Human, "reviewer:ada"),
        };
        var observation = baseObservation with { Evidence = [evidence], Findings = findings };

        var loaded = QualityObservationJson.Deserialize(QualityObservationJson.Serialize(observation));

        Assert.Equal(
            new[] { QualityProducerKind.Agent, QualityProducerKind.DeterministicSensor, QualityProducerKind.Human },
            loaded.Findings.Select(item => item.Source.Kind));
        Assert.All(loaded.Findings, finding => Assert.Equal(["ev-1"], finding.EvidenceRefs));
    }

    [Fact]
    public async Task Partial_lifecycle_line_does_not_hide_a_later_valid_event()
    {
        var root = Directory.CreateTempSubdirectory("finding-lifecycle-partial-");
        try
        {
            var store = new FindingLifecycleStore(root.FullName);
            var observation = WithFinding(QualityTaxonomyContractTests.CreateObservation(new string('6', 64)), 'f');
            await store.ObserveAsync(observation, TestContext.Current.CancellationToken);
            await File.AppendAllTextAsync(store.Path, "{\"eventId\":\n", TestContext.Current.CancellationToken);
            var replay = observation with
            {
                ObservationId = "observation-sha256:" + new string('7', 64),
                Findings = [observation.Findings[0] with
                {
                    IssueId = "issue-sha256:" + new string('8', 64),
                }],
            };

            await store.ObserveAsync(replay, TestContext.Current.CancellationToken);
            var projection = await store.ReadAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, projection.ByIssueId.Count);
            Assert.Equal(1, projection.MalformedLines);
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static QualityObservation WithFinding(QualityObservation observation, char seed)
    {
        var evidence = new QualityEvidence("ev-1", QualityEvidenceKind.SourceCode,
            new QualityEvidenceLocator("src/A.cs", StartLine: 1, EndLine: 1),
            "The issue occurs here.", null, null, null, QualityObservationJson.NoExtensions);
        return observation with { Evidence = [evidence], Findings = [Finding(seed, QualityProducerKind.Agent, "self")] };
    }

    private static QualityObservationFinding Finding(char seed, QualityProducerKind source, string producerRef)
    {
        var hash = new string(seed, 64);
        return new QualityObservationFinding(
            "of-" + seed,
            "issue-sha256:" + hash,
            "sha256:" + new string((char)(seed + 1), 64),
            QualityObservationIdentity.FingerprintAlgorithm,
            ["sha256:" + new string((char)(seed + 2), 64)],
            "built-in/code.correctness.test@1",
            "code.correctness",
            FindingSeverity.High,
            "Issue " + seed,
            "The issue is evidenced.",
            "Fix the issue.",
            ["ev-1"],
            new QualityFindingSource(source, producerRef),
            QualityObservationJson.NoExtensions);
    }
}
