# Security sensor and agent combination

Quality Studio publishes one security statement per reviewed unit. A security review combines deterministic evidence from every enabled sensor with the review agent's judgement in one `review-meta.security.json` document. The rule is identified in metadata as `security-sensor-agent-v1`.

## Inputs and provenance

Before the agent runs, each enabled registry sensor scans without writing review sidecars. Findings are filtered to the files in the reviewed unit and supplied to the security prompt as machine-produced facts. The agent may interpret their effect on posture, but must not contradict, weaken, or duplicate them.

The final sidecar records the agent identity in `reviewer` and sensor references in `reviewer.sensors`. Each reference contains the sensor id, sensor version, and a `sha256` result hash. The full availability and verdict records are stored in `security.sensors`. Sensor-backed findings remain normal security findings; their `evidence` JSON names the sensor id, version, result hash, and original machine fact.

The result hash uses canonical JSON containing the sensor id and version, availability, unavailable reason, tool versions, verdict, and every unit-filtered finding field (including locations and original evidence). Scan timestamps are excluded so identical evidence has an identical hash.

## Combination rule

Each sensor maps active `critical` or `high` findings to `block`, any other active finding to `warn`, no active findings to `pass`, and a failed or unavailable run to `unavailable`.

The unit verdict uses this precedence:

1. `unavailable` if any enabled sensor is unavailable.
2. `block` if any available sensor blocks.
3. `warn` if any available sensor warns.
4. `pass` otherwise.

The agent supplies the qualitative review and an initial numeric grade. Quality Studio then applies the sensor result:

| Sensor verdict | Final grade rule |
| --- | --- |
| `pass` | Preserve the agent score. |
| `warn` | Cap the score at 79 (`C`). |
| `block` | Cap the score at 59 (`F`). |
| `unavailable` | Cap the score at 59 (`F`) and explicitly state that the result is not clean. |

The cap applies after the agent response, so an agent grade can never override machine evidence. `unavailable` has precedence because unknown coverage must remain visible; other sensor findings are still retained in the same statement.

Baseline-accepted Gitleaks matches are not active sensor findings and therefore do not block or warn. Finding lifecycle actions remain visible, but they do not rewrite the recorded machine verdict for that review run; rerun the security review after changing a baseline or sensor configuration.

## Project posture

A project-level security review is a posture summary with these named aspects:

- `secrets`
- `dependencies`
- `authentication-authorization`
- `input-validation`
- `configuration-iac`

The first two are grounded directly by configured sensors when available. The agent evaluates the remaining trust-boundary and design aspects from the reviewed project content.
