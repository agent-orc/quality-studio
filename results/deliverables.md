# QS-117 deliverables

- Shadow-only `POST /api/repos/{id}/change-review` and default-repository `POST /api/change-review`, returning the versioned pass/concerns/block contract with exact changed-line citations, stable finding identities, policy and rule-set hashes, and explicit infrastructure outcomes.
- Versioned `.quality/policy.json` threshold contract and schemas. Applicable security-category code findings follow the configured blocking rule and severity thresholds.
- Corrected rule-pool route ownership and regression coverage for default/scoped routes, foreign identities, and security-category policy behavior.
- [20-task Agent Studio shadow comparison](shadow-comparison.md): 5/20 exact agreements (25.0%), 15 individually recorded disagreements, [paired input](shadow-pairs.json), [machine-readable comparison](shadow-comparison.json), and 20 retained endpoint responses in `qs-verdicts/`.
- [Status and verification evidence](status.md). The implemented choice is shadow mode under QS-W6; no Agent Studio gate or review decision consumes the new verdict.
- [Integration recovery evidence](integration-recovery-evidence.txt): the reviewed `3f0130b3` delivery and refreshed `origin/main` are both ancestors of this task branch after a conflict-free merge. The failed pipeline stage was `merge-into-develop`; the original `pipeline-execution.json` was unavailable in this workspace.
