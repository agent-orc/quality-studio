using System.Diagnostics;
using AgentOrchestrator.CodeQuality;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class SecurityPreflightFingerprintTests
{
    [Fact]
    public void Compute_IsStableWhenOnlyReviewSidecarsChange()
    {
        using var fixture = new GitFixture();
        fixture.Commit("initial", ("src/Small.cs", "class Small {}\n"));
        var before = SecurityPreflightFingerprint.Compute(fixture.Root);

        Directory.CreateDirectory(Path.Combine(fixture.Root, "src", ".quality", "reviews"));
        File.WriteAllText(
            Path.Combine(fixture.Root, "src", ".quality", "reviews", "state.json"), "{}");
        File.WriteAllText(
            Path.Combine(fixture.Root, "src", "Small.review-meta.security.json"), "{}");

        var after = SecurityPreflightFingerprint.Compute(fixture.Root);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Compute_ChangesWhenTrackedSourceChanges()
    {
        using var fixture = new GitFixture();
        fixture.Commit("initial", ("src/Small.cs", "class Small {}\n"));
        var before = SecurityPreflightFingerprint.Compute(fixture.Root);

        File.WriteAllText(Path.Combine(fixture.Root, "src", "Small.cs"), "class Small { void M() {} }\n");

        var after = SecurityPreflightFingerprint.Compute(fixture.Root);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Compute_ChangesWhenUntrackedSourceFileIsAdded()
    {
        using var fixture = new GitFixture();
        fixture.Commit("initial", ("src/Small.cs", "class Small {}\n"));
        var before = SecurityPreflightFingerprint.Compute(fixture.Root);

        File.WriteAllText(Path.Combine(fixture.Root, "src", "New.cs"), "class New {}\n");

        var after = SecurityPreflightFingerprint.Compute(fixture.Root);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Compute_OutsideGitRepository_ReturnsNonRepeatingUnverifiableFingerprint()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-nogit-").FullName;
        try
        {
            var first = SecurityPreflightFingerprint.Compute(root);
            var second = SecurityPreflightFingerprint.Compute(root);
            Assert.StartsWith("unverifiable:", first, StringComparison.Ordinal);
            Assert.StartsWith("unverifiable:", second, StringComparison.Ordinal);
            Assert.NotEqual(first, second);
        }
        finally
        {
            TestDirectory.Delete(root);
        }
    }

    private sealed class GitFixture : IDisposable
    {
        public GitFixture()
        {
            Root = Directory.CreateTempSubdirectory("quality-studio-preflight-").FullName;
            Directory.CreateDirectory(Path.Combine(Root, "src"));
            Run("init", "--quiet");
            Run("config", "user.email", "fixture@example.test");
            Run("config", "user.name", "Fixture");
        }

        public string Root { get; }

        public void Commit(string message, params (string Path, string Content)[] files)
        {
            foreach (var (path, content) in files)
            {
                var full = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }
            Run("add", ".");
            Run("commit", "--quiet", "-m", message);
        }

        private void Run(params string[] arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git")
                {
                    WorkingDirectory = Root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }

        public void Dispose() => TestDirectory.Delete(Root);
    }
}

public sealed class SecurityPreflightSnapshotTests
{
    [Fact]
    public void Matches_TrueWhenFingerprintAndSensorSetAgree()
    {
        var snapshot = new SecurityPreflightSnapshot(
            "sha256:aaaa", ["dependency-audit", "gitleaks"], SecurityEvidenceBundle.Empty);

        Assert.True(snapshot.Matches("sha256:aaaa",
            [new ReviewSensorConfiguration("gitleaks"), new ReviewSensorConfiguration("dependency-audit")]));
    }

    [Fact]
    public void Matches_FalseWhenFingerprintDiffers()
    {
        var snapshot = new SecurityPreflightSnapshot("sha256:aaaa", ["gitleaks"], SecurityEvidenceBundle.Empty);
        Assert.False(snapshot.Matches("sha256:bbbb", [new ReviewSensorConfiguration("gitleaks")]));
    }

    [Fact]
    public void Matches_FalseWhenRequestedSensorSetDiffers()
    {
        var snapshot = new SecurityPreflightSnapshot("sha256:aaaa", ["gitleaks"], SecurityEvidenceBundle.Empty);
        Assert.False(snapshot.Matches("sha256:aaaa",
            [new ReviewSensorConfiguration("gitleaks"), new ReviewSensorConfiguration("dependency-audit")]));
    }

    [Fact]
    public void Matches_FalseWhenSnapshotFingerprintIsUnverifiable()
    {
        var snapshot = new SecurityPreflightSnapshot(
            "unverifiable:1234", ["gitleaks"], SecurityEvidenceBundle.Empty);
        Assert.False(snapshot.Matches("unverifiable:1234", [new ReviewSensorConfiguration("gitleaks")]));
    }
}

public sealed class SecurityEvidenceProjectionTests
{
    [Fact]
    public void ForSubjects_DropsFindingsOutsideSubjectAndRecomputesVerdict()
    {
        var blocking = new ReviewFinding(
            "gitleaks-a", "secrets", FindingSeverity.High, "Secret in A", "desc", "rotate",
            [new FindingLocation("src/A.cs", new FindingRange(new FindingPosition(1, 1), new FindingPosition(1, 5)))],
            "sha256:" + new string('a', 64), "generic-api-key");
        var repositoryBundle = new SecurityEvidenceBundle(
            SecurityEvidenceVerdict.Block,
            [new SecuritySensorEvidence(
                "gitleaks", "8.24.2", "sha256:" + new string('c', 64), true, null,
                SecurityEvidenceVerdict.Block, new Dictionary<string, string>(), [blocking])]);

        var projectedForB = SecurityEvidenceProjection.ForSubjects(repositoryBundle, ["src/B.cs"]);
        Assert.Equal(SecurityEvidenceVerdict.Pass, projectedForB.Verdict);
        Assert.Empty(Assert.Single(projectedForB.Sensors).Findings);

        var projectedForA = SecurityEvidenceProjection.ForSubjects(repositoryBundle, ["src/A.cs"]);
        Assert.Equal(SecurityEvidenceVerdict.Block, projectedForA.Verdict);
        var sensor = Assert.Single(projectedForA.Sensors);
        Assert.Single(sensor.Findings);
        Assert.Equal("sha256:" + new string('c', 64), sensor.ResultHash);
    }
}

public sealed class SecurityPreflightReuseTests
{
    [Fact]
    public async Task SecurityReview_ReusesPreflightSnapshotAcrossFilesWithoutRescanning()
    {
        await using var fixture = new PreflightFixture();
        fixture.Commit();
        var sensor = CountingSensor.BlockingSecretOn("src/A.cs");
        var registry = new SensorRegistry([sensor]);
        var configurations = new ReviewSensorConfiguration[] { new(sensor.Id) };
        var fingerprint = SecurityPreflightFingerprint.Compute(fixture.Root);
        var repositoryBundle = await new SecurityEvidenceCollector(registry)
            .CollectRepositoryAsync(fixture.Root, configurations, TestContext.Current.CancellationToken);
        var preflight = new SecurityPreflightSnapshot(fingerprint, ["gitleaks"], repositoryBundle);
        Assert.Equal(1, sensor.RunCount);

        var runner = new ReviewRunner(new FakeAgent(), sensorRegistry: registry);
        var resultA = await runner.ReviewAsync(new ReviewRequest(
            "src/A.cs", "security", RepositoryRoot: fixture.Root,
            Sensors: configurations, SecurityPreflight: preflight), TestContext.Current.CancellationToken);
        var resultB = await runner.ReviewAsync(new ReviewRequest(
            "src/B.cs", "security", RepositoryRoot: fixture.Root,
            Sensors: configurations, SecurityPreflight: preflight), TestContext.Current.CancellationToken);

        Assert.Equal(1, sensor.RunCount);

        var metaA = await File.ReadAllTextAsync(resultA.MetaPath, TestContext.Current.CancellationToken);
        var metaB = await File.ReadAllTextAsync(resultB.MetaPath, TestContext.Current.CancellationToken);
        Assert.Contains("\"verdict\": \"block\"", metaA, StringComparison.Ordinal);
        Assert.DoesNotContain("gitleaks-secret-in-a", metaB, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecurityReview_CollectsFreshWhenSourceChangedAfterPreflightCapture()
    {
        await using var fixture = new PreflightFixture();
        fixture.Commit();
        var sensor = CountingSensor.BlockingSecretOn("src/A.cs");
        var registry = new SensorRegistry([sensor]);
        var configurations = new ReviewSensorConfiguration[] { new(sensor.Id) };
        var fingerprint = SecurityPreflightFingerprint.Compute(fixture.Root);
        var repositoryBundle = await new SecurityEvidenceCollector(registry)
            .CollectRepositoryAsync(fixture.Root, configurations, TestContext.Current.CancellationToken);
        var preflight = new SecurityPreflightSnapshot(fingerprint, ["gitleaks"], repositoryBundle);
        Assert.Equal(1, sensor.RunCount);

        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "src", "A.cs"), "class A { void M() {} }\n", TestContext.Current.CancellationToken);

        var runner = new ReviewRunner(new FakeAgent(), sensorRegistry: registry);
        await runner.ReviewAsync(new ReviewRequest(
            "src/A.cs", "security", RepositoryRoot: fixture.Root,
            Sensors: configurations, SecurityPreflight: preflight), TestContext.Current.CancellationToken);

        Assert.Equal(2, sensor.RunCount);
    }

    private sealed class PreflightFixture : IAsyncDisposable
    {
        public PreflightFixture()
        {
            Root = Directory.CreateTempSubdirectory("quality-studio-preflight-reuse-").FullName;
            Directory.CreateDirectory(Path.Combine(Root, "src"));
            File.WriteAllText(Path.Combine(Root, "src", "A.cs"), "class A {}\n");
            File.WriteAllText(Path.Combine(Root, "src", "B.cs"), "class B {}\n");
            Run("init", "--quiet");
            Run("config", "user.email", "fixture@example.test");
            Run("config", "user.name", "Fixture");
        }

        public string Root { get; }

        public void Commit()
        {
            Run("add", ".");
            Run("commit", "--quiet", "-m", "initial");
        }

        private void Run(params string[] arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git")
                {
                    WorkingDirectory = Root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }

        public ValueTask DisposeAsync()
        {
            TestDirectory.Delete(Root);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingSensor(string blockingPath) : IReviewSensor
    {
        public int RunCount { get; private set; }
        public string Id => "gitleaks";
        public string Version => "8.24.2";
        public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

        public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorAvailability(true, null, new Dictionary<string, string> { ["gitleaks"] = Version }));

        public Task<SensorScanResult> RunAsync(SensorScanRequest request, CancellationToken cancellationToken = default)
        {
            RunCount++;
            return Task.FromResult(new SensorScanResult(
                true,
                null,
                [new ReviewFinding(
                    "gitleaks-secret-in-a", "secrets", FindingSeverity.High, "Secret in A",
                    "Gitleaks detected a high-confidence test secret.", "Remove and rotate the credential.",
                    [new FindingLocation(blockingPath,
                        new FindingRange(new FindingPosition(1, 1), new FindingPosition(1, 8)))],
                    "sha256:" + new string('b', 64), "generic-api-key")],
                new SensorProvenance(
                    Id, Version, "repository", ".", "2026-08-28T10:00:00.000Z",
                    new Dictionary<string, string> { ["gitleaks"] = Version })));
        }

        public static CountingSensor BlockingSecretOn(string path) => new(path);
    }

    private sealed class FakeAgent : IReviewAgent
    {
        public string AgentName => "test-agent";
        public string? Model => "deterministic";

        public Task<ReviewAgentResult> RunAsync(string prompt, string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewAgentResult("run-test", $"```json\n{ReviewResponseParserTests.ValidResponse}\n```",
                new TokenUsage(120, 34, 56, 7, 890), Model));
    }
}
