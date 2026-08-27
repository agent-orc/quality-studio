using System.Diagnostics;
using AgentOrchestrator.CodeQuality;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// Covers the snapshot-bound security preflight: security sensors run once per sweep at repository
/// scope and every reviewed subject is served from that one collection.
/// </summary>
public sealed class SecurityPreflightTests
{
    [Fact]
    public async Task RepositoryCollection_RunsEachSensorOnceRegardlessOfSubjectCount()
    {
        var sensor = new CountingSensor(SecretIn("src/a.cs"), SecretIn("src/b.cs"));
        var collector = new SecurityEvidenceCollector(new SensorRegistry([sensor]));

        var repository = await collector.CollectRepositoryAsync(
            ".", [new ReviewSensorConfiguration(sensor.Id)], TestContext.Current.CancellationToken);

        var subjects = Enumerable.Range(0, 92).Select(index => $"src/file{index}.cs").ToArray();
        foreach (var subject in subjects) SecurityEvidenceProjection.ForSubjects(repository, [subject]);

        Assert.Equal(1, sensor.Runs);
        Assert.Equal(2, repository.Sensors.Single().Findings.Count);
    }

    [Fact]
    public async Task PerSubjectCollection_RunsTheSensorOncePerSubject()
    {
        // Documents the behaviour the preflight replaces: without a shared snapshot every subject
        // triggers its own repository-wide scan.
        var sensor = new CountingSensor(SecretIn("src/a.cs"));
        var collector = new SecurityEvidenceCollector(new SensorRegistry([sensor]));

        foreach (var subject in new[] { "src/a.cs", "src/b.cs", "src/c.cs" })
        {
            await collector.CollectAsync(".", [subject], [new ReviewSensorConfiguration(sensor.Id)],
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(3, sensor.Runs);
    }

    [Fact]
    public async Task Projection_KeepsOnlyFindingsLocatedInTheSubject()
    {
        var repository = await CollectAsync(SecretIn("src/a.cs"), SecretIn("src/b.cs"));

        var projected = SecurityEvidenceProjection.ForSubjects(repository, ["src/a.cs"]);

        var finding = Assert.Single(projected.Sensors.Single().Findings);
        Assert.Equal("src/a.cs", Assert.Single(finding.Locations).Path);
        Assert.Equal(SecurityEvidenceVerdict.Block, projected.Verdict);
    }

    [Fact]
    public async Task Projection_LeavesSubjectsWithoutFindingsClean()
    {
        var repository = await CollectAsync(SecretIn("src/a.cs"));

        var projected = SecurityEvidenceProjection.ForSubjects(repository, ["src/unrelated.cs"]);

        Assert.Empty(projected.Sensors.Single().Findings);
        Assert.Equal(SecurityEvidenceVerdict.Pass, projected.Verdict);
    }

    [Fact]
    public async Task Projection_KeepsAnUnavailableSensorUnavailableForEverySubject()
    {
        var sensor = new CountingSensor { Available = false, UnavailableReason = "gitleaks is offline" };
        var repository = await new SecurityEvidenceCollector(new SensorRegistry([sensor]))
            .CollectRepositoryAsync(".", [new ReviewSensorConfiguration(sensor.Id)],
                TestContext.Current.CancellationToken);

        var projected = SecurityEvidenceProjection.ForSubjects(repository, ["src/unrelated.cs"]);

        // An unavailable check has no findings to project; it must not read as a clean subject.
        Assert.Empty(projected.Sensors.Single().Findings);
        Assert.Equal(SecurityEvidenceVerdict.Unavailable, projected.Sensors.Single().Verdict);
        Assert.Equal(SecurityEvidenceVerdict.Unavailable, projected.Verdict);
        Assert.Equal("gitleaks is offline", projected.Sensors.Single().UnavailableReason);
    }

    [Fact]
    public async Task Projection_HashesWhatTheSubjectActuallySees()
    {
        var repository = await CollectAsync(SecretIn("src/a.cs"), SecretIn("src/b.cs"));

        var first = SecurityEvidenceProjection.ForSubjects(repository, ["src/a.cs"]).Sensors.Single();
        var second = SecurityEvidenceProjection.ForSubjects(repository, ["src/b.cs"]).Sensors.Single();

        Assert.Matches("^sha256:[a-f0-9]{64}$", first.ResultHash);
        Assert.NotEqual(repository.Sensors.Single().ResultHash, first.ResultHash);
        Assert.NotEqual(first.ResultHash, second.ResultHash);
    }

    [Fact]
    public async Task Projection_MatchesSingleShotCollectionForTheSameSubject()
    {
        var sensor = new CountingSensor(SecretIn("src/a.cs"), SecretIn("src/b.cs"));
        var collector = new SecurityEvidenceCollector(new SensorRegistry([sensor]));
        var configuration = new[] { new ReviewSensorConfiguration(sensor.Id) };

        var direct = await collector.CollectAsync(".", ["src/a.cs"], configuration,
            TestContext.Current.CancellationToken);
        var projected = SecurityEvidenceProjection.ForSubjects(
            await collector.CollectRepositoryAsync(".", configuration, TestContext.Current.CancellationToken),
            ["src/a.cs"]);

        Assert.Equal(direct.Verdict, projected.Verdict);
        Assert.Equal(direct.Sensors.Single().ResultHash, projected.Sensors.Single().ResultHash);
    }

    [Fact]
    public void Snapshot_StopsMatchingWhenTheSourceIsMutated()
    {
        WithGitRepository(root =>
        {
            var before = SecurityPreflightSnapshot.FingerprintSource(root);
            Assert.Equal(before, SecurityPreflightSnapshot.FingerprintSource(root));

            File.WriteAllText(Path.Combine(root, "src", "a.cs"), "internal static class A { int x; }\n");

            Assert.NotEqual(before, SecurityPreflightSnapshot.FingerprintSource(root));
        });
    }

    [Fact]
    public void Snapshot_SurvivesTheReviewWritingItsOwnArtifacts()
    {
        WithGitRepository(root =>
        {
            var before = SecurityPreflightSnapshot.FingerprintSource(root);

            // What a sweep itself produces while reviewing: sidecars next to the subject and run
            // artifacts under .quality/. Neither is source, so neither may invalidate the snapshot.
            File.WriteAllText(Path.Combine(root, "src", "a.cs.review-meta.security.json"), "{}\n");
            Directory.CreateDirectory(Path.Combine(root, ".quality", "runs"));
            File.WriteAllText(Path.Combine(root, ".quality", "runs", "status.json"), "{}\n");
            // Sidecars are written to a .quality directory beside the subject, which git reports as
            // an untracked directory rather than as the files inside it.
            Directory.CreateDirectory(Path.Combine(root, "src", ".quality", "reviews", "file"));
            File.WriteAllText(
                Path.Combine(root, "src", ".quality", "reviews", "file", "a.review-meta.security.json"), "{}\n");

            Assert.Equal(before, SecurityPreflightSnapshot.FingerprintSource(root));

            File.WriteAllText(Path.Combine(root, "src", "b.cs"), "internal static class B { int y; }\n");

            Assert.NotEqual(before, SecurityPreflightSnapshot.FingerprintSource(root));
        });
    }

    [Fact]
    public void Snapshot_StopsMatchingWhenTheSensorConfigurationChanges()
    {
        var snapshot = new SecurityPreflightSnapshot(
            SecurityEvidenceBundle.Empty,
            "sha256:source",
            SecurityPreflightSnapshot.FingerprintConfiguration([new ReviewSensorConfiguration("gitleaks")]),
            DateTimeOffset.UnixEpoch);

        Assert.True(snapshot.Matches("sha256:source",
            SecurityPreflightSnapshot.FingerprintConfiguration([new ReviewSensorConfiguration("gitleaks")])));
        Assert.False(snapshot.Matches("sha256:source",
            SecurityPreflightSnapshot.FingerprintConfiguration(
                [new ReviewSensorConfiguration("gitleaks"), new ReviewSensorConfiguration("dependencies")])));
        Assert.False(snapshot.Matches("sha256:mutated",
            SecurityPreflightSnapshot.FingerprintConfiguration([new ReviewSensorConfiguration("gitleaks")])));
    }

    [Fact]
    public void ConfigurationFingerprint_IgnoresOrderAndDuplicates()
    {
        var one = SecurityPreflightSnapshot.FingerprintConfiguration(
            [new ReviewSensorConfiguration("gitleaks"), new ReviewSensorConfiguration("dependencies")]);
        var other = SecurityPreflightSnapshot.FingerprintConfiguration(
            [new ReviewSensorConfiguration("dependencies"), new ReviewSensorConfiguration("gitleaks"),
             new ReviewSensorConfiguration("gitleaks")]);

        Assert.Equal(one, other);
    }

    [Fact]
    public void ConfigurationFingerprint_TracksSensorSettings()
    {
        var one = SecurityPreflightSnapshot.FingerprintConfiguration([
            new ReviewSensorConfiguration("gitleaks", new Dictionary<string, string> { ["mode"] = "repository" })]);
        var other = SecurityPreflightSnapshot.FingerprintConfiguration([
            new ReviewSensorConfiguration("gitleaks", new Dictionary<string, string> { ["mode"] = "staged" })]);

        Assert.NotEqual(one, other);
    }

    [Fact]
    public void SourceFingerprint_IsNeverReusableOutsideAGitWorkingTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-preflight-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Without git the source state cannot be verified, so no snapshot may be reused.
            Assert.NotEqual(
                SecurityPreflightSnapshot.FingerprintSource(root),
                SecurityPreflightSnapshot.FingerprintSource(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Review_ServesSubjectsFromTheSnapshotWithoutRunningSensorsAgain()
    {
        await WithGitRepositoryAsync(async root =>
        {
            var sensor = new CountingSensor(SecretIn("src/a.cs"));
            var registry = new SensorRegistry([sensor]);
            var configuration = new[] { new ReviewSensorConfiguration(sensor.Id) };
            var snapshot = new SecurityPreflightSnapshot(
                await new SecurityEvidenceCollector(registry)
                    .CollectRepositoryAsync(root, configuration, TestContext.Current.CancellationToken),
                SecurityPreflightSnapshot.FingerprintSource(root),
                SecurityPreflightSnapshot.FingerprintConfiguration(configuration),
                DateTimeOffset.UtcNow);
            Assert.Equal(1, sensor.Runs);

            foreach (var subject in new[] { "src/a.cs", "src/b.cs", "src/c.cs" })
            {
                await new ReviewRunner(new FakeReviewAgent(), sensorRegistry: registry).ReviewAsync(
                    new ReviewRequest(subject, "security", RepositoryRoot: root,
                        Sensors: configuration, SecurityPreflight: snapshot),
                    TestContext.Current.CancellationToken);
            }

            Assert.Equal(1, sensor.Runs);
        });
    }

    [Fact]
    public async Task Review_CollectsFreshEvidenceWhenTheSnapshotNoLongerDescribesTheSource()
    {
        await WithGitRepositoryAsync(async root =>
        {
            var sensor = new CountingSensor(SecretIn("src/a.cs"));
            var registry = new SensorRegistry([sensor]);
            var configuration = new[] { new ReviewSensorConfiguration(sensor.Id) };
            var stale = new SecurityPreflightSnapshot(
                SecurityEvidenceBundle.Empty,
                "sha256:a-source-state-that-no-longer-exists",
                SecurityPreflightSnapshot.FingerprintConfiguration(configuration),
                DateTimeOffset.UtcNow);

            await new ReviewRunner(new FakeReviewAgent(), sensorRegistry: registry).ReviewAsync(
                new ReviewRequest("src/a.cs", "security", RepositoryRoot: root,
                    Sensors: configuration, SecurityPreflight: stale),
                TestContext.Current.CancellationToken);

            Assert.Equal(1, sensor.Runs);
        });
    }

    private static async Task<SecurityEvidenceBundle> CollectAsync(params ReviewFinding[] findings)
    {
        var sensor = new CountingSensor(findings);
        return await new SecurityEvidenceCollector(new SensorRegistry([sensor]))
            .CollectRepositoryAsync(".", [new ReviewSensorConfiguration(sensor.Id)],
                TestContext.Current.CancellationToken);
    }

    private static ReviewFinding SecretIn(string path) => new(
        $"gitleaks-{path}",
        "secrets",
        FindingSeverity.High,
        "Planted test secret",
        "Gitleaks detected a high-confidence test secret.",
        "Remove and rotate the credential.",
        [new FindingLocation(path, new FindingRange(new FindingPosition(1, 1), new FindingPosition(1, 8)))],
        "sha256:" + path.GetHashCode(StringComparison.Ordinal).ToString("x8").PadLeft(64, '0'),
        "generic-api-key",
        Source: new FindingSource(FindingSourceKind.Deterministic, "gitleaks", "gitleaks", "8.24.2"));

    private static void WithGitRepository(Action<string> test) =>
        WithGitRepositoryAsync(root =>
        {
            test(root);
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();

    private static async Task WithGitRepositoryAsync(Func<string, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-preflight-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        foreach (var name in new[] { "a.cs", "b.cs", "c.cs" })
        {
            await File.WriteAllTextAsync(Path.Combine(root, "src", name),
                $"internal static class {Path.GetFileNameWithoutExtension(name).ToUpperInvariant()} {{ }}\n",
                TestContext.Current.CancellationToken);
        }
        Git(root, "init", "--quiet");
        Git(root, "config", "user.email", "tests@quality-studio.invalid");
        Git(root, "config", "user.name", "Quality Studio Tests");
        Git(root, "add", ".");
        Git(root, "commit", "--quiet", "-m", "test fixture");
        try
        {
            await test(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void Git(string root, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
    }

    /// <summary>Counts how often the sweep actually invokes the sensor.</summary>
    private sealed class CountingSensor(params ReviewFinding[] findings) : IReviewSensor
    {
        private int _runs;

        public bool Available { get; init; } = true;

        public string? UnavailableReason { get; init; }

        public int Runs => Volatile.Read(ref _runs);

        public string Id => "gitleaks";

        public string Version => "8.24.2";

        public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

        public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorAvailability(Available, UnavailableReason,
                new Dictionary<string, string> { ["gitleaks"] = Version }));

        public Task<SensorScanResult> RunAsync(
            SensorScanRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _runs);
            return Task.FromResult(new SensorScanResult(
                Available,
                UnavailableReason,
                Available ? findings : [],
                new SensorProvenance(Id, Version, "repository", ".", "2026-07-25T10:00:00.000Z",
                    new Dictionary<string, string> { ["gitleaks"] = Version })));
        }
    }

    private sealed class FakeReviewAgent : IReviewAgent
    {
        public string AgentName => "test-agent";

        public string? Model => "deterministic";

        public Task<ReviewAgentResult> RunAsync(
            string prompt,
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewAgentResult("run-test",
                $"```json\n{ReviewResponseParserTests.ValidResponse}\n```",
                new TokenUsage(120, 34, 56, 7, 890), "deterministic"));
    }
}
