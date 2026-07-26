using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

[CollectionDefinition("GitleaksSecurityScanner", DisableParallelization = true)]
public sealed class GitleaksSecurityScannerCollection
{
}

[Collection("GitleaksSecurityScanner")]
public sealed class GitleaksSecurityScannerTests : IAsyncLifetime
{
    private const string Version = "8.24.2";
    private const string SecretSentinel = "SECRET-VALUE-DO-NOT-PERSIST";

    private string? _fakeGitleaksRoot;
    private string? _fakeGitleaksPath;
    private string? _previousGitleaksPath;

    public async ValueTask InitializeAsync()
    {
        _fakeGitleaksRoot = Directory.CreateTempSubdirectory("quality-studio-fake-gitleaks-").FullName;
        _fakeGitleaksPath = await BuildFakeGitleaksAsync(_fakeGitleaksRoot, TestContext.Current.CancellationToken);
        _previousGitleaksPath = Environment.GetEnvironmentVariable("QUALITY_GITLEAKS_PATH");
        Environment.SetEnvironmentVariable("QUALITY_GITLEAKS_PATH", _fakeGitleaksPath);
    }

    public ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable("QUALITY_GITLEAKS_PATH", _previousGitleaksPath);
        if (_fakeGitleaksRoot is not null)
        {
            TryDelete(_fakeGitleaksRoot);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ScanAsync_RepositoryMode_AcceptsBaselineAndRedactsSecrets()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("quality-studio-gitleaks-repo-").FullName;
        try
        {
            await InitializeGitRepositoryAsync(root, cancellationToken);
            Directory.CreateDirectory(Path.Combine(root, ".quality", "security"));
            await File.WriteAllTextAsync(Path.Combine(root, ".quality", "security", "gitleaks.toml"), """
                title = "Quality Studio Gitleaks configuration"

                [extend]
                useDefault = true
                """, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "bin/\n", cancellationToken);
            await WriteRepoFixtureAsync(root, "src/placeholder.txt", "placeholder fixture\n", cancellationToken);
            await WriteRepoFixtureAsync(root, "src/private-key.pem", "-----BEGIN PRIVATE KEY-----\nfixture\n", cancellationToken);
            await WriteRepoFixtureAsync(root, "src/bearer-token.ts", "const token = 'fixture';\n", cancellationToken);
            await WriteRepoFixtureAsync(root, "src/entropy.txt", "entropy false positive fixture\n", cancellationToken);
            await WriteRepoFixtureAsync(root, "bin/Generated.cs", "generated output fixture\n", cancellationToken);

            var baselinePath = Path.Combine(root, ".quality", "security", "gitleaks.baseline.json");
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            await File.WriteAllTextAsync(baselinePath, """
                [
                  { "Fingerprint": "sha256:accepted-placeholder" }
                ]
                """, cancellationToken);

            SetScenario("repository");
            var result = await new GitleaksSecurityScanner().ScanAsync(new SecurityScanRequest(
                root,
                SecurityScanMode.Repository,
                BaselinePath: baselinePath),
                cancellationToken);

            Assert.True(result.Report.Available);
            Assert.Equal(SecurityVerdict.Block, result.Report.Verdict);
            Assert.Equal(Version, result.Report.Version);
            Assert.Equal(7, result.Report.FilesScanned);
            Assert.Equal(3, result.Report.NewFindings);
            Assert.Equal(1, result.Report.AcceptedFindings);
            Assert.Equal(2, result.Report.BlockFindings);
            Assert.Equal(1, result.Report.WarnFindings);
            Assert.Equal(3, result.Report.CleanFiles);
            Assert.Equal(4, result.Findings.Count);
            Assert.Contains(result.Findings, finding => finding.Accepted && finding.RuleId == "accepted-placeholder");
            Assert.All(result.Findings, finding => Assert.Null(finding.Evidence));

            var sidecars = Directory.EnumerateFiles(root, "*.review-meta.security.json", SearchOption.AllDirectories).ToArray();
            Assert.Equal(4, sidecars.Length);
            Assert.Contains(sidecars, path => path.Contains($"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
            foreach (var path in sidecars)
            {
                var content = await File.ReadAllTextAsync(path, cancellationToken);
                Assert.DoesNotContain(SecretSentinel, content, StringComparison.Ordinal);
                Assert.DoesNotContain("-----BEGIN PRIVATE KEY-----", content, StringComparison.Ordinal);
                Assert.DoesNotContain("Bearer eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9", content, StringComparison.Ordinal);
            }

            string? placeholderMetaPath = null;
            foreach (var path in sidecars)
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
                if (document.RootElement.GetProperty("unit").GetProperty("path").GetString() == "src/placeholder.txt")
                {
                    placeholderMetaPath = path;
                    break;
                }
            }

            Assert.NotNull(placeholderMetaPath);
            var resolvedPlaceholderMetaPath = placeholderMetaPath!;
            using var placeholderDocument = JsonDocument.Parse(await File.ReadAllTextAsync(resolvedPlaceholderMetaPath, cancellationToken));
            var acceptedFinding = Assert.Single(placeholderDocument.RootElement.GetProperty("findings").EnumerateArray());
            Assert.Equal("accepted-placeholder", acceptedFinding.GetProperty("ruleId").GetString());
            Assert.Equal(100, placeholderDocument.RootElement.GetProperty("grade").GetProperty("score").GetInt32());
            var lifecycle = await new FindingStateStore(root).ReadAsync(cancellationToken);
            Assert.Equal(FindingState.Accepted, lifecycle[acceptedFinding.GetProperty("fingerprint").GetString()!].State);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ScanAsync_RangeMode_ParsesSarifAndTracksRenameDiffs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("quality-studio-gitleaks-range-").FullName;
        try
        {
            await InitializeGitRepositoryAsync(root, cancellationToken);
            await WriteRepoFixtureAsync(root, "src/OldSecret.cs", "namespace Sample;\n", cancellationToken);
            await CommitAsync(root, "initial", cancellationToken);

            await RunGitAsync(root, cancellationToken, "mv", "src/OldSecret.cs", "src/RenamedSecret.cs");
            await CommitAsync(root, "rename", cancellationToken);

            SetScenario("range");
            var range = "HEAD~1..HEAD";
            var result = await new GitleaksSecurityScanner().ScanAsync(new SecurityScanRequest(
                root,
                SecurityScanMode.Range,
                Range: range),
                cancellationToken);

            Assert.True(result.Report.Available);
            Assert.Equal(SecurityVerdict.Block, result.Report.Verdict);
            Assert.Equal("range", result.Report.Mode);
            Assert.Equal(range, result.Report.Range);
            Assert.Equal(1, result.Report.FilesScanned);
            Assert.Single(result.Findings);

            var finding = Assert.Single(result.Findings);
            Assert.Equal("renamed-secret", finding.RuleId);
            Assert.Equal("src/RenamedSecret.cs", finding.Path);
            Assert.Matches("^sha256:[a-f0-9]{64}$", finding.Fingerprint);
            Assert.Equal(FindingSeverity.Critical, finding.Severity);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ScanAsync_StagedMode_UsesDeletedAndRenamedFiles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("quality-studio-gitleaks-staged-").FullName;
        try
        {
            await InitializeGitRepositoryAsync(root, cancellationToken);
            await WriteRepoFixtureAsync(root, "src/DeletedSecret.cs", "namespace Sample;\n", cancellationToken);
            await WriteRepoFixtureAsync(root, "src/OldName.cs", "namespace Sample;\n", cancellationToken);
            await CommitAsync(root, "initial", cancellationToken);

            await RunGitAsync(root, cancellationToken, "rm", "src/DeletedSecret.cs");
            await RunGitAsync(root, cancellationToken, "mv", "src/OldName.cs", "src/RenamedSecret.cs");

            SetScenario("staged");
            var result = await new GitleaksSecurityScanner().ScanAsync(new SecurityScanRequest(
                root,
                SecurityScanMode.Staged),
                cancellationToken);

            Assert.True(result.Report.Available);
            Assert.Equal(SecurityVerdict.Block, result.Report.Verdict);
            Assert.Equal(2, result.Report.FilesScanned);
            Assert.Equal(2, result.Findings.Count);
            Assert.Contains(result.Findings, finding => finding.Path == "src/DeletedSecret.cs");
            Assert.Contains(result.Findings, finding => finding.Path == "src/RenamedSecret.cs");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ScanAsync_ReturnsUnavailableWhenPinnedVersionDoesNotMatch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("quality-studio-gitleaks-unavailable-").FullName;
        try
        {
            await InitializeGitRepositoryAsync(root, cancellationToken);
            await WriteRepoFixtureAsync(root, "src/Example.cs", "namespace Sample;\n", cancellationToken);

            SetScenario("repository", "v0.0.0");
            var result = await new GitleaksSecurityScanner().ScanAsync(new SecurityScanRequest(
                root,
                SecurityScanMode.Repository),
                cancellationToken);

            Assert.False(result.Report.Available);
            Assert.Equal(SecurityVerdict.Unavailable, result.Report.Verdict);
            Assert.Contains(Version, result.Report.UnavailableReason ?? string.Empty, StringComparison.Ordinal);
            Assert.Empty(result.Findings);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void SetScenario(string scenario, string version = Version)
    {
        Environment.SetEnvironmentVariable("FAKE_GITLEAKS_SCENARIO", scenario);
        Environment.SetEnvironmentVariable("FAKE_GITLEAKS_VERSION", version);
    }

    private static async Task WriteRepoFixtureAsync(string root, string relativePath, string content, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, cancellationToken);
    }

    private static async Task InitializeGitRepositoryAsync(string root, CancellationToken cancellationToken)
    {
        await RunGitAsync(root, cancellationToken, "init", "--quiet");
    }

    private static async Task CommitAsync(string root, string message, CancellationToken cancellationToken)
    {
        await RunGitAsync(root, cancellationToken, "add", "-A");
        await RunGitAsync(root, cancellationToken, "-c", "user.name=Quality Studio", "-c", "user.email=quality@example.com", "commit", "--quiet", "-m", message);
    }

    private static async Task RunGitAsync(string root, CancellationToken cancellationToken, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("Git did not start.");
        }

        await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        Assert.Equal(0, process.ExitCode);
    }

    private static async Task<string> BuildFakeGitleaksAsync(string root, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "FakeGitleaks.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <AssemblyName>FakeGitleaks</AssemblyName>
              </PropertyGroup>
            </Project>
            """, cancellationToken);

        await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), """
            using System.Text.Json;

            var version = Environment.GetEnvironmentVariable("FAKE_GITLEAKS_VERSION") ?? "v8.24.2";
            var scenario = Environment.GetEnvironmentVariable("FAKE_GITLEAKS_SCENARIO") ?? "repository";
            var command = args.FirstOrDefault(arg => arg is "version" or "dir" or "git");

            if (command == "version")
            {
                Console.WriteLine(version);
                return 0;
            }

            if (command == "dir")
            {
                Console.WriteLine(JsonSerializer.Serialize(scenario switch
                {
                    "repository" => RepositoryFindings(),
                    "staged" => StagedFindings(),
                    _ => Array.Empty<Dictionary<string, object?>>(),
                }));
                return 1;
            }

            if (command == "git")
            {
                Console.WriteLine(JsonSerializer.Serialize(RangeSarif()));
                return 1;
            }

            Console.Error.WriteLine("unexpected command");
            return 2;

            static Dictionary<string, object?> Finding(
                string ruleId,
                string file,
                int startLine,
                int endLine,
                int startColumn,
                int endColumn,
                string description,
                string severity,
                string fingerprint,
                string secret)
            {
                return new Dictionary<string, object?>
                {
                    ["RuleID"] = ruleId,
                    ["File"] = file,
                    ["StartLine"] = startLine,
                    ["EndLine"] = endLine,
                    ["StartColumn"] = startColumn,
                    ["EndColumn"] = endColumn,
                    ["Description"] = description,
                    ["Severity"] = severity,
                    ["Fingerprint"] = fingerprint,
                    ["Secret"] = secret,
                };
            }

            static IReadOnlyList<Dictionary<string, object?>> RepositoryFindings() => new[]
            {
                Finding(
                    "accepted-placeholder",
                    "src/placeholder.txt",
                    1,
                    1,
                    1,
                    24,
                    "Generic placeholder token detected.",
                    "High",
                    "sha256:accepted-placeholder",
                    "placeholder-secret"),
                Finding(
                    "private-key",
                    "src/private-key.pem",
                    2,
                    6,
                    1,
                    32,
                    "Private key material detected.",
                    "Critical",
                    "sha256:private-key",
                    "-----BEGIN PRIVATE KEY-----"),
                Finding(
                    "bearer-token",
                    "src/bearer-token.ts",
                    3,
                    3,
                    15,
                    38,
                    "Bearer token detected.",
                    "High",
                    "sha256:bearer-token",
                    "Bearer eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9"),
                Finding(
                    "entropy-false-positive",
                    "src/entropy.txt",
                    1,
                    1,
                    1,
                    40,
                    "High entropy string that is a known fixture.",
                    "Low",
                    "sha256:entropy-fp",
                    "QWxhZGRpbjpvcGVuIHNlc2FtZQ=="),
            };

            static IReadOnlyList<Dictionary<string, object?>> StagedFindings() => new[]
            {
                Finding(
                    "deleted-secret",
                    "src/DeletedSecret.cs",
                    1,
                    1,
                    1,
                    26,
                    "Deleted secret fixture detected from HEAD.",
                    "High",
                    "sha256:deleted-secret",
                    "deleted-secret"),
                Finding(
                    "renamed-secret",
                    "src/RenamedSecret.cs",
                    7,
                    7,
                    5,
                    18,
                    "Renamed secret fixture detected from the staged index.",
                    "High",
                    "sha256:renamed-secret",
                    "renamed-secret"),
            };

            static Dictionary<string, object?> RangeSarif() => new()
            {
                ["version"] = "2.1.0",
                ["runs"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["tool"] = new Dictionary<string, object?>
                        {
                            ["driver"] = new Dictionary<string, object?>
                            {
                                ["name"] = "gitleaks",
                                ["rules"] = new object[]
                                {
                                    new Dictionary<string, object?>
                                    {
                                        ["id"] = "renamed-secret",
                                        ["shortDescription"] = new Dictionary<string, object?>
                                        {
                                            ["text"] = "Secret moved in a rename.",
                                        },
                                    },
                                },
                            },
                        },
                        ["results"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["ruleId"] = "renamed-secret",
                                ["level"] = "error",
                                ["message"] = new Dictionary<string, object?>
                                {
                                    ["text"] = "Secret moved in a rename.",
                                },
                                ["locations"] = new object[]
                                {
                                    new Dictionary<string, object?>
                                    {
                                        ["physicalLocation"] = new Dictionary<string, object?>
                                        {
                                            ["artifactLocation"] = new Dictionary<string, object?>
                                            {
                                                ["uri"] = "src/RenamedSecret.cs",
                                            },
                                            ["region"] = new Dictionary<string, object?>
                                            {
                                                ["startLine"] = 7,
                                                ["endLine"] = 7,
                                                ["startColumn"] = 5,
                                                ["endColumn"] = 18,
                                            },
                                        },
                                    },
                                },
                                ["partialFingerprints"] = new Dictionary<string, object?>
                                {
                                    ["fingerprint"] = "sha256:range-renamed-secret",
                                },
                            },
                        },
                    },
                },
            };
            """, cancellationToken);

        var publishDirectory = Path.Combine(root, "publish");
        var arguments = new List<string>
        {
            "publish",
            Path.Combine(root, "FakeGitleaks.csproj"),
            "-c",
            "Release",
            "-o",
            publishDirectory,
            "--self-contained",
            "false",
        };

        var runtimeIdentifier = GetRuntimeIdentifier();
        if (runtimeIdentifier is not null)
        {
            arguments.Add("-r");
            arguments.Add(runtimeIdentifier);
        }

        await RunProcessAsync("dotnet", arguments, root, cancellationToken);

        var executableName = OperatingSystem.IsWindows() ? "FakeGitleaks.exe" : "FakeGitleaks";
        var executablePath = Path.Combine(publishDirectory, executableName);
        if (!File.Exists(executablePath))
        {
            executablePath = Directory.EnumerateFiles(publishDirectory, "FakeGitleaks*", SearchOption.AllDirectories).First();
        }

        return executablePath;
    }

    private static async Task RunProcessAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"{fileName} did not start.");
        }

        await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} failed with exit code {process.ExitCode}.");
        }
    }

    private static string? GetRuntimeIdentifier()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };

        if (architecture is null)
        {
            return null;
        }

        return OperatingSystem.IsWindows()
            ? $"win-{architecture}"
            : OperatingSystem.IsLinux()
                ? $"linux-{architecture}"
                : OperatingSystem.IsMacOS()
                    ? $"osx-{architecture}"
                    : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
        }
    }
}
