using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Model;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// How a reviewer CLI is isolated from everything except the prompt Quality Studio hands it
/// (defect D12: a repository could steer its own grade through its instruction files).
/// <para>
/// Three layers, all applied to every run:
/// <list type="number">
/// <item>Clean context: the runner relocates the CLI's config home to a per-run temp dir, so the
/// operator's memory, sessions, user instruction file, skills directory and user MCP registry are
/// absent.</item>
/// <item>Launch flags: the CLI is told not to load repository instruction files, project or local
/// settings (hooks, MCP servers, permissions), skills, plugins, or execpolicy rules.</item>
/// <item>Observation: after the run, the CLI's own transcript is read back and the instruction
/// files, skills and MCP servers it actually loaded are recorded. A run that loaded any of them,
/// or whose transcript could not be read, is refused — see <see cref="ReviewerIsolationException"/>.</item>
/// </list>
/// </para>
/// <para>
/// Repository instruction files (<c>CLAUDE.md</c>, <c>AGENTS.md</c>, …) are <b>excluded</b>, not
/// quoted. They stay readable as ordinary repository files — the reviewer may open them, and when
/// one is the review subject it reaches the prompt inside the untrusted-data markers like any other
/// file — but the CLI never promotes them to instructions.
/// </para>
/// See docs/reviewer-isolation.md.
/// </summary>
public static class ReviewerIsolation
{
    /// <summary>The runner context mode every review uses.</summary>
    public const string ContextMode = CliContextModes.Clean;

    /// <summary>What happens to repository instruction files; recorded in the review metadata.</summary>
    public const string RepositoryInstructionPolicy = "excluded";

    /// <summary>
    /// File names a coding-agent CLI treats as instructions when it finds them in a checkout.
    /// Used to list what was present and excluded; the CLI flags, not this list, do the excluding.
    /// </summary>
    internal static readonly IReadOnlyList<string> InstructionFileNames =
        ["CLAUDE.md", "CLAUDE.local.md", "AGENTS.md", "AGENTS.override.md", "GEMINI.md"];

    /// <summary>Repository-relative files and directories that configure a CLI rather than instruct it.</summary>
    internal static readonly IReadOnlyList<string> RepositoryAgentConfiguration =
    [
        ".claude/CLAUDE.md", ".claude/rules", ".claude/skills", ".claude/settings.json",
        ".claude/settings.local.json", ".claude/agents", ".claude/commands", ".mcp.json",
        ".agents/skills", ".codex", ".github/copilot-instructions.md",
    ];

    private static readonly IReadOnlyList<string> ClaudeArguments =
    [
        // Only the operator's base settings (auth-adjacent config); never the checkout's
        // .claude/settings*.json and never CLAUDE.md / CLAUDE.local.md / .claude/rules.
        "--setting-sources", "user",
        // No skills or slash commands, whether operator-installed or shipped in the checkout.
        "--disable-slash-commands",
        // No MCP servers from ~/.claude.json, .mcp.json or settings.
        "--strict-mcp-config", "--mcp-config", """{"mcpServers":{}}""",
        // No hooks, including the operator's user-level ones.
        "--settings", """{"disableAllHooks":true}""",
    ];

    private static readonly IReadOnlyList<string> CodexArguments =
    [
        // The copied operator config.toml carries MCP servers and skill settings; auth still comes
        // from CODEX_HOME. `-c mcp_servers={}` merges instead of replacing, so it is not enough.
        "--ignore-user-config",
        "--ignore-rules",
        "--disable", "hooks",
        "--disable", "plugins",
        // AGENTS.md from the checkout (root to working directory) is never read.
        "-c", "project_doc_max_bytes=0",
        // Repository skills (.agents/skills) are not advertised to the model.
        "-c", "skills.include_instructions=false",
    ];

