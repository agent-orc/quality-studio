# Quality Studio named rules

This tree is the source of truth for Quality Studio's language-specific best
practices. Each rule is one English Markdown document with a stable id. The core
package embeds these files so review runs against other registered repositories
receive the same catalogue.
All nine seed rules are explicitly DEFAULT-ON; projects may record exact-id
exceptions in their own versioned `.quality/rules.json` file.

Rule ids are permanent. A rule may be clarified in place with a version and
change-history update, or deprecated, but its id is never reassigned to a
different statement. See [`docs/rule-library.md`](../docs/rule-library.md) for
the normative format, resolution behavior, seed-set inventory, and enforcement
contract.

Current namespaces:

- `QS-NG-*`: Angular and TypeScript
- `QS-CS-*`: C# and .NET
