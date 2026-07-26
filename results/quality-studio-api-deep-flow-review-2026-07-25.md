# Quality Studio API deep-flow review — 2026-07-25

This is the required dogfood review of the Quality Studio API. It is a manual
application of `flow-business-logic-review` v1.0.0 by Codex (GPT-5), completed
at `2026-07-25T14:29:05Z`. It is deliberately not presented as a persisted
`FlowReviewRunner` run: this orchestrator did not expose per-turn token metrics,
so usage and monetary cost for this dogfood pass are **undetermined**, not zero.

Input provenance:

- source set: `Program.cs`, `ApiSecurity.cs`, `ReviewJobs.cs`,
  `ReviewRunStore.cs`, `RepositoryRegistry.cs`, and `RepositoryAccess.cs`;
- input hash: `sha256:1696984e80240da79ca32f1ab0c08fe479061c3da4f3db4fb15a817e0839074b`
  (`sha256` over the ordered `sha256sum` manifest of those paths);
- boundary analyser/schema hash:
  `sha256:b786fa899670860f04373bde5b3e2c171780c1481d47648bab15a95a20cf2f1a`;
- prompt template hash:
  `sha256:ced59a8ea023418bcfddf3f43cf4aded1aa9ee3fee14dc42f8be245c39b11f16`;
- cost status: `usageUnavailable`; and
- false-positive candidates examined: **1**, retained below.

This evidence becomes stale when any source in the manifest, the boundary
catalogue, or the prompt changes.

## Flow matrix

| Flow | Verdict | Findings | False positives | Reason |
| --- | --- | ---: | ---: | --- |
| Update an existing repository registration | fail | 1 | 0 | A repository-scoped client can repoint its authorized id at another allowed repository without registrar privilege. |
| Start a review run | fail | 1 | 0 | Replaying the same mutation creates and queues another independently billable run. |
| Authenticate a hosted API request | fail | 1 | 0 | Static bearer credentials have no application-level expiry or live revocation. |
| Access a review run by id | pass | 0 | 1 | The apparent IDOR is stopped by repository authorization both before and inside run lookup. |
| Provision/rotate a hosted credential | undetermined | 0 | 0 | Issuance, secret storage, deployment, and restart/rotation procedure are outside this repository. |

## Findings

### High — repository-scoped client can repoint its repository

Class: horizontal privilege escalation. Weakest point:
`src/QualityStudio.Api/Program.cs:163`.

Flow path:

1. Entry — `Program.cs:209`: `PUT /api/repos/{repoId}` accepts a complete
   repository registration.
2. Authentication — `Program.cs:139`: the bearer credential is authenticated;
   `Program.cs:151` also requires the matching mutation client id.
3. Authorization — `Program.cs:163`: `CanRegisterRepositories` is checked only
   for collection POST/import. A PUT falls through to the `CanAccess(repoId)`
   check at line 172.
4. State transition — `RepositoryRegistry.cs:119`: the existing authorized
   registration is loaded; lines 125-130 validate and replace it.
5. Persistence — `RepositoryRegistry.cs:131`: the replacement, including its
   new root path, is persisted.
6. Response — `Program.cs:211`: the changed registration is returned. Later
   file requests using the same authorized id read from the new root.

An API client scoped to repository A but lacking registrar privilege can set A's
`rootPath` to repository B (or another Git repository under an allowed root).
The allowed-root check is path confinement, not tenant ownership. Require
registrar privilege for PUT/DELETE of repository registrations, or make the
root and privilege-bearing fields immutable to repository-scoped clients.

### High — review-start mutation is replayable

Class: replay/quota abuse. Weakest point:
`src/QualityStudio.Api/ReviewJobs.cs:145`.

Flow path:

1. Entry — `Program.cs:246`: `POST /api/review` enters the spend-rate-limited
   route.
2. Authentication/authorization — `Program.cs:139-175`: bearer, mutation
   client id, and repository access are checked.
3. State transition — `Program.cs:721`: the request is passed to
   `EnqueueAsync`.
4. Persistence — `ReviewJobs.cs:145`: every call receives a fresh random run
   id; lines 169-173 create durable state and queue it.
5. Response — `Program.cs:723`: each replay returns a different accepted run.

The rate limit bounds frequency but does not make a retry safe. A client or
proxy retry after a lost `202` can run the same expensive sweep twice; per-run
caps do not deduplicate the spend. Accept an idempotency key scoped to client,
repository, and canonical request, persist its result atomically with enqueue,
and return the original run for matching replays.

### High — hosted bearer credential lacks live expiry/revocation

Class: session lifecycle. Weakest point:
`src/QualityStudio.Api/ApiSecurity.cs:18`.

Flow path:

1. Entry — `Program.cs:117`: every `/api` request enters the security
   middleware.
2. Authentication — `Program.cs:139` calls `Authenticate`;
   `ApiSecurity.cs:78-90` hashes the supplied bearer and compares it with the
   constructor-loaded client list.
3. Authorization — `Program.cs:159-181` applies registration/repository
   privileges from the matched identity.
4. Response — `Program.cs:185` dispatches the request and the selected endpoint
   responds.

The singleton captures credential hashes once at construction
(`ApiSecurity.cs:18-65`). No expiry, idle timeout, token audience, session
version, revocation lookup, or configuration reload exists in the flow. A leaked
credential therefore remains useful until the process is restarted with
different configuration. Add explicit expiry and revocation/version data to
authentication, reload rotation state safely, and document the maximum replay
window. The upstream credential issuance and deployment rotation procedure
remain undetermined as recorded in the matrix.

## Retained false positive

Candidate: “A guessable review run id permits cross-repository access.”

Disposition: **false positive** on this source snapshot. The request first has
to pass `identity.CanAccess(repoId)` at `Program.cs:172`. The endpoint resolves
that same repository at `Program.cs:743-746`, and `ReviewJobService.Find` checks
both the run id and `run.Repository.Id == repositoryId` at
`ReviewJobs.cs:490-495`; a mismatch becomes the same not-found outcome. The
candidate is counted here instead of being dropped.

## Cost calibration evidence

The automated planted-flow acceptance uses 2,000 input, 500 output, and 250
cached-input tokens per flow with a priced fixture model. Each report records
`0.012825 USD`, its token breakdown, model, prompt/input/catalogue hashes, and
timestamp; the operation is also appended to `.quality/usage/`. This is fixture
calibration, not a claim about the unavailable cost of this manual dogfood pass.
