# Security review

Work item: WI-0100 · Commit: `e19101f` plus this branch · Date: 2026-11-08

## Verdict

**There are no known vulnerable dependencies and no committed secrets, and the deployed CSP is in force.** One **integrity defect** (records are not hash-checked on load) and two **deployment-hardening gaps** must be closed before real sign-in and storage go live. Security readiness for the current demo deployment: **pass**. For production with sign-in and storage: **not yet**.

## Checks run

| Check | Result | Evidence |
|---|---|---|
| `npm audit` (prod and all) | 0 vulnerabilities | run 2026-11-08 |
| `dotnet list package --vulnerable --include-transitive` | no vulnerable packages in any project | run 2026-11-08 |
| Secret patterns (`git grep` for GitHub tokens, AWS keys, private keys, client secrets) | only test fixtures (`gho_SIGNAL…TOKEN…`) and the detector regex in `ConfigPackage.fs:128` | run 2026-11-08 |
| Vendored NuGet integrity | `conditor verify` checks every `vendor/nuget` asset against Registry SHA-256 locks | `evidence/conditor-verify-2026-11-08.txt` |
| Deployed CSP | `<meta http-equiv="Content-Security-Policy">` present on `/web/` and `/web/admin/`: `default-src 'self'`, `script-src 'self' 'wasm-unsafe-eval'`, `object-src 'none'`, `base-uri 'self'` | curl 2026-11-08 |
| CI token scope | workflows declare least-privilege `permissions:`; only the Pages deploy job holds `pages`/`id-token` | `.github/workflows/*.yml` |
| Aegis redaction | faults carry no credentials, PII or answer state (AER-021..031 partial; redaction tested in `BoundaryTests`) | gap analysis |
| Credential retention | administrator token held in memory only; offline withholds all mutation; cross-tab notices only downgrade (ADM-056) | VERIFIED by independent review |
| Server-side authorization | Signal has no server. GitHub's repository permissions are the enforcement point, and Signal's capability model (`Access`) only gates the UI and commands. ADM-071 and ADM-072 are VERIFIED at the domain level and **not** against a real provider. | see APPLICATION-READINESS.md |

## Findings

| ID | Severity | Finding | Required action |
|---|---|---|---|
| SEC-01 | **high (integrity)** | ADM-025 and ADM-046: when records are loaded, the content hash is computed but never compared to an expected value. Arca's `Integrity.unchanged` is not called. A record whose body was edited in the data repository but stays canonical JSON loads as authentic. Only published templates re-verify their hash (`TemplateRecord.fs:261`). | WI-0101, before production storage is enabled. Verify every record's hash on read, and add a tamper test that keeps the JSON canonical. |
| SEC-02 | medium | ARP-005: importing into a group that is already complete is not refused (`GroupResult.fs:153`, `accepted >= expected`), so contributions beyond the expected count are accepted silently. | WI-0101 |
| SEC-03 | medium (deployment) | GitHub Pages cannot send `frame-ancestors` or `X-Frame-Options` (clickjacking), or HSTS. Low risk while the site is demo-only. | Before real sign-in: host with response headers, or record an accepted-risk DF decision (MOD-SEC-001, proposed). |
| SEC-04 | low | ADM-032: saved analyses are checked for credentials only by the absence of the string "token". | Reuse `ConfigPackage`'s credential detector (WI-0101). |
| SEC-05 | info | Mutation testing is absent (SIGNAL-TEST-001): count-withheld-as-zero, off-by-one minimum and similar mutants are not registered. | C3 (Dokimos) |

## Not verified (blocked or out of reach)

- Real-provider authentication and authorization (a GitHub App, the Fides exchange, repository permissions). No production deployment configuration exists, so this is **blocked** on owner-provided resources. See APPLICATION-READINESS.md.
- Dokimos and Aegis security gates in CI. Dokimos is not installed (C1/C2).
