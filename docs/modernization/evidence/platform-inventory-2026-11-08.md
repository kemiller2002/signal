# Echelon Foundry platform inventory for Signal (research date 2026-11-08)

Read-only research. Signal checkout: `<repo>` (not modified). External clones: `<ext>/<repo>` (shallow, depth 50, plus fetched tags as noted).

**Authority used.** The registry's current channel is `channels/echelon-current/channel.json` in `<ext>/echelon-registry` (HEAD `5752400`, 2026-11-04). It names **profile echelon-current 1.37.0**. Component selections come from `channels/echelon-current/linux-x64.json`; release manifests are in `releases/<system>/<ver>.release.json`.

**Signal's authority.** `.conditor/authority/resolved-release-set.json` pins **echelon-current 1.22.0** (linux-x64). Per `HANDOFF.md` (line 11) it was applied with Conditor 0.10.1. The lock is `.conditor/lock.json`. The **pin sources** are `conditor.json`, `Directory.Packages.props`, `package.json`, `.echelon/foundations.json` and `.echelon/toolchain.json`, plus `vendor/nuget/*.nupkg` and `*.lock`.

**Labels used below:**
- "Latest stable": the newest published release, meaning a git release tag and/or a registry release manifest.
- "Channel": what echelon-current 1.37.0 selects, which is the version the registry marks as compatible.
- "Dev": unreleased state on the default branch.

## Summary table

| System | Latest stable (tag) | Channel 1.37.0 selects (req.) | Signal pin | Integration contract | Signal requires? |
|---|---|---|---|---|---|
| conditor | v0.10.21 (tag, 2026-11-06); registry manifests only to 0.10.19 | not a channel component (installer) | none in files; HANDOFF says 0.10.1 was used | native CLI `conditor` (GitHub release); `conditor.json`, `.conditor/lock.json` | Yes (installer/verifier of all pins) |
| praxis | v3.25.0 | 3.25.0 (required) | 3.11.0 (`conditor.json`, `.echelon/toolchain.json`, `.echelon/ros.json`) | native CLI `praxis` (GitHub release); npm `@echelon-foundry/repository-operating-system` is stale on npm (latest 3.1.4) | Yes (declared foundation) |
| ordo | v1.20.0 | 1.20.0 (required) | 1.7.0 (`conditor.json`, `.echelon/toolchain.json`, `.echelon/sde.json`, `.sde/MANIFEST.json`) | native CLI `ordo` (alias `sde`); npm `@echelon-foundry/sde` is stale (latest 1.3.0); installs `.sde/` | Yes (declared foundation) |
| dokimos | dokimos-v0.11.1 | 0.11.1 (required) | not pinned (only appears in the 1.22.0 resolved set at 0.2.0) | native CLI `dokimos`; reusable action `kemiller2002/dokimos/actions/quality-gate`; `.dokimos/policy.json` | Not today. Required by the channel; needed if Praxis >= 3.17 attested completion is used |
| aegis | 1.0.0 on nuget.org (registry tag `nuget:EchelonFoundry.Aegis.Core@1.0.0`); no git tags | 1.0.0 (required) | 1.0.0 (`Directory.Packages.props`: Aegis.Core, Aegis.Integration.GitHub) | NuGet `EchelonFoundry.Aegis.Core`, `.Integration.GitHub`, `.Store.GitHub`; `aegis-boundaries.json` | Yes (StorageFaults) |
| vigila | none (no tags, no registry manifest) | not in channel | not pinned | n/a (task-organizer *application*) | No |
| fides | v0.2.0 | 0.2.0 (optional) | 0.2.0 (vendor/nuget, `Directory.Packages.props`) | NuGet (GitHub release assets) `EchelonFoundry.Fides`, `.Client`, `.Arca`, `.Hosting` | Yes (admin sign-in) |
| percepta | percepta-repo-v0.1.0; percepta-semantic-v0.4.0 | percepta 0.1.0 (req.), percepta-semantic 0.4.0 (req.) | not pinned | CLIs `percepta-repo`, percepta-semantic (host tool) | No evidence of use |
| limen (npm) | v0.9.1 | 0.9.1 (required) | 0.9.0 (`package.json` `@echelon-foundry/limen`, `.echelon/limen.json`) | npm `@echelon-foundry/limen`; `limen verify --strict`; `limen.config.json` | Yes |
| limen-fsharp | v0.9.0 | 0.9.0 (optional) | 0.9.0 (Limen.Routing, Store, Contract) | NuGet (release assets) `EchelonFoundry.Limen.{Contract,Guest,Routing,Store}` | Yes (Routing); Store/Contract pinned, unreferenced |
| forma | v0.13.0 | 0.13.0 (required) | 0.6.1 (`package.json` tarball, `conditor.json`) | npm/GitHub tarball `@echelon-foundry/design-system` (CSS/HTML, zero runtime) | Yes (all UI) |
| forma-studio | none (no tags; package 0.0.0) | not in channel | not pinned | n/a (authoring application) | No |
| folio | v0.4.1 | 0.4.1 (required) | 0.4.0 (`package.json` tarball, `conditor.json`) | GitHub-release tarball `@echelon-foundry/print-components` (not on npm) | Yes (print / ef-print-* elements) |
| arca | v0.7.0 | 0.7.0 (optional) | 0.5.0 (Core, GitHub, Limen) | NuGet (release assets) `EchelonFoundry.Arca.{Core,GitHub,Limen}` | Yes (Core, GitHub); Arca.Limen pinned, unreferenced |
| iter | none (no tags, no manifest) | not in channel | not pinned | .NET library (typed routing above Limen); unreleased | No (Signal uses limen-fsharp routing; hits for "iter" are `List.iter`) |
| vitium | none (no tags; package 0.1.0) | not in channel | not pinned | defect-reporting *application* | No |
| "tutors" -> **tutela** | v0.1.0 | 0.1.0 (required) | not pinned | GitHub release `@echelon-foundry/tutela` (repository lifecycle) | Not today; required by the channel |
| visual-engineering | visual-engineering-v1.0.1 | 1.0.1 (required) | 1.0.1 (`conditor.json`, `.echelon/visual-engineering.json`) | npm `@echelon-foundry/visual-engineering` | Yes (up to date) |
| communication-engineering | v1.0.0 | 1.0.0 (required) | 1.0.0 installed (`.echelon/communication-engineering.json`), not in `conditor.json` | GitHub release `@echelon-foundry/communication-engineering` | Installed; up to date |
| strata | v0.1.1 | 0.1.1 (optional) | not pinned | CLI `strata` (PostgreSQL schema) | No |
| summa-contracts | contracts-v0.1.0 | 0.1.0 (optional) | not pinned | NuGet `EchelonFoundry.Summa.Contracts` | No |
| forma-workflow | npm 1.2.0 | not in channel (Conditor 0.10.21 adds it as a built-in component) | not pinned | npm `@echelon-foundry/forma-workflow`, NuGet `EchelonFoundry.Forma.Workflow` | No |
| specula | none (no tags; package 1.0.0) | not in registry | not referenced | read-only observer *application* | No |
| observer | **empty repository** (no commits) | not in registry | n/a | n/a | No |
| chrona | none (no tags) | registered system, no release | not pinned (Signal code cites "the pattern Chrona established") | sibling *application* on the same stack | No |
| tekmerion | v0.2.0 tag | registered, no manifest in `releases/` | not pinned | `@echelon-foundry/research-publisher` | No |

