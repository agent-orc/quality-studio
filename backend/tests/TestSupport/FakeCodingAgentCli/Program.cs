using System.Text;
using System.Text.Json;

// Fake claude/codex CLI. Its dialect comes from the name it runs under, like the real CLIs:
// a copy named claude[.exe] or codex[.exe] (see CodingAgentReviewAgentLargePromptTests) acts
// as that CLI for every invocation, including the runner's argv-less `--version` probe. It
// records its argv and its complete stdin to fake-coding-agent-invocation.json in the working
// directory, then emits the minimal stream-json frames of that dialect.
var cli = Path.GetFileNameWithoutExtension(Environment.ProcessPath)?.ToLowerInvariant();
if (cli is not ("claude" or "codex"))
{
    Console.Error.WriteLine(
        $"fake-coding-agent: run it as a copy named claude or codex, not '{Environment.ProcessPath}'.");
    return 64;
}

var isCodex = cli == "codex";
if (args.Length == 1 && args[0] == "--version")
{
    Console.WriteLine(isCodex ? "codex-cli 0.155.0" : "2.1.281 (Claude Code)");
    return 0;
}

Console.InputEncoding = new UTF8Encoding(false);
var stdin = await Console.In.ReadToEndAsync();
await File.WriteAllTextAsync(
    Path.Combine(Directory.GetCurrentDirectory(), "fake-coding-agent-invocation.json"),
    JsonSerializer.Serialize(new { cli, argv = args, stdin }));

const string Reply = "fake review complete";
string[] frames = isCodex
    ?
    [
        """{"type":"thread.started","thread_id":"00000000-0000-0000-0000-000000000001"}""",
        """{"type":"turn.started"}""",
        $$$"""{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"{{{Reply}}}"}}""",
        """{"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}""",
    ]
    :
    [
        """{"type":"system","subtype":"init","session_id":"fake-session","model":"claude-fake"}""",
        $$$"""{"type":"assistant","message":{"content":[{"type":"text","text":"{{{Reply}}}"}]}}""",
        $$$"""{"type":"result","subtype":"success","is_error":false,"result":"{{{Reply}}}","session_id":"fake-session","usage":{"input_tokens":1,"output_tokens":1}}""",
    ];
foreach (var frame in frames) Console.WriteLine(frame);
return 0;
