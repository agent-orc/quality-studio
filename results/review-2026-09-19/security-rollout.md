# Security rollout and acceptance gates

This is a proposed rollout contract, not a statement that worker isolation or multi-tenant hosting exists. Read alongside the [implemented controls and evidence](security-review.md).

| Operating profile | Current controls and explicit boundary | Release condition |
| --- | --- | --- |
| **Local: trusted operator** | Loopback binding and Host/Origin checks; local callers inherit the operator's authority. Processes and review CLIs still use the host. | Operator-controlled repositories only. Local mode is not protection against another local process or malicious build logic. |
| **Hosted: trusted team** | Bearer identities, repository membership, registrar restriction, HTTPS/proxy controls and request limits exist. Membership permits broad actions; credentials have no expiry/live revocation. | Restricted internal deployment with trusted users and operator-controlled repositories. Keep the backend private behind the configured edge. Treat shared host execution as an accepted boundary, not tenant isolation. |
| **Hostile repositories / multiple tenants** | **Not supported by the current execution architecture.** Request authorization does not confine repository programs or model tools. | Isolated disposable workers, separated tenant data/credentials, action permissions, revocation and the P0 gates below must exist and pass before admission. |

## Next implementation boundary: worker protocol

The API admits a **job** containing an opaque job ID, actor/tenant/repository IDs, immutable analyzer profile ID, policy version and reserved budget. It sends no caller-supplied command text.

A **source manifest** binds the revision and normalized file paths to content hashes. The worker receives only that snapshot, a disposable write area and an allowlisted environment/CLI home. Provider access uses limited credentials and declared egress rules; host homes, other repositories and container sockets remain inaccessible.

The protocol declares CPU/memory/process/disk/time limits and output byte/count/depth limits. Existing application caps cover only parts of this requirement. Results must match job/manifest identity and pass schema, path and secret checks. Exactly one terminal outcome records success, unavailable, cancellation, timeout or quota exhaustion; partial or invalid evidence cannot mean clean.

## Negative acceptance tests

These are required future end-to-end gates; existing unit/API tests cover only subsets.

| Priority | Test and required observation |
| --- | --- |
| **P0** | Use tenant A's identity with B's run IDs, paths and report references across read/export/compare/cancel/pin/triage/handover. Deny access without modifying B or disclosing its evidence. |
| **P0** | A malicious build, analyzer and Git hook try to read host/other-tenant canaries and send them externally. Reads and undeclared network destinations fail. |
| **P0** | Snapshot and result fixtures use traversal, symlinks and junctions. No access or write escapes the assigned source, scratch and result areas. |
| **P0** | Fork, stdout and disk floods exceed each quota. Descendants terminate, storage stays bounded, and the job releases its reservation without reporting success. |
| **P0** | Repository/model text requests host writes, another repository or an automatic handover. Independent tool policy denies the action even if the model follows the instruction. |
| **P0** | Expire/revoke credentials with a job queued and running. New privileged work is denied; cancellation terminates descendants and leaves an attributable terminal record. |
| **P1** | Feed missing, partial, malformed and externalized analyzer results. They become unavailable; valid explicit empty results remain accepted. |
| **P1** | Plant synthetic secrets in generated review output and handover payloads. Persistence, exports, logs and downstream requests contain no unredacted fixture values. |

Implementation guidance: [OWASP container hardening](https://cheatsheetseries.owasp.org/cheatsheets/Docker_Security_Cheat_Sheet.html), [MSBuild trust boundary](https://learn.microsoft.com/en-us/visualstudio/msbuild/msbuild-security-best-practices?view=visualstudio), [OWASP prompt-injection controls](https://cheatsheetseries.owasp.org/cheatsheets/LLM_Prompt_Injection_Prevention_Cheat_Sheet.html).
