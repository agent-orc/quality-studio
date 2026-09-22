# Hosted API authorization matrix, 2026-09-19

The static inventory contains **104 API route declarations**, plus public `GET /health`. Fifty declarations contain a `{repoId}` route parameter. [Machine-readable inventory](security-api-endpoint-inventory.json) records methods, patterns, lines and the middleware policy; this is a source inventory, not an external penetration test.

## Result

No additional cross-repository authorization bypass was reproduced in this follow-up. Four new integration tests passed, with **0 failures and 0 skips** in six seconds. [Raw result](security-api-repository-boundaries.log). These are coverage additions; the first draft incorrectly expected the intentionally filtered `/api/report` collection to be default-only, and that test expectation was corrected. There was no product fix for this follow-up.

The fixtures create two independent temporary Git checkouts, scoped Alice/Bob identities, an actual running review held inside an in-memory executor, and stored outcome documents containing a private marker. They use a rejecting HTTP handler for attempted outbound handovers; its call count remains zero. No model, external task creation or real customer data is used.

## Enforced selection chain

1. `Program.cs:171-251` authenticates every `/api` request and requires the matching `X-Client-Id` on mutation methods.
2. Authorization uses the selected endpoint pattern. A scoped route reads `repoId` only from `Request.RouteValues` (`Program.cs:1814-1815`). Query/body properties named `repoId` or `repositoryId` cannot replace it.
3. Legacy operation routes use the configured default repository. The repository/report collections filter by membership. Quotas are intentionally host-global for authenticated clients. Registry mutations require a wildcard registrar.
4. Review jobs additionally match both repository ID and run ID in `ReviewJobs.cs:658-663`. Stored export/compare/pin operations instantiate a store from the authorized root; export and comparison also check the embedded report repository ID (`Program.cs:1571-1661`).
5. Triage/thread/handover body paths pass through that repository's access object; metadata lookup includes its root. Finding-state/suppression changes find the fingerprint in the selected metadata document before writing.

## Coverage matrix

| Boundary | Source evidence | Executed evidence | Remaining gap |
| --- | --- | --- | --- |
| Bearer identity and matching mutation client ID | `ApiSecurity.cs:114-142`; middleware | Existing Hosted authentication tests reject no token, missing client ID and mismatched client ID. | Not repeated for every mutation route; common middleware is the enforcement point. |
| Registry create/import/update/archive | Matched endpoint policy, `Program.cs:221-237` | Existing registrar tests plus this review's trailing-slash/case regressions. | Registrar deliberately has wildcard access; it is an operator role. |
| Explicit foreign repo and legacy default routes | Membership branch, `Program.cs:238-250` | New matrix makes **41 denied requests** across reads, run export/list/trend/compare/retention/pins, cancel/pause/resume, pin/unpin, triage, threads and handover. The 42nd request checks filtered collection success. | Individual foreign-repo attempts for guideline, scope-rule, attack-judgement and sensor routes remain source-reviewed; the common middleware covers them. |
| Existing foreign live run ID under an allowed route | `ReviewJobs.Find` | **8 denials**: GET/DELETE/pause/resume under both allowed scoped and default routes, with foreign query/body repository IDs. Owner still sees the run running; own run lists stay empty. | Cancel/pause/resume concurrency with repository re-registration not stress-tested. |
| Existing foreign outcome ID in export/compare/pins | Root-specific stores and embedded repo check | Both aliases deny export and pin; comparisons report candidate missing; unpin only changes the local pin store and leaves the owner's foreign pin intact. | Deliberately corrupted snapshots with a mismatched embedded repository ID were not added here; source checks exist for export/compare/pin. |
| Triage/thread/handover body traversal | `Program.cs:912-1050,1696-1713`; `RepositoryAccess.FindMetaDocument` | Four mutation requests containing `../foreign/Foreign.cs` and spoofed repo fields return 400 without path disclosure or outbound requests. | Fingerprint/unit-ID substitution between two existing metadata documents in the same authorized repository was not exercised. |
| Global report filtering | `Program.cs:1465-1469` | Existing Alice/Bob report tests and a positive Bob collection assertion in the new matrix. | Report content redaction and generated-output secret coverage are separate controls. |
| Quotas and model catalog | Explicit quota exception; model routes retain default membership requirement | Existing anonymous quota rejection. | Quotas expose shared host usage to every authenticated client by design; they are not per-tenant accounting. |

## Authorization model limits

Repository membership grants both reads and substantial actions; the API has no separate read/review/triage/handover scopes. Body-supplied author/display names are not the authenticated actor identity. Audit records should retain the authenticated actor separately before shared hosting is supported.

Isolation currently assumes different authorization resources use different working copies. The registry accepts different IDs with the same physical root (`RepositoryRegistry.CreateAsync:157-175` checks ID uniqueness), and `QualityDataRoot.ProjectKey:111-131` deliberately identifies generated data by canonical checkout path. Such aliases therefore share storage; repository IDs alone must not be marketed as a tenant boundary. This is a configuration/design constraint, not a demonstrated ability for an ordinary scoped client to re-register roots.

The tested authorization chain does not sandbox repository programs or model tools. Worker isolation, credential lifecycle, action scopes and generated-output secret detection remain necessary as described in the main [security review](security-review.md).

New tests: `backend/tests/QualityStudio.Api.Tests/ApiSecurityTests.RepositoryBoundaries.cs`. The original test fixture was made partial to reuse its isolated roots and Hosted identities; application source was unchanged for this follow-up.