## Per-system notes and sources

### conditor
- Tags `v0.10.0`..`v0.10.21` (`git ls-remote`). HEAD `528ef45` is "Release v0.10.21" with `Directory.Build.props` `<Version>0.10.21`.
- Registry manifests stop at `releases/conditor/0.10.19.release.json`, so 0.10.20 and 0.10.21 have tags but no registry manifest yet.
- `conditor/docs/component-contract.md:110` says echelon-current 1.37.0 is qualified (commit `eca5ce0`, "WI-0069 qualify echelon-current 1.37.0 ... no Conditor release"). 0.10.21 adds `forma-workflow` as a built-in component (`8313de7`).
- Signal records no Conditor version in its files. `HANDOFF.md:11` says 1.21.0 and 1.22.0 were applied with Conditor 0.10.1.
- Conditor reads `conformance/conditor-declared-components.json`, which lists Signal among the consumers.

### praxis
- Tag `v3.25.0`; `praxis/release.json` `"version": "3.25.0"` (package `@echelon-foundry/repository-operating-system`). Registry manifests cover 3.6.0 to 3.25.0.
- Signal is on 3.11.0. The 14 minor releases in between have no CHANGELOG file in the repo. `praxis/docs/migrations/` holds `ADAPTIVE-TELEMETRY-1.0.md`, `ROS-1.0.0-EXECUTABLE-CONTRACT.md`, `fsharp/` and `praxis-internal-namespaces.md`.
- **Capability pairing** (`profiles/echelon-current.profile.json:154-168`, `verified-completion-attested`): Praxis >= 3.17.0 issues attested completion claims that only Dokimos >= 0.7.0 can verify. Upgrading Praxis past 3.17 therefore pulls Dokimos in.
- The detailed per-version changes between 3.11 and 3.25 are **unverified** (no changelog).

