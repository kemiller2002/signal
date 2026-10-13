// Published sample links on a phone (AUT-006 §§61-62): each demo survey the
// site publishes opens from its link, fits the screen without sideways
// scrolling, and is answered and submitted by touch.
import { readFileSync } from "node:fs";
import { test, expect } from "./support.js";

const links = JSON.parse(readFileSync(new URL("./fixtures/survey-links.json", import.meta.url), "utf8"));

async function open(page, link) {
  await page.goto(link);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
}

const fitsTheScreen = (page) => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth);

for (const name of ["identified", "kinds", "compound", "paged", "banked", "timed"]) {
  test(`the ${name} sample link opens on a phone and fits the screen`, async ({ page }) => {
    await open(page, links[name]);
    await expect(page.locator("#questions-title")).toBeVisible();
    await expect(page.locator("#survey-rows .survey-row").first()).toBeVisible();
    expect(await fitsTheScreen(page)).toBe(true);
  });
}

test("a survey is answered and submitted by touch on a phone", async ({ page }) => {
  await open(page, links.identified);
  for (const row of ["0-3", "1-4", "2-6", "3-1", "4-2"]) await page.locator(`[data-row="${row}"] input[data-event="answered"]`).tap();
  await expect(page.locator("#progress")).toHaveText("5 of 5 answered");
  await page.locator("#submit-answers").tap();
  await expect(page.locator("#submitted-title")).toHaveText("Submitted");
  expect(await fitsTheScreen(page)).toBe(true);
});

// SCS-014 (WI-0081): zoom, reduced motion and touch targets.
// Zoomed to 400% on a 1280-pixel screen, the page is 320 CSS pixels wide
// (WCAG 1.4.10 reflow): every demo survey still fits without sideways scrolling.
for (const name of ["identified", "kinds", "compound", "paged"]) {
  test(`the ${name} sample link reflows at 400% zoom without sideways scrolling`, async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 640 });
    await open(page, links[name]);
    await expect(page.locator("#survey-rows .survey-row").first()).toBeVisible();
    expect(await fitsTheScreen(page)).toBe(true);
  });
}

// Perceptible motion, explicitly: anything longer than 1 ms. Under
// prefers-reduced-motion, Forma 0.6.1 (foundations.css) does not remove
// transitions and animations; it shortens them to 0.01 ms, so a tap can leave
// a 0.01 ms CSSTransition registered for one frame (seen in CI on a label's
// colour). 1 ms is far below anything a person can perceive (WCAG 2.3.3
// Animation from Interactions); anything longer is motion and fails.
// Reported upstream: https://github.com/kemiller2002/forma/issues/157
const PERCEPTIBLE_MS = 1;

test("with reduced motion asked for, answering runs no animation or transition", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await open(page, links.identified);
  await page.locator('[data-row="0-3"] input[data-event="answered"]').tap();
  await expect(page.locator("#progress")).toHaveText("1 of 5 answered");
  // Every running or declared animation and transition longer than 1 ms,
  // named so a failure says what moved.
  const moving = await page.evaluate((limit) => {
    const name = (e) => (e && e.tagName ? e.tagName.toLowerCase() + (e.id ? "#" + e.id : "") + (e.className && typeof e.className === "string" ? "." + e.className.trim().split(/\s+/).join(".") : "") : String(e));
    const ms = (d) => (d.trim().endsWith("ms") ? parseFloat(d) : parseFloat(d) * 1000);
    const running = document
      .getAnimations()
      .filter((a) => Number(a.effect && a.effect.getTiming().duration) > limit)
      .map((a) => `running ${a.constructor.name} ${a.transitionProperty ?? a.animationName ?? ""} on ${name(a.effect && a.effect.target)} (${a.effect.getTiming().duration}ms)`);
    const declared = [...document.querySelectorAll("*")].flatMap((e) => {
      const s = getComputedStyle(e);
      return [
        ...(s.transitionDuration.split(",").some((d) => ms(d) > limit) ? [`transition ${s.transitionProperty} ${s.transitionDuration} on ${name(e)}`] : []),
        ...(s.animationName !== "none" && s.animationDuration.split(",").some((d) => ms(d) > limit) ? [`animation ${s.animationName} ${s.animationDuration} on ${name(e)}`] : [])
      ];
    });
    return [...running, ...declared];
  }, PERCEPTIBLE_MS);
  expect(moving).toEqual([]);
});

test("every answer's touch target is at least 24 by 24 CSS pixels", async ({ page }) => {
  await open(page, links.identified);
  const small = await page.evaluate(() =>
    [...document.querySelectorAll("#survey-rows label")]
      .filter((l) => l.offsetParent !== null)
      .map((l) => l.getBoundingClientRect())
      .filter((r) => r.width < 24 || r.height < 24).length
  );
  expect(small).toBe(0);
});
