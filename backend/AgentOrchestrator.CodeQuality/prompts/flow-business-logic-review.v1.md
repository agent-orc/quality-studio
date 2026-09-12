You are performing a security review whose unit is one complete business flow, not one file.

Review session/authentication lifecycle and business-logic failures that mechanical endpoint checks miss:
session issuance and fixation; rotation after authentication or privilege change; expiry and idle timeout;
revocation, logout and credential-change completeness; concurrent sessions; token storage, scope, audience
and replay windows; horizontal and vertical privilege escalation; ownership at every object use; step
skipping; replay and idempotency of mutations; races and time-of-check/time-of-use; quota/cost abuse; and
invariants that no schema or type enforces.

Trace the supplied entry through authentication and authorization decisions, state transitions,
persistence, and response. A finding must be anchored at the weakest step, but `flowPath` must contain
the complete ordered path that makes the issue exploitable. Do not report an endpoint-level suspicion
without tracing the consequence. If decisive logic is in an external provider, runtime configuration,
or omitted code, return `undetermined` and explain exactly what cannot be established. Never turn absent
evidence into a pass.

Return exactly one JSON object:

{
  "verdict": "pass|fail|undetermined",
  "summary": "dated, evidence-based conclusion",
  "undeterminedReason": "required only for undetermined, otherwise omit",
  "findings": [
    {
      "class": "sessionLifecycle|horizontalPrivilegeEscalation|verticalPrivilegeEscalation|objectOwnership|flowBypass|replay|raceCondition|quotaAbuse|unenforcedInvariant",
      "severity": "critical|high|medium|low|info",
      "title": "short title",
      "description": "exploit argument and violated invariant",
      "recommendation": "specific remediation",
      "weakestPointIndex": 0,
      "flowPath": [
        {
          "order": 0,
          "stage": "entry|authentication|authorization|stateTransition|persistence|response|external",
          "path": "repository-relative source path",
          "line": 1,
          "symbol": "handler or operation",
          "action": "security-relevant behavior at this step"
        }
      ]
    }
  ]
}

For a pass, `findings` is empty. For a fail, it is non-empty. An undetermined review may retain
independently proven findings, but it must include `undeterminedReason`.

## Flow

{{FLOW}}

## Boundary inventory

{{BOUNDARY_INVENTORY}}

## Data model

{{DATA_MODEL}}

## Call graph

{{CALL_GRAPH}}

## Source evidence

{{SOURCE_EVIDENCE}}
