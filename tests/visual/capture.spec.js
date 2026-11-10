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
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { test, expect } from "@playwright/test";
import { FakeGitHubStore } from "../browser/fakes/github-store.js";
import { INVITATION, deployment, links, origin, routeSignIn } from "../browser/ui-conformance-fixtures.mjs";
import { AUTH, describeSnapshot, distinctStates, logSince, matrix, shotPath, slug } from "./capture-plan.mjs";

const OUT = resolve(process.env.VISUAL_OUT ?? "visual-capture");
const OBSERVER = join(dirname(fileURLToPath(import.meta.url)), "observe-init.js");
const PAGES_ORIGIN = `http://127.0.0.1:${Number(process.env.SIGNAL_TEST_PORT ?? 4321) + 1}`;
const SITES = Object.freeze({
  checkout: { id: "checkout", base: `${origin}/`, about: "repository-root server (python http.server), as the browser suite serves it" },
  pages: { id: "pages", base: `${PAGES_ORIGIN}/signal/`, about: "the assembled GitHub Pages site (tools/pages/assemble-site.mjs) under /signal/, with the demo banner and Content-Security-Policy" }
});
const FRAMEWORK = "**/build/wasm/wwwroot/_framework/dotnet.js";
const KERNEL_MS = 60_000;
const STORE_MS = 90_000;

// ---------------------------------------------------------------------------
// The recording session: one per scenario. It holds the scenario's screens,
// unreached screens and steps since the last screen, and writes one fragment
// that build-manifest.mjs merges.

