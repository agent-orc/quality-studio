using System.Text;
using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ReviewMetaContractTests
{
    private static readonly Lazy<JsonSchema> ReviewMetaSchema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        FindRepositoryRoot(), "schemas", "review-meta.v1.schema.json"))));
    private static readonly Lazy<JsonSchema> ReviewMetaV2Schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        FindRepositoryRoot(), "schemas", "review-meta.v2.schema.json"))));

    [Fact]
    public void SerializerRoundTripsAndIgnoresUnknownFields()
    {
        var original = CreateDocument();

        var json = ReviewMetaJson.Serialize(original);
        var withFutureField = json.Replace(
            "\"schemaVersion\": 2,",
            "\"schemaVersion\": 2,\n  \"x-future-field\": { \"enabled\": true },",
            StringComparison.Ordinal);
        var loaded = ReviewMetaJson.Deserialize(withFutureField);

        Assert.Equal(original.Unit, loaded.Unit);
        Assert.Equal(original.ReviewedAt, loaded.ReviewedAt);
        Assert.Equal(original.Kind, loaded.Kind);
        Assert.Equal(original.Grade, loaded.Grade);
        Assert.Equal(original.SubjectInputs, loaded.SubjectInputs);
        Assert.Single(loaded.Findings);
        Assert.Equal(original.Findings[0].Id, loaded.Findings[0].Id);
        Assert.Equal(original.Findings[0].Locations[0], loaded.Findings[0].Locations[0]);
        Assert.Equal("thread-1", Assert.Single(loaded.Threads).Id);
        Assert.Equal(json, ReviewMetaJson.Serialize(loaded));
        Assert.True(json.IndexOf("\"$schema\"", StringComparison.Ordinal) <
                    json.IndexOf("\"schemaVersion\"", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"schemaVersion\"", StringComparison.Ordinal) <
                    json.IndexOf("\"unit\"", StringComparison.Ordinal));
        Assert.Contains("\"reviewedAt\": \"2026-07-11T14:32:09.417Z\"", json, StringComparison.Ordinal);
        Assert.Contains("\"band\": \"A\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void LoaderRejectsUnsupportedSchemaVersion()
    {
        var json = ReviewMetaJson.Serialize(CreateDocument())
            .Replace("\"schemaVersion\": 2", "\"schemaVersion\": 3", StringComparison.Ordinal);

        var exception = Assert.Throws<JsonException>(() => ReviewMetaJson.Deserialize(json));

        Assert.Contains("Unsupported review metadata schemaVersion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContentHashNormalizesOnlyBomEncodingAndLineEndings()
    {
        var directory = Directory.CreateTempSubdirectory("quality-studio-hash-");
        try
        {
            var variants = new Dictionary<string, byte[]>
            {
                ["utf8-lf.ts"] = Encoding.UTF8.GetBytes("const x = 1;\n"),
                ["utf8-bom-crlf.ts"] = Encoding.UTF8.Preamble.ToArray().Concat(Encoding.UTF8.GetBytes("const x = 1;\r\n")).ToArray(),
                ["utf16le-cr.ts"] = Encoding.Unicode.Preamble.ToArray().Concat(Encoding.Unicode.GetBytes("const x = 1;\r")).ToArray(),
                ["utf16be-crlf.ts"] = Encoding.BigEndianUnicode.Preamble.ToArray().Concat(Encoding.BigEndianUnicode.GetBytes("const x = 1;\r\n")).ToArray(),
            };

            foreach (var variant in variants)
            {
                var path = Path.Combine(directory.FullName, variant.Key);
                await File.WriteAllBytesAsync(path, variant.Value, TestContext.Current.CancellationToken);
                Assert.Equal(
                    "sha256:95befdd6e691d4d89031a2a2901cc74fc6242109980b060e08ddf87829924483",
                    await ReviewSubjectHasher.ComputeFileContentHashAsync(path, TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void ManifestHashMatchesQs1ConformanceVector()
    {
        var value = ReviewSubjectHasher.ComputeManifestHash(
            "qs-v1/angular/file/7b1bd2568ea481d83c2b97850fafd54c0e1981d94960926ab3b4cc5180daec3e",
            [new SubjectInputHash(
                "src/a.ts",
                "file",
                "sha256:95befdd6e691d4d89031a2a2901cc74fc6242109980b060e08ddf87829924483")]);

        Assert.Equal("8ea241557b3e9f1bd4f3c9bf88f5e36684fd86a59829e98e11fabadd5462531f", value);
    }

    [Fact]
    public void HandWrittenSampleValidatesAgainstSchemaAndLoads()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var sample = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            repositoryRoot, "samples", "review-meta.v1.sample.json")));

        var result = ReviewMetaSchema.Value.Evaluate(sample.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        Assert.True(result.IsValid, result.ToString());
        var loaded = ReviewMetaJson.Deserialize(sample.RootElement.GetRawText());
        Assert.Equal(ReviewKind.Code, loaded.Kind);
        Assert.Equal(1240, loaded.Reviewer.Usage?.InputTokens);
    }

    [Fact]
    public void V2SchemaAcceptsGenericAdapterWhileV1SidecarsRemainReadable()
    {
        var document = CreateDocument() with
        {
            Unit = new ReviewUnit(
                "qs-v1/generic/file/7b1bd2568ea481d83c2b97850fafd54c0e1981d94960926ab3b4cc5180daec3e",
                ReviewAdapter.Generic,
                ReviewLevel.File,
                "src/a.py",
                "a.py"),
        };
        using var json = JsonDocument.Parse(ReviewMetaJson.Serialize(document));
        var result = ReviewMetaV2Schema.Value.Evaluate(
            json.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, result.ToString());
        Assert.Equal(2, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("generic", json.RootElement.GetProperty("unit").GetProperty("adapter").GetString());
    }

    [Fact]
    public void V2SchemaAcceptsMergedSecurityProvenance()
    {
        var resultHash = "sha256:" + new string('b', 64);
        var document = CreateDocument() with
        {
            Kind = ReviewKind.Security,
            Reviewer = new ReviewerIdentity(
                "codex",
                "gpt-5",
                Sensors: [new ReviewerSensorReference("gitleaks", "8.24.2", resultHash)]),
            Security = new SecurityReviewMetadata(
                "block",
                "security-sensor-agent-v1",
                [new SecuritySensorMetadata(
                    "gitleaks",
                    "8.24.2",
                    resultHash,
                    true,
                    null,
                    "block",
                    new Dictionary<string, string> { ["gitleaks"] = "8.24.2" })]),
        };
        using var json = JsonDocument.Parse(ReviewMetaJson.Serialize(document));
        var result = ReviewMetaV2Schema.Value.Evaluate(
            json.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, result.ToString());
        Assert.Equal(resultHash,
            json.RootElement.GetProperty("reviewer").GetProperty("sensors")[0].GetProperty("resultHash").GetString());
        Assert.Equal("block", json.RootElement.GetProperty("security").GetProperty("verdict").GetString());
    }

    [Fact]
    public void GenericAdapterCannotMasqueradeAsV1Contract()
    {
        var json = ReviewMetaJson.Serialize(CreateDocument() with
        {
            Unit = new ReviewUnit(
                "qs-v1/generic/file/7b1bd2568ea481d83c2b97850fafd54c0e1981d94960926ab3b4cc5180daec3e",
                ReviewAdapter.Generic, ReviewLevel.File, "src/a.py", "a.py"),
        }).Replace(ReviewMetaDocument.SchemaId, ReviewMetaDocument.LegacySchemaId, StringComparison.Ordinal)
          .Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1", StringComparison.Ordinal);

        var exception = Assert.Throws<JsonException>(() => ReviewMetaJson.Deserialize(json));

        Assert.Contains("requires review metadata schemaVersion '2'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DependencyRealRunSidecarValidatesAgainstSchemaAndLoads()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var sample = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            repositoryRoot, "samples", "dependency-vulnerability.real-run.review-meta.security.json")));

        var result = ReviewMetaSchema.Value.Evaluate(sample.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        Assert.True(result.IsValid, result.ToString());
        var loaded = ReviewMetaJson.Deserialize(sample.RootElement.GetRawText());
        Assert.Equal(ReviewKind.Security, loaded.Kind);
        Assert.Equal("GHSA-v2hh-gcrm-f6hx", Assert.Single(loaded.Findings).RuleId);
    }

    [Fact]
    public async Task UsageLedgerParsesV1AndAggregatesV2OperationsByDurableReviewRun()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-usage-");
        try
        {
            var timestamp = new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero);
            var legacyEntry = new ReviewUsageEntry("legacy-cli-run", timestamp, "gpt-5", "codex",
                new TokenUsage(100, 25, 40, 5, 1200), "code", "file", "src/a.ts");
            await UsageLedger.AppendAsync(root.FullName, legacyEntry, TestContext.Current.CancellationToken);

            var ledgerPath = UsageLedger.GetLedgerPath(root.FullName, timestamp);
            var legacyLine = Assert.Single(await File.ReadAllLinesAsync(ledgerPath, TestContext.Current.CancellationToken));
            ValidateUsageLedgerLine(legacyLine, "usage-ledger.v1.schema.json");

            await UsageLedger.AppendAsync(root.FullName, new ReviewUsageEntry(
                "cli-run-1", timestamp.AddMinutes(1), "gpt-5", "codex",
                new TokenUsage(50, 10, 20, 2, 600), "code", "file", "src/b.ts",
                "review-sweep-1", 2), TestContext.Current.CancellationToken);
            await UsageLedger.AppendAsync(root.FullName, new ReviewUsageEntry(
                "cli-run-2", timestamp.AddMinutes(2), "gpt-5", "codex",
                new TokenUsage(25, 5, 10, 1, 300), "code", "module", "src",
                "review-sweep-1", 2), TestContext.Current.CancellationToken);

            var ledgerLines = await File.ReadAllLinesAsync(ledgerPath, TestContext.Current.CancellationToken);
            Assert.Equal(legacyLine, ledgerLines[0]);
            ValidateUsageLedgerLine(ledgerLines[1], "usage-ledger.v2.schema.json");
            using (var secondV2 = JsonDocument.Parse(ledgerLines[2]))
            {
                Assert.Equal(2, secondV2.RootElement.GetProperty("schemaVersion").GetInt32());
                Assert.Equal("review-sweep-1", secondV2.RootElement.GetProperty("reviewRunId").GetString());
            }

            await File.AppendAllTextAsync(ledgerPath,
                """{"runId":"incomplete","timestamp":"2026-07-21T10:03:00Z","schemaVersion":1}""" + "\n",
                TestContext.Current.CancellationToken);
            var report = await UsageLedger.QueryAsync(root.FullName, timestamp.AddMinutes(-1), "code", cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(3, report.Runs);
            Assert.Equal(175, report.InputTokens);
            Assert.Equal("gpt-5", Assert.Single(report.ByModel).Key);
            Assert.Equal("code", Assert.Single(report.ByKind).Key);
            Assert.Equal("2026-07-21", Assert.Single(report.ByDay).Key);
            var sweep = Assert.Single(report.ByReviewRun, aggregate => aggregate.Key == "review-sweep-1");
            Assert.Equal(2, sweep.Runs);
            Assert.Equal(75, sweep.InputTokens);
            var legacy = Assert.Single(report.ByReviewRun, aggregate => aggregate.Key == "legacy-cli-run");
            Assert.Equal(1, legacy.Runs);
            Assert.Equal(100, legacy.InputTokens);
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static void ValidateUsageLedgerLine(string line, string schemaFile)
    {
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "schemas", schemaFile)));
        using var json = JsonDocument.Parse(line);
        var validation = schema.Evaluate(json.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(validation.IsValid, validation.ToString());
    }

    private static ReviewMetaDocument CreateDocument() => new()
    {
        Unit = new ReviewUnit(
            "qs-v1/angular/file/7b1bd2568ea481d83c2b97850fafd54c0e1981d94960926ab3b4cc5180daec3e",
            ReviewAdapter.Angular,
            ReviewLevel.File,
            "src/a.ts",
            "a.ts"),
        ReviewedAt = new DateTimeOffset(2026, 7, 11, 14, 32, 9, 417, TimeSpan.Zero),
        Kind = ReviewKind.Code,
        Reviewer = new ReviewerIdentity("codex", "gpt-5", "1.0.0"),
        ReviewedHash = ManifestHash.Subject(
            "8ea241557b3e9f1bd4f3c9bf88f5e36684fd86a59829e98e11fabadd5462531f"),
        SubjectInputs = [new SubjectInputHash(
            "src/a.ts", "file",
            "sha256:95befdd6e691d4d89031a2a2901cc74fc6242109980b060e08ddf87829924483")],
        ReviewInputs = new ReviewInputs(
            ManifestHash.ReviewInput(new string('a', 64)),
            true,
            [],
            [],
            new PromptReference("file-code-review", "1.0.0", "sha256:" + new string('b', 64))),
        Grade = new ReviewGrade(92, GradeBand.A, "Correct and concise."),
        Summary = "A concise review.",
        Aspects = [new ReviewAspect("correctness", "Correctness",
            new ReviewGrade(92, GradeBand.A, "Correct and concise."))],
        Findings = [new ReviewFinding(
            "prefer-const-name",
            "correctness",
            FindingSeverity.Info,
            "Name could express intent",
            "The name is intentionally short for this sample.",
            "Use a domain name in production code.",
            [new FindingLocation("src/a.ts", new FindingRange(
                new FindingPosition(1, 7), new FindingPosition(1, 8)))],
            "sha256:" + new string('e', 64),
            "built-in:code")],
        Threads = [new ReviewThread(
            "thread-1",
            new ReviewThreadAnchor("src/a.ts", "sha256:" + new string('c', 64), "sha256:" + new string('d', 64),
                new FindingRange(new FindingPosition(1, 1), new FindingPosition(1, 8))),
            ReviewThreadStatus.Open,
            [new ReviewThreadEntry("entry-1", new ReviewThreadAuthor(ReviewAuthorKind.Human, Name: "Ada"),
                new DateTimeOffset(2026, 7, 11, 15, 0, 0, TimeSpan.Zero), "Could this be clearer?")],
            ReviewAnchorState.Anchored)],
    };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QualityStudio.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
