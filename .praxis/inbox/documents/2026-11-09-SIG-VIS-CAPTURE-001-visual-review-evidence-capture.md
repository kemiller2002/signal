# Pending Praxis intake: SIG-VIS-CAPTURE-001 visual review evidence capture

**Status:** Unprocessed source material. This is **not** a canonical requirement record, work item, approved decision, or proof of implementation.

- Submitted: 2026-11-09
- Source: coordinator request relayed to the executing agent (Claude Code session), to capture Signal's current visual baseline from the real built app.
- Kind: evidence tooling. Visual-review evidence is recorded separately from functional test evidence; screenshots are not test results and pass no conformance check.

## Constraints of the requesting environment

The requesting environment cannot build the WebAssembly engine (NuGet is blocked), cannot reach the deployed site and cannot download Actions artifacts. Only git, npm and the GitHub REST API work there. The screenshots are therefore produced in GitHub Actions and returned through git.

## Requirements

**SIG-VIS-CAPTURE-001.1** A Playwright capture reuses the browser suite's setup (the repository-root server and the assembled Pages site of `playwright.config.js`, the test-only network fakes of `tests/browser/ui-conformance-fixtures.mjs` and `tests/browser/fakes/github-store.js`) and captures, with the real built WASM engine and the demo data: the landing page (`web/home/`); the start-up screen before the engine mounts, plus its slow, failed and timed-out states as the browser suite provokes them; the administrator console's unsigned screens and, signed in against the fakes, its home, areas, survey authoring (draft and preview, and published versions where reachable); the respondent survey (`web/survey/`): first question, a mid-survey validation refusal and the completion screen; the assessment page (`web/`) with its results and scoring; group results, scoring, imports, empty states and errors.

**SIG-VIS-CAPTURE-001.2** Every screen is captured at desktop 1440x900, tablet 834x1112 and mobile 390x844, in light and dark colour schemes, full page; the landing page also viewport-only.

**SIG-VIS-CAPTURE-001.3** A JSON manifest records, per screen: the URL, the steps taken, whether it needed sign-in (the demo deployment has none configured) and how it was reached, every loading or asynchronous state observed while waiting (blank, a word such as "Loading", a spinner, disabled controls) and any screen that could not be reached, with the reason.

**SIG-VIS-CAPTURE-001.4** A workflow on `visual/**` pushes and `workflow_dispatch` (never `pull_request`) builds the WASM engine as the Build and Pages workflows do, assembles the site, runs the capture and commits the PNGs and manifest to `visual-evidence/baseline-<short sha>` with `GITHUB_TOKEN` (`contents: write`), not as an artifact only.

## Acceptance

- The workflow succeeds on `visual/baseline-capture` and the evidence branch carries the PNGs and the manifest.
- The evidence is fetched through git and a sample of the images is inspected.
- No functional test claim is made from the screenshots.