const session = (scenario) => {
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

  const record = async (page, screen, shots) => {
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
      shots
    });
    previous = screen.id;
    pending = [];
  };

  // Every (viewport, scheme) on the page as it stands: the viewport is
  // resized and the colour scheme emulated in place, so a state reached once
  // (a signed-in store, a submitted survey) is photographed six ways.
  const shoot = async (page, screen) => {
    const shots = [];
    for (const { viewport, scheme } of matrix()) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      await page.emulateMedia({ colorScheme: scheme });
      await settle(page);
      for (const extent of screen.extents ?? ["full"]) {
        const file = shotPath(screen.id, viewport.id, scheme, extent);
        mkdirSync(dirname(join(OUT, file)), { recursive: true });
        await page.screenshot({ path: join(OUT, file), fullPage: extent === "full", caret: "hide" });
        shots.push({ viewport: viewport.id, scheme, extent, file, shown: await lastSnapshot(page) });
      }
    }
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.emulateMedia({ colorScheme: "light" });
    await record(page, screen, shots);
  };

  // Every (viewport, scheme) in a fresh context created at that size and
  // scheme: for states that cannot be resized in place (the start-up screen,
  // whose slow note appears on a timer).
  const shootFresh = async (browser, screen, reach) => {
    const shots = [];
    let last = null;
    for (const { viewport, scheme } of matrix()) {
      const browserContext = await browser.newContext({ viewport: { width: viewport.width, height: viewport.height }, colorScheme: scheme });
      const page = await preparePage(await browserContext.newPage());
      try {
        pending = [];
        const verified = await reach(page);
        await settle(page);
        const file = shotPath(screen.id, viewport.id, scheme, "full");
        mkdirSync(dirname(join(OUT, file)), { recursive: true });
        await page.screenshot({ path: join(OUT, file), fullPage: true, caret: "hide" });
        shots.push({ viewport: viewport.id, scheme, extent: "full", file, shown: await lastSnapshot(page), verified: verified ?? null });
        last = { page: await context(page), url: page.url(), steps: pending };
      } finally {
        await browserContext.close();
      }
    }
    screens.push({
      id: screen.id, title: screen.title, scenario, site: screen.site ?? "checkout", url: last?.url ?? null,
      auth: screen.auth ?? AUTH.none, reachedFrom: null, steps: last?.steps ?? [], notes: screen.notes ?? null,
      page: last?.page ?? null, shots
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
const fresh = async (page, url) => {
  await page.goto("about:blank");
  return page.goto(url);
};

const kernelRunning = (page) => expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: KERNEL_MS });

// A scenario is one Playwright test that always writes its fragment, even
// when something it did not expect stops it part way.
const scenario = (id, body) =>
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

// Independent scenarios: one that stops part way does not skip the others.
test.describe.configure({ timeout: 45 * 60_000 });

// ---------------------------------------------------------------------------
// The public landing page: static, no script, no engine.

scenario("landing", async ({ page, capture }) => {
  for (const site of [SITES.checkout, SITES.pages]) {
    await capture.attempt(page, {
      id: site.id === "checkout" ? "landing" : "landing-pages-site",
      title: `Landing page (web/home/) — ${site.about}`,
      site: site.id,
      extents: ["full", "viewport"]
    }, async () => {
      await capture.step(page, `open ${site.base}web/home/index.html`, () => page.goto(`${site.base}web/home/index.html`), { operation: "static page load" });
      await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
    });
  }
  // The Pages site root sends the browser to the landing page.
  await capture.attempt(page, { id: "pages-site-root", title: "Pages site root (redirects to web/home/)", site: "pages", extents: ["full", "viewport"] }, async () => {
    await capture.step(page, `open ${SITES.pages.base}`, () => page.goto(SITES.pages.base), { operation: "static page load" });
    await page.waitForURL(/\/web\/home\//, { timeout: 15_000 });
    await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
  });
});

// ---------------------------------------------------------------------------
// The pre-runtime start-up screen of each engine page, before the engine
// mounts, and the states the browser suite provokes (experience.spec.js):
// held download (starting, then slow once Forma's 8 s reveal passes), failed
// download, failed page module, and the 60 s watchdog time-out.

const ENGINE_PAGES = [
  { id: "assessment", path: "web/index.html", name: "assessment page (web/)" },
  { id: "survey", path: "web/survey/index.html", name: "survey page (web/survey/)" },
  { id: "admin", path: "web/admin/index.html", name: "administrator console (web/admin/)" }
];

const startupState = (page) =>
  page.evaluate(() => {
    const screen = document.getElementById("app-startup");
    const slow = screen?.querySelector(".forma-startup__slow");
    return {
      state: screen?.dataset.formaState ?? null,
      hidden: screen?.hidden ?? null,
      slowNoteVisible: slow ? getComputedStyle(slow).visibility === "visible" && slow.getClientRects().length > 0 : null,
      kernel: document.documentElement.dataset.kernel ?? null
    };
  });

const STARTUP_STATES = [
  {
    id: "starting",
    title: "start-up screen before the engine mounts (download held)",
    reach: async (page, capture, url) => {
      await page.route(FRAMEWORK, () => new Promise(() => {}));
      await capture.step(page, `hold ${FRAMEWORK} (never answered), then open ${url}`, () => page.goto(url), { operation: "engine start-up" });
      await expect(page.locator("#app-startup")).toBeVisible();
      return startupState(page);
    }
  },
  {
    id: "slow",
    title: "start-up screen still starting after Forma's 8 s slow-note delay (download held)",
    reach: async (page, capture, url) => {
      await page.route(FRAMEWORK, () => new Promise(() => {}));
      await capture.step(page, `hold ${FRAMEWORK}, open ${url}, wait until the slow note is shown (about 8 s)`, async () => {
        await page.goto(url);
        await expect.poll(async () => (await startupState(page)).slowNoteVisible, { timeout: 20_000 }).toBe(true);
      }, { operation: "engine start-up" });
      return startupState(page);
    }
  },
  {
    id: "failed-download",
    title: "start-up failed: the engine download is aborted",
    reach: async (page, capture, url) => {
      await page.route(FRAMEWORK, (route) => route.abort());
      await capture.step(page, `abort ${FRAMEWORK}, open ${url}`, async () => {
        await page.goto(url);
        await expect(page.locator("#app-startup")).toHaveAttribute("data-forma-state", "failed", { timeout: 30_000 });
      }, { operation: "engine start-up" });
      return startupState(page);
    }
  },
  {
    id: "failed-module",
    title: "start-up failed: a page module (web-kernel/limen-wasm.js) answers 404",
    reach: async (page, capture, url) => {
      await page.route("**/web-kernel/limen-wasm.js", (route) => route.fulfill({ status: 404, body: "" }));
      await capture.step(page, `answer web-kernel/limen-wasm.js with 404, open ${url}`, async () => {
        await page.goto(url);
        await expect(page.locator("#app-startup")).toHaveAttribute("data-forma-state", "failed", { timeout: 15_000 });
      }, { operation: "engine start-up" });
      return startupState(page);
    }
  },
  {
    id: "timed-out",
    title: "start-up timed out: the 60 s watchdog fires (fake clock)",
    reach: async (page, capture, url) => {
      await page.clock.install();
      await page.route(FRAMEWORK, () => new Promise(() => {}));
      await capture.step(page, `install Playwright's fake clock, hold ${FRAMEWORK}, open ${url}, advance the clock 61 s`, async () => {
        await page.goto(url);
        await page.clock.runFor(61_000);
        await expect(page.locator("#app-startup")).toHaveAttribute("data-forma-state", "timed-out");
      }, { operation: "engine start-up" });
      return startupState(page);
    },
    notes: "The page's clock is Playwright's fake clock, advanced 61 s; CSS animations run on real time, so the slow note's 8 s reveal may not have run."
  }
];

scenario("startup", async ({ browser, capture }) => {
  for (const enginePage of ENGINE_PAGES) {
    for (const state of STARTUP_STATES) {
      const url = `${SITES.checkout.base}${enginePage.path}`;
      const screen = { id: `startup-${enginePage.id}-${state.id}`, title: `${enginePage.name}: ${state.title}`, notes: state.notes ?? null };
      try {
        await capture.shootFresh(browser, screen, (page) => state.reach(page, capture, url));
      } catch (error) {
        capture.unreachable(screen, error.message.split("\n")[0]);
      }
    }
  }
});

// ---------------------------------------------------------------------------
// The assessment page (web/): first questions, a refused "see results",
// scored results, and an invitation answered and submitted.

const choice = (page, item, value) => page.locator(`section[data-item="${item}"] input[type="radio"][value="${value}"]`);
const ITEMS = Array.from({ length: 15 }, (_, i) => `CORE-${String(i + 1).padStart(3, "0")}`);

scenario("assessment", async ({ page, capture }) => {
  const url = `${SITES.checkout.base}web/index.html`;
  await capture.attempt(page, { id: "assessment-first-questions", title: "Assessment page (web/): the questions, nothing answered" }, async () => {
    await capture.step(page, `open ${url} and wait for the kernel`, async () => { await page.goto(url); await kernelRunning(page); }, { operation: "engine start-up" });
    await expect(page.locator("#progress")).toHaveText("0 of 15 answered");
  });
  await capture.attempt(page, { id: "assessment-results-refused", title: "Assessment page: \"See results\" refused with 14 questions unanswered (validation)" }, async () => {
    await capture.step(page, "answer CORE-001 with 3", () => choice(page, "CORE-001", "3").check());
    await capture.step(page, "click See results", () => page.click("#see-results"), { operation: "see results (engine)" });
    await expect(page.locator("#refusal-message")).toBeVisible();
  });
  await capture.attempt(page, { id: "assessment-results", title: "Assessment page: scored results, one dimension not scored (results and scoring)" }, async () => {
    await capture.step(page, "answer every question with 3, then CORE-006=4, CORE-007/011 don't know, CORE-012 not observed, CORE-013 not applicable", async () => {
      for (const item of ITEMS) await choice(page, item, "3").check();
      for (const [item, value] of [["CORE-006", "4"], ["CORE-007", "dont-know"], ["CORE-011", "dont-know"], ["CORE-012", "not-observed"], ["CORE-013", "not-applicable"]]) await choice(page, item, value).check();
    });
    await capture.step(page, "click See results", () => page.click("#see-results"), { operation: "see results (engine)" });
    await expect(page.locator("#results-body tr")).toHaveCount(3);
  });
  await capture.attempt(page, { id: "assessment-invitation", title: "Assessment invitation link: the questions, nothing answered" }, async () => {
    await capture.step(page, `open the anonymous invitation ${INVITATION}`, async () => { await fresh(page, `${origin}${INVITATION}`); await kernelRunning(page); }, { operation: "engine start-up" });
    await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
  });
  await capture.attempt(page, { id: "assessment-invitation-results", title: "Assessment invitation: results before submitting" }, async () => {
    await capture.step(page, "answer every question \"Often\"", async () => {
      const groups = page.locator("fieldset.forma-ordinal-scale");
      for (const group of await groups.all()) await group.getByRole("radio", { name: "Often", exact: true }).check();
    });
    await capture.step(page, "click See results", () => page.getByRole("button", { name: "See results" }).click(), { operation: "see results (engine)" });
    await expect(page.getByRole("button", { name: "Submit answers" })).toBeVisible();
  });
  await capture.attempt(page, { id: "assessment-invitation-submitted", title: "Assessment invitation: submitted, with the submission link (completion)" }, async () => {
    await capture.step(page, "click Submit answers", () => page.getByRole("button", { name: "Submit answers" }).click(), { operation: "submit answers (engine)" });
    await expect(page.getByRole("textbox", { name: "Submission link" })).toBeVisible();
  });
});

// ---------------------------------------------------------------------------
// The respondent survey (web/survey/) with the site's published demo surveys.

const pick = (page, row) => page.locator(`[data-row="${row}"] input[data-event="answered"]`);
const openSurvey = async (page, capture, link, site = SITES.checkout) =>
  capture.step(page, `open ${link} and wait for the kernel`, async () => { await fresh(page, `${site.base}${link}`); await kernelRunning(page); }, { operation: "engine start-up + survey template fetch" });

scenario("survey", async ({ page, capture }) => {
  // The template fetch held, to photograph what the page shows meanwhile.
  let release = () => {};
  await capture.attempt(page, { id: "survey-loading", title: "Survey page while the published survey file is being fetched (fetch held)" }, async () => {
    const held = new Promise((resolve) => { release = resolve; });
    await page.route(`**/${links.templateFile}`, async (route) => { await held; await route.continue(); });
    await capture.step(page, `hold ${links.templateFile}, open ${links.identified}`, async () => {
      await page.goto(`${SITES.checkout.base}${links.identified}`);
      await kernelRunning(page);
      await expect(page.locator("#loading")).toBeVisible({ timeout: 15_000 });
    }, { operation: "survey template fetch" });
  });
  await capture.attempt(page, { id: "survey-first-question", title: "Survey (single page): first question, nothing answered" }, async () => {
    await capture.step(page, `release ${links.templateFile}`, async () => {
      release();
      await expect(page.locator("#survey-title")).toHaveText("Signal demo survey");
    }, { operation: "survey template fetch" });
    await page.unroute(`**/${links.templateFile}`);
  });
  await capture.attempt(page, { id: "survey-submit-refused", title: "Survey (single page): submit refused, each unanswered question marked (validation)" }, async () => {
    await capture.step(page, "click Submit with nothing answered", () => page.locator("#submit-answers").click(), { operation: "submit answers (engine)" });
    await expect(page.locator("#q0-error")).toBeVisible();
  });

  await capture.attempt(page, { id: "survey-paged-first-question", title: "Survey in pages: page 1 of 3, first question" }, async () => {
    await openSurvey(page, capture, links.paged);
    await expect(page.locator("#page-label")).toHaveText("Page 1 of 3");
  });
  await capture.attempt(page, { id: "survey-paged-validation", title: "Survey in pages: Next refused until the page's required questions are answered (mid-survey validation)" }, async () => {
    await capture.step(page, "click Next with nothing answered", () => page.locator("#page-next").click(), { operation: "page navigation (engine)" });
    await expect(page.locator("#submit-refusal-message")).toBeVisible();
  });
  await capture.attempt(page, { id: "survey-paged-mid", title: "Survey in pages: page 2 of 3 (mid-survey)" }, async () => {
    await capture.step(page, "answer rows 0-3 and 1-4, click Next", async () => {
      await pick(page, "0-3").check();
      await pick(page, "1-4").check();
      await page.locator("#page-next").click();
    }, { operation: "page navigation (engine)" });
    await expect(page.locator("#page-label")).toHaveText("Page 2 of 3");
  });
  await capture.attempt(page, { id: "survey-paged-last", title: "Survey in pages: page 3 of 3, every question answered, ready to submit" }, async () => {
    await capture.step(page, "answer row 2-6, click Next, answer rows 3-1 and 4-2", async () => {
      await pick(page, "2-6").check();
      await page.locator("#page-next").click();
      await expect(page.locator("#page-label")).toHaveText("Page 3 of 3");
      await pick(page, "3-1").check();
      await pick(page, "4-2").check();
    }, { operation: "page navigation (engine)" });
    await expect(page.locator("#progress")).toHaveText("5 of 5 answered");
  });
  await capture.attempt(page, { id: "survey-completion", title: "Survey: submitted, with the submission link (completion)" }, async () => {
    await capture.step(page, "click Submit", () => page.locator("#submit-answers").click(), { operation: "submit answers (engine)" });
    await expect(page.locator("#submitted-title")).toHaveText("Submitted");
  });

  const variants = [
    ["survey-no-link", "Survey page opened without a link (error)", "web/survey/", () => expect(page.locator("#survey-refused-message")).toBeVisible()],
    ["survey-unpublished", "Survey link naming a version this site does not publish (error)", links.unpublished, () => expect(page.locator("#survey-refused-message")).toBeVisible()],
    ["survey-expired", "Expired invitation (error)", links.expired, () => expect(page.locator("#survey-refused-message")).toBeVisible()],
    ["survey-test-link", "Test link, marked as a test", links.test, () => expect(page.locator("#test-notice")).toBeVisible()],
    ["survey-timed", "Timed survey, with its deadline notice", links.timed, () => expect(page.locator("#timer-notice")).toBeVisible()],
    ["survey-timed-expired", "Timed survey whose enforced timer ran out", links.timedExpired, () => expect(page.locator("#timer-notice")).toBeVisible()],
    ["survey-localized", "Survey in the invitation's locale (fr-CA)", links.localized, () => expect(page.locator("main")).toHaveAttribute("lang", "fr-CA")],
    ["survey-kinds", "Survey with other answer kinds (numbers, yes/no, checkboxes)", links.kinds, () => expect(page.locator("#survey-title")).toHaveText("Mixed kinds")],
    ["survey-compound", "Survey with rankings, allocations, best-worst, ranges and option trees", links.compound, () => expect(page.locator("#survey-title")).toHaveText("Compound kinds")]
  ];
  for (const [id, title, link, shown] of variants) {
    await capture.attempt(page, { id, title }, async () => { await openSurvey(page, capture, link); await shown(); });
  }

  await capture.attempt(page, { id: "survey-altered-file", title: "Survey whose published file was altered: refused (error)" }, async () => {
    await page.route(`**/${links.templateFile}`, async (route) => {
      const original = await (await route.fetch()).text();
      await route.fulfill({ status: 200, contentType: "application/json", body: original.replace("In an office", "In an 0ffice") });
    });
    await capture.step(page, `answer ${links.templateFile} with one altered word`, async () => {});
    await openSurvey(page, capture, links.identified);
    await expect(page.locator("#survey-refused-message")).toBeVisible();
    await page.unroute(`**/${links.templateFile}`);
  });

  // The published form: the assembled Pages site, with its banner and policy.
  await capture.attempt(page, { id: "survey-paged-first-question-pages-site", title: "Survey in pages, page 1, on the assembled Pages site", site: "pages" }, async () => {
    await openSurvey(page, capture, links.paged, SITES.pages);
    await expect(page.locator("#page-label")).toHaveText("Page 1 of 3");
  });
});

// ---------------------------------------------------------------------------
// The administrator console before sign-in: loading its configuration, the
// demo's "no store", a broken configuration, and the sign-in screen.

const DEPLOYMENT = "**/web/admin/signal.deployment.json";

scenario("admin-unsigned", async ({ page, capture }) => {
  const url = `${SITES.checkout.base}web/admin/index.html`;
  let release = () => {};
  await capture.attempt(page, { id: "admin-loading-configuration", title: "Console while its deployment configuration loads (fetch held)" }, async () => {
    const held = new Promise((resolve) => { release = resolve; });
    await page.route(DEPLOYMENT, async (route) => { await held; await route.continue(); });
    await capture.step(page, `hold signal.deployment.json, open ${url}`, async () => {
      await page.goto(url);
      await kernelRunning(page);
      await expect(page.locator("#loading")).toBeVisible({ timeout: 15_000 });
    }, { operation: "deployment configuration fetch" });
  });
  await capture.attempt(page, { id: "admin-unconfigured", title: "Console with no data store configured (the demo deployment; empty state)" }, async () => {
    await capture.step(page, "release signal.deployment.json (the checkout has none: 404)", async () => {
      release();
      await expect(page.getByRole("heading", { name: "No data store is configured" })).toBeVisible();
    }, { operation: "deployment configuration fetch" });
    await page.unroute(DEPLOYMENT);
  });
  await capture.attempt(page, { id: "admin-misconfigured", title: "Console with a deployment configuration it cannot use (error)", auth: AUTH.configuredOnly }, async () => {
    await page.route(DEPLOYMENT, (route) => route.fulfill({ status: 200, contentType: "application/json",
      body: JSON.stringify({ ...deployment, datasets: [{ ...deployment.datasets[0], profile: "missing" }] }) }));
    await capture.step(page, "answer signal.deployment.json with a dataset whose profile is not configured, reload", async () => {
      await fresh(page, url);
      await kernelRunning(page);
    }, { operation: "deployment configuration fetch" });
    await expect(page.locator("#misconfigured")).toBeVisible({ timeout: 15_000 });
    await page.unroute(DEPLOYMENT);
  });
  await capture.attempt(page, { id: "admin-sign-in", title: "Console sign-in screen (a configured deployment, signed out)", auth: AUTH.configuredOnly }, async () => {
    await routeSignIn(page);
    await capture.step(page, "answer signal.deployment.json with the suite's test deployment (GitHub through Fides), reload", async () => {
      await fresh(page, url);
      await kernelRunning(page);
    }, { operation: "deployment configuration fetch" });
    await expect(page.getByRole("heading", { name: "Sign in with GitHub" })).toBeVisible();
  });
  await capture.attempt(page, { id: "admin-pages-site-demo", title: "Console on the assembled Pages site (the published demo: no store, no sign-in)", site: "pages" }, async () => {
    await capture.step(page, `open ${SITES.pages.base}web/admin/`, async () => {
      await page.goto(`${SITES.pages.base}web/admin/`);
      await kernelRunning(page);
    }, { operation: "engine start-up + deployment configuration fetch" });
    await expect(page.locator("h2").first()).toBeVisible();
  });
});

// ---------------------------------------------------------------------------
// The signed-in console against the suite's fake GitHub store: every area,
// a group from empty to imported results, a survey from draft to published,
// and a store failure.

const nav = (page) => page.getByRole("navigation", { name: "Administration" });
const area = (page, name) => nav(page).getByRole("link", { name, exact: true }).click();
const groupTab = (page, name) => page.getByRole("navigation", { name: "Group" }).getByRole("link", { name, exact: true }).click();
const notice = (page) => page.locator("#notice-message");
const fill = async (scope, name, value, nth = 0) => {
  const box = scope.getByRole("textbox", { name, exact: true }).nth(nth);
  await box.fill(value);
  await box.press("Tab");
};
const spin = async (scope, name, value) => {
  const box = scope.getByRole("spinbutton", { name });
  await box.fill(value);
  await box.press("Tab");
};

scenario("admin-signed-in", async ({ page, capture }) => {
  const signedIn = { auth: AUTH.fakes };
  const store = new FakeGitHubStore({ origin });
  await routeSignIn(page);
  await store.install(page);

  // SIG-VIS-PENDING-001: the dataset open after sign-in, held at the store's
  // first request (GET /user), to photograph the busy dataset button and the
  // activity region; then released.
  const openHold = store.holdNext("GET", /^\/user$/);
  await capture.attempt(page, { ...signedIn, id: "admin-dataset-opening", title: "Dataset opening after sign-in (store request held): busy button and activity region" }, async () => {
    await capture.step(page, "open web/admin/ with the test deployment", async () => {
      await page.goto(`${SITES.checkout.base}web/admin/index.html`);
      await kernelRunning(page);
      await expect(page.getByRole("heading", { name: "Sign in with GitHub" })).toBeVisible();
    }, { operation: "engine start-up + deployment configuration fetch" });
    await capture.step(page, "click Sign in (GitHub authorize redirect and Fides exchange, both faked); hold GET /user", async () => {
      await page.getByRole("button", { name: "Sign in", exact: true }).click();
      await page.waitForURL(/\/web\/admin\/index\.html/);
      await kernelRunning(page);
      await openHold.reached;
      await expect(page.locator("#operation-pending")).toBeVisible({ timeout: 15_000 });
    }, { operation: "sign-in (OAuth redirect and token exchange)" });
  });
  const reached = await capture.attempt(page, { ...signedIn, id: "admin-overview-empty", title: "Console home (Overview), signed in, a new empty dataset" }, async () => {
    openHold.release();
    await capture.step(page, "wait for the dataset to open writable", () =>
      expect(page.getByRole("status").filter({ hasText: "Signed in; changes can be saved." })).toBeVisible({ timeout: STORE_MS }),
    { operation: "dataset open (store reads and first set-up writes)" });
    await expect(page.getByRole("heading", { level: 2, name: "Overview" })).toBeVisible();
  });
  if (!reached) return;

  const visit = (id, title, name, shown) =>
    capture.attempt(page, { ...signedIn, id, title }, async () => {
      await capture.step(page, `open the ${name} area`, () => area(page, name), { operation: "area navigation" });
      await shown();
    });
  await visit("admin-groups-empty", "Groups, none yet (empty state) with the new-group form", "Groups", () => expect(page.locator("#new-group")).toBeVisible());
  await visit("admin-compare-empty", "Compare with no groups (empty state)", "Compare", () => expect(page.getByRole("heading", { level: 2 }).first()).toBeVisible());
  await visit("admin-assessments-empty", "Assessments: the built-in catalog, nothing published in the dataset, the start-a-survey form", "Assessments", () => expect(page.locator("#new-draft")).toBeVisible());
  await visit("admin-reports-empty", "Report definitions, none yet (empty state)", "Reports", () => expect(page.getByRole("heading", { name: "Report definitions" })).toBeVisible());
  await visit("admin-administrators", "Administrators", "Administrators", () => expect(page.getByRole("heading", { level: 2 }).first()).toBeVisible());
  await visit("admin-storage", "Storage and dataset lifecycle", "Storage", () => expect(page.locator("#dataset-lifecycle-state")).toBeVisible());

  await capture.attempt(page, { ...signedIn, id: "admin-route-problem", title: "Console address naming no view (error)" }, async () => {
    await capture.step(page, "set the address to #/no-such-view", () => page.evaluate(() => { location.hash = "#/no-such-view"; }), { operation: "route change" });
    await expect(page.locator("#route-problem-title")).toBeVisible({ timeout: 15_000 });
  });

  // A group, empty, then with an imported response.
  await capture.attempt(page, { ...signedIn, id: "admin-group-overview", title: "A new group's overview and lifecycle" }, async () => {
    await capture.step(page, "open Groups, set expected surveys 2 and minimum reportable count 1, click Create group", async () => {
      await area(page, "Groups");
      const form = page.locator("#new-group");
      await spin(form, "Expected surveys", "2");
      await spin(form, "Minimum reportable count", "1");
      await form.getByRole("button", { name: "Create group" }).click();
      await expect(page.getByRole("heading", { level: 2, name: /^Group [0-9a-f]{32}$/ })).toBeVisible({ timeout: STORE_MS });
    }, { operation: "create group (store write)" });
  });
  const tab = (id, title, name, shown) =>
    capture.attempt(page, { ...signedIn, id, title }, async () => {
      await capture.step(page, `open the group's ${name} tab`, () => groupTab(page, name), { operation: "group tab navigation" });
      await shown();
    });
  await tab("admin-group-results-empty", "Group results with no imported surveys (empty state)", "Results", () => expect(page.locator("#exploration")).toBeVisible());
  await tab("admin-group-scoring-empty", "Group scoring with no imported surveys (empty state)", "Scoring", () => expect(page.getByRole("region", { name: "Scoring" })).toBeVisible());
  await tab("admin-group-imports-empty", "Group imports: invitation and import forms, nothing imported", "Imported surveys", () => expect(page.locator("#import")).toBeVisible());

  let submission = null;
  await capture.attempt(page, { ...signedIn, id: "admin-group-invitations-issued", title: "Group imports: two invitation links issued" }, async () => {
    const invitations = page.locator("#invitations");
    await capture.step(page, "set How many links to 2, click Create links", async () => {
      await spin(invitations, "How many links", "2");
      await invitations.getByRole("button", { name: "Create links" }).click();
      await expect(page.locator("#invitation-summary")).toHaveText(/2 link\(s\) issued/, { timeout: STORE_MS });
    }, { operation: "issue invitations (store write)" });
    const issued = await page.locator("#invitation-links code").allTextContents();
    const respondent = await capture.preparePage(await page.context().newPage());
    await capture.step(respondent, "in a second tab, open the first link, answer every question \"Often\", See results, Submit answers", async () => {
      await respondent.goto(new URL(issued[0], page.url()).href);
      await kernelRunning(respondent);
      for (const group of await respondent.locator("fieldset.forma-ordinal-scale").all()) await group.getByRole("radio", { name: "Often", exact: true }).check();
      await respondent.getByRole("button", { name: "See results" }).click();
      await respondent.getByRole("button", { name: "Submit answers" }).click();
      submission = await respondent.getByRole("textbox", { name: "Submission link" }).inputValue();
    }, { operation: "respondent answers an invitation" });
    await respondent.close();
  });
  await capture.attempt(page, { ...signedIn, id: "admin-group-import-outcome", title: "Group imports: one link accepted, one rejected" }, async () => {
    if (!submission) throw new Error("no submission link: the invitation was not answered");
    const imports = page.locator("#import");
    await capture.step(page, "paste the submission link and \"not a link\", click Import", async () => {
      await imports.getByRole("textbox", { name: "Completed survey links, one per line" }).fill(`${submission}\nnot a link`);
      await imports.getByRole("button", { name: "Import" }).click();
      await expect(page.locator("#importAccepted li")).toHaveCount(1, { timeout: STORE_MS });
    }, { operation: "import submissions (store write)" });
  });
  await tab("admin-group-results", "Group results with one imported survey", "Results", () => expect(page.locator("#exploration")).toBeVisible());
  await tab("admin-group-scoring", "Group scoring with one imported survey", "Scoring", () => expect(page.getByRole("region", { name: "Scoring" })).toBeVisible());
  await tab("admin-group-report", "Group report", "Report", () => expect(page.locator("#report")).toBeVisible());
  // SIG-VIS-PENDING-002: the group finalized and its results retired through
  // the confirmation, the retirement held, then the status with focus.
  const retireHold = { hold: null };
  await capture.attempt(page, { ...signedIn, id: "admin-group-retiring", title: "Retire accepted results confirmed, retirement in flight (store write held): the confirmation stays, its button busy" }, async () => {
    await capture.step(page, "rebuild the index, close and finalize the group, click Retire accepted results…, hold its first store read, confirm", async () => {
      await area(page, "Storage");
      await page.getByRole("button", { name: "Rebuild the contribution index", exact: true }).click();
      await expect(notice(page)).toContainText("The contribution index was rebuilt", { timeout: STORE_MS });
      await area(page, "Groups");
      await page.locator("#group-list tbody tr a").first().click();
      await page.locator("#lifecycle").getByRole("button", { name: "Close incomplete", exact: true }).click();
      await expect(page.locator("#group-phase")).toHaveAttribute("data-phase", "ClosedIncomplete", { timeout: STORE_MS });
      await page.locator("#lifecycle").getByRole("button", { name: "Finalize", exact: true }).click();
      await expect(page.locator("#group-phase")).toHaveAttribute("data-phase", /^Finalized/, { timeout: STORE_MS });
      const retention = page.locator("#group-retention");
      await retention.getByRole("button", { name: "Retire accepted results…", exact: true }).click();
      retireHold.hold = store.holdNext("GET", /./);
      await retention.getByRole("button", { name: "Retire accepted results", exact: true }).click();
      await retireHold.hold.reached;
      await expect(retention.getByRole("button", { name: "Retiring accepted results", exact: true })).toHaveAttribute("aria-busy", "true");
    }, { operation: "retire accepted results (store write)" });
  });
  await capture.attempt(page, { ...signedIn, id: "admin-group-retired", title: "Accepted results retired: the confirmation closed, focus on the status" }, async () => {
    await capture.step(page, "release the branch update", async () => {
      retireHold.hold?.release();
      await expect(page.locator("#retire-confirm")).toHaveCount(0, { timeout: STORE_MS });
    }, { operation: "retire accepted results (store write)" });
  });
  await visit("admin-overview", "Console home (Overview) with one group", "Overview", () => expect(page.getByRole("heading", { level: 2, name: "Overview" })).toBeVisible());
  await visit("admin-groups-list", "Groups list with one group", "Groups", () => expect(page.locator("#group-list tbody tr")).toHaveCount(1));

  // A survey from a new draft to a published version.
  const draft = page.locator("#draft");
  await capture.attempt(page, { ...signedIn, id: "admin-draft-new", title: "Survey authoring: a new draft with its validation findings" }, async () => {
    await capture.step(page, "open Assessments, enter PULSE / Team pulse, click Start draft", async () => {
      await area(page, "Assessments");
      const start = page.locator("#new-draft");
      await fill(start, "Survey identifier", "PULSE");
      await fill(start, "Title", "Team pulse");
      await start.getByRole("button", { name: "Start draft" }).click();
      await expect(draft.getByRole("heading", { name: "Team pulse (PULSE, draft)" })).toBeVisible({ timeout: STORE_MS });
    }, { operation: "start draft" });
  });
  await capture.attempt(page, { ...signedIn, id: "admin-draft-edited", title: "Survey authoring: a section, two questions and a fixture added, unsaved" }, async () => {
    await capture.step(page, "add section Delivery, two questions and the midpoint fixture", async () => {
      await fill(draft, "New section title", "Delivery");
      await draft.getByRole("button", { name: "Add section" }).click();
      for (const prompt of ["We ship every week.", "We fix bugs fast."]) {
        await fill(draft, "New question", prompt);
        await draft.getByRole("button", { name: "Add the question below here" }).click();
      }
      await draft.getByRole("button", { name: "Add a fixture: every answer at the midpoint" }).click();
      await expect(page.locator("#draft-fixtures li")).toHaveCount(1);
    }, { operation: "draft editing (engine)" });
  });
  await capture.attempt(page, { ...signedIn, id: "admin-draft-preview", title: "Survey authoring: the draft previewed as a respondent" }, async () => {
    const preview = page.locator("#draft-preview-area");
    await capture.step(page, "click Preview as a respondent, answer the first question \"Often\"", async () => {
      await preview.getByRole("button", { name: "Preview as a respondent" }).click();
      await preview.locator("#preview-rows").getByRole("radio", { name: "Often", exact: true }).first().check();
    }, { operation: "draft preview (engine)" });
    await expect(preview.getByRole("note")).toBeVisible();
  });
  // SIG-VIS-PENDING-001: the save held at its branch update, to photograph
  // the busy Save draft button, the disabled others and the activity region.
  const saveHold = store.holdNext("PATCH", /\/git\/refs\/heads\/main$/);
  await capture.attempt(page, { ...signedIn, id: "admin-draft-saving", title: "Survey authoring: Save draft in flight (store write held): busy button, others unavailable, not saved yet" }, async () => {
    await capture.step(page, "close the preview, click Save draft, hold the branch update", async () => {
      await page.locator("#draft-preview-area").getByRole("button", { name: "Close the preview" }).click();
      await draft.getByRole("button", { name: "Save draft", exact: true }).click();
      await saveHold.reached;
      await expect(draft.getByRole("button", { name: "Saving draft", exact: true })).toHaveAttribute("aria-busy", "true");
    }, { operation: "save draft (store write)" });
  });
  await capture.attempt(page, { ...signedIn, id: "admin-draft-saved", title: "Survey authoring: the draft saved" }, async () => {
    await capture.step(page, "release the branch update", async () => {
      saveHold.release();
      await expect(notice(page)).toHaveText("The draft of PULSE was saved.", { timeout: STORE_MS });
    }, { operation: "save draft (store write)" });
  });
  await capture.attempt(page, { ...signedIn, id: "admin-draft-reviewed", title: "Survey authoring: the draft submitted for review and approved" }, async () => {
    await capture.step(page, "click Submit for review, then Approve as reviewed", async () => {
      await draft.getByRole("button", { name: "Submit for review" }).click();
      await expect(page.locator("#draft-review")).toHaveText("Ready for review", { timeout: STORE_MS });
      await draft.getByRole("button", { name: "Approve as reviewed" }).click();
      await expect(page.locator("#draft-review")).toHaveText("Reviewed", { timeout: STORE_MS });
    }, { operation: "review transitions (store write)" });
  });
  await capture.attempt(page, { ...signedIn, id: "admin-publish-refused", title: "Survey authoring: Publish refused until the warnings are acknowledged (error)" }, async () => {
    await capture.step(page, "click Publish without acknowledging the warnings", async () => {
      await draft.getByRole("button", { name: "Publish" }).click();
      await expect(notice(page)).toContainText("Acknowledge the warnings first", { timeout: STORE_MS });
    }, { operation: "publish (store write)" });
  });
  await capture.attempt(page, { ...signedIn, id: "admin-draft-published", title: "Survey authoring: published as version 1" }, async () => {
    await capture.step(page, "acknowledge the warnings, click Publish", async () => {
      await draft.getByRole("checkbox", { name: "I have read the warnings and accept them" }).check();
      await draft.getByRole("button", { name: "Publish" }).click();
      await expect(notice(page)).toContainText(/^PULSE version 1 is published/, { timeout: STORE_MS });
    }, { operation: "publish (store write)" });
  });
  await visit("admin-assessments-published", "Assessments with a published version in the dataset (published-template view)", "Assessments",
    () => expect(page.locator("#template-versions li")).toContainText("Team pulse (PULSE 1)"));
  await capture.attempt(page, { ...signedIn, id: "admin-assessments-test-links", title: "Assessments: test links for the published version" }, async () => {
    await capture.step(page, "click Test links", () => page.locator("#template-versions li").getByRole("button", { name: "Test links" }).click(), { operation: "test links (engine)" });
    await expect(page.getByRole("region", { name: "Test links for Team pulse (PULSE 1)" })).toBeVisible();
  });

  // SIG-VIS-PENDING-002: a second draft deleted through its confirmation,
  // the deletion held (busy confirm button, the way out disabled), then the
  // status it hands focus to.
  const deleteHold = { hold: null };
  await capture.attempt(page, { ...signedIn, id: "admin-draft-deleting", title: "Delete draft confirmed, deletion in flight (store write held): the confirmation stays, its button busy" }, async () => {
    await capture.step(page, "start and save draft TEMP, click Delete draft…, hold the branch update, click Delete draft", async () => {
      await area(page, "Assessments");
      const start = page.locator("#new-draft");
      await fill(start, "Survey identifier", "TEMP");
      await fill(start, "Title", "Temporary");
      await start.getByRole("button", { name: "Start draft" }).click();
      await draft.getByRole("button", { name: "Save draft", exact: true }).click();
      await expect(notice(page)).toHaveText("The draft of TEMP was saved.", { timeout: STORE_MS });
      await draft.getByRole("button", { name: "Delete draft…", exact: true }).click();
      deleteHold.hold = store.holdNext("PATCH", /\/git\/refs\/heads\/main$/);
      await draft.getByRole("button", { name: "Delete draft", exact: true }).click();
      await deleteHold.hold.reached;
      await expect(draft.getByRole("button", { name: "Deleting draft", exact: true })).toHaveAttribute("aria-busy", "true");
    }, { operation: "delete draft (store write)" });
  });
  await capture.attempt(page, { ...signedIn, id: "admin-draft-deleted", title: "Draft deleted: the confirmation closed, focus on the status beside the notice" }, async () => {
    await capture.step(page, "release the branch update", async () => {
      deleteHold.hold?.release();
      await expect(page.locator("#activity")).toBeFocused({ timeout: STORE_MS });
    }, { operation: "delete draft (store write)" });
  });

  // A store that keeps failing mid-write: offline and read-only.
  await capture.attempt(page, { ...signedIn, id: "admin-store-offline", title: "Store failing mid-write: reported offline and read-only, nothing queued (error)" }, async () => {
    store.failNext("PATCH", /\/git\/refs\/heads\/main$/);
    store.failNext("PATCH", /\/git\/refs\/heads\/main$/);
    await capture.step(page, "make the store answer the next two ref updates with 503, then create a group", async () => {
      await area(page, "Groups");
      const form = page.locator("#new-group");
      await spin(form, "Expected surveys", "2");
      await form.getByRole("button", { name: "Create group" }).click();
      await expect(page.getByRole("status").filter({ hasText: "Engagement" })).toContainText("Read-only.", { timeout: STORE_MS });
    }, { operation: "create group (store write, failing)" });
  });
  await capture.attempt(page, { ...signedIn, id: "admin-store-recovered", title: "Store recovered after Refresh" }, async () => {
    await capture.step(page, "click Refresh", async () => {
      await page.getByRole("button", { name: "Refresh" }).click();
      await expect(page.getByRole("status").filter({ hasText: "Engagement" })).not.toContainText("Offline", { timeout: STORE_MS });
    }, { operation: "refresh (store reads)" });
  });
});
