using System.Text;
using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// The QS-113 profile fixes: Roslyn's per-project SARIF 2.1 logs, ESLint and tsc resolved from the
/// analysed repository, suppressed SARIF results, and probes that run where the analyzer will.
/// </summary>
public sealed class AnalyzerProfileResolutionTests
{
    [Fact]
    public void Roslyn_errorlog_import_escapes_the_version_comma_and_names_one_log_per_project()
    {
        var root = CreateRepository();
        try
        {
            var reports = Path.Combine(root, ".quality", "preflight", "roslyn");

            var targets = AnalyzerCommand.WriteRoslynErrorLogTargets(reports);

            var content = File.ReadAllText(targets);
            Assert.Equal(Path.Combine(reports, AnalyzerCommand.RoslynErrorLogTargetsFileName), targets);
            Assert.Contains(".sarif%2Cversion=2.1</ErrorLog>", content, StringComparison.Ordinal);
            Assert.DoesNotContain(".sarif,version", content, StringComparison.Ordinal);
            Assert.Contains("$(MSBuildProjectName)-", content, StringComparison.Ordinal);
            Assert.Contains("StableStringHash('$(MSBuildProjectFullPath)')", content, StringComparison.Ordinal);
            Assert.Contains("$(TargetFramework).sarif", content, StringComparison.Ordinal);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public void Roslyn_errorlog_import_escapes_msbuild_and_xml_characters_in_the_report_directory()
    {
        var root = CreateRepository();
        try
        {
            var reports = Path.Combine(root, "odd $(Dir) & 100%");

            var content = File.ReadAllText(AnalyzerCommand.WriteRoslynErrorLogTargets(reports));

            Assert.Contains("odd %24(Dir) &amp; 100%25", content, StringComparison.Ordinal);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public void The_roslyn_profile_forces_a_compile_and_hands_msbuild_the_generated_import()
    {
        var root = CreateRepository();
        try
        {
            Assert.True(AnalyzerProfileCatalog.BuiltIn.TryResolve("roslyn", "roslyn-build-sarif", out var profile));
            var reports = Path.Combine(root, ".quality", "preflight", "roslyn");

            var command = AnalyzerCommand.Expand(
                profile.Command, root, root, reports + Path.DirectorySeparatorChar, root, reports);

            Assert.Equal("dotnet", command[0]);
            Assert.Contains("--no-incremental", command);
            Assert.Contains(
                "-p:CustomAfterMicrosoftCommonTargets=" +
                Path.Combine(reports, AnalyzerCommand.RoslynErrorLogTargetsFileName),
                command);
            Assert.DoesNotContain(command, argument => argument.Contains("ErrorLog=", StringComparison.Ordinal));
            Assert.EndsWith("/", profile.ReportPath, StringComparison.Ordinal);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task A_report_directory_merges_every_project_log_and_ignores_logs_from_an_earlier_run()
    {
        var root = CreateRepository("src/A/A.cs", "src/B/B.cs");
        try
        {
            var reports = Path.Combine(root, ".quality", "preflight", "roslyn");
            Directory.CreateDirectory(reports);
            File.WriteAllText(Path.Combine(reports, "Stale-1-net10.0.sarif"), Log("CA9999", "src/A/A.cs", 1));
            var runner = new CallbackRunner((_, _, _) =>
            {
                File.WriteAllText(Path.Combine(reports, "A-1-net10.0.sarif"), Log("CA1822", "src/A/A.cs", 3));
                File.WriteAllText(Path.Combine(reports, "B-2-net10.0.sarif"), Log("CA2016", "src/B/B.cs", 7, suppressed: true));
                return new SensorCommandResult(0, string.Empty, string.Empty);
            });

            var result = await new RoslynAnalyzerSensor(runner).RunAsync(
                new SensorScanRequest(root, Configuration: Profile("roslyn-build-sarif")),
                TestContext.Current.CancellationToken);

            Assert.True(result.Available, result.UnavailableReason);
            var finding = Assert.Single(result.Findings);
            Assert.Equal("CA1822", finding.RuleId);
            Assert.Equal("src/A/A.cs", finding.Locations[0].Path);
            Assert.Equal(1, result.SuppressedFindings);
            Assert.False(File.Exists(Path.Combine(reports, "Stale-1-net10.0.sarif")));
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task A_failed_build_without_reported_errors_is_unavailable_rather_than_partially_clean()
    {
        var root = CreateRepository("src/A/A.cs");
        try
        {
            var reports = Path.Combine(root, ".quality", "preflight", "roslyn");
            var runner = new CallbackRunner((_, _, _) =>
            {
                Directory.CreateDirectory(reports);
                File.WriteAllText(Path.Combine(reports, "A-1-net10.0.sarif"), Log("CA1822", "src/A/A.cs", 3));
                return new SensorCommandResult(1, "error MSB4019: imported project not found", string.Empty);
            });

            var result = await new RoslynAnalyzerSensor(runner).RunAsync(
                new SensorScanRequest(root, Configuration: Profile("roslyn-build-sarif")),
                TestContext.Current.CancellationToken);

            Assert.False(result.Available);
            Assert.Contains("incomplete", result.UnavailableReason, StringComparison.Ordinal);
            Assert.Empty(result.Findings);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Theory]
    [InlineData("""[{"kind":"inSource"}]""", true)]
    [InlineData("""[{"kind":"external","status":"accepted"}]""", true)]
    [InlineData("""[{"kind":"inSource"},{"kind":"external","status":"underReview"}]""", false)]
    [InlineData("""[{"kind":"external","status":"rejected"}]""", false)]
    [InlineData("""[]""", false)]
    public async Task Sarif_suppressions_follow_the_sarif_status_rules(string suppressions, bool suppressed)
    {
        var root = CreateRepository("src/A.cs");
        try
        {
            var document = Log("CA1822", "src/A.cs", 3).Replace(
                "\"ruleId\":\"CA1822\"", $"\"ruleId\":\"CA1822\",\"suppressions\":{suppressions}", StringComparison.Ordinal);
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document));

            var parsed = await SarifSensor.ParseReportAsync(
                stream, root, "roslyn", TestContext.Current.CancellationToken);

            Assert.Equal(suppressed ? 0 : 1, parsed.Findings.Count);
            Assert.Equal(suppressed ? 1 : 0, parsed.SuppressedFindings);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task Eslint_resolves_its_tools_and_config_from_the_workspace_including_hoisted_modules()
    {
        // A workspace layout: the config lives in frontend/, the packages are hoisted to the root.
        var root = CreateRepository(
            "frontend/src/app.ts",
            "frontend/eslint.config.js",
            "node_modules/eslint/bin/eslint.js",
            "node_modules/@microsoft/eslint-formatter-sarif/sarif.js");
        try
        {
            IReadOnlyList<string>? arguments = null;
            string? workingDirectory = null;
            var runner = new CallbackRunner((_, args, directory) =>
            {
                arguments = args;
                workingDirectory = directory;
                var report = args[args.ToList().IndexOf("--output-file") + 1];
                File.WriteAllText(report, Log("no-eval", "frontend/src/app.ts", 2));
                return new SensorCommandResult(1, string.Empty, string.Empty);
            });

            var result = await new EslintAnalyzerSensor(runner).RunAsync(
                new SensorScanRequest(root, Configuration: Profile("eslint-frontend-sarif")),
                TestContext.Current.CancellationToken);

            Assert.True(result.Available, result.UnavailableReason);
            Assert.Equal(Path.Combine(root, "frontend"), workingDirectory);
            Assert.Equal(Path.Combine(root, "node_modules", "eslint", "bin", "eslint.js"), arguments![0]);
            Assert.Equal(Path.Combine(root, "frontend", "eslint.config.js"), arguments[arguments.ToList().IndexOf("--config") + 1]);
            Assert.Equal(
                Path.Combine(root, "node_modules", "@microsoft", "eslint-formatter-sarif", "sarif.js"),
                arguments[arguments.ToList().IndexOf("--format") + 1]);
            Assert.Equal("no-eval", Assert.Single(result.Findings).RuleId);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task Eslint_without_installed_packages_is_unavailable_with_the_install_hint_and_runs_nothing()
    {
        var root = CreateRepository("frontend/src/app.ts", "frontend/eslint.config.mjs");
        try
        {
            var runner = new CallbackRunner((_, _, _) => throw new InvalidOperationException("must not run"));

            var result = await new EslintAnalyzerSensor(runner).RunAsync(
                new SensorScanRequest(root, Configuration: Profile("eslint-frontend-sarif")),
                TestContext.Current.CancellationToken);

            Assert.False(result.Available);
            Assert.Contains("eslint/bin/eslint.js' is not installed", result.UnavailableReason, StringComparison.Ordinal);
            Assert.Contains("npm ci", result.UnavailableReason, StringComparison.Ordinal);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public void A_node_module_path_cannot_climb_out_of_node_modules()
    {
        var root = CreateRepository();
        try
        {
            Assert.Throws<ArgumentException>(() => AnalyzerCommand.NodeModule(root, root, "../../etc/passwd"));
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task Tsc_checks_each_project_a_solution_style_tsconfig_references()
    {
        var root = CreateRepository("frontend/src/main.ts", "frontend/node_modules/typescript/bin/tsc");
        try
        {
            // Angular's generated root config: comments, trailing commas, `files: []` and references.
            File.WriteAllText(Path.Combine(root, "frontend", "tsconfig.json"), """
                /* To learn more about Typescript configuration file */
                {
                  "compilerOptions": { "strict": true, },
                  "files": [],
                  "references": [
                    { "path": "./tsconfig.app.json" },
                    { "path": "./tsconfig.spec.json" },
                  ]
                }
                """);
            File.WriteAllText(Path.Combine(root, "frontend", "tsconfig.app.json"), """{ "files": ["src/main.ts"] }""");
            File.WriteAllText(Path.Combine(root, "frontend", "tsconfig.spec.json"), """{ "include": ["src/**/*.spec.ts"] }""");
            var invocations = new List<IReadOnlyList<string>>();
            var runner = new CallbackRunner((executable, args, _) =>
            {
                invocations.Add([executable, .. args]);
                return invocations.Count == 1
                    ? new SensorCommandResult(2, "src/main.ts(3,7): error TS2322: Type 'string' is not assignable to type 'number'.\n", string.Empty)
                    : new SensorCommandResult(0, string.Empty, string.Empty);
            });

            var result = await new TypeScriptAnalyzerSensor(runner).RunAsync(
                new SensorScanRequest(root, Configuration: Profile("tsc-frontend")),
                TestContext.Current.CancellationToken);

            Assert.True(result.Available, result.UnavailableReason);
            Assert.Equal(2, invocations.Count);
            Assert.All(invocations, invocation =>
            {
                Assert.Equal("node", invocation[0]);
                Assert.Equal(Path.Combine(root, "frontend", "node_modules", "typescript", "bin", "tsc"), invocation[1]);
                Assert.Equal("-p", invocation[2]);
                Assert.Contains("--noEmit", invocation);
            });
            Assert.Equal(Path.Combine(root, "frontend", "tsconfig.app.json"), invocations[0][3]);
            Assert.Equal(Path.Combine(root, "frontend", "tsconfig.spec.json"), invocations[1][3]);
            var finding = Assert.Single(result.Findings);
            Assert.Equal("TS2322", finding.RuleId);
            Assert.Equal("frontend/src/main.ts", finding.Locations[0].Path);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task Tsc_reports_a_missing_solution_reference_even_when_other_projects_exist()
    {
        var root = CreateRepository("frontend/src/main.ts", "frontend/node_modules/typescript/bin/tsc");
        try
        {
            File.WriteAllText(Path.Combine(root, "frontend", "tsconfig.json"), """
                { "files": [], "references": [
                    { "path": "./tsconfig.app.json" },
                    { "path": "./missing.json" }
                ] }
                """);
            File.WriteAllText(Path.Combine(root, "frontend", "tsconfig.app.json"), """{ "files": ["src/main.ts"] }""");
            var runner = new CallbackRunner((_, _, _) => throw new InvalidOperationException("must not run"));

            var result = await new TypeScriptAnalyzerSensor(runner).RunAsync(
                new SensorScanRequest(root, Configuration: Profile("tsc-frontend")),
                TestContext.Current.CancellationToken);
            var probe = await new TypeScriptAnalyzerSensor(runner).ProbeAvailabilityAsync(
                root, Profile("tsc-frontend"), TestContext.Current.CancellationToken);

            Assert.False(result.Available);
            Assert.Contains("missing TypeScript project", result.UnavailableReason, StringComparison.Ordinal);
            Assert.Contains("frontend/missing.json", result.UnavailableReason, StringComparison.Ordinal);
            Assert.Empty(result.Findings);
            Assert.False(probe.Available);
            Assert.Contains("frontend/missing.json", probe.UnavailableReason, StringComparison.Ordinal);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{ \"path\": 42 }")]
    [InlineData("{ \"path\": \" \" }")]
    public async Task Tsc_reports_a_malformed_solution_reference_even_when_other_projects_exist(string invalidReference)
    {
        var root = CreateRepository("frontend/src/main.ts", "frontend/node_modules/typescript/bin/tsc");
        try
        {
            File.WriteAllText(Path.Combine(root, "frontend", "tsconfig.json"),
                $$"""{ "files": [], "references": [{ "path": "./tsconfig.app.json" }, {{invalidReference}}] }""");
            File.WriteAllText(Path.Combine(root, "frontend", "tsconfig.app.json"), """{ "files": ["src/main.ts"] }""");
            var runner = new CallbackRunner((_, _, _) => throw new InvalidOperationException("must not run"));

            var result = await new TypeScriptAnalyzerSensor(runner).RunAsync(
                new SensorScanRequest(root, Configuration: Profile("tsc-frontend")),
                TestContext.Current.CancellationToken);

            Assert.False(result.Available);
            Assert.Contains("reference without a valid path", result.UnavailableReason, StringComparison.Ordinal);
            Assert.Empty(result.Findings);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public void Tsc_reports_the_project_count_limit_instead_of_omitting_projects()
    {
        var root = CreateRepository();
        try
        {
            var references = Enumerable.Range(0, 33)
                .Select(index => $"{{ \"path\": \"./tsconfig.{index}.json\" }}");
            File.WriteAllText(Path.Combine(root, "tsconfig.json"),
                "{ \"files\": [], \"references\": [" + string.Join(",", references) + "] }");
            foreach (var index in Enumerable.Range(0, 33))
                File.WriteAllText(Path.Combine(root, $"tsconfig.{index}.json"), """{ "include": ["src"] }""");

            var error = Assert.Throws<ArgumentException>(() => TypeScriptProjects.Resolve(root, root));

            Assert.Contains("reference count exceeds the limit of 32", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public void Tsc_reports_the_reference_depth_limit_instead_of_omitting_projects()
    {
        var root = CreateRepository();
        try
        {
            for (var depth = 0; depth <= 9; depth++)
            {
                var config = depth == 0 ? "tsconfig.json" : $"tsconfig.{depth}.json";
                var content = depth == 9
                    ? """{ "include": ["src"] }"""
                    : "{ \"files\": [], \"references\": [{ \"path\": \"./tsconfig." + (depth + 1) + ".json\" }] }";
                File.WriteAllText(Path.Combine(root, config), content);
            }

            var error = Assert.Throws<ArgumentException>(() => TypeScriptProjects.Resolve(root, root));

            Assert.Contains("reference depth limit of 8", error.Message, StringComparison.Ordinal);
            Assert.Contains("tsconfig.9.json", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task Tsc_without_a_tsconfig_in_the_working_directory_is_unavailable_instead_of_printing_help()
    {
        var root = CreateRepository("src/main.ts", "node_modules/typescript/bin/tsc");
        try
        {
            var runner = new CallbackRunner((_, _, _) => throw new InvalidOperationException("must not run"));

            var result = await new TypeScriptAnalyzerSensor(runner).RunAsync(
                new SensorScanRequest(root, Configuration: Profile("tsc-noemit")),
                TestContext.Current.CancellationToken);

            Assert.False(result.Available);
            Assert.Contains("No tsconfig.json", result.UnavailableReason, StringComparison.Ordinal);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task Probes_run_in_the_profile_working_directory_of_the_repository()
    {
        var root = CreateRepository(
            "frontend/eslint.config.mjs",
            "frontend/node_modules/eslint/bin/eslint.js",
            "frontend/node_modules/@microsoft/eslint-formatter-sarif/sarif.js",
            "frontend/node_modules/typescript/bin/tsc");
        try
        {
            File.WriteAllText(Path.Combine(root, "frontend", "tsconfig.json"), """{ "include": ["src"] }""");
            var directories = new List<(string Executable, string Directory)>();
            var runner = new CallbackRunner((executable, _, directory) =>
            {
                directories.Add((executable, directory));
                return new SensorCommandResult(0, "v1.0.0", string.Empty);
            });

            var eslint = await new EslintAnalyzerSensor(runner).ProbeAvailabilityAsync(
                root, Profile("eslint-frontend-sarif"), TestContext.Current.CancellationToken);
            var tsc = await new TypeScriptAnalyzerSensor(runner).ProbeAvailabilityAsync(
                root, Profile("tsc-frontend"), TestContext.Current.CancellationToken);
            var roslyn = await new RoslynAnalyzerSensor(runner).ProbeAvailabilityAsync(
                root, Profile("roslyn-build-sarif"), TestContext.Current.CancellationToken);

            Assert.True(eslint.Available, eslint.UnavailableReason);
            Assert.True(tsc.Available, tsc.UnavailableReason);
            Assert.True(roslyn.Available, roslyn.UnavailableReason);
            Assert.Equal(
                [("node", Path.Combine(root, "frontend")), ("node", Path.Combine(root, "frontend")), ("dotnet", root)],
                directories);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task A_probe_reports_a_missing_workspace_install_as_unavailable()
    {
        var root = CreateRepository("frontend/eslint.config.mjs");
        try
        {
            var runner = new CallbackRunner((_, _, _) => new SensorCommandResult(0, "v22", string.Empty));

            var eslint = await new EslintAnalyzerSensor(runner).ProbeAvailabilityAsync(
                root, Profile("eslint-frontend-sarif"), TestContext.Current.CancellationToken);
            var tsc = await new TypeScriptAnalyzerSensor(runner).ProbeAvailabilityAsync(
                root, Profile("tsc-frontend"), TestContext.Current.CancellationToken);

            Assert.False(eslint.Available);
            Assert.Contains("not installed", eslint.UnavailableReason, StringComparison.Ordinal);
            Assert.False(tsc.Available);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    internal static Dictionary<string, string> Profile(string id) => new(StringComparer.Ordinal)
    {
        [AnalyzerSensorConfiguration.ProfileKey] = id,
    };

    internal static string Log(string ruleId, string path, int line, bool suppressed = false) =>
        "{\"version\":\"2.1.0\",\"runs\":[{\"tool\":{\"driver\":{\"name\":\"Microsoft.CodeAnalysis\",\"version\":\"5.6.0\"}}," +
        "\"invocations\":[{\"executionSuccessful\":true}]," +
        $"\"results\":[{{\"ruleId\":\"{ruleId}\",\"level\":\"warning\",\"message\":{{\"text\":\"{ruleId} reported.\"}}," +
        (suppressed ? "\"suppressions\":[{\"kind\":\"inSource\"}]," : string.Empty) +
        $"\"locations\":[{{\"physicalLocation\":{{\"artifactLocation\":{{\"uri\":\"{path}\"}},\"region\":{{\"startLine\":{line},\"startColumn\":1}}}}}}]}}]}}]}}";

    internal static string CreateRepository(params string[] paths)
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-profiles-").FullName;
        foreach (var path in paths)
        {
            var absolute = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, string.Empty);
        }
        return root;
    }

    private sealed class CallbackRunner(
        Func<string, IReadOnlyList<string>, string, SensorCommandResult> callback) : ISensorCommandRunner
    {
        public Task<SensorCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(callback(executable, arguments, workingDirectory));
    }
}
