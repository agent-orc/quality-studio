namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// The paths named by <c>git status --porcelain=v1 -z</c>.
/// <para>
/// The <c>-z</c> form is the only parseable one: without it Git quotes and octal-escapes any path
/// that is not plain ASCII, so a dirty <c>café.json</c> arrives as <c>"caf\303\251.json"</c> and no
/// amount of trimming recovers the real name. With <c>-z</c> records are NUL-separated and verbatim,
/// and a rename or copy spends a second record on the path it came from - which is a path that was
/// also touched, so both are reported.
/// </para>
/// </summary>
internal static class GitStatusPaths
{
    public static IEnumerable<string> Parse(string status)
    {
        var records = status.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (record.Length < 4) continue;
            yield return record[3..].Replace('\\', '/');
            if (record[0] is 'R' or 'C' || record[1] is 'R' or 'C')
            {
                if (++index < records.Length) yield return records[index].Replace('\\', '/');
            }
        }
    }
}
