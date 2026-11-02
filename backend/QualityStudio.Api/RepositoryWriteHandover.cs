using System.Diagnostics;
using System.Text;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

/// <summary>Plans checkout-owned edits away from the checkout, then asks Studio to apply their patch.</summary>
internal sealed class RepositoryWriteHandover(
    ApiSecurity security, AgentStudioFederation federation, AgentStudioTaskClient tasks,
    AgentStudioTaskOptions options, ILogger<RepositoryWriteHandover> logger)
{
    private static readonly string[] OwnedPaths =
    [
        FindingSuppressionStore.RelativePath,
        ".quality/scope.json",
        ".quality/inputs",
        ".quality/rules",
        ".quality/policy.json",
    ];

    public bool Enabled => !security.IsLocal && federation.Enabled;

    public async Task<IResult> ExecuteAsync(HttpContext context, string repositoryRoot, string action,
        string? reason, Func<string, Task<IResult>> mutation, bool requireReason = false)
    {
        if (!Enabled) return await mutation(repositoryRoot);
        if (requireReason && string.IsNullOrWhiteSpace(reason))
            return Results.BadRequest(new { title = "A reason is required for a repository change request." });
        if (!options.IsTargetConfigured)
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Agent Studio handover target is not configured");
        var shadow = Directory.CreateTempSubdirectory("quality-studio-handover-").FullName;
        try
        {
            foreach (var relative in OwnedPaths) CopyOwned(repositoryRoot, shadow, relative);
            var planned = await mutation(shadow);
            if (planned is IStatusCodeHttpResult { StatusCode: >= 400 }) return planned;
            var patch = await PatchAsync(repositoryRoot, shadow, context.RequestAborted);
            if (patch.Length == 0) return Results.Conflict(new { title = "The proposed change has no repository patch." });
            var actor = security.Identity(context).Session?.Subject ?? security.Identity(context).Id;
            var card = new AgentStudioTaskCard(
                $"Quality Studio: {action}",
                string.Empty,
                $"""
                Apply this Quality Studio repository change.

                Acting user: {actor}
                Reason: {(reason?.Trim() is { Length: > 0 } why ? why : action)}
                Repository: {context.Request.RouteValues.GetValueOrDefault("repoId") ?? "default"}

                Apply the following exact patch and review the result:

                ```diff
                {patch}
                ```
                """);
            var result = await tasks.CreateTaskAsync(card, context.RequestAborted);
            var url = result.TaskId is null ? null : federation.StudioPublicUrl + "/#/tasks/" + Uri.EscapeDataString(result.TaskId);
            return Results.Ok(new { requested = true, result.DryRun, result.TaskId, url, result.Card });
        }
        finally
        {
            try { Directory.Delete(shadow, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not remove repository change planning directory {PlanningDirectory}", shadow);
            }
        }
    }

    public Task<IResult> ExecuteAsync(HttpContext context, string repositoryRoot, string action,
        string? reason, Func<string, IResult> mutation, bool requireReason = false) =>
        ExecuteAsync(context, repositoryRoot, action, reason, root => Task.FromResult(mutation(root)), requireReason);

    private static void CopyOwned(string sourceRoot, string shadowRoot, string relative)
    {
        var source = Path.Combine(sourceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        var target = Path.Combine(shadowRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(source))
        {
            if (new DirectoryInfo(source).LinkTarget is not null)
                throw new InvalidDataException($"Repository configuration '{relative}' is a symbolic link.");
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source))
                CopyOwned(sourceRoot, shadowRoot, Path.GetRelativePath(sourceRoot, file).Replace('\\', '/'));
            foreach (var directory in Directory.EnumerateDirectories(source))
                CopyOwned(sourceRoot, shadowRoot, Path.GetRelativePath(sourceRoot, directory).Replace('\\', '/'));
        }
        else if (File.Exists(source))
        {
            if (new FileInfo(source).LinkTarget is not null)
                throw new InvalidDataException($"Repository configuration '{relative}' is a symbolic link.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
    }

    private static async Task<string> PatchAsync(string beforeRoot, string afterRoot, CancellationToken cancellationToken)
    {
        var files = OwnedPaths.SelectMany(relative => Files(beforeRoot, relative).Concat(Files(afterRoot, relative)))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var patch = new StringBuilder();
        foreach (var relative in files)
        {
            var before = Path.Combine(beforeRoot, relative);
            var after = Path.Combine(afterRoot, relative);
            if (File.Exists(before) && File.Exists(after))
            {
                var beforeBytes = await File.ReadAllBytesAsync(before, cancellationToken);
                var afterBytes = await File.ReadAllBytesAsync(after, cancellationToken);
                if (Enumerable.SequenceEqual(beforeBytes, afterBytes)) continue;
            }
            var start = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "diff", "--no-index", "--binary", "--", File.Exists(before) ? before : "/dev/null", File.Exists(after) ? after : "/dev/null" })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git diff.");
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode is not 1) throw new InvalidOperationException("Could not generate repository patch: " + error);
            var lines = output.Split('\n');
            var inHunk = false;
            for (var index = 0; index < lines.Length; index++)
            {
                if (lines[index].StartsWith("@@ ", StringComparison.Ordinal)) inHunk = true;
                if (inHunk) continue;
                if (lines[index].StartsWith("diff --git ", StringComparison.Ordinal)) lines[index] = $"diff --git a/{relative} b/{relative}";
                else if (lines[index].StartsWith("--- ", StringComparison.Ordinal)) lines[index] = "--- " + (File.Exists(before) ? "a/" + relative : "/dev/null");
                else if (lines[index].StartsWith("+++ ", StringComparison.Ordinal)) lines[index] = "+++ " + (File.Exists(after) ? "b/" + relative : "/dev/null");
            }
            patch.Append(string.Join('\n', lines));
        }
        return patch.ToString();
    }

    private static IEnumerable<string> Files(string root, string relative)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path)) return [relative];
        return Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            : [];
    }
}
