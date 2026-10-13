// End-to-end configuration for the assessment page.
//
// The .NET tests prove the engine decides correctly. They cannot prove the
// engine reaches the browser: the [JSExport] shim, the WASM transport,
// Limen's kernel, the HTML bindings and the Forma and Folio presentation sit
// between the two, and none of them is type-checked against the others.
// These tests drive the real page in a real browser.
import { existsSync } from "node:fs";
import { defineConfig, devices } from "@playwright/test";

// SIGNAL_TEST_PORT moves the suite off 4321 when that port is taken locally.
const port = Number(process.env.SIGNAL_TEST_PORT ?? 4321);
const origin = `http://127.0.0.1:${port}`;

// The assembled GitHub Pages site (tools/pages/assemble-site.mjs), served
// under a sub-path, so deep links are proven against what Pages publishes
// (SIG-LINK-012). It needs `npm run build:wasm` first, as the suite does.
const pagesPort = port + 1;
const pagesOrigin = `http://127.0.0.1:${pagesPort}`;

// Some environments ship a Chromium that Playwright did not download itself
// and must not try to. Where that binary exists it is used as-is; everywhere
// else Playwright resolves its own, so CI needs no special case.
const preinstalledChromium = "/opt/pw-browsers/chromium";
const launchOptions = existsSync(preinstalledChromium) ? { executablePath: preinstalledChromium } : {};

export default defineConfig({
  testDir: "./tests/browser",
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: 0,
  timeout: 60_000,
  reporter: process.env.CI ? [["github"], ["list"]] : [["list"]],
  use: { baseURL: origin, trace: "retain-on-failure" },
  projects: [
    { name: "chromium", testIgnore: /(survey-devices|ui-conformance-[a-z-]+)\.spec\.js/, use: { ...devices["Desktop Chrome"], launchOptions } },
    // Universal UI layout and interaction conformance
    // (UI-LAYOUT-INTERACTION@1.0.0, signal#104): its own project, so
    // `npm run ui:conformance` runs exactly it and the full suite runs it once.
    // Each test sets the viewport widths it measures (320/390/768/1280).
    { name: "ui-conformance", testMatch: /ui-conformance-[a-z-]+\.spec\.js/, use: { ...devices["Desktop Chrome"], launchOptions } },
    // Sample links on a phone (AUT-006 §§61-62): touch, a narrow viewport and
    // a mobile user agent, on the same Chromium. WebKit (iPhone Safari) and
    // Firefox need OS libraries this suite does not install; which devices
    // Signal supports is a product decision still to be made.
    {
      name: "mobile",
      testMatch: /survey-devices\.spec\.js/,
      use: { ...devices["Pixel 7"], launchOptions }
    },
    // Real WebKit and Firefox (DF-SIGNAL-2026-0016): only when the
    // cross-browser workflow asks for them (SIGNAL_CROSS_BROWSER=1), never in
    // the per-pull-request suite, so their OS libraries never hold up PR CI.
    ...(process.env.SIGNAL_CROSS_BROWSER === "1"
      ? [
          { name: "webkit-iphone", testMatch: /(survey|survey-timers|survey-devices|survey-max-link|survey-localized)\.spec\.js/, use: { ...devices["iPhone 13"] } },
          { name: "firefox", testMatch: /(survey|survey-timers|survey-max-link|survey-localized)\.spec\.js/, use: { ...devices["Desktop Firefox"] } }
        ]
      : []),
    {
      name: "pages",
      testMatch: /(deep-links|survey|survey-timers|survey-max-link|survey-localized)\.spec\.js/,
      use: { ...devices["Desktop Chrome"], launchOptions, baseURL: `${pagesOrigin}/signal/` }
    }
  ],
  // Served from the repository root: index.html reaches up into node_modules/
  // for Limen, Forma and Folio, into web-kernel/ for the kernel, and into
  // build/ for the published engine.
  webServer: [
    {
      command: `python3 -m http.server ${port} --bind 127.0.0.1`,
      url: `${origin}/web/index.html`,
      reuseExistingServer: !process.env.CI,
      timeout: 60_000
    },
    {
      command: `node tools/pages/assemble-site.mjs dist-pages-root/signal && python3 -m http.server ${pagesPort} --bind 127.0.0.1 --directory dist-pages-root`,
      url: `${pagesOrigin}/signal/web/admin/index.html`,
      reuseExistingServer: !process.env.CI,
      timeout: 120_000
    }
  ]
});
