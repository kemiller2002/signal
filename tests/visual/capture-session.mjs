// Visual-review evidence capture (SIG-VIS-CAPTURE-001, WI-0110): screenshots
// of every reachable screen of the real built app, at three viewports in
// light and dark, with a manifest of how each was reached and what the page
// showed while it waited. Run by .github/workflows/visual-capture.yml through
// tests/visual/visual.config.js, never by the per-pull-request suite.
//
// This is evidence tooling, not a test. It asserts nothing about the design;
// a screen it cannot reach is recorded as unreached with the reason, and the
// run carries on. The waits below exist only to photograph the state named.
//
// The setup is the browser suite's: the repository-root server and the
// assembled Pages site (playwright.config.js), the published demo surveys
// (tests/browser/fixtures/survey-links.json), and, for the signed-in console,
// the suite's test-only network fakes of GitHub's authorize redirect, Fides'
// exchange and the GitHub store (tests/browser/ui-conformance-fixtures.mjs,
// tests/browser/fakes/github-store.js).
//
// This module holds the capture's shared session: where the evidence goes,
// the sites, and the scenario wrapper that records every screen and its
// accessibility audit (tests/visual/audit.mjs). The scenarios themselves are
// in capture-public.spec.js (landing, start-up, assessment, survey) and
// capture-admin.spec.js (the administrator console), split by concern
// (SDE-STRUCT-001).
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { test, expect } from "@playwright/test";
import { origin } from "../browser/ui-conformance-fixtures.mjs";
import { AUTH, describeSnapshot, distinctStates, logSince, matrix, shotPath, slug } from "./capture-plan.mjs";
import { auditPage } from "./audit.mjs";

export const OUT = resolve(process.env.VISUAL_OUT ?? "visual-capture");
const OBSERVER = join(dirname(fileURLToPath(import.meta.url)), "observe-init.js");
export const PAGES_ORIGIN = `http://127.0.0.1:${Number(process.env.SIGNAL_TEST_PORT ?? 4321) + 1}`;
export const SITES = Object.freeze({
  checkout: { id: "checkout", base: `${origin}/`, about: "repository-root server (python http.server), as the browser suite serves it" },
  pages: { id: "pages", base: `${PAGES_ORIGIN}/signal/`, about: "the assembled GitHub Pages site (tools/pages/assemble-site.mjs) under /signal/, with the demo banner and Content-Security-Policy" }
});
export const FRAMEWORK = "**/build/wasm/wwwroot/_framework/dotnet.js";
export const KERNEL_MS = 60_000;
export const STORE_MS = 90_000;

// ---------------------------------------------------------------------------
// The recording session: one per scenario. It holds the scenario's screens,
// unreached screens and steps since the last screen, and writes one fragment
// that build-manifest.mjs merges.

