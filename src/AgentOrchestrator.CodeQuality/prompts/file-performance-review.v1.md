# File performance review v1

Review `{{FILE_PATH}}` for measurable performance risks: algorithmic complexity, allocations, blocking or serialized work, unnecessary I/O, contention, and unbounded resource use. The complete reviewed content is supplied below. Do not use tools, edit files, or run commands. Avoid micro-optimization advice without concrete impact.

## Reviewed file content

<reviewed-file path="{{FILE_PATH}}">
{{FILE_CONTENT}}
</reviewed-file>

## Review guidelines

Global guidelines:
{{GLOBAL_GUIDELINES}}

Project guidelines:
{{PROJECT_GUIDELINES}}

## Strict output format

Return exactly one fenced `json` block and no other text. Use this exact top-level structure: `{"grade":{"score":0,"band":"F","rationale":"..."},"summary":"...","aspects":[{"id":"performance","title":"Performance","grade":{"score":0,"band":"F","rationale":"..."}}],"findings":[],"threadUpdates":[]}`. In particular, `aspects` is an array, never an object map. `grade` and every aspect grade have integer `score` (0-100), matching `band` (A=90-100, B=80-89, C=70-79, D=60-69, F=0-59), and non-empty `rationale`. Every finding has `id`, `aspect`, `severity` (`critical|high|medium|low|info`), `title`, `description`, `recommendation`, and `locations`. Each location must use repository-relative path `{{FILE_PATH}}` and a one-based `range` with `start` and `end` line/column. File reviews require at least one location per finding. Use an empty findings array when there are no issues. Finding aspect values must name an aspect id. Use an empty `threadUpdates` array when no open thread context was supplied.
