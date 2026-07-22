using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class StalenessEvaluatorTests
{
    [Fact]
    public async Task Scan_reports_fresh_stale_and_missing_files()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.WriteSourceAsync("src/fresh.cs", "class Fresh {}\r\n");
        await fixture.WriteSourceAsync("src/stale.cs", "class Before {}\n");
        await fixture.WriteSourceAsync("src/missing.cs", "class Missing {}\n");
        await fixture.WriteMetaAsync("src/fresh.cs", "class Fresh {}\n");
        await fixture.WriteMetaAsync("src/stale.cs", "class Before {}\n");
        await fixture.WriteSourceAsync("src/stale.cs", "class After {}\n");

        var report = await new StalenessEvaluator().ScanAsync(
            fixture.Root,
            new StalenessEvaluatorOptions { IncludeGlobs = ["**/*.cs"] },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, report.Files.Count);
        Assert.Equal(StalenessState.Fresh, Assert.Single(report.Files, file => file.RelativePath == "src/fresh.cs").State);
        Assert.Equal(StalenessState.Stale, Assert.Single(report.Files, file => file.RelativePath == "src/stale.cs").State);
        Assert.Equal(StalenessState.Missing, Assert.Single(report.Files, file => file.RelativePath == "src/missing.cs").State);
        Assert.Equal((1, 1, 1), (report.FreshCount, report.StaleCount, report.MissingCount));
    }

    [Fact]
    public async Task Scan_respects_gitignore_and_include_globs()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.WriteSourceAsync(".gitignore", "ignored/\n");
        await fixture.WriteSourceAsync("ignored/no.cs", "ignored");
        await fixture.WriteSourceAsync("src/yes.cs", "included");
        await fixture.WriteSourceAsync("src/no.txt", "wrong extension");

        var report = await new StalenessEvaluator().ScanAsync(
            fixture.Root,
            new StalenessEvaluatorOptions { IncludeGlobs = ["src/**/*.cs"] },
            TestContext.Current.CancellationToken);

        var file = Assert.Single(report.Files);
        Assert.Equal("src/yes.cs", file.RelativePath);
        Assert.Equal(StalenessState.Missing, file.State);
    }

    [Fact]
    public async Task Scan_does_not_hash_a_file_without_metadata()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        await File.WriteAllBytesAsync(
            Path.Combine(fixture.Root, "binary.cs"),
            [0xff, 0xfe, 0x00],
            TestContext.Current.CancellationToken);

        var report = await new StalenessEvaluator().ScanAsync(
            fixture.Root,
            new StalenessEvaluatorOptions { IncludeGlobs = ["**/*.cs"] },
            TestContext.Current.CancellationToken);

        Assert.Equal(StalenessState.Missing, Assert.Single(report.Files).State);
    }

    [Fact]
    public async Task Scan_discovers_a_root_file_sidecar()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.WriteSourceAsync("root.cs", "class Root {}\n");
        await fixture.WriteMetaAsync("root.cs", "class Root {}\n");

        var report = await new StalenessEvaluator().ScanAsync(
            fixture.Root,
            new StalenessEvaluatorOptions { IncludeGlobs = ["**/*.cs"] },
            TestContext.Current.CancellationToken);

        Assert.Equal(StalenessState.Fresh, Assert.Single(report.Files).State);
    }

    [Fact]
    public async Task Guideline_change_reports_policy_drift_without_changing_the_code_hash()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.WriteSourceAsync("src/stable.cs", "class Stable {}\n");
        await fixture.WriteSourceAsync(".quality/inputs/style.md", "---\nid: stable-style\nenabled: true\nkinds: [code]\nlevels: [file]\npriority: 10\n---\nBefore.\n");
        var metaPath = await fixture.WriteMetaAsync("src/stable.cs", "class Stable {}\n");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(metaPath, cancellationToken))!.AsObject();
        var resolved = new InputResolver().Resolve(fixture.Root, "code", ReviewLevel.File);
        root["reviewInputs"] = new JsonObject { ["effectiveHash"] = new JsonObject { ["value"] = resolved.EffectiveHash(ReviewPromptBuilder.TemplateHash("code")) } };
        await File.WriteAllTextAsync(metaPath, root.ToJsonString(), cancellationToken);
        var reviewedHash = root["reviewedHash"]!["value"]!.GetValue<string>();
        await fixture.WriteSourceAsync(".quality/inputs/style.md", "---\nid: stable-style\nenabled: true\nkinds: [code]\nlevels: [file]\npriority: 10\n---\nAfter.\n");

        var report = await new StalenessEvaluator().ScanAsync(fixture.Root,
            new StalenessEvaluatorOptions { IncludeGlobs = ["**/*.cs"] }, cancellationToken);

        Assert.Equal(StalenessState.PolicyDrift, Assert.Single(report.Files).State);
        Assert.Equal(1, report.PolicyDriftCount);
        using var unchanged = JsonDocument.Parse(await File.ReadAllTextAsync(metaPath, cancellationToken));
        Assert.Equal(reviewedHash, unchanged.RootElement.GetProperty("reviewedHash").GetProperty("value").GetString());
    }

    [Fact]
    public void Hasher_matches_the_normative_conformance_vector()
    {
        var hash = ReviewSubjectHasher.ComputeManifestHash(
            "qs-v1/angular/file/7b1bd2568ea481d83c2b97850fafd54c0e1981d94960926ab3b4cc5180daec3e",
            [new SubjectInputHash("src/a.ts", "file", "sha256:95befdd6e691d4d89031a2a2901cc74fc6242109980b060e08ddf87829924483")]);

        Assert.Equal("8ea241557b3e9f1bd4f3c9bf88f5e36684fd86a59829e98e11fabadd5462531f", hash);
    }

    private sealed class RepositoryFixture : IDisposable
    {
        private RepositoryFixture(string root) => Root = root;

        public string Root { get; }

        public static async Task<RepositoryFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "quality-studio-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            await RunGitAsync(root, "init", "--quiet");
            return new RepositoryFixture(root);
        }

        public async Task WriteSourceAsync(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
        }

        public async Task<string> WriteMetaAsync(string subjectPath, string reviewedContent)
        {
            var unitId = "qs-v1/test/file/" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(subjectPath)));
            var contentPath = Path.Combine(Root, subjectPath.Replace('/', Path.DirectorySeparatorChar));
            var temporaryPath = contentPath + ".reviewed";
            await File.WriteAllTextAsync(temporaryPath, reviewedContent);
            var contentHash = await ReviewSubjectHasher.ComputeFileContentHashAsync(temporaryPath);
            File.Delete(temporaryPath);
            var reviewedHash = ReviewSubjectHasher.ComputeManifestHash(
                unitId,
                [new SubjectInputHash(subjectPath, "file", contentHash)]);
            var key = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(unitId)));
            var directory = Path.Combine(Path.GetDirectoryName(contentPath)!, ".quality", "reviews", "files");
            Directory.CreateDirectory(directory);
            var metaPath = Path.Combine(directory, $"file.{key}.review-meta.code.json");
            var document = new
            {
                schemaVersion = 1,
                unit = new { id = unitId, level = "file", path = subjectPath },
                kind = "code",
                reviewedHash = new { algorithm = "sha256", value = reviewedHash },
                subjectInputs = new[] { new { path = subjectPath, selector = "file", contentHash } },
            };
            await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(document));
            return metaPath;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
                // Test cleanup is best effort on Windows, where Git may briefly retain a handle.
            }
        }

        private static async Task RunGitAsync(string root, params string[] arguments)
        {
            using var process = Process.Start(new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
            })!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
    }
}
