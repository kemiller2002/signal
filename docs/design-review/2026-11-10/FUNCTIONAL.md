# Functional verification — separate from visual review

Work: SIG-VIS-REFINE-20261110.

The final presentation harness uses the locally built Signal runtime at `http://127.0.0.1:4321`; exploratory runs used the deployed runtime with locally intercepted HTML/CSS/package assets. It does not deploy changes or use production credentials. Fixture links and answers are synthetic. Scripts are retained alongside this report for reproducibility.

- Unanswered assessment: refused with the existing visible validation state.
- Fifteen midpoint numeric answers: three existing dimension scores remain `50.0`; methodology matches the live baseline; Edit answers returns to the questionnaire.
- Generic survey: all five answers selected through native inputs; progress reaches `5 of 5 answered`; submission reaches Submitted and exposes the submission link.
- No browser page errors in the completed harness run.
- Fresh layout assertions and recorded viewports are in `after/functional-checks.json`.
- `node --test tests/visual/audit.test.mjs tests/visual/capture-plan.test.mjs`: 2 test files passed.

Repository build, .NET, Limen and browser-suite results are recorded below when completed. Visual acceptance is tracked in README.md, not inferred from these results.

- `npm run build` using workspace .NET SDK 10.0.403: passed. The solution build had zero warnings/errors; WebAssembly publish emitted FSharp.Core trim warnings and the optional wasm-tools optimization notice.
- `dotnet test Echelon.Signal.sln --no-build -c Release`: **692 passed, zero failed/skipped**, after replacing an application CSS selector on `.forma-data-grid` with the application-owned `.result-grid` class. The Forma ownership guard remains unchanged. See `functional/dotnet-tests.log` and `functional/dotnet-tests.trx`.
- `npm run check:limen`: strict verification passed (171 engine and 7 kernel source files).
- `./praxis validate`: passed.
- Optional pixel comparator smoke checks: identical screenshots accepted; deliberately changed and missing images rejected. This tests the tool, not the application's visual acceptance.

Two existing structural tests were updated for the new documented composition: the assessment uses Forma application identity in its header and composes questions with Echelon panels; the header geometry check still requires identity/type to precede the title without overlap on both assessment and administrator pages.
