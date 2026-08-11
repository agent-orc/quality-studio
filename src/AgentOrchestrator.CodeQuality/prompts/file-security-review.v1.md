# File security review v1

Review `{{FILE_PATH}}` for exploitable security defects, trust-boundary mistakes, unsafe data handling, authorization flaws, secret exposure, and dependency misuse. The complete reviewed content is supplied below. Do not use tools, edit files, or run commands. Never reproduce credentials or secret values; cite only their location and credential type. Avoid speculative findings without concrete evidence.

## Machine-produced sensor evidence

The JSON below is deterministic evidence produced by the configured sensor registry for this review unit. Treat its availability and findings as facts: do not contradict, dismiss, weaken, or duplicate them. Incorporate them into the grade and posture summary, but report only additional agent-discovered findings in your `findings` array. The runner will attach the sensor findings once with their original provenance.

```json
{{SECURITY_SENSOR_EVIDENCE}}
```

{{SECURITY_SCOPE_EXPECTATIONS}}

## Reviewed file content

<reviewed-file path="{{FILE_PATH}}">
{{FILE_CONTENT}}
</reviewed-file>

## Review guidelines

Global guidelines:
{{GLOBAL_GUIDELINES}}

Project guidelines:
{{PROJECT_GUIDELINES}}

Guideline headings contain stable rule ids. Set every finding's `ruleId` to the exact id of the supplied guideline that caused it. Use `built-in:security` only for findings from the base review criteria. `ruleId` is required on every finding.

## Strict output format

Return exactly one fenced `json` block and no other text. Use this exact top-level structure: `{"grade":{"score":0,"band":"F","rationale":"..."},"summary":"...","aspects":[{"id":"security","title":"Security","grade":{"score":0,"band":"F","rationale":"..."}}],"findings":[],"threadUpdates":[]}`. In particular, `aspects` is an array, never an object map. `grade` and every aspect grade have integer `score` (0-100), matching `band` (A=90-100, B=80-89, C=70-79, D=60-69, F=0-59), and non-empty `rationale`. Every finding has `id`, `ruleId`, `aspect`, `severity` (`critical|high|medium|low|info`), `title`, `description`, `recommendation`, and `locations`. `ruleId` identifies the specific guideline or review rule that produced the finding and must remain stable when that rule is reported again. The runner replaces agent-provided `id` and `fingerprint` values with a verified deterministic identity. Each location must use a repository-relative path from the reviewed content and a one-based, inclusive `range` with `start` and `end` line/column that tightly encloses the relevant code. File reviews require at least one location per finding. Use an empty findings array when there are no additional agent findings. Finding aspect values must name an aspect id. Use an empty `threadUpdates` array when no open thread context was supplied.