### ordo (SDE)
- Tag `v1.20.0` (HEAD `05933df` is "RELEASE-1-20-0"). `ordo/distribution/package.json` reads 1.20.0.
- **Playbook / method version.** The installed `.sde/` package is the curated playbook. `distribution/DISTRIBUTION-MAP.json` sets `methodVersionSource: method/CONSTRUCTION-METHOD-v0.2.md`, so the **method is 0.2**. Signal's `.sde/MANIFEST.json` reads `sdeVersion 1.7.0, methodVersion 0.2`, so the method version is the same; only the package version lags.
- The configuration version is 2 in both (`docs/upgrading.md`: no migration after 1→2 is documented; Signal `.echelon/sde.json` `configurationVersion: 2`). Upgrading means replacing the payload; there is no new migration.
- Additions since 1.7 (git log): verification schedule (1.18), evidence-freshness policy (1.19), decision events JSONL (1.20) and `ordo boundary assess`.
- **Consumer-check commands** (`ordo/docs/cli.md`):
  - `ordo verify [--strict|--integrity-only] [--json]` is read-only. `--integrity-only` is the Conditor adoption gate. It fails if `.echelon/toolchain.json`'s `ordo` pin differs from the CLI version.
  - `ordo doctor [--json]` reports codes SDE-DOCTOR-001..120.
  - `ordo upgrade --check` exits 5 if a change is needed.
  - `ordo status`.
- npm `@echelon-foundry/sde` is stale (latest 1.3.0, `npm view`). Use the native GitHub release.

### dokimos
- Tag `dokimos-v0.11.1` (registry `releases/dokimos/0.11.1.release.json`, commit `fecbe0c`).
- **What it is.** Echelon Foundry's evidence-based code-quality system (`dokimos/README.md`). It records immutable quality snapshots over time, baselines, compare and evaluate gates, a change-quality ratchet, and attested-completion verification.
- **Consumer setup** (`dokimos/docs/operations/INTEGRATION.md`):
  - `dokimos init` writes `.github/workflows/dokimos.yml`, `.dokimos/policy.json` and `.dokimos/installation.json`.
  - Alternatively, use the action `kemiller2002/dokimos/actions/quality-gate@<sha>` with `version`, `package-sha256`, `source`, `policy`, `build-log` and `test-results`.
  - Evidence goes on the `dokimos-evidence` branch, and the job needs `contents: write`. Exit codes run 0 to 6.
  - Ratchet: `dokimos ratchet baseline init --repository owner/name --write`.
  - Lifecycle contract `echelon.repository-lifecycle` v1: Conditor calls `dokimos status|init|verify|doctor|upgrade --root`.
- Signal has no `.dokimos/` and no dokimos entry in `conditor.json`. It appears only in Signal's 1.22.0 resolved set (0.2.0, required) and in Praxis schemas.
- Releases 0.7 through 0.11 added attested completion evidence (0.7.0), evidence reuse (0.8.0), mandatory final verification (0.9.0), a verification compiler (0.10.0) and manifest-driven requirement verification (0.11.x) (dokimos git log).
- `EchelonFoundry.Dokimos.Cli` is **not on nuget.org** (BlobNotFound). It is distributed only as a GitHub release.

### aegis
- No git tags. nuget.org lists **only 1.0.0** for `EchelonFoundry.Aegis.Core` and `.Integration.GitHub` (flat-container index).
- On main, `src/*/*.fsproj` declares `<Version>2.0.0` for Core, Integration.GitHub and Store.GitHub. That is **unpublished development state**.
- The registry has only `releases/aegis/1.0.0.release.json`. Signal is on the latest published version.
- Signal pins FSharp.Core 10.1.401 because of Aegis/Fides (`Directory.Packages.props` comment).

### vigila
- No tags, not in the registry's `releases/`. It is an application ("Task organizer", README), not a platform library.
- Signal mentions it only in `research/decisions/DF-SIGNAL-FND-2026-0002...` (line 52, line 153: "Pin Forma 0.2.0 and Folio commit 273b18f as Vigila does. Rejected").

### fides
- Tag `v0.2.0` is the only stable release after 0.1.0 (`fides/CHANGELOG.md`). Signal is current.
- **Limitation.** `EchelonFoundry.Fides.Arca` 0.2.0 was "built against EchelonFoundry.Arca.Core 0.1.0" (CHANGELOG). Its compatibility with Arca 0.6/0.7, after the breaking change, is **unverified**, although the channel co-selects fides 0.2.0 with arca 0.7.0.