    private static readonly IReadOnlyDictionary<string, string> ClaudeEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Second, independent switch for the same CLAUDE.md exclusion as --setting-sources.
            ["CLAUDE_CODE_DISABLE_CLAUDE_MDS"] = "1",
        };

    public static bool Supports(string cliType) => cliType is CliTypes.Claude or CliTypes.Codex;

    /// <summary>The isolation arguments for <paramref name="cliType"/>; empty for a CLI without a recipe.</summary>
    public static IReadOnlyList<string> Arguments(string cliType) => cliType switch
    {
        CliTypes.Claude => ClaudeArguments,
        CliTypes.Codex => CodexArguments,
        _ => [],
    };

    /// <summary>Environment variables every isolated run of <paramref name="cliType"/> gets.</summary>
    public static IReadOnlyDictionary<string, string> Environment(string cliType) => cliType == CliTypes.Claude
        ? ClaudeEnvironment
        : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Adds the isolation arguments and environment to a launch the runner has already built.
    /// Claude takes its flags anywhere, so they go first and cannot land between an option and its
    /// value; Codex needs them after the <c>exec</c> subcommand.
    /// </summary>
    internal static void Apply(ProcessStartInfo startInfo, string cliType)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!Supports(cliType))
            throw new ReviewerIsolationException($"The '{cliType}' reviewer CLI has no isolation recipe.");
        var insertAt = 0;
        if (cliType == CliTypes.Codex)
        {
            var exec = startInfo.ArgumentList.IndexOf("exec");
            if (exec < 0)
                throw new ReviewerIsolationException("The codex launch has no 'exec' subcommand to isolate.");
            insertAt = exec + 1;
        }
        var arguments = Arguments(cliType);
        for (var index = 0; index < arguments.Count; index++)
            startInfo.ArgumentList.Insert(insertAt + index, arguments[index]);
        foreach (var (name, value) in Environment(cliType))
            startInfo.Environment[name] = value;
    }

    /// <summary>
    /// The repository instruction and agent-configuration files present under
    /// <paramref name="workingDirectory"/>, repository-relative and sorted. These are what an
    /// unisolated CLI would have loaded; recording them shows what the isolation kept out.
    /// </summary>
    internal static IReadOnlyList<string> FindRepositoryInstructionFiles(string workingDirectory, int maxEntries = 50_000)
    {
        var root = Path.GetFullPath(workingDirectory);
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var relative in RepositoryAgentConfiguration)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path) || Directory.Exists(path)) found.Add(relative);
        }

        var pending = new Stack<string>();
        pending.Push(root);
        var visited = 0;
        while (pending.Count > 0 && visited < maxEntries)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in entries)
            {
                if (++visited > maxEntries) break;
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (!SkippedDirectories.Contains(name) &&
                        !File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
                        pending.Push(entry);
                }
                else if (InstructionFileNames.Contains(name, StringComparer.Ordinal))
                {
                    found.Add(Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/'));
                }
            }
        }
        return found.ToArray();
    }

    private static readonly HashSet<string> SkippedDirectories =
        new([".git", "node_modules", "bin", "obj", ".quality", ".angular", "dist"], StringComparer.Ordinal);
}

/// <summary>
/// A spawner that adds <see cref="ReviewerIsolation"/>'s arguments and environment to every launch,
/// then starts the process exactly as the runner's built-in spawn does (or hands it to the
/// caller's own spawner, e.g. a Windows pseudo-terminal).
/// </summary>
internal sealed class IsolatingCliProcessSpawner(string cliType, ICliProcessSpawner? inner) : ICliProcessSpawner
{
    public CliSpawn Spawn(ProcessStartInfo startInfo)
    {
        ReviewerIsolation.Apply(startInfo, cliType);
        if (inner is not null) return inner.Spawn(startInfo);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        var stdin = startInfo.RedirectStandardInput ? process.StandardInput.BaseStream : Stream.Null;
        return new CliSpawn(process, stdin, process.StandardOutput, process.StandardError);
    }
}

/// <summary>A review run whose isolation could not be established or was observed to fail.</summary>
public sealed class ReviewerIsolationException(string message) : Exception(message);

