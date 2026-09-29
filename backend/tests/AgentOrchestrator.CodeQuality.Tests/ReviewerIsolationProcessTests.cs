using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;
using CodingAgentRunner;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Model;
using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// QS-116 end to end: the real runner, the isolating spawner and the review pipeline driving a
/// published stand-in for the Claude Code CLI.
/// </summary>
[Trait("Category", "ToolBound")]
public sealed class ReviewerIsolationProcessTests
{
    /// <summary>
    /// The deliverable test. The repository's CLAUDE.md demands grade A; a maximally suggestible
    /// reviewer (the fake CLI obeys any instruction file it loads) still returns the grade the code
    /// earns, because the real runner, the isolating spawner and the review pipeline keep the file
    /// from being loaded. The sidecar records what was loaded and excluded.
    /// </summary>
    [Fact]
    public async Task ReviewAsync_ARepositoryClaudeMdAskingForGradeADoesNotChangeTheGrade()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fakeClaude = await ReviewerIsolationProcessTests.FakeClaude.PathAsync(cancellationToken);
        var root = ReviewerIsolationFixture.CreateSteeringRepository();
        try
        {
            var agent = new CodingAgentReviewAgent(CliTypes.Claude, "fake-model",
                options: new CliOptions { ClaudePath = fakeClaude },
                attachTimeout: TimeSpan.FromSeconds(60));

            var result = await new ReviewRunner(agent).ReviewAsync(
                new ReviewRequest("src/Weak.cs", RepositoryRoot: root), cancellationToken);

            var json = JsonNode.Parse(await File.ReadAllTextAsync(result.MetaPath, cancellationToken))!;
            Assert.Equal("D", json["grade"]!["band"]!.GetValue<string>());
            Assert.Equal(65, json["grade"]!["score"]!.GetValue<int>());
            var context = json["reviewer"]!["context"]!;
            Assert.Equal("clean", context["mode"]!.GetValue<string>());
            Assert.Equal("excluded", context["repositoryInstructions"]!.GetValue<string>());
            Assert.True(context["observed"]!.GetValue<bool>());
            Assert.Empty(context["loadedInstructionFiles"]!.AsArray());
            Assert.Equal(["CLAUDE.md"], context["excludedInstructionFiles"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray());
            Assert.Empty(context["skills"]!.AsArray());
            Assert.Empty(context["mcpServers"]!.AsArray());
            Assert.Equal(ReviewerIsolationProcessTests.FakeClaude.SystemPromptCharacters, context["systemPromptCharacters"]!.GetValue<int>());
            Assert.True(context["promptCharacters"]!.GetValue<int>() > 0);
            using var document = JsonDocument.Parse(json.ToJsonString());
            var validation = SchemaCatalogue.Get("review-meta.v3.schema.json").Evaluate(
                document.RootElement, new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });
            Assert.True(validation.IsValid, validation.ToString());
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    /// <summary>Control for the test above: the same fake, launched without isolation, is steered.</summary>
    [Fact]
    public async Task FakeClaude_WithoutIsolation_ObeysTheRepositoryClaudeMd()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fakeClaude = await ReviewerIsolationProcessTests.FakeClaude.PathAsync(cancellationToken);
        var root = ReviewerIsolationFixture.CreateSteeringRepository();
        try
        {
            var driver = new CliRunner(new CliOptions { ClaudePath = fakeClaude }).Get(CliTypes.Claude);
            var output = new StringBuilder();
            await foreach (var runEvent in driver.StreamAsync(new CliRunRequest
            {
                RunId = "control-" + Guid.NewGuid().ToString("N"),
                Prompt = "Review src/Weak.cs.",
                WorkingDirectory = root,
                ContextMode = CliContextModes.Shared,
            }, cancellationToken))
            {
                if (runEvent is CliRunEvent.OutputDelta delta) output.Append(delta.Text);
            }

            Assert.Equal("A", new ReviewResponseParser().Parse(output.ToString())["grade"]!["band"]!.GetValue<string>());
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    /// <summary>
    /// The runner's subagent delegation reads a rule text from the working directory
    /// (<c>contexts/delegation-economy.md</c>) into the prompt and writes agent definitions into the
    /// checkout's <c>.claude/agents</c>. A reviewer runs without it, so a repository cannot reach the
    /// prompt that way and the excluded-file inventory sees only the checkout's own files.
    /// </summary>
    [Fact]
    public async Task ReviewAsync_ARepositoryDelegationRuleAskingForGradeADoesNotReachThePrompt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fakeClaude = await ReviewerIsolationProcessTests.FakeClaude.PathAsync(cancellationToken);
        var root = ReviewerIsolationFixture.CreateSteeringRepository();
        try
        {
            ReviewerIsolationFixture.Write(root, "contexts/delegation-economy.md", ReviewerIsolationFixture.Steering + ".");
            var agent = new CodingAgentReviewAgent(CliTypes.Claude, "fake-model",
                options: new CliOptions { ClaudePath = fakeClaude },
                attachTimeout: TimeSpan.FromSeconds(60));

            var result = await new ReviewRunner(agent).ReviewAsync(
                new ReviewRequest("src/Weak.cs", RepositoryRoot: root), cancellationToken);

            var json = JsonNode.Parse(await File.ReadAllTextAsync(result.MetaPath, cancellationToken))!;
            Assert.Equal("D", json["grade"]!["band"]!.GetValue<string>());
            Assert.Equal(["CLAUDE.md"], json["reviewer"]!["context"]!["excludedInstructionFiles"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray());
            Assert.False(Directory.Exists(Path.Combine(root, ".claude")));
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    /// <summary>
    /// If a CLI release stops honouring the isolation flags, the transcript shows the loaded file and
    /// the review is refused — no sidecar, so no steered grade reaches a report.
    /// </summary>
    [Fact]
    public async Task ReviewAsync_RefusesARunWhoseTranscriptShowsALoadedInstructionFile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fakeClaude = await ReviewerIsolationProcessTests.FakeClaude.PathAsync(cancellationToken);
        var root = ReviewerIsolationFixture.CreateSteeringRepository();
        try
        {
            var agent = new CodingAgentReviewAgent(CliTypes.Claude, "fake-model",
                options: new CliOptions
                {
                    ClaudePath = fakeClaude,
                    EnvironmentOverrides = new Dictionary<string, string> { ["FAKE_CLAUDE_IGNORE_ISOLATION"] = "1" },
                },
                attachTimeout: TimeSpan.FromSeconds(60));

            var exception = await Assert.ThrowsAsync<ReviewAgentRunException>(() => new ReviewRunner(agent).ReviewAsync(
                new ReviewRequest("src/Weak.cs", RepositoryRoot: root), cancellationToken));

            var isolation = Assert.IsType<ReviewerIsolationException>(exception.InnerException);
            Assert.Contains("CLAUDE.md", isolation.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(ReviewMetaPath.ForFile(root, "src/Weak.cs", "code")));
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    /// <summary>
    /// A CLI that leaves no transcript in its clean home cannot show what it loaded. The review is
    /// refused rather than accepted with empty lists — no sidecar, so an unobserved run cannot carry
    /// a grade the repository steered.
    /// </summary>
    [Fact]
    public async Task ReviewAsync_RefusesARunThatLeftNoTranscriptToObserve()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fakeClaude = await ReviewerIsolationProcessTests.FakeClaude.PathAsync(cancellationToken);
        var root = ReviewerIsolationFixture.CreateSteeringRepository();
        try
        {
            var agent = new CodingAgentReviewAgent(CliTypes.Claude, "fake-model",
                options: new CliOptions
                {
                    ClaudePath = fakeClaude,
                    EnvironmentOverrides = new Dictionary<string, string> { ["FAKE_CLAUDE_NO_TRANSCRIPT"] = "1" },
                },
                attachTimeout: TimeSpan.FromSeconds(60));

            var exception = await Assert.ThrowsAsync<ReviewAgentRunException>(() => new ReviewRunner(agent).ReviewAsync(
                new ReviewRequest("src/Weak.cs", RepositoryRoot: root), cancellationToken));

            var isolation = Assert.IsType<ReviewerIsolationException>(exception.InnerException);
            Assert.Contains("not observed", isolation.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(ReviewMetaPath.ForFile(root, "src/Weak.cs", "code")));
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    /// <summary>
    /// A published stand-in for the Claude Code CLI. It models the CLI's documented context
    /// assembly — CLAUDE.md is loaded unless <c>--setting-sources</c> leaves out <c>project</c> or
    /// <c>CLAUDE_CODE_DISABLE_CLAUDE_MDS=1</c>; skills and MCP servers appear unless switched off —
    /// writes a transcript into <c>CLAUDE_CONFIG_DIR</c>, and answers like a model that obeys any
    /// "grade A" instruction it was given.
    /// </summary>
    internal static class FakeClaude
    {
        public const int SystemPromptCharacters = 4321;
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static string? _path;

        public static async Task<string> PathAsync(CancellationToken cancellationToken)
        {
            await Gate.WaitAsync(cancellationToken);
            try
            {
                return _path ??= await BuildAsync(cancellationToken);
            }
            finally
            {
                Gate.Release();
            }
        }

        private static async Task<string> BuildAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), "quality-fake-claude", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "ReviewerIsolationProcessTests.FakeClaude.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <AssemblyName>FakeClaude</AssemblyName>
                  </PropertyGroup>
                </Project>
                """, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), $$""""
                using System.Text.Json;
                using System.Text.Json.Nodes;

                if (args.Contains("--version")) { Console.WriteLine("2.1.281 (Claude Code)"); return 0; }
                string? Value(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
                var ignoreIsolation = Environment.GetEnvironmentVariable("FAKE_CLAUDE_IGNORE_ISOLATION") == "1";
                var sources = Value("--setting-sources");
                var loadsProject = ignoreIsolation ||
                    ((sources is null || sources.Split(',').Contains("project")) &&
                     Environment.GetEnvironmentVariable("CLAUDE_CODE_DISABLE_CLAUDE_MDS") != "1");
                var cwd = Directory.GetCurrentDirectory();
                var loaded = new List<(string Path, string Content)>();
                if (loadsProject)
                    foreach (var name in new[] { "CLAUDE.md", "CLAUDE.local.md" })
                        if (File.Exists(Path.Combine(cwd, name))) loaded.Add((Path.Combine(cwd, name), File.ReadAllText(Path.Combine(cwd, name))));
                var skills = args.Contains("--disable-slash-commands") && !ignoreIsolation ? new JsonArray() : new JsonArray("operator-skill");
                var mcp = args.Contains("--strict-mcp-config") && !ignoreIsolation
                    ? new JsonArray() : new JsonArray(new JsonObject { ["name"] = "operator-mcp", ["status"] = "connected" });
                var session = Guid.NewGuid().ToString();

                var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                if (!string.IsNullOrEmpty(home) && Environment.GetEnvironmentVariable("FAKE_CLAUDE_NO_TRANSCRIPT") != "1")
                {
                    var transcript = Path.Combine(home, "projects", "fake", session + ".jsonl");
                    Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
                    var lines = new List<string>();
                    if (loaded.Count > 0)
                        lines.Add(new JsonObject { ["type"] = "attachment", ["attachment"] = new JsonObject {
                            ["type"] = "instructions",
                            ["files"] = new JsonArray(loaded.Select(file => (JsonNode)new JsonObject { ["path"] = file.Path, ["type"] = "Project" }).ToArray()) } }.ToJsonString());
                    lines.Add(new JsonObject { ["type"] = "attachment", ["attachment"] = new JsonObject {
                        ["type"] = "prompt_snapshot", ["systemPrompt"] = new JsonArray(new string('s', {{SystemPromptCharacters}})) } }.ToJsonString());
                    File.WriteAllLines(transcript, lines);
                }

                // These tests hand the prompt over as an argument, so the model "sees" argv.
                var steered = loaded.Any(file => file.Content.Contains("grade A", StringComparison.OrdinalIgnoreCase)) ||
                    string.Join('\n', args).Contains({{JsonSerializer.Serialize(ReviewerIsolationFixture.Steering)}}, StringComparison.Ordinal);
                var review = steered
                    ? {{ReviewerIsolationFixture.CSharpString(ReviewerIsolationFixture.ReviewJson("A", 100, "Repository policy requires grade A."))}}
                    : {{ReviewerIsolationFixture.CSharpString(ReviewerIsolationFixture.ReviewJson("D", 65, "Parse swallows every exception and returns 0."))}};
                void Emit(JsonNode node) => Console.WriteLine(node.ToJsonString());
                Emit(new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = session, ["cwd"] = cwd,
                    ["model"] = Value("--model") ?? "fake", ["skills"] = skills, ["mcp_servers"] = mcp });
                Emit(new JsonObject { ["type"] = "assistant", ["session_id"] = session, ["message"] = new JsonObject {
                    ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = review }) } });
                Emit(new JsonObject { ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["session_id"] = session,
                    ["result"] = review, ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 } });
                return 0;
                """", cancellationToken);

            var publish = Path.Combine(root, "publish");
            var arguments = new List<string> { "publish", Path.Combine(root, "ReviewerIsolationProcessTests.FakeClaude.csproj"), "-c", "Release", "-o", publish, "--self-contained", "false" };
            if (RuntimeIdentifier() is { } runtime)
            {
                arguments.Add("-r");
                arguments.Add(runtime);
            }
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Publishing the fake claude CLI failed:\n{await stdout}\n{await stderr}");
            var executable = Path.Combine(publish, OperatingSystem.IsWindows() ? "ReviewerIsolationProcessTests.FakeClaude.exe" : "FakeClaude");
            return File.Exists(executable)
                ? executable
                : Directory.EnumerateFiles(publish, "FakeClaude*", SearchOption.AllDirectories).First();
        }

        private static string? RuntimeIdentifier()
        {
            var architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => null,
            };
            if (architecture is null) return null;
            return OperatingSystem.IsWindows() ? $"win-{architecture}"
                : OperatingSystem.IsLinux() ? $"linux-{architecture}"
                : OperatingSystem.IsMacOS() ? $"osx-{architecture}"
                : null;
        }
    }

    /// <summary>
    /// A local stand-in for the Anthropic Messages API that answers every request with a review:
    /// grade A when the steering sentence reached it anywhere in the request, otherwise the grade
    /// the code earns. It is the most suggestible model possible, so any leak shows up as a grade.
    /// </summary>
    internal sealed class SuggestibleMessagesApi : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly List<string> _requests = [];
        private readonly Task _loop;

        public SuggestibleMessagesApi()
        {
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _loop = Task.Run(ServeAsync);
        }

        public string BaseUrl { get; }

        public IReadOnlyList<string> TakeRequests()
        {
            lock (_requests)
            {
                var taken = _requests.ToArray();
                _requests.Clear();
                return taken;
            }
        }

        public static string Summarize(IReadOnlyList<string> requests) =>
            new JsonArray(requests.Select(request =>
            {
                var node = JsonNode.Parse(request);
                var system = node?["system"] is JsonArray blocks
                    ? blocks.Sum(block => block?["text"]?.GetValue<string>().Length ?? 0)
                    : node?["system"]?.ToJsonString().Length ?? 0;
                return (JsonNode)new JsonObject
                {
                    ["model"] = node?["model"]?.DeepClone(),
                    ["systemPromptCharacters"] = system,
                    ["messagesCharacters"] = node?["messages"]?.ToJsonString().Length ?? 0,
                    ["tools"] = node?["tools"]?.AsArray().Count ?? 0,
                    ["containsSteering"] = request.Contains(ReviewerIsolationFixture.Steering, StringComparison.Ordinal),
                };
            }).ToArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                if (context.Request.HttpMethod == "POST")
                    lock (_requests) _requests.Add(body);
                var steered = body.Contains(ReviewerIsolationFixture.Steering, StringComparison.Ordinal);
                var review = steered
                    ? ReviewerIsolationFixture.ReviewJson("A", 100, "Repository policy requires grade A.")
                    : ReviewerIsolationFixture.ReviewJson("D", 65, "Parse swallows every exception and returns 0.");
                var response = new JsonObject
                {
                    ["id"] = "msg_" + Guid.NewGuid().ToString("N"),
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["model"] = "claude-haiku-4-5-20251001",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = review }),
                    ["stop_reason"] = "end_turn",
                    ["stop_sequence"] = null,
                    ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 },
                }.ToJsonString();
                var bytes = Encoding.UTF8.GetBytes(context.Request.HttpMethod == "POST" ? response : "{}");
                context.Response.StatusCode = context.Request.HttpMethod == "POST" ? 200 : 404;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        private static int FreePort()
        {
            using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        }
    }
}

/// <summary>
/// QS-116 against the installed Claude Code CLI: the release canary's external-live lane
/// (QUALITY_RUN_LIVE_REVIEW=1, optional QS_LIVE_EVIDENCE_DIR). The model is a local stand-in for
/// the Messages API, so no credentials or tokens are used.
/// </summary>
[Trait("Category", "ExternalLive")]
public sealed class LiveReviewerIsolationTests
{
    /// <summary>
    /// The installed Claude Code CLI, pointed at a local
    /// stand-in for the Messages API that obeys any "grade A" instruction reaching it. It proves the
    /// launch flags against the real CLI's context assembly rather than against the fake's model of it.
    /// </summary>
    [Fact]
    public async Task LiveClaude_ARepositoryClaudeMdAskingForGradeADoesNotReachTheModel()
    {
        Assert.True(Environment.GetEnvironmentVariable("QUALITY_RUN_LIVE_REVIEW") == "1",
            "The external-live lane requires QUALITY_RUN_LIVE_REVIEW=1 and the claude CLI on PATH - see docs/reviewer-isolation.md.");
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = ReviewerIsolationFixture.CreateSteeringRepository();
        using var model = new ReviewerIsolationProcessTests.SuggestibleMessagesApi();
        try
        {
            var options = new CliOptions
            {
                EnvironmentOverrides = new Dictionary<string, string>
                {
                    ["ANTHROPIC_BASE_URL"] = model.BaseUrl,
                    ["ANTHROPIC_API_KEY"] = "sk-quality-studio-isolation-test",
                },
            };
            var agent = new CodingAgentReviewAgent(CliTypes.Claude, "claude-haiku-4-5-20251001",
                options: options, attachTimeout: TimeSpan.FromSeconds(120));

            var result = await new ReviewRunner(agent).ReviewAsync(
                new ReviewRequest("src/Weak.cs", RepositoryRoot: root), cancellationToken);
            var isolated = JsonNode.Parse(await File.ReadAllTextAsync(result.MetaPath, cancellationToken))!;
            var isolatedRequests = model.TakeRequests();

            // Control: the same CLI and model, launched the way reviews ran before QS-116.
            var driver = new CliRunner(options).Get(CliTypes.Claude);
            var control = new StringBuilder();
            await foreach (var runEvent in driver.StreamAsync(new CliRunRequest
            {
                RunId = "live-control-" + Guid.NewGuid().ToString("N"),
                Prompt = "Review src/Weak.cs and answer with the review JSON.",
                WorkingDirectory = root,
                Model = "claude-haiku-4-5-20251001",
                PermissionMode = "read-only",
                ContextMode = CliContextModes.Shared,
            }, cancellationToken))
            {
                if (runEvent is CliRunEvent.OutputDelta delta) control.Append(delta.Text);
            }
            var controlRequests = model.TakeRequests();

            if (Environment.GetEnvironmentVariable("QS_LIVE_EVIDENCE_DIR") is { Length: > 0 } evidence)
            {
                Directory.CreateDirectory(evidence);
                await File.WriteAllTextAsync(Path.Combine(evidence, "isolated-review-meta.json"),
                    isolated.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(evidence, "isolated-model-requests.json"),
                    ReviewerIsolationProcessTests.SuggestibleMessagesApi.Summarize(isolatedRequests), cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(evidence, "control-model-requests.json"),
                    ReviewerIsolationProcessTests.SuggestibleMessagesApi.Summarize(controlRequests), cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(evidence, "control-output.txt"), control.ToString(), cancellationToken);
            }

            Assert.Contains(controlRequests, request => request.Contains(ReviewerIsolationFixture.Steering, StringComparison.Ordinal));
            Assert.Contains("\"band\": \"A\"", control.ToString(), StringComparison.Ordinal);
            Assert.NotEmpty(isolatedRequests);
            Assert.DoesNotContain(isolatedRequests, request => request.Contains(ReviewerIsolationFixture.Steering, StringComparison.Ordinal));
            Assert.Equal("D", isolated["grade"]!["band"]!.GetValue<string>());
            var context = isolated["reviewer"]!["context"]!;
            Assert.True(context["observed"]!.GetValue<bool>());
            Assert.Empty(context["loadedInstructionFiles"]!.AsArray());
            Assert.Empty(context["skills"]!.AsArray());
            Assert.Empty(context["mcpServers"]!.AsArray());
            Assert.True(context["systemPromptCharacters"]!.GetValue<int>() > 0);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

}