### percepta
- Tags `percepta-repo-v0.1.0` and `percepta-semantic-v0.1.0`..`v0.4.0`.
- The channel requires both percepta 0.1.0 and percepta-semantic 0.4.0 (host tool).
- Percepta describes itself as "the Echelon Foundry UI system repository" (README). Signal does not use it.

### limen
- npm `@echelon-foundry/limen` versions run to 0.9.1 (`npm view`); tag `v0.9.1`.
- 0.9.0 → 0.9.1 (`limen/CHANGELOG.md:28`) only stamps the version into the browser kernel and registers it in `window.__ECHELON_RUNTIME__`. It is a low-risk patch.
- Unreleased: `limen wasm-check` (LIMEN030/031).
- limen-fsharp is at 0.9.0, the latest, with no 0.9.1 NuGet in `releases/limen-fsharp/`.

### forma (design system): 0.6.1 → 0.13.0
- Tags `v0.6.1` (`0bce8d1`, 2026-10-09) and `v0.13.0` (`2fcd107`, 2026-10-30). npm latest is 0.13.0.
- Main carries an **unreleased 0.13.1 candidate** (marketing containment, `Forma.Workflow.RequirementIdentity`). Source: `forma/CHANGELOG.md`.
- **Exports added since 0.6.1** (`git show v0.6.1:package.json` vs `v0.13.0`):
  - `./runtime` (0.9.1; registers the version in `window.__ECHELON_RUNTIME__`)
  - `./color-contract` and `./contracts/color-contract.json` (0.9.0; `forma-color` CLI)
  - `./design/forma-design.schema.json`, `./contracts/design-capabilities.json` and `./design/examples/*` (0.8.0, Design by Construction)
  - The package also ships `tools/TokenValidator` (F#, 0.13.0).
  - No export was removed.
- **Patterns.** 174 → 176 files in `patterns/`. Added: `form` and `form-row` (0.7.0 / 0.8.0). None removed.
- **Separate packages:**
  - `@echelon-foundry/forma-conformance` 0.3.0 (dev-only Playwright helpers: containment, focus visibility, corner containment)
  - `@echelon-foundry/forma-workflow` 1.2.0
- **Breaking changes.** None to class, token, element or attribute names (QD-004, stated in every entry). There are **behaviour changes**:
  - 0.7.0: shared control heights; layout fixes at 320/390px.
  - 0.10.0: marketing button/badge `overflow-wrap:anywhere`; diagram node sizing in rem.
  - 0.12.0: workflow validation is stricter (`A11Y-NONTEXT-CONTRAST`; `render` refuses failing docs).
  - 0.13.0: long tokens now break inside 21 containers; `.ef-cluster`/`.ef-actions` children are capped at 100%; `scroll-margin` on focus.
  - 0.11.0 typography roles (`data-ef-type`) are opt-in.
  - Visual regressions in Signal's Playwright/ui-conformance baselines are likely.
- **Known limitations (0.13.0).**
  - `.ef-dashboard-grid`, `.ef-split-pane` and `.ef-operation-status` reflow only at a viewport media query.
  - An SVG with `height` attributes is letterboxed.
  - Firefox ignores `scroll-margin`.
  - The token validator needs the .NET SDK.
- **Signal's usage:**
  - `web/styles.css` and `web/admin/admin.css` import `dist/all.css`.
  - Most-used classes: `ef-button` (79), `ef-stack` (60), `ef-field`, `ef-alert`, `ef-ordinal-option`, `ef-fault`, `ef-record-header`, `ef-cluster`, plus survey patterns.
  - `ef-cluster`, `ef-record-header` and `ef-operation-status` are directly affected by the 0.13.0 changes.

### folio (print components): 0.4.0 → 0.4.1
- Tags `v0.4.0` and `v0.4.1` (`a94cea7`). It is **not on npm** (E404); distribution is a GitHub release tarball only.
- Main declares 0.4.2 (runtime version stamp, export `./runtime`, commit `20598e2`), but there is **no v0.4.2 tag**. That is dev state.
- **Exports.**
  - 0.4.0: `.`, `./print.css`, `./machine-operability.json`, `./forma-icons`.
  - 0.4.1 adds `./forma-icon-print`.
- **Components.** The same 27 `ef-print-*` custom elements exist in both versions (`src/components/register.js` is unchanged between the tags). They are: back-page, break, callout, code, columns, document, figure, finding, footer, header, integrity, keep, layer, metric, note, page-number, section, security-{evidence,exception,invariant,posture,record,unknown}, sidebar, table, title-page, toc.
- **Changes in 0.4.1.** Only `src/styles/print.css` changed (+58 lines; GH-54 / ULI-L12):
  - `overflow-wrap: var(--ef-print-overflow-wrap, break-word)`;
  - header/footer slots get fixed grid columns, and RTL keeps physical left/right;
  - page-number may wrap.
  - Plus ui-conformance tooling and tests.
  - No breaking changes.
- No CHANGELOG file exists. The source is `git diff v0.4.0 v0.4.1`.
- **Signal's usage.** It imports `src/styles/print.css` (print media) and `src/components/register.js` (`web-kernel/limen-wasm.js:11`). It uses ef-print-document, header, footer, metric, page-number, section (×16), table, title-page and toc in `web/index.html` and `web/admin/index.html`.

### arca: 0.5.0 → 0.7.0
- Source: `arca/CHANGELOG.md`.
- **0.6.0 is BREAKING** (DF-ARCA-2026-0014):
  - `EntryState`, `QueueEntry`, `OfflineQueue` and `CommitReceipt` are opaque.
  - Application code cannot construct states or receipts.
  - New `QueueError.ReceiptMismatch`; receipt gains `IdempotencyKey`.
  - A migration table is at CHANGELOG lines ~130-145.
- 0.7.0 is additive (Deferral, SyncSchedule, RetryPolicy, SaveState). One source caveat: `CallFailure` gains `ServerError`.
- Exposure in Signal: `Store.fs`, `GroupStore.fs` and `RosterStore.fs` only *read* `CommitReceipt` and pattern-match `ReconcileOutcome.Landed receipt`, which is compatible per the table. Tests do not construct receipts, so exposure looks low. This is **unverified by compile**.

### iter, vitium, forma-studio, specula, observer, chrona, tekmerion
None of these has a stable release in the registry, and Signal does not use any of them.
- **iter**: no tags. It is a typed-routing .NET library and is unreleased.
- **vitium**: an application.
- **forma-studio**: an application. Its last merge adopted forma 0.10.0.
- **specula**: an observer application (Nov 4).
- **observer**: an empty repo.
- **chrona**: a sibling application. Its `conditor.json` pins exactly Signal's set (ordo 1.7.0, praxis 3.11.0, forma 0.6.1, folio 0.4.0, ...).
- **tekmerion**: has tag v0.2.0 but is not in the registry's `releases/`.

### "tutors" → tutela (resolved)
- `echelon-registry/registry/systems-v2.json` lists `tutela` with `aliases: ["tutors"]`.
- `spec/ecosystem-distribution-readiness.md:149`: "`tutela` is canonical. References to 'Tutors' must not create another Registry system id."
- Dokimos's README and AGENTS still say "Aegis/Tutors".
- Tutela is a security-engineering discipline (invariants, threats, controls, evidence, release posture). It is distributed as a GitHub-release package `@echelon-foundry/tutela`, at v0.1.0, which the channel requires. Signal has not installed it.

## Key gaps (Signal vs echelon-current 1.37.0)
1. The profile is 15 minor versions behind (1.22.0 → 1.37.0). Signal's own Conditor (0.10.1) is far behind 0.10.21.
2. **Praxis 3.11.0 → 3.25.0** and **Ordo 1.7.0 → 1.20.0** (both required). Praxis >= 3.17 requires **Dokimos >= 0.7.0** (pairing), and Signal has no Dokimos installation.
3. **Forma 0.6.1 → 0.13.0**: non-breaking names, but multiple layout behaviour changes. Expect visual baseline churn.
4. **Arca 0.5.0 → 0.7.0**: includes a breaking 0.6.0 (opaque queue/receipt types). Signal's exposure looks low.
5. Folio 0.4.0 → 0.4.1 and Limen 0.9.0 → 0.9.1: small patches.
6. The channel requires systems Signal does not install: **dokimos 0.11.1, tutela 0.1.0, percepta 0.1.0, percepta-semantic 0.4.0**. communication-engineering 1.0.0 is installed but not declared in `conditor.json`.
7. Already current: aegis 1.0.0 (2.0.0 exists only as unpublished source), fides 0.2.0, limen-fsharp 0.9.0, visual-engineering 1.0.1.
8. Signal carries two pinned but unreferenced packages: `EchelonFoundry.Arca.Limen` and Limen.Store/Contract (`Directory.Packages.props`).