/// <summary>
/// The context one reviewer run actually saw, recorded as <c>reviewer.context</c> in the review
/// metadata.
/// </summary>
/// <param name="Mode">The runner context mode (<c>clean</c>).</param>
/// <param name="RepositoryInstructions">What happened to repository instruction files (<c>excluded</c>).</param>
/// <param name="Observed">Whether the CLI's own transcript was found, read, well-formed and held
/// its system-prompt record and, for Claude, an init frame with skills and MCP server arrays.
/// When false, the loaded lists cannot be trusted to be empty, so the run is refused; an accepted
/// review always records true.</param>
/// <param name="LoadedInstructionFiles">Instruction files the CLI reported loading. Always empty for
/// an accepted review; repository-relative, or <c>external:&lt;name&gt;</c> outside the checkout.</param>
/// <param name="ExcludedInstructionFiles">Instruction and agent-configuration files present in the
/// checkout that the CLI was told not to load.</param>
/// <param name="Skills">Skills the CLI advertised to the model.</param>
/// <param name="McpServers">MCP servers wired into the run.</param>
/// <param name="SystemPromptCharacters">Characters of the CLI's own system prompt / base
/// instructions as it sent them to the model.</param>
/// <param name="PromptCharacters">Characters of the Quality Studio prompt handed to the CLI.</param>
public sealed record ReviewerContext(
    [property: JsonPropertyOrder(0)] string Mode,
    [property: JsonPropertyOrder(1)] string RepositoryInstructions,
    [property: JsonPropertyOrder(2)] bool Observed,
    [property: JsonPropertyOrder(3)] IReadOnlyList<string> LoadedInstructionFiles,
    [property: JsonPropertyOrder(4)] IReadOnlyList<string> ExcludedInstructionFiles,
    [property: JsonPropertyOrder(5)] IReadOnlyList<string> Skills,
    [property: JsonPropertyOrder(6)] IReadOnlyList<string> McpServers,
    [property: JsonPropertyOrder(7), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SystemPromptCharacters,
    [property: JsonPropertyOrder(8)] int PromptCharacters);

/// <summary>
/// Reads what a finished CLI run loaded from the CLI's own records in its per-run home: Claude's
/// session transcript (<c>projects/*/&lt;session&gt;.jsonl</c>: <c>instructions</c>,
/// <c>nested_memory</c>, <c>skill_listing</c> and <c>prompt_snapshot</c> attachments) plus its
/// stream-json init frame, and Codex's rollout (<c>sessions/**/rollout-*-&lt;thread&gt;.jsonl</c>:
/// <c>session_meta.base_instructions</c> and <c>world_state.agents_md</c>).
/// </summary>
internal sealed class ReviewerContextObservation
{
    private readonly List<string> _loaded = [];
    private readonly SortedSet<string> _skills = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _mcpServers = new(StringComparer.Ordinal);
    private bool _claudeInitObserved;
    private bool _claudeInitMalformed;
    private bool _codexAgentsMdObserved;

    public bool Observed { get; private set; }
    public IReadOnlyList<string> LoadedInstructionFiles => _loaded.Distinct(StringComparer.Ordinal).ToArray();
    public IReadOnlyList<string> Skills => _skills.ToArray();
    public IReadOnlyList<string> McpServers => _mcpServers.ToArray();
    public int? SystemPromptCharacters { get; private set; }

    /// <summary>
    /// Claude's stream-json <c>system/init</c> frame names the skills and MCP servers. A frame that
    /// mentions <c>init</c> but does not parse, or an init frame without both arrays, could have
    /// reported either, so it leaves the run unobserved even next to a valid frame.
    /// </summary>
    public void ObserveClaudeInitFrame(string line)
    {
        if (!line.Contains("\"init\"", StringComparison.Ordinal)) return;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !IsString(root, "type", "system") || !IsString(root, "subtype", "init")) return;
            if (!root.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("mcp_servers", out var servers) || servers.ValueKind != JsonValueKind.Array)
                throw new JsonException("Claude init frame has no skills or mcp_servers array.");
            var skillNames = new List<string>();
            foreach (var skill in skills.EnumerateArray())
            {
                if (skill.ValueKind != JsonValueKind.String)
                    throw new JsonException("Claude init frame has a skill that is not a string.");
                skillNames.Add(skill.GetString()!);
            }
            var serverNames = new List<string>();
            foreach (var server in servers.EnumerateArray())
            {
                if (server.ValueKind != JsonValueKind.Object || !server.TryGetProperty("name", out var name) ||
                    name.ValueKind != JsonValueKind.String)
                    throw new JsonException("Claude init frame has an MCP server without a name.");
                serverNames.Add(name.GetString()!);
            }
            foreach (var skill in skillNames) _skills.Add(skill);
            foreach (var server in serverNames) _mcpServers.Add(server);
            _claudeInitObserved = true;
        }
        catch (JsonException)
        {
            _claudeInitMalformed = true;
        }
    }

    /// <summary>
    /// Reads the CLI's session record from <paramref name="home"/>; a no-op when it is absent. The
    /// record counts as observed only when it is the only record of the session, every line is a
    /// JSON object with the shape the CLI writes (see <see cref="ReadClaudeTranscriptRecord"/> and
    /// <see cref="ReadCodexRolloutRecord"/>), it holds the system-prompt record every run writes,
    /// Claude's init frames established skills and MCP server values, and a Codex rollout holds a
    /// <c>world_state.agents_md</c> record: a malformed line could have hidden an instructions
    /// attachment, and a record without the system prompt, init context or <c>agents_md</c> cannot
    /// establish what the CLI loaded. Anything that stops the record being read to
    /// the end leaves the run unobserved; nothing escapes to the runner's event handler.
    /// </summary>
    public void ReadSessionRecord(string cliType, string? home, string? sessionId, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(home) || string.IsNullOrWhiteSpace(sessionId) || !Directory.Exists(home)) return;
        var file = cliType switch
        {
            CliTypes.Claude => FindFile(Path.Combine(home, "projects"), sessionId + ".jsonl"),
            CliTypes.Codex => FindFile(Path.Combine(home, "sessions"), "rollout-*-" + sessionId + ".jsonl"),
            _ => null,
        };
        if (file is null) return;
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return;
                if (cliType == CliTypes.Claude) ReadClaudeTranscriptRecord(document.RootElement, workingDirectory);
                else ReadCodexRolloutRecord(document.RootElement, workingDirectory);
            }
            Observed = SystemPromptCharacters is not null &&
                (cliType != CliTypes.Claude || _claudeInitObserved && !_claudeInitMalformed) &&
                (cliType != CliTypes.Codex || _codexAgentsMdObserved);
        }
        catch (Exception)
        {
            // Fail closed: whatever could not be read may have been an instruction-loading record.
        }
    }

    /// <summary>
    /// Every transcript record has a string <c>type</c>; an <c>attachment</c> record, and any record
    /// that carries an <c>attachment</c>, has an object attachment with a nonempty string
    /// <c>type</c>. Attachment types that load instructions, list skills or snapshot the system
    /// prompt must be complete; other types are not interpreted.
    /// </summary>
    private void ReadClaudeTranscriptRecord(JsonElement root, string workingDirectory)
    {
        if (!root.TryGetProperty("type", out var recordType) || recordType.ValueKind != JsonValueKind.String)
            throw new JsonException("Claude transcript record has no string type.");
        var hasAttachment = root.TryGetProperty("attachment", out var attachment);
        if (!hasAttachment && recordType.GetString() != "attachment") return;
        if (!hasAttachment || attachment.ValueKind != JsonValueKind.Object ||
            !attachment.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(type.GetString()))
            throw new JsonException("Claude attachment record has no attachment object with a string type.");
        switch (type.GetString())
        {
            case "instructions":
                if (!attachment.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Claude instructions attachment has no files array.");
                foreach (var instruction in files.EnumerateArray())
                {
                    if (instruction.ValueKind != JsonValueKind.Object ||
                        !instruction.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(path.GetString()))
                        throw new JsonException("Claude instructions attachment has a file without a path.");
                    _loaded.Add(Describe(path.GetString()!, workingDirectory));
                }
                break;
            case "nested_memory":
                if (!attachment.TryGetProperty("path", out var nested) || nested.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(nested.GetString()))
                    throw new JsonException("Claude nested-memory attachment has no path.");
                _loaded.Add(Describe(nested.GetString()!, workingDirectory));
                break;
            case "skill_listing":
                if (!attachment.TryGetProperty("content", out var listing) || listing.ValueKind != JsonValueKind.String)
                    throw new JsonException("Claude skill-listing attachment has no string content.");
                foreach (var entry in listing.GetString()!.Split('\n'))
                    if (entry.StartsWith("- ", StringComparison.Ordinal) && entry.IndexOf(':') is > 2 and var colon)
                        _skills.Add(entry[2..colon]);
                break;
            case "prompt_snapshot":
                var characters = attachment.TryGetProperty("systemPrompt", out var prompt) ? prompt.ValueKind switch
                {
                    JsonValueKind.Array when prompt.EnumerateArray().All(part => part.ValueKind == JsonValueKind.String) =>
                        prompt.EnumerateArray().Sum(part => part.GetString()!.Length),
                    JsonValueKind.String => prompt.GetString()!.Length,
                    _ => (int?)null,
                } : null;
                if (characters is null)
                    throw new JsonException("Claude prompt snapshot has no string or string-array systemPrompt.");
                SystemPromptCharacters ??= characters;
                break;
        }
    }

    /// <summary>
    /// Every rollout record has a string <c>type</c> and an object <c>payload</c>; a rollout is only
    /// complete with at least one valid <c>agents_md</c> record, which establishes whether AGENTS.md
    /// was loaded. A
    /// non-null <c>base_instructions</c> must carry string <c>text</c>; a <c>state</c> that is
    /// present must be an object, and its <c>agents_md</c> an object — empty when no AGENTS.md was
    /// loaded, otherwise with string <c>text</c> and an optional string <c>directory</c>.
    /// </summary>
    private void ReadCodexRolloutRecord(JsonElement root, string workingDirectory)
    {
        if (!root.TryGetProperty("type", out var recordType) || recordType.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            throw new JsonException("Codex rollout record has no string type and object payload.");
        if (recordType.GetString() == "session_meta" &&
            payload.TryGetProperty("base_instructions", out var instructions) &&
            instructions.ValueKind != JsonValueKind.Null)
        {
            if (instructions.ValueKind != JsonValueKind.Object ||
                !instructions.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                throw new JsonException("Codex session_meta base_instructions has no string text.");
            SystemPromptCharacters ??= text.GetString()!.Length;
        }
        if (!payload.TryGetProperty("state", out var state)) return;
        if (state.ValueKind != JsonValueKind.Object)
            throw new JsonException("Codex rollout state is not an object.");
        if (!state.TryGetProperty("agents_md", out var agents)) return;
        if (agents.ValueKind != JsonValueKind.Object)
            throw new JsonException("Codex agents_md is not an object.");
        var hasText = agents.TryGetProperty("text", out var agentsText);
        var hasDirectory = agents.TryGetProperty("directory", out var directory);
        if ((hasText || hasDirectory) && (!hasText || agentsText.ValueKind != JsonValueKind.String ||
            hasDirectory && directory.ValueKind != JsonValueKind.String))
            throw new JsonException("Codex agents_md has no string text or a non-string directory.");
        _codexAgentsMdObserved = true;
        if (!hasText) return;
        if (agentsText.GetString()!.Length == 0) return;
        _loaded.Add(hasDirectory
            ? Describe(Path.Combine(directory.GetString()!, "AGENTS.md"), workingDirectory)
            : "external:AGENTS.md");
    }

    /// <summary>Repository-relative inside the checkout; outside it only the file name, so a
    /// sidecar that is committed never carries a home directory or user name.</summary>
    internal static string Describe(string path, string workingDirectory)
    {
        var root = Path.GetFullPath(workingDirectory);
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, full);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? "external:" + Path.GetFileName(full)
            : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>The session's one record; null when there is none or more than one.</summary>
    private static string? FindFile(string directory, string pattern)
    {
        if (!Directory.Exists(directory)) return null;
        try
        {
            var matches = Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsString(JsonElement element, string property, string expected) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() == expected;
}
