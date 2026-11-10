// Visual-review evidence capture (WI-0110): merges the scenarios' fragments
// into manifest.json and writes a README that lists every screen with its
// images, so the evidence branch reads on GitHub without tooling.
//
//   node tests/visual/build-manifest.mjs [output directory, default $VISUAL_OUT or visual-capture]
//
// Exits non-zero when no screenshot was taken at all; unreached screens are
// recorded, not fatal.
import { existsSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { buildManifest, SCHEMES, VIEWPORTS } from "./capture-plan.mjs";
import { auditSummary } from "./audit.mjs";

const out = resolve(process.argv[2] ?? process.env.VISUAL_OUT ?? "visual-capture");
const fragmentsDir = join(out, "fragments");

const fragments = existsSync(fragmentsDir)
  ? readdirSync(fragmentsDir).filter((name) => name.endsWith(".json")).sort().map((name) => JSON.parse(readFileSync(join(fragmentsDir, name), "utf8")))
  : [];

const env = process.env;
const manifest = buildManifest(fragments, {
  generatedAt: new Date().toISOString(),
  source: {
    repository: env.GITHUB_REPOSITORY ?? null,
    ref: env.GITHUB_REF_NAME ?? null,
    commit: env.GITHUB_SHA ?? null,
    run: env.GITHUB_RUN_ID ? `${env.GITHUB_SERVER_URL}/${env.GITHUB_REPOSITORY}/actions/runs/${env.GITHUB_RUN_ID}` : null
  },
  browser: [...new Set(fragments.map((fragment) => fragment.browserVersion).filter(Boolean))],
  method: {
    capture: "tests/visual/capture-public.spec.js and capture-admin.spec.js (shared session: capture-session.mjs), run with tests/visual/visual.config.js (the browser suite's servers and Chromium)",
    viewports: "viewport size only (Desktop Chrome device: no mobile user agent or touch emulation); a reached state is resized and its colour scheme emulated in place, except the start-up states, which load once per viewport and scheme",
    colourScheme: "prefers-color-scheme emulated by Playwright (light, dark)",
    observation: "tests/visual/observe-init.js records the page's waiting indicators on every DOM change; each step lists the distinct states seen while it ran"
  }
});

writeFileSync(join(out, "manifest.json"), JSON.stringify(manifest, null, 2));

const cell = (screen, viewport, scheme, extent) => {
  const shot = screen.shots.find((s) => s.viewport === viewport && s.scheme === scheme && s.extent === extent);
  return shot ? `[${scheme}](${shot.file})` : "—";
};
const lines = [
  "# Signal visual baseline",
  "",
  "Visual-review evidence (WI-0110), not functional test evidence: these images show what was on screen and prove nothing about correctness.",
  "",
  `Source: ${manifest.source.repository ?? "local"} \`${manifest.source.commit ?? "uncommitted"}\` (${manifest.source.ref ?? "no ref"})${manifest.source.run ? `, run ${manifest.source.run}` : ""}.`,
  `Browser: ${manifest.browser.join(", ") || "unknown"}. ${manifest.summary.screens} screens, ${manifest.summary.screenshots} screenshots. Details: [manifest.json](manifest.json).`,
  "",
  `| Screen | Site | Sign-in | ${VIEWPORTS.map((v) => `${v.id} ${v.width}x${v.height}`).join(" | ")} |`,
  `|---|---|---|${VIEWPORTS.map(() => "---").join("|")}|`,
  ...manifest.screens.map((screen) =>
    `| \`${screen.id}\` ${screen.title} | ${screen.site} | ${screen.auth.required ? "fakes" : "no"} | ${VIEWPORTS.map((v) => SCHEMES.map((s) => cell(screen, v.id, s, "full")).join(" ")).join(" | ")} |`),
  "",
  "## Accessibility audit (tests/visual/audit.mjs)",
  "",
  "Measured on each screen as captured: WCAG AA text contrast (light, dark; text below AA / text measured), keyboard focus stops with a visible indicator, horizontal overflow with 200% text at 320 CSS px, and running shimmer animations under reduced motion. A forced-colors screenshot is linked. Findings are in manifest.json under each screen's `audit`.",
  "",
  ...manifest.screens.map((screen) =>
    `- \`${screen.id}\`: ${screen.audit?.error ? `audit failed: ${screen.audit.error}` : auditSummary(screen.audit)}${screen.audit?.forcedColors?.file ? ` ([forced colors](${screen.audit.forcedColors.file}))` : ""}`),
  "",
  "## Not captured",
  "",
  ...(manifest.unreached.length
    ? manifest.unreached.map((item) => `- \`${item.screen}\` ${item.title ?? ""}: ${item.reason}${item.diagnostic ? ` ([diagnostic](${item.diagnostic}))` : ""}`)
    : ["Every planned screen was captured."]),
  ""
];
writeFileSync(join(out, "README.md"), lines.join("\n"));

console.log(JSON.stringify({ out, ...manifest.summary, loadingByOperation: Object.keys(manifest.summary.loadingByOperation) }, null, 2));
if (manifest.summary.screenshots === 0) {
  console.error("visual capture: no screenshot was taken");
  process.exit(1);
}
