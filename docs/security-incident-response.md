# Credential incident response

Quality Studio incident artifacts must never contain the credential value. Identify a suspected exposure by repository id, path, safe line range, Gitleaks rule id and fingerprint, source commit, review run id, and—when available—the deployment key id.

## Contain and revoke

1. Stop affected reviews and handovers. Disable the affected hosted client or provider identity in the deployment secret source.
2. Revoke the credential at its issuing system. Treat removal from Git or review metadata as cleanup, not revocation.
3. Record who performed the revocation, the issuer's non-secret credential or key id, and the revocation time. Do not paste command output if it contains the value.

## Rotate

1. Create a replacement through the issuer's secret-management workflow and grant only the required repository and action scope.
2. Store the replacement only in the deployment secret source. For hosted static clients, derive and deploy the replacement SHA-256 credential hash; never add cleartext values to tracked configuration.
3. Restart the service when the active credential mechanism requires it. Until S5 provides live revocation, record that restart as part of the rotation evidence.
4. Invalidate related sessions, cached credentials, and downstream provider tokens where the issuer supports that operation.

## Remove and verify

1. Remove the value from source, review metadata, task payloads, and logs using the owning system's approved history-rewrite or retention process. Coordinate history rewrites before force-updating shared refs.
2. Run the current-worktree and all-ref redacted Gitleaks scans. A suppression is allowed only for a reviewed false-positive fingerprint; never suppress a real credential to make the gate green.
3. Verify the old credential is rejected and the replacement has only its intended scope. Retain scan reports, timestamps, fingerprints, commits, key ids, and authorization decisions as the incident evidence.
