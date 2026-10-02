using System.Text.Json;
using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

[Trait("Category", "ToolBound")]
public sealed class WindowsNpmCommandTests
{
    [Theory]
    [InlineData("npm")]
    [InlineData("npx")]
    public async Task Default_runner_can_launch_installed_npm_tools_without_a_command_shell(string command)
    {
        RequireWindows();
        var root = Directory.CreateTempSubdirectory("quality-windows-npm-").FullName;
        try
        {
            var result = await new ProcessSensorCommandRunner(TimeSpan.FromSeconds(15))
                .RunAsync(command, ["--version"], root, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Matches(@"^\d+\.\d+\.\d+", result.StandardOutput.Trim());
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task Default_dependency_probe_recognizes_installed_windows_npm()
    {
        RequireWindows();

        var availability = await new DependencyVulnerabilitySensor()
            .ProbeAvailabilityAsync(TestContext.Current.CancellationToken);

        Assert.True(availability.Available, availability.UnavailableReason);
        Assert.Contains("npm", availability.ToolVersions!.Keys);
        Assert.NotEmpty(availability.ToolVersions["npm"]);
    }

    [Theory]
    [InlineData("npm")]
    [InlineData("npx")]
    public async Task Resolved_cli_preserves_spaces_metacharacters_and_cwd_with_real_node(string command)
    {
        RequireWindows();
        var root = Directory.CreateTempSubdirectory("quality npm & (paths)-").FullName;
        try
        {
            var runtime = WindowsNpmCommand.Resolve(["--version"], command).Executable;
            var cli = await WriteFixtureCli(root, command);
            var marker = Path.Combine(root, "unexpected-shell-output.txt");
            string[] arguments =
            [
                "audit", "--json", "space value", "quoted\"value",
                "& echo injected > " + marker, "$(echo shell)", "%PATH%", "!value!", "^|",
            ];
            var launch = WindowsNpmCommand.Resolve(arguments,
                ["", ".", "relative/tools", root, Path.GetDirectoryName(runtime)!], command);

            Assert.Equal(runtime, launch.Executable, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(cli, launch.Arguments[0]);
            var result = await new ProcessSensorCommandRunner(TimeSpan.FromSeconds(15))
                .RunAsync(launch.Executable, launch.Arguments, root, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            using var document = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal(arguments, document.RootElement.GetProperty("args").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
            Assert.Equal(Path.GetFullPath(root), document.RootElement.GetProperty("cwd").GetString(),
                StringComparer.OrdinalIgnoreCase);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task Matching_installation_node_takes_precedence_over_another_path_runtime()
    {
        RequireWindows();
        var root = Directory.CreateTempSubdirectory("quality npm installation-").FullName;
        try
        {
            var otherRuntime = WindowsNpmCommand.Resolve(["--version"]).Executable;
            await WriteFixtureCli(root);
            var matchingRuntime = Path.Combine(root, "node.exe");
            await File.WriteAllBytesAsync(matchingRuntime, [], TestContext.Current.CancellationToken);

            var launch = WindowsNpmCommand.Resolve(["--version"],
                ["\"" + root + "\"", Path.GetDirectoryName(otherRuntime)!]);

            Assert.Equal(matchingRuntime, launch.Executable);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public void Relative_and_empty_path_entries_cannot_select_an_npm_installation()
    {
        RequireWindows();
        Assert.Throws<SecurityScannerUnavailableException>(() =>
            WindowsNpmCommand.Resolve(["--version"], ["", ".", "relative/tools", "nodejs"]));
    }

    [Fact]
    public async Task Audit_uses_the_discovered_lock_root_instead_of_an_ancestor_package()
    {
        RequireWindows();
        var root = Directory.CreateTempSubdirectory("quality npm ancestor-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "package.json"),
                """{"name":"ancestor","private":true}""", TestContext.Current.CancellationToken);
            var lockRoot = Path.Combine(root, "lock-only & (nested)");
            Directory.CreateDirectory(lockRoot);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dependencies", "package-lock.json"),
                Path.Combine(lockRoot, "package-lock.json"));
            var runner = new PrefixObservingRunner();

            var result = await new DependencyVulnerabilitySensor(runner).RunAsync(
                new SensorScanRequest(root, Configuration: new Dictionary<string, string> { ["ecosystems"] = "npm" }),
                TestContext.Current.CancellationToken);

            Assert.True(result.Available, result.UnavailableReason);
            Assert.Equal(lockRoot, Assert.Single(runner.EffectivePrefixes), StringComparer.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(lockRoot, "package.json")));
            var finding = Assert.Single(result.Findings);
            Assert.Equal(FindingSeverity.High, finding.Severity);
            Assert.Equal("lock-only & (nested)/package-lock.json", Assert.Single(finding.Locations).Path);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    private sealed class PrefixObservingRunner : ISensorCommandRunner
    {
        private readonly ProcessSensorCommandRunner runner = new(TimeSpan.FromSeconds(15));
        public List<string> EffectivePrefixes { get; } = [];

        public async Task<SensorCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            string workingDirectory, CancellationToken cancellationToken = default)
        {
            if (!arguments.Contains("audit", StringComparer.Ordinal))
                return await runner.RunAsync(executable, arguments, workingDirectory, cancellationToken);

            // Ask real npm which project the exact options/CWD select, without depending on a
            // registry request. The audit itself then consumes a recorded vulnerable report.
            var prefixArguments = arguments.Where(argument => argument != "--json")
                .Select(argument => argument == "audit" ? "prefix" : argument).ToArray();
            var prefix = await runner.RunAsync(executable, prefixArguments, workingDirectory, cancellationToken);
            Assert.Equal(0, prefix.ExitCode);
            EffectivePrefixes.Add(prefix.StandardOutput.Trim());
            var report = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "dependencies", "npm-audit.json"), cancellationToken);
            return new SensorCommandResult(1, report, string.Empty);
        }
    }

    private static async Task<string> WriteFixtureCli(string root, string command = "npm")
    {
        var directory = Path.Combine(root, "node_modules", "npm", "bin");
        Directory.CreateDirectory(directory);
        var cli = Path.Combine(directory, command + "-cli.js");
        await File.WriteAllTextAsync(cli,
            "process.stdout.write(JSON.stringify({args:process.argv.slice(2),cwd:process.cwd()}));",
            TestContext.Current.CancellationToken);
        return cli;
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This regression exercises the Windows npm/npx command shim installation layout.");
    }
}
