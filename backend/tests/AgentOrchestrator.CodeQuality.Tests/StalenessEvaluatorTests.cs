using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;
using QualityStudio.Testing;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

[Trait("Category", "ToolBound")]
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
        var reviewedHash = ReviewMetaReader.Load(metaPath).Document.ReviewedHash.Value;
        await fixture.WriteSourceAsync(".quality/inputs/style.md", "---\nid: stable-style\nenabled: true\nkinds: [code]\nlevels: [file]\npriority: 10\n---\nAfter.\n");

        var report = await new StalenessEvaluator().ScanAsync(fixture.Root,
            new StalenessEvaluatorOptions { IncludeGlobs = ["**/*.cs"] }, cancellationToken);

        Assert.Equal(StalenessState.PolicyDrift, Assert.Single(report.Files).State);
        Assert.Equal(1, report.PolicyDriftCount);
        using var unchanged = JsonDocument.Parse(await File.ReadAllTextAsync(metaPath, cancellationToken));
        Assert.Equal(reviewedHash, unchanged.RootElement.GetProperty("reviewedHash").GetProperty("value").GetString());
    }

    [Fact]
    public async Task An_unreadable_sidecar_is_invalid_rather_than_fresh_stale_or_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.WriteSourceAsync("src/broken.cs", "class Broken {}\n");
        await fixture.WriteSourceAsync("src/intact.cs", "class Intact {}\n");
        await fixture.WriteMetaAsync("src/intact.cs", "class Intact {}\n");
        var brokenMeta = await fixture.WriteMetaAsync("src/broken.cs", "class Broken {}\n");
        await File.WriteAllTextAsync(brokenMeta, "{ \"schemaVersion\": ", cancellationToken);

        var report = await new StalenessEvaluator().ScanAsync(fixture.Root,
            new StalenessEvaluatorOptions { IncludeGlobs = ["**/*.cs"] }, cancellationToken);

        var broken = Assert.Single(report.Files, file => file.RelativePath == "src/broken.cs");
        Assert.Equal(StalenessState.Invalid, broken.State);
        Assert.Equal(ReviewMetaPath.Describe(fixture.Root, brokenMeta), broken.MetaRelativePath);
        Assert.Equal(1, report.InvalidCount);
        // The rest of the scan still runs; one bad sidecar is not a scan failure.
        Assert.Equal(StalenessState.Fresh, Assert.Single(report.Files, file => file.RelativePath == "src/intact.cs").State);
        Assert.Equal(0, report.MissingCount);
    }

    [Fact]
    public async Task An_unreadable_sidecar_is_never_treated_as_a_fresh_review()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("quality-review-invalid-");
        try
        {
            var metaPath = Path.Combine(directory.FullName, "review-meta.json");
            await File.WriteAllTextAsync(metaPath, "{ \"schemaVersion\": 3 }", cancellationToken);

            var freshness = await new StalenessEvaluator().EvaluateReviewAsync(
                metaPath, "subject", "inputs", "model", cancellationToken);

            Assert.False(freshness.IsFresh);
            Assert.False(freshness.SubjectUnchanged);
            Assert.False(freshness.ReviewInputsUnchanged);
            Assert.False(freshness.ModelUnchanged);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void Hasher_matches_the_normative_conformance_vector()
    {
        var hash = ReviewSubjectHasher.ComputeManifestHash(
            "qs-v1/angular/file/7b1bd2568ea481d83c2b97850fafd54c0e1981d94960926ab3b4cc5180daec3e",
            [new SubjectInputHash("src/a.ts", "file", "sha256:95befdd6e691d4d89031a2a2901cc74fc6242109980b060e08ddf87829924483")]);

        Assert.Equal("8ea241557b3e9f1bd4f3c9bf88f5e36684fd86a59829e98e11fabadd5462531f", hash);
    }

    [Theory]
    [InlineData("subject-current", "inputs-current", "model-current", true, true, true)]
    [InlineData("subject-old", "inputs-current", "model-current", false, true, true)]
    [InlineData("subject-current", "inputs-old", "model-current", true, false, true)]
    [InlineData("subject-current", "inputs-current", "model-old", true, true, false)]
    public async Task EvaluateReview_compares_subject_inputs_and_model_independently(
        string storedSubject,
        string storedInputs,
        string storedModel,
        bool subjectUnchanged,
        bool inputsUnchanged,
        bool modelUnchanged)
    {
        var directory = Directory.CreateTempSubdirectory("quality-review-freshness-");
        try
        {
            var metaPath = Path.Combine(directory.FullName, "review-meta.json");
            await ReviewMetaFixture.WriteAsync(metaPath, ReviewMetaFixture.Document(
                "qs-v1/generic/file/" + new string('a', 64),
                "src/a.cs",
                storedSubject,
                [new SubjectInputHash("src/a.cs", "file", "sha256:" + new string('c', 64))],
                effectiveHash: storedInputs,
                model: storedModel), TestContext.Current.CancellationToken);

            var result = await new StalenessEvaluator().EvaluateReviewAsync(
                metaPath,
                "subject-current",
                "inputs-current",
                "model-current",
                TestContext.Current.CancellationToken);

            Assert.Equal(subjectUnchanged, result.SubjectUnchanged);
            Assert.Equal(inputsUnchanged, result.ReviewInputsUnchanged);
            Assert.Equal(modelUnchanged, result.ModelUnchanged);
            Assert.Equal(subjectUnchanged && inputsUnchanged && modelUnchanged, result.IsFresh);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed class RepositoryFixture : IDisposable
    {
        private RepositoryFixture(string root) => Root = root;

        public string Root { get; }

        public static async Task<RepositoryFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "quality-studio-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            await GitTestRepository.InitializeAsync(root, TestContext.Current.CancellationToken);
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
            var unitId = "qs-v1/generic/file/" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(subjectPath)));
            var contentPath = Path.Combine(Root, subjectPath.Replace('/', Path.DirectorySeparatorChar));
            var temporaryPath = contentPath + ".reviewed";
            await File.WriteAllTextAsync(temporaryPath, reviewedContent);
            var contentHash = await ReviewSubjectHasher.ComputeFileContentHashAsync(temporaryPath);
            File.Delete(temporaryPath);
            var inputs = new[] { new SubjectInputHash(subjectPath, "file", contentHash) };
            var metaPath = ReviewMetaPath.ForFile(Root, subjectPath, "code");
            await ReviewMetaFixture.WriteAsync(metaPath, ReviewMetaFixture.Document(
                unitId, subjectPath, ReviewSubjectHasher.ComputeManifestHash(unitId, inputs), inputs,
                effectiveHash: ReviewMetaFixture.EffectiveHash(Root)), default);
            return metaPath;
        }

        public void Dispose()
        {
            try
            {
                TemporaryDirectory.Delete(Root);
            }
            catch (IOException)
            {
                // Test cleanup is best effort on Windows, where Git may briefly retain a handle.
            }
        }

    }
}
