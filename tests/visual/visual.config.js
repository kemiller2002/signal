// Visual-review evidence capture (WI-0110). Its own configuration, so the
// per-pull-request suite (playwright.config.js, testDir tests/browser) never
// runs it. It reuses that suite's servers, browser and launch options as they
// are: the repository-root server and the assembled Pages site, both serving
// the WASM engine `npm run build:wasm` published.
//
//   npm run build:wasm
//   npx playwright test -c tests/visual/visual.config.js
//   node tests/visual/build-manifest.mjs
//
// Output goes to $VISUAL_OUT (default visual-capture/, ignored by git).
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig } from "@playwright/test";
import suite from "../../playwright.config.js";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const chromium = suite.projects.find((project) => project.name === "chromium");

export default defineConfig({
  testDir: ".",
  testMatch: /capture-(public|admin)\.spec\.js$/,
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  timeout: 45 * 60_000,
  reporter: [["list"]],
  outputDir: resolve(root, "test-results/visual"),
  use: { ...suite.use, trace: "off", screenshot: "off" },
  projects: [{ name: "visual-capture", use: { ...chromium.use, viewport: { width: 1440, height: 900 } } }],
  // The suite's servers run from the repository root, wherever this file is.
  webServer: suite.webServer.map((server) => ({ ...server, cwd: root }))
});
