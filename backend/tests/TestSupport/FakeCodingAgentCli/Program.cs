using System.Text;
using System.Text.Json;

// Fake claude/codex CLI. It answers the runner's `--version` health probe, records its
// argv and its complete stdin to fake-coding-agent-invocation.json in the working
// directory, then emits the minimal stream-json frames of the dialect it was launched as
// (codex is launched with `exec` first; everything else is treated as claude).
var isCodex = args.Length > 0 && args[0] == "exec";
if (args.Length == 1 && args[0] == "--version")
{
    Console.WriteLine(isCodex ? "codex-cli 0.155.0" : "2.1.281 (Claude Code)");
    return 0;
}

Console.InputEncoding = new UTF8Encoding(false);
var stdin = await Console.In.ReadToEndAsync();
await File.WriteAllTextAsync(
    Path.Combine(Directory.GetCurrentDirectory(), "fake-coding-agent-invocation.json"),
    JsonSerializer.Serialize(new { cli = isCodex ? "codex" : "claude", argv = args, stdin }));

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
