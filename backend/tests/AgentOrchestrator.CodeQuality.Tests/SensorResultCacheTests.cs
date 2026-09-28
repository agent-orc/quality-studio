using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

[Trait("Category", "ToolBound")]
public sealed class SensorResultCacheTests
{
    [Fact]
    public async Task Second_run_on_the_same_commit_reuses_the_stored_result_without_running_the_sensor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = await CommittedRepositoryAsync(cancellationToken);
        try
        {
            var sensor = new CountingSensor("counting-check", deterministic: true);
            var first = await SensorResultCache.OpenAsync(root, cancellationToken: cancellationToken);
            var ran = await first.GetOrRunAsync(sensor, Request(root), cancellationToken);
            var second = await SensorResultCache.OpenAsync(root, cancellationToken: cancellationToken);
            var reused = await second.GetOrRunAsync(sensor, Request(root), cancellationToken);

            Assert.Equal(1, sensor.Runs);
            Assert.Equal(SensorOutcome.Ran, Assert.Single(first.Executions).Outcome);
            Assert.Equal(SensorOutcome.Cached, Assert.Single(second.Executions).Outcome);
            Assert.Equal(ran.Provenance.ScannedAt, reused.Provenance.ScannedAt);
            Assert.Equal(Assert.Single(ran.Findings).Fingerprint, Assert.Single(reused.Findings).Fingerprint);
            Assert.Equal(FindingSourceKind.Deterministic, reused.Findings[0].Source!.Kind);
            Assert.NotNull(SensorResultCache.LastObservedDurationMs(root, sensor.Id));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task A_new_commit_an_uncommitted_edit_or_a_new_configuration_runs_the_sensor_again()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = await CommittedRepositoryAsync(cancellationToken);
        try
        {
            var sensor = new CountingSensor("counting-check", deterministic: true);
            async Task RunAsync(IReadOnlyDictionary<string, string>? configuration = null) =>
                await (await SensorResultCache.OpenAsync(root, cancellationToken: cancellationToken))
                    .GetOrRunAsync(sensor, Request(root, configuration), cancellationToken);

            await RunAsync();
            await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), "class Sample { int x; }", cancellationToken);
            await RunAsync();
            Assert.Equal(2, sensor.Runs);
            await RunAsync();
            Assert.Equal(2, sensor.Runs);

            await File.WriteAllTextAsync(Path.Combine(root, "Untracked.cs"), "class Untracked { }", cancellationToken);
            await RunAsync();
            Assert.Equal(3, sensor.Runs);

            await GitTestRepository.RunAsync(root, cancellationToken, "add", ".");
            await GitTestRepository.RunAsync(root, cancellationToken, "commit", "--quiet", "-m", "second");
            await RunAsync();
            Assert.Equal(4, sensor.Runs);

