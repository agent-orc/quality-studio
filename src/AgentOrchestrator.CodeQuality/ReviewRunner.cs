using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

public sealed record ReviewRequest(
    string FilePath,
    string Kind = "code",
    ReviewLevel Level = ReviewLevel.File,
    string? GlobalGuidelines = null,
    string? ProjectGuidelines = null,
    string? RepositoryRoot = null);

public sealed record ReviewResult(string MetaPath, string ReviewedHash, string RunId);

public sealed class ReviewRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly IReviewAgent _agent;
    private readonly ReviewPromptBuilder _promptBuilder;
    private readonly ReviewResponseParser _responseParser;

    public ReviewRunner(
        IReviewAgent? agent = null,
        ReviewPromptBuilder? promptBuilder = null,
        ReviewResponseParser? responseParser = null)
    {
        _agent = agent ?? new CodingAgentReviewAgent();
        _promptBuilder = promptBuilder ?? new ReviewPromptBuilder();
        _responseParser = responseParser ?? new ReviewResponseParser();
    }

    public async Task<ReviewResult> ReviewAsync(ReviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.GetFullPath(request.RepositoryRoot ?? Directory.GetCurrentDirectory());
        var file = Path.GetFullPath(Path.IsPathRooted(request.FilePath)
            ? request.FilePath
            : Path.Combine(root, request.FilePath));
        EnsureContained(root, file);
        if (!File.Exists(file))
        {
            throw new FileNotFoundException("Review target does not exist.", file);
        }

        var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
        var fileContent = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        var prompt = _promptBuilder.Build(
            relativePath,
            request.Kind,
            request.GlobalGuidelines,
            request.ProjectGuidelines,
            fileContent);
        var initialContentHash = await ReviewSubjectHasher.ComputeFileContentHashAsync(file, cancellationToken)
            .ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        QualityStudioEventSource.Log.ReviewStarted(relativePath, request.Kind, _agent.AgentName);
        try
        {
            var agentResult = await _agent.RunAsync(prompt, root, cancellationToken).ConfigureAwait(false);
            var response = _responseParser.Parse(agentResult.Response);
            var finalContentHash = await ReviewSubjectHasher.ComputeFileContentHashAsync(file, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(initialContentHash, finalContentHash, StringComparison.Ordinal))
            {
                throw new ReviewRunException("The review target changed while the agent was reviewing it; no metadata was written.");
            }

            var adapter = GetAdapter(file);
            var unitId = $"qs-v1/{adapter}/file/{Sha256($"{adapter}\0{relativePath}")}";
            var subjectInputs = new[] { new SubjectInputHash(relativePath, "file", initialContentHash) };
            var reviewedHash = ReviewSubjectHasher.ComputeManifestHash(unitId, subjectInputs);
            var meta = CreateMeta(
                response,
                relativePath,
                request.Kind,
                adapter,
                unitId,
                initialContentHash,
                reviewedHash,
                prompt,
                agentResult.RunId);
            var metaPath = GetMetaPath(file, request.Kind, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
            var temporaryPath = metaPath + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(
                temporaryPath,
                meta.ToJsonString(JsonOptions) + Environment.NewLine,
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, metaPath, true);
            QualityStudioEventSource.Log.ReviewCompleted(relativePath, request.Kind, agentResult.RunId, stopwatch.ElapsedMilliseconds);
            return new ReviewResult(metaPath, reviewedHash, agentResult.RunId);
        }
        catch (Exception exception)
        {
            QualityStudioEventSource.Log.ReviewFailed(relativePath, request.Kind, exception.GetType().Name, exception.Message);
            throw;
        }
    }

    private JsonObject CreateMeta(
        JsonObject response,
        string relativePath,
        string kind,
        string adapter,
        string unitId,
        string contentHash,
        string reviewedHash,
        string prompt,
        string runId)
    {
        var promptHash = "sha256:" + Sha256(prompt);
        var effectiveHash = Sha256($"quality-studio-review-inputs-v1\0{kind}\0{promptHash}");
        var reviewer = new JsonObject
        {
            ["agent"] = _agent.AgentName,
            ["runId"] = runId,
        };
        if (!string.IsNullOrWhiteSpace(_agent.Model))
        {
            reviewer["model"] = _agent.Model;
        }

        return new JsonObject
        {
            ["$schema"] = "https://agent-orchestrator.dev/quality/schemas/review-meta.v1.schema.json",
            ["schemaVersion"] = 1,
            ["unit"] = new JsonObject
            {
                ["id"] = unitId,
                ["adapter"] = adapter,
                ["level"] = "file",
                ["path"] = relativePath,
                ["displayName"] = Path.GetFileName(relativePath),
            },
            ["reviewedAt"] = DateTime.UtcNow.ToString("O"),
            ["kind"] = kind,
            ["reviewer"] = reviewer,
            ["reviewedHash"] = new JsonObject
            {
                ["algorithm"] = "sha256",
                ["canonicalization"] = "quality-studio-subject-manifest-v1",
                ["value"] = reviewedHash,
            },
            ["subjectInputs"] = new JsonArray(new JsonObject
            {
                ["path"] = relativePath,
                ["selector"] = "file",
                ["contentHash"] = contentHash,
            }),
            ["reviewInputs"] = new JsonObject
            {
                ["effectiveHash"] = new JsonObject
                {
                    ["algorithm"] = "sha256",
                    ["canonicalization"] = "quality-studio-review-inputs-v1",
                    ["value"] = effectiveHash,
                },
                ["complete"] = true,
                ["standards"] = new JsonArray(),
                ["omitted"] = new JsonArray(),
                ["prompt"] = new JsonObject
                {
                    ["id"] = $"file-{kind}-review",
                    ["version"] = "1.0.0",
                    ["contentHash"] = promptHash,
                },
            },
            ["grade"] = response["grade"]!.DeepClone(),
            ["summary"] = response["summary"]!.DeepClone(),
            ["aspects"] = response["aspects"]!.DeepClone(),
            ["findings"] = response["findings"]!.DeepClone(),
        };
    }

    private static string GetMetaPath(string file, string kind, string relativePath)
    {
        var key = Sha256(relativePath);
        return Path.Combine(Path.GetDirectoryName(file)!, ".quality", "reviews", "files", $"file.{key}.review-meta.{kind}.json");
    }

    private static string GetAdapter(string file) =>
        Path.GetExtension(file).ToLowerInvariant() is ".cs" or ".fs" or ".vb" ? "dotnet" : "angular";

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void EnsureContained(string root, string file)
    {
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!file.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Review target must be inside the repository root.");
        }
    }
}

public sealed class ReviewRunException(string message) : Exception(message);
