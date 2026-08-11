using System.Diagnostics;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;

var findings = Enumerable.Range(1, 30).Select(index => new
{
    id = $"finding-{index}",
    ruleId = $"correctness.rule-{index}",
    aspect = "correctness",
    severity = index % 5 == 0 ? "high" : "medium",
    title = $"Finding {index}",
    description = "A representative review finding with enough content to exercise strict contract parsing.",
    recommendation = "Apply the focused correction and retain the regression test.",
    locations = new[]
    {
        new
        {
            path = "src/CartTotals.cs",
            range = new
            {
                start = new { line = index, column = 1 },
                end = new { line = index, column = 12 },
            },
        },
    },
}).ToArray();
var response = JsonSerializer.Serialize(new
{
    grade = new { score = 82, band = "B", rationale = "Representative response." },
    summary = "Representative response used to isolate strict parser cost.",
    aspects = new[]
    {
        new
        {
            id = "correctness",
            title = "Correctness",
            grade = new { score = 82, band = "B", rationale = "Representative aspect." },
        },
    },
    findings,
});
var parser = new ReviewResponseParser();
for (var iteration = 0; iteration < 200; iteration++) _ = parser.Parse(response);
const int repetitions = 10_000;
var samples = new double[repetitions];
for (var iteration = 0; iteration < repetitions; iteration++)
{
    var started = Stopwatch.GetTimestamp();
    _ = parser.Parse(response);
    samples[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
Array.Sort(samples);
var result = new
{
    measuredAt = DateTimeOffset.UtcNow,
    responseBytes = System.Text.Encoding.UTF8.GetByteCount(response),
    findings = findings.Length,
    repetitions,
    parserMilliseconds = new
    {
        min = Round(samples[0]),
        median = Round(Percentile(samples, 0.5)),
        p95 = Round(Percentile(samples, 0.95)),
        max = Round(samples[^1]),
    },
};
var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);
var resultsRoot = Environment.GetEnvironmentVariable("JOB_RESULTS_DIR");
if (!string.IsNullOrWhiteSpace(resultsRoot))
{
    Directory.CreateDirectory(resultsRoot);
    File.WriteAllText(Path.Combine(resultsRoot, "review-parser-latency.json"), json + Environment.NewLine);
}

static double Percentile(double[] sorted, double quantile)
{
    var index = (sorted.Length - 1) * quantile;
    var lower = (int)Math.Floor(index);
    var upper = (int)Math.Ceiling(index);
    return lower == upper ? sorted[lower] : sorted[lower] + ((sorted[upper] - sorted[lower]) * (index - lower));
}

static double Round(double value) => Math.Round(value, 4);
