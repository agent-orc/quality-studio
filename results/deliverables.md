# QS-112 deliverables

- Preserved rule-pool management: repository and global overrides with audit trails, runtime custom rules and packs, per-project applicability, versioned rule-set import and export, API and UI, tests, and indexed documentation.
- Closed the review block "Invalid rule-set imports can cause an unhandled null exception". An explicit JSON `null` is now reported as a located diagnostic instead of throwing. This covers custom-rule fields and list entries, pack selector and `projectTypes` entries, applicability pack names, and override ids, through rule-set import, the pack and applicability writes, and hand-edited files. 17 new store test cases (15 of them failed before the fix) and 1 new API test pass.
- Merged current `origin/main` (`bfe24ce5`) into the preserved delivery. The only conflict was `CHANGELOG.md`, and both sections were kept.
- See `status.md` for the finding-to-change mapping and the gate counts.
- Collected results are in `/home/agent/runner-work/tasks/QS-112/results/`: the four gate logs, nine rule-pool UI screenshots, `rule-pool-evidence.json`, `rule-pool-evidence.log`, `rule-pool-exported-rule-set.json`, `status.md` and `deliverables.md`.
