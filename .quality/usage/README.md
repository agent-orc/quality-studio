# Usage ledger history

Monthly `YYYY-MM.jsonl` files in this directory are append-only repository
history and must be committed. They are deliberately not ignored. A union merge
driver is configured in the repository so independent line appends from
different branches are retained.

Do not reformat, compact, reorder, or delete historical lines. Commit the active
monthly file with the code and review metadata it records. Quality Studio writes
the ledger but does not run Git commands automatically.