export const session = (scenario) => {
  const screens = [];
  const unreached = [];
  const consoleErrors = [];
  const started = Date.now();
  let pending = [];
  let previous = null;

  const watch = (page) => {
    page.on("console", (message) => message.type() === "error" && consoleErrors.push(message.text().slice(0, 300)));
    page.on("pageerror", (error) => consoleErrors.push(`pageerror: ${error.message.slice(0, 300)}`));
    return page;
  };

  const preparePage = async (page) => {
    await page.addInitScript({ path: OBSERVER });
    return watch(page);
  };

  const readLog = (page) =>
    page.evaluate(() => {
      const state = window.__visualCapture;
      return state ? { doc: state.doc, length: state.log.length, log: state.log } : null;
    }).catch(() => null);

  // One action toward a screen: what was done, which asynchronous operation
  // it started (if any), how long it took and every distinct waiting state
  // the page showed meanwhile.
  const step = async (page, action, run, { operation = null } = {}) => {
    const before = await readLog(page);
    const at = Date.now();
    const result = await run();
    await page.waitForLoadState("domcontentloaded").catch(() => {});
    const after = await readLog(page);
    pending = [...pending, { action, operation, durationMs: Date.now() - at, observed: distinctStates(logSince(before, after)) }];
    return result;
  };

  const settle = (page) =>
    page.evaluate(async () => {
      await document.fonts?.ready;
      await new Promise((done) => requestAnimationFrame(() => requestAnimationFrame(done)));
    }).catch(() => {});

  const context = async (page) =>
    page.evaluate(() => ({
      title: document.title,
      headings: Array.from(document.querySelectorAll("h1, h2"))
        .filter((h) => h.getClientRects().length > 0)
        .map((h) => (h.innerText ?? "").replace(/\s+/g, " ").trim())
        .filter(Boolean)
        .slice(0, 8)
    })).catch(() => ({ title: null, headings: [] }));

  const lastSnapshot = async (page) => {
    const log = await readLog(page);
    return log && log.log.length ? describeSnapshot(log.log[log.log.length - 1]) : [];
  };

  const record = async (page, screen, shots, audit = null) => {
    screens.push({
      id: screen.id,
      title: screen.title,
      scenario,
      site: screen.site ?? "checkout",
      url: page.url(),
      auth: screen.auth ?? AUTH.none,
      reachedFrom: previous,
      steps: pending,
      notes: screen.notes ?? null,
      page: await context(page),
      shots,
      audit
    });
    previous = screen.id;
    pending = [];
  };

  // Every (viewport, scheme) on the page as it stands: the viewport is
  // resized and the colour scheme emulated in place, so a state reached once
  // (a signed-in store, a submitted survey) is photographed six ways.
  // Before each shot: the pointer rests at the page's top-left corner,
  // outside any control, so no hover state is photographed; and every
  // finite transition finishes, so a colour scheme switched for this shot is
  // photographed settled, not half-way (signal#135 review: the landing's
  // "See how it works" kept a fill in light shots taken after a dark one,
  // mid background-colour transition).
  const restPointer = async (page) => {
    await page.mouse.move(1, 1).catch(() => {});
    await page.evaluate(() => Promise.all(document.getAnimations()
      .filter((animation) => animation.effect?.getTiming().iterations !== Infinity)
      .map((animation) => animation.finished.catch(() => {})))).catch(() => {});
  };

  // The accessibility audit (tests/visual/audit.mjs) on the page as it
  // stands; a failure of the audit itself is recorded, not thrown.
  const audit = (page, screen) => {
    const forced = shotPath(screen.id, "desktop", "forced-colors", "viewport");
    return auditPage(page, {
      screenshot: async () => {
        mkdirSync(dirname(join(OUT, forced)), { recursive: true });
        await page.screenshot({ path: join(OUT, forced), caret: "hide" });
        return forced;
      }
    }).catch((error) => ({ error: String(error).slice(0, 300) }));
  };

  const shoot = async (page, screen) => {
    const shots = [];
    for (const { viewport, scheme } of matrix()) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      await page.emulateMedia({ colorScheme: scheme });
      await settle(page);
      await restPointer(page);
      for (const extent of screen.extents ?? ["full"]) {
        const file = shotPath(screen.id, viewport.id, scheme, extent);
        mkdirSync(dirname(join(OUT, file)), { recursive: true });
        await page.screenshot({ path: join(OUT, file), fullPage: extent === "full", caret: "hide" });
        shots.push({ viewport: viewport.id, scheme, extent, file, shown: await lastSnapshot(page) });
      }
    }
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.emulateMedia({ colorScheme: "light" });
    const audited = await audit(page, screen);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.emulateMedia({ colorScheme: "light", reducedMotion: "no-preference", forcedColors: "none" });
    await record(page, screen, shots, audited);
  };

  // Every (viewport, scheme) in a fresh context created at that size and
  // scheme: for states that cannot be resized in place (the start-up screen,
  // whose slow note appears on a timer).
  const shootFresh = async (browser, screen, reach) => {
    const shots = [];
    let last = null;
    let audited = null;
    for (const { viewport, scheme } of matrix()) {
      const browserContext = await browser.newContext({ viewport: { width: viewport.width, height: viewport.height }, colorScheme: scheme });
      const page = await preparePage(await browserContext.newPage());
      try {
        pending = [];
        const verified = await reach(page);
        await settle(page);
        await restPointer(page);
        const file = shotPath(screen.id, viewport.id, scheme, "full");
        mkdirSync(dirname(join(OUT, file)), { recursive: true });
        await page.screenshot({ path: join(OUT, file), fullPage: true, caret: "hide" });
        shots.push({ viewport: viewport.id, scheme, extent: "full", file, shown: await lastSnapshot(page), verified: verified ?? null });
        last = { page: await context(page), url: page.url(), steps: pending };
        // Start-up states are audited once, in the desktop light context,
        // after they are photographed (signal#134 review).
        if (viewport.id === "desktop" && scheme === "light") audited = await audit(page, screen);
      } finally {
        await browserContext.close();
      }
    }
    screens.push({
      id: screen.id, title: screen.title, scenario, site: screen.site ?? "checkout", url: last?.url ?? null,
      auth: screen.auth ?? AUTH.none, reachedFrom: null, steps: last?.steps ?? [], notes: screen.notes ?? null,
      page: last?.page ?? null, shots, audit: audited
    });
    pending = [];
  };

  // Reaches and photographs one screen; a failure is recorded as unreached
  // with the reason and a diagnostic screenshot, and the scenario carries on.
  const attempt = async (page, screen, reach) => {
    try {
      await reach();
      await shoot(page, screen);
      return true;
    } catch (error) {
      const diagnostic = `diagnostics/${screen.id}.png`;
      mkdirSync(join(OUT, "diagnostics"), { recursive: true });
      const saved = await page.screenshot({ path: join(OUT, diagnostic), fullPage: true }).then(() => diagnostic, () => null);
      unreached.push({ screen: screen.id, title: screen.title, scenario, reason: error.message.split("\n").slice(0, 6).join(" ").slice(0, 600), url: page.url(), steps: pending, diagnostic: saved });
      pending = [];
      return false;
    }
  };

  const unreachable = (screen, reason) => unreached.push({ screen: screen.id, title: screen.title, scenario, reason, url: null, steps: [], diagnostic: null });

  const write = (extra = {}) => {
    mkdirSync(join(OUT, "fragments"), { recursive: true });
    writeFileSync(join(OUT, "fragments", `${slug(scenario)}.json`),
      JSON.stringify({ scenario, durationMs: Date.now() - started, consoleErrors: [...new Set(consoleErrors)], screens, unreached, ...extra }, null, 2));
  };

  return { preparePage, step, shoot, shootFresh, attempt, unreachable, write };
};

// Every survey and assessment link differs from the last only after "#", which
// a browser treats as a same-document move; a blank page between them makes
// every open a real load, as a respondent opening a link has.
export const fresh = async (page, url) => {
  await page.goto("about:blank");
  return page.goto(url);
};

export const kernelRunning = (page) => expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: KERNEL_MS });

// A scenario is one Playwright test that always writes its fragment, even
// when something it did not expect stops it part way.
export const scenario = (id, body) =>
  test(id, async ({ page, browser }) => {
    const capture = session(id);
    await capture.preparePage(page);
    await page.setViewportSize({ width: 1440, height: 900 });
    try {
      await body({ page, browser, capture });
    } catch (error) {
      capture.unreachable({ id: `${slug(id)}-rest`, title: "the rest of this scenario" }, `the scenario stopped: ${error.message.split("\n")[0]}`);
    } finally {
      capture.write({ browserVersion: browser.version() });
    }
  });
