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
    { name: "chromium", use: { ...devices["Desktop Chrome"], launchOptions } },
    {
      name: "pages",
      testMatch: /deep-links\.spec\.js/,
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
