# Quality Studio to Agent Studio handover

Quality Studio remains the engineer's review room. It does not embed a review
surface into Agent Studio. After triage, an engineer turns a selected finding
into a normal Agent Studio card and Agent Studio owns its execution lifecycle.

## Contract

The client follows Agent Studio's task contract as inspected at upstream commit
`7f8aca886351818fb5a07e724a4e2a13b0febbb5` (2026-07-11):

- `POST {BaseUrl}/api/tasks`
- `X-Client-Id: {ClientId}` on the mutation; the configured id must already be
  registered with Agent Studio
- request fields used by Quality Studio: `title`, preferred path-free `project`,
  `promptMarkdown`, and `taskType: bug`
- success response: `200 { "id": "<task slug>" }`

`BaseUrl`, `ClientId`, and `Project` are configuration only and are never
compiled into the client. A target is considered configured only when all
three are present and the base URL is absolute. The host configuration is:

```json
{
  "AgentStudio": {
    "BaseUrl": "http://127.0.0.1:5030",
    "ClientId": "<registered-client-id>",
    "Project": "<project-id-or-short-code>",
    "DryRun": true
  }
}
```

Dry-run is `true` by default. It prints the exact create-card JSON but performs
no HTTP mutation. Set `DryRun` explicitly to `false` to create cards.

## Finding-to-card template

The title is `Fix: <finding summary> in <file>`. The prompt records the
repository-relative file path, complete finding text, review kind, and review
meta reference. Its acceptance criteria require a review rerun and require that
rerun to come back `fresh+clean`.

The browser calls `GET /api/handover` to discover whether a target exists. It
shows **Create task** on metadata findings only when configured. Clicking it
sends the snapshot to `POST /api/handover`; the host confines the file path to
the configured repository, applies the template, then either prints or posts
the card. The `FindingHandedOver` structured log records file, kind, dry-run
state, returned task id, and elapsed time.

## Project discovery contract

The same `AgentStudio:BaseUrl` also backs a second, read-only contract used to
discover local repositories to onboard, independent of the handover target
(`ClientId`/`Project` are not required for this call):

- `GET {BaseUrl}/api/projects`
- no auth header — this is a read
- response fields used by Quality Studio: `id`, `displayName`, `shortCode`,
  nullable `repositoryPath`, and `archived`
- success response: `200 [ { "id": "...", "displayName": "...", ... }, ... ]`

`POST /api/repos/import-from-agent-studio` (see [api.md](../api.md#one-click-import-from-agent-studio))
calls this endpoint to onboard every non-archived project with a valid,
existing `repositoryPath` as a Quality Studio repository registration, by
path so re-imports stay idempotent. The project list is fetched in full
before any registry write, so an offline or unconfigured Agent Studio target
fails the request cleanly with zero partial writes.

## Flow and ownership

1. A review writes project-owned review metadata below the external data root.
2. The engineer inspects and triages its findings in Quality Studio.
3. The engineer hands over only the actionable findings they choose.
4. Agent Studio receives an ordinary backlog card through its normal mutation
   boundary and owns prioritization, execution, and archival.
5. Completion is verified by rerunning the Quality Studio review; the fresh,
   clean review statement remains associated with the code in the external mirror.

Handover is preferable to embedding because each product keeps one clear job.
Quality Studio owns durable quality truth and review UX; Agent Studio owns task
state and agent execution. A small snapshot contract avoids coupled releases,
duplicate task state, and an Agent Studio-only route to review data, while the
file and meta references retain enough provenance to act and verify.