            await RunAsync(new Dictionary<string, string> { ["target"] = "Other.sln" });
            Assert.Equal(5, sensor.Runs);
            await RunAsync(new Dictionary<string, string> { ["target"] = "Other.sln" });
            Assert.Equal(5, sensor.Runs);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task A_root_below_the_git_top_level_hashes_its_own_edits_and_ignores_sibling_edits()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var top = Directory.CreateTempSubdirectory("quality-sensor-cache-nested-").FullName;
        var root = Path.Combine(top, "apps", "web");
        try
        {
            await GitTestRepository.InitializeAsync(top, cancellationToken);
            Directory.CreateDirectory(Path.Combine(root, "src"));
            await File.WriteAllTextAsync(Path.Combine(root, "src", "Sample.cs"), "class Sample { }", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(top, "Sibling.cs"), "class Sibling { }", cancellationToken);
            await GitTestRepository.RunAsync(top, cancellationToken, "add", ".");
            await GitTestRepository.RunAsync(top, cancellationToken, "commit", "--quiet", "-m", "initial");
            async Task<SensorInputFingerprint> FingerprintAsync() =>
                (await SensorInputFingerprint.ComputeAsync(root, cancellationToken))!;

            var clean = await FingerprintAsync();
            await File.WriteAllTextAsync(Path.Combine(root, "src", "Sample.cs"), "class Sample { int x; }", cancellationToken);
            var firstEdit = await FingerprintAsync();
            await File.WriteAllTextAsync(Path.Combine(root, "src", "Sample.cs"), "class Sample { int y; }", cancellationToken);
            var secondEdit = await FingerprintAsync();

            // Two different contents of the same dirty file must differ; a path resolved against the wrong
            // directory would read both as "deleted" and let the first edit's result stand for the second.
            Assert.Equal(0, clean.DirtyFiles);
            Assert.Equal(1, firstEdit.DirtyFiles);
            Assert.NotEqual(clean.Value, firstEdit.Value);
            Assert.NotEqual(firstEdit.Value, secondEdit.Value);

            await File.WriteAllTextAsync(Path.Combine(top, "Sibling.cs"), "class Sibling { int z; }", cancellationToken);
            Assert.Equal(secondEdit, await FingerprintAsync());

            var sensor = new CountingSensor("counting-check", deterministic: true)
            {
                OnRun = () => File.WriteAllText(Path.Combine(
                    Directory.CreateDirectory(Path.Combine(root, ".quality", "preflight")).FullName,
                    "eslint.sarif"), Guid.NewGuid().ToString()),
            };
            async Task RunAsync() =>
                await (await SensorResultCache.OpenAsync(root, cancellationToken: cancellationToken))
                    .GetOrRunAsync(sensor, Request(root), cancellationToken);

            await RunAsync();
            await RunAsync();
            Assert.Equal(1, sensor.Runs);
            await File.WriteAllTextAsync(Path.Combine(root, "src", "Sample.cs"), "class Sample { int w; }", cancellationToken);
            await RunAsync();
            Assert.Equal(2, sensor.Runs);
            File.Delete(Path.Combine(root, "src", "Sample.cs"));
            await RunAsync();
            Assert.Equal(3, sensor.Runs);
        }
        finally
        {
            TemporaryDirectory.Delete(QualityDataRoot.For(root));
            Cleanup(top);
        }
    }

    [Fact]
    public async Task Refresh_runs_the_sensor_and_replaces_the_entry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = await CommittedRepositoryAsync(cancellationToken);
        try
        {
            var sensor = new CountingSensor("counting-check", deterministic: true);
            await (await SensorResultCache.OpenAsync(root, cancellationToken: cancellationToken))
                .GetOrRunAsync(sensor, Request(root), cancellationToken);
            var refreshing = await SensorResultCache.OpenAsync(root, refresh: true, cancellationToken);
            await refreshing.GetOrRunAsync(sensor, Request(root), cancellationToken);
            var after = await (await SensorResultCache.OpenAsync(root, cancellationToken: cancellationToken))
                .GetOrRunAsync(sensor, Request(root), cancellationToken);

            Assert.Equal(2, sensor.Runs);
            Assert.Equal(SensorOutcome.Ran, Assert.Single(refreshing.Executions).Outcome);
            Assert.Equal("scan-2", after.Provenance.ScannedAt);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Analyzer_reports_written_into_the_checkout_do_not_invalidate_the_entry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = await CommittedRepositoryAsync(cancellationToken);
        try
        {
            var sensor = new CountingSensor("counting-check", deterministic: true)
            {
                OnRun = () => File.WriteAllText(Path.Combine(
                    Directory.CreateDirectory(Path.Combine(root, ".quality", "preflight")).FullName,
                    "eslint.sarif"), Guid.NewGuid().ToString()),
            };
            await (await SensorResultCache.OpenAsync(root, cancellationToken: cancellationToken))
                .GetOrRunAsync(sensor, Request(root), cancellationToken);
            await (await SensorResultCache.OpenAsync(root, cancellationToken: cancellationToken))
                .GetOrRunAsync(sensor, Request(root), cancellationToken);

            Assert.Equal(1, sensor.Runs);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Unavailable_results_and_checkouts_without_a_commit_are_never_reused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = await CommittedRepositoryAsync(cancellationToken);
        var plain = Directory.CreateTempSubdirectory("quality-sensor-cache-plain-").FullName;
        try
        {
            var unavailable = new CountingSensor("missing-tool", deterministic: true) { Available = false };
            for (var attempt = 0; attempt < 2; attempt++)
                await (await SensorResultCache.OpenAsync(root, cancellationToken: cancellationToken))
                    .GetOrRunAsync(unavailable, Request(root), cancellationToken);
            Assert.Equal(2, unavailable.Runs);

            var sensor = new CountingSensor("counting-check", deterministic: true);
            var uncached = await SensorResultCache.OpenAsync(plain, cancellationToken: cancellationToken);
            Assert.Null(uncached.Fingerprint);
            await uncached.GetOrRunAsync(sensor, Request(plain), cancellationToken);
            await (await SensorResultCache.OpenAsync(plain, cancellationToken: cancellationToken))
                .GetOrRunAsync(sensor, Request(plain), cancellationToken);
            Assert.Equal(2, sensor.Runs);
            Assert.Contains("no Git commit", Assert.Single(uncached.Executions).Detail);
        }
        finally
        {
            Cleanup(root);
            Cleanup(plain);
        }
    }

    [Fact]
    public async Task Plan_holds_back_build_sensors_until_opted_in_and_reports_cache_hits_and_durations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = await CommittedRepositoryAsync(cancellationToken);
        try
        {
            var build = new CountingSensor("dotnet-build", deterministic: true);
            var lint = new CountingSensor("eslint", deterministic: true);
            var secrets = new CountingSensor("gitleaks", deterministic: false);
            var enabled = new (ReviewSensorConfiguration, IReviewSensor, string)[]
            {
                (new ReviewSensorConfiguration(build.Id), build, SensorRole.Deterministic),
                (new ReviewSensorConfiguration(lint.Id), lint, SensorRole.Deterministic),
                (new ReviewSensorConfiguration(secrets.Id), secrets, SensorRole.Security),
            };
            await (await SensorResultCache.OpenAsync(root, cancellationToken: cancellationToken))
                .GetOrRunAsync(lint, Request(root), cancellationToken);

            var withoutOptIn = SensorExecutionPlan.Build(root, enabled, null,
                await SensorResultCache.OpenAsync(root, cancellationToken: cancellationToken));
            var byId = withoutOptIn.Sensors.ToDictionary(entry => entry.SensorId);
            Assert.Equal(SensorOutcome.OptInRequired, byId["dotnet-build"].Decision);
            Assert.True(byId["dotnet-build"].OptIn);
            Assert.Equal(SensorOutcome.Cached, byId["eslint"].Decision);
            Assert.Equal(0, byId["eslint"].ExpectedDurationMs);
            Assert.Equal("run", byId["gitleaks"].Decision);
            Assert.Equal(SensorExecutionPolicy.DefaultDurationMs("gitleaks"), byId["gitleaks"].ExpectedDurationMs);
            Assert.Equal("default", byId["gitleaks"].DurationSource);
            Assert.Equal(SensorExecutionPolicy.DefaultDurationMs("gitleaks"), withoutOptIn.ExpectedDurationMs);

            var optedIn = SensorExecutionPlan.Build(root, enabled, ["DOTNET-BUILD"],
                await SensorResultCache.OpenAsync(root, cancellationToken: cancellationToken));
            var buildEntry = optedIn.Sensors.Single(entry => entry.SensorId == "dotnet-build");
            Assert.Equal("run", buildEntry.Decision);
            Assert.True(buildEntry.OptedIn);
            Assert.Equal(SensorExecutionPolicy.DefaultDurationMs("dotnet-build"), optedIn.ExpectedDurationMs);

            var refreshed = SensorExecutionPlan.Build(root, enabled, null,
                await SensorResultCache.OpenAsync(root, refresh: true, cancellationToken));
            var lintEntry = refreshed.Sensors.Single(entry => entry.SensorId == "eslint");
            Assert.Equal("run", lintEntry.Decision);
            Assert.Equal("observed", lintEntry.DurationSource);

            Assert.Equal(
                ["eslint", "gitleaks"],
                SensorExecutionPolicy.Admit(enabled.Select(item => item.Item1), null).Select(item => item.Id));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Security_results_collected_once_project_like_a_per_subject_collection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("quality-sensor-cache-security-").FullName;
        try
        {
            var sensor = new CountingSensor("gitleaks", deterministic: false);
            var registry = new SensorRegistry([sensor]);
            var configurations = new[] { new ReviewSensorConfiguration(sensor.Id), new ReviewSensorConfiguration("absent") };
            var collector = new SecurityEvidenceCollector(registry);
            var perSubject = await collector.CollectAsync(root, ["Sample.cs"], configurations, cancellationToken);
            var runsBefore = sensor.Runs;
            var shared = await collector.RunAsync(root, configurations, cancellationToken);
            var projected = SecurityEvidenceCollector.Project(shared, ["Sample.cs"]);
            var elsewhere = SecurityEvidenceCollector.Project(shared, ["Other.cs"]);

            Assert.Equal(runsBefore + 1, sensor.Runs);
            Assert.Equal(perSubject.Verdict, projected.Verdict);
            Assert.Equal(
                perSubject.Sensors.Select(item => (item.SensorId, item.Verdict, item.ResultHash)),
                projected.Sensors.Select(item => (item.SensorId, item.Verdict, item.ResultHash)));
            Assert.Equal(SecurityEvidenceVerdict.Unavailable, projected.Sensors.Single(item => item.SensorId == "absent").Verdict);
            Assert.Single(projected.Sensors.Single(item => item.SensorId == "gitleaks").Findings);
            Assert.Empty(elsewhere.Sensors.Single(item => item.SensorId == "gitleaks").Findings);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static SensorScanRequest Request(string root, IReadOnlyDictionary<string, string>? configuration = null) =>
        new(root, SensorScope.Repository, Configuration: configuration, PersistMetadata: false);

    private static async Task<string> CommittedRepositoryAsync(CancellationToken cancellationToken)
    {
        var root = Directory.CreateTempSubdirectory("quality-sensor-cache-").FullName;
        await GitTestRepository.InitializeAsync(root, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), "class Sample { }", cancellationToken);
        await GitTestRepository.RunAsync(root, cancellationToken, "add", ".");
        await GitTestRepository.RunAsync(root, cancellationToken, "commit", "--quiet", "-m", "initial");
        return root;
    }

    private static void Cleanup(string root)
    {
        TemporaryDirectory.Delete(QualityDataRoot.For(root));
        TemporaryDirectory.Delete(root);
    }

    private sealed class CountingSensor(string id, bool deterministic) : IDeterministicEvidenceSensor
    {
        private int runs;
        public string Id => id;
        public string Version => "1.0.0";
        public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];
        public int Runs => Volatile.Read(ref runs);
        public bool Available { get; init; } = true;
        public Action? OnRun { get; init; }

        public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorAvailability(Available));

        public Task<SensorScanResult> RunAsync(SensorScanRequest request, CancellationToken cancellationToken = default)
        {
            var run = Interlocked.Increment(ref runs);
            OnRun?.Invoke();
            var source = deterministic
                ? new FindingSource(FindingSourceKind.Deterministic, Id, Id, Version)
                : null;
            IReadOnlyList<ReviewFinding> findings = Available
                ?
                [
                    new ReviewFinding(
                        $"{Id}-finding", "security", FindingSeverity.High, "Test finding", "Found by the test sensor.",
                        "Fix it.", [new FindingLocation("Sample.cs")],
                        "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "TEST2001", Source: source),
                ]
                : [];
            return Task.FromResult(new SensorScanResult(
                Available, Available ? null : "tool missing", findings,
                new SensorProvenance(Id, Version, "repository", ".", $"scan-{run}", new Dictionary<string, string>())));
        }
    }
}
