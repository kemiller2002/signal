// Visual-review evidence capture (WI-0110), public pages: the landing page,
// the engine pages' pre-runtime start-up states, the assessment page and the
// survey page. The shared session, sites and scenario wrapper are in
// capture-session.mjs; the administrator console is capture-admin.spec.js.
// Evidence tooling, not a test: see capture-session.mjs.
import { expect, test } from "@playwright/test";
import { INVITATION, links, origin } from "../browser/ui-conformance-fixtures.mjs";
import { FRAMEWORK, SITES, fresh, kernelRunning, scenario } from "./capture-session.mjs";

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
  // SIG-ECHELON-APP-002 (d): Copy link's outcome, shown under the button and
  // announced by the page's one live region (#activity).
  await capture.attempt(page, { id: "survey-link-copied", title: "Survey: the submission link copied, its outcome shown and announced by #activity", extents: ["full", "viewport"], anchor: "#copy-link" }, async () => {
    await capture.step(page, "allow the clipboard, click Copy link", async () => {
      await page.context().grantPermissions(["clipboard-read", "clipboard-write"]).catch(() => {});
      await page.locator("#copy-link").click();
      await expect(page.locator("#copy-notice")).toBeVisible();
      await expect(page.locator("#activity")).toContainText(await page.locator("#copy-notice").innerText());
    }, { operation: "copy to the clipboard" });
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
