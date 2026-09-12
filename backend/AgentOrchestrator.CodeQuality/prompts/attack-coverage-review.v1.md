# Attack coverage review v1

Assess exactly one repository boundary against exactly one attack-catalogue
entry. Use only the supplied boundary definition, covered code, deterministic
sensor input, and catalogue evidence requirements.

Return one verdict: `pass`, `finding`, or `not-applicable`. A pass must cite
positive evidence. A finding must link a finding-lifecycle id or fingerprint.
Not-applicable must explain why the catalogue predicate was too broad for this
specific boundary. Do not turn missing evidence into a pass.

Treat deterministic sensor results as authoritative. State uncertainty and do
not infer controls that are not present in the supplied input.
