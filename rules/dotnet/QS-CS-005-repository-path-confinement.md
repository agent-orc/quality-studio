---
id: QS-CS-005
version: 1.0.0
title: Confine every repository path through the shared confinement helper
technology: dotnet
kinds: [security]
category: path-confinement
severity: critical
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: dotnet-api-safety
since: 1.2.0
---

## Statement

A path that reaches the filesystem from a request, a configuration file, or a stored document is
canonicalised with `Path.GetFullPath`, checked to sit under its configured root, and walked
segment by segment to reject symbolic links and junctions — through `PathConfinement`, not through
a second implementation. Reject rooted paths and `..` segments instead of normalising them away
silently.

## Rationale

`RepositoryAccess.NormalizeRelativePath` and `RepositoryAccess.ResolveFile` funnel every
Studio file read through `PathConfinement.IsWithin` and `PathConfinement.RejectReparseTraversal`,
which is why `GET /api/file?path=../other-repo/Second.cs` answers 400 instead of serving another
repository's source. A prefix comparison on its own is not enough on Windows, where case differs
and a junction inside the root points anywhere; a second, slightly different copy of the check is
how one call site ends up with the weaker half.

## Detection

Look for `Path.Combine` or `Path.GetFullPath` on a value that arrived from outside the process
followed by a file or directory operation with no containment check, for `StartsWith` comparisons
that skip the trailing separator or use the wrong `StringComparison`, and for a private
`IsWithin`/`ContainedPath` helper duplicating `PathConfinement`. A path built entirely from
constants is not a violation.

## Good example

```csharp
// src/QualityStudio.Api/PathConfinement.cs
public static void RejectReparseTraversal(string root, string candidate)
{
    if (!IsWithin(root, candidate)) throw new ArgumentException("Path escapes its configured root.");
    var current = Path.GetFullPath(root);
    foreach (var segment in Path.GetRelativePath(root, candidate).Split(
                 [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
    {
        current = Path.Combine(current, segment);
        if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            throw new ArgumentException("Paths cannot traverse symbolic links or junctions.");
    }
}
```

## Bad example

```csharp
var absolute = Path.Combine(repositoryRoot, request.Path);   // ".." is still in there
if (absolute.StartsWith(repositoryRoot))                     // no separator, no case rule, no link check
{
    return await File.ReadAllTextAsync(absolute, cancellationToken);
}
```

## Change history

- 1.0.0 (2026-09-06): Initial rule, grounded in `PathConfinement` and the `RepositoryAccess`
  call sites that route every repository read through it.
