using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// Every child process the backend starts must run without a console window.
/// The API itself may run without a console (detached, as a service, or behind a
/// launcher). A child started without <c>CreateNoWindow = true</c> then allocates a
/// fresh console, and Windows shows a terminal window for every git call.
/// </summary>
public sealed class ProcessStartGuardTests
{
    private static readonly Regex StartInfoPattern = new(@"new\s+ProcessStartInfo\b", RegexOptions.Compiled);
    private static readonly Regex UseShellExecuteFalse = new(@"\bUseShellExecute\s*=\s*false\b", RegexOptions.Compiled);
    private static readonly Regex CreateNoWindowTrue = new(@"\bCreateNoWindow\s*=\s*true\b", RegexOptions.Compiled);

    [Fact]
    public void Every_process_start_info_disables_shell_execute_and_console_window()
    {
        var root = RepositoryTestContext.FindRepositoryRoot();
        var sourceRoot = Path.Combine(root, "backend", "src");
        Assert.True(Directory.Exists(sourceRoot), $"backend/src not found under {root}");

        var sites = 0;
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (Match match in StartInfoPattern.Matches(text))
            {
                sites++;
                var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                var initializer = ExtractInitializer(text, match.Index + match.Length);
                if (initializer is null)
                {
                    violations.Add($"{relative}:{line}: ProcessStartInfo without an object initializer; set UseShellExecute = false and CreateNoWindow = true inline");
                    continue;
                }

                if (!UseShellExecuteFalse.IsMatch(initializer))
                {
                    violations.Add($"{relative}:{line}: missing UseShellExecute = false");
                }

                if (!CreateNoWindowTrue.IsMatch(initializer))
                {
                    violations.Add($"{relative}:{line}: missing CreateNoWindow = true");
                }
            }
        }

        Assert.True(sites > 0, "No ProcessStartInfo sites found; the guard scans the wrong directory.");
        Assert.True(violations.Count == 0, "Child processes must not open console windows:\n" + string.Join('\n', violations));
    }

    /// <summary>
    /// Returns the object initializer block that follows <c>new ProcessStartInfo(...)</c>,
    /// or null when the construction has none.
    /// </summary>
    private static string? ExtractInitializer(string text, int start)
    {
        var index = SkipWhitespace(text, start);
        if (index < text.Length && text[index] == '(')
        {
            var depth = 0;
            for (; index < text.Length; index++)
            {
                if (text[index] == '(')
                {
                    depth++;
                }
                else if (text[index] == ')' && --depth == 0)
                {
                    index++;
                    break;
                }
            }
        }

        index = SkipWhitespace(text, index);
        if (index >= text.Length || text[index] != '{')
        {
            return null;
        }

        var open = index;
        var braces = 0;
        for (; index < text.Length; index++)
        {
            if (text[index] == '{')
            {
                braces++;
            }
            else if (text[index] == '}' && --braces == 0)
            {
                return text.Substring(open, index - open + 1);
            }
        }

        return null;
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }
}
