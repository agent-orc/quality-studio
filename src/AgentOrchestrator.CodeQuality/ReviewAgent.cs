using System.Text;
using CodingAgentRunner;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;

namespace AgentOrchestrator.CodeQuality;

public interface IReviewAgent
{
    string AgentName { get; }

    string? Model { get; }

    Task<ReviewAgentResult> RunAsync(string prompt, string workingDirectory, CancellationToken cancellationToken = default);
}

public sealed record ReviewAgentResult(string RunId, string Response);

public sealed class CodingAgentReviewAgent : IReviewAgent
{
    private readonly string _cliType;
    private readonly CliRunner _runner;

    public CodingAgentReviewAgent(string cliType = "codex", string? model = null, CliOptions? options = null)
    {
        _cliType = cliType;
        Model = model;
        _runner = new CliRunner(options ?? new CliOptions());
        _runner.Get(cliType); // Fail at construction for unknown adapters.
    }

    public string AgentName => _cliType;

    public string? Model { get; }

    public async Task<ReviewAgentResult> RunAsync(
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var runId = "quality-" + Guid.NewGuid().ToString("N");
        var output = new StringBuilder();
        var driver = _runner.Get(_cliType);
        await foreach (var runEvent in driver.StreamAsync(new CliRunRequest
        {
            RunId = runId,
            Prompt = prompt,
            WorkingDirectory = workingDirectory,
            Model = Model,
            PermissionMode = "read-only",
            ContextMode = "shared",
        }, cancellationToken))
        {
            if (runEvent is CliRunEvent.OutputDelta delta)
            {
                output.Append(delta.Text);
            }
        }

        return new ReviewAgentResult(runId, output.ToString());
    }
}
