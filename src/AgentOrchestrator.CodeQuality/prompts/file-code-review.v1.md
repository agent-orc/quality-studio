# File code review v1

Review `{{FILE_PATH}}` for correctness, maintainability, clarity, error handling, and testability. The complete reviewed content is supplied below. Do not use tools, edit files, or run commands.

## Reviewed file content

<reviewed-file path="{{FILE_PATH}}">
{{FILE_CONTENT}}
</reviewed-file>

## Review guidelines

Global guidelines:
{{GLOBAL_GUIDELINES}}

Project guidelines:
{{PROJECT_GUIDELINES}}

Guideline headings contain stable rule ids. Set every finding's `ruleId` to the exact id of the supplied guideline that caused it. Use `built-in:code` only for findings from the base review criteria. `ruleId` is required on every finding.

## Strict output format

Return exactly one fenced `json` block and no other text. Use this exact top-level structure: `{"grade":{"score":0,"band":"F","rationale":"..."},"summary":"...","aspects":[{"id":"correctness","title":"Correctness","grade":{"score":0,"band":"F","rationale":"..."}}],"findings":[],"threadUpdates":[]}`. In particular, `aspects` is an array, never an object map. `grade` and every aspect grade have integer `score` (0-100), matching `band` (A=90-100, B=80-89, C=70-79, D=60-69, F=0-59), and non-empty `rationale`. Every finding has `id`, `ruleId`, `aspect`, `severity` (`critical|high|medium|low|info`), `title`, `description`, `impact`, `recommendation`, `locations`, and `reproduction`. `reproduction` is either `{"status":"specified","steps":["..."],"expected":"...","observed":"..."}`, `{"status":"not-applicable","reason":"..."}`, `{"status":"blocked","reason":"..."}`, or `{"status":"unknown"}`. Because this review cannot execute commands, never claim `verified` status or include execution attempts. Optional free-text `evidence` is treated as an unverified agent claim; the runner adds trusted captured-span and deterministic evidence separately. `ruleId` identifies the specific guideline or review rule that produced the finding and must remain stable when that rule is reported again. The runner replaces agent-provided `id` and `fingerprint` values with a verified deterministic identity. Each location must use repository-relative path `{{FILE_PATH}}` and a one-based, inclusive `range` with `start` and `end` line/column that tightly encloses the relevant code. File reviews require at least one location per finding. Use an empty findings array when there are no issues. Finding aspect values must name an aspect id. Use an empty `threadUpdates` array when no open thread context was supplied.
