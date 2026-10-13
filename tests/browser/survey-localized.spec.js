// The survey page in the link's locale (WI-0091, DF-SIGNAL-2026-0020): a
// translated template shows its translation for the link's language, keeps
// the source text where there is none, and applies the template's
// accessibility text: an accessible name read in place of the prompt and a
// description read after it. That the answers and their encoding are the
// same in every language is proven in LocalizedPageTests.
import { readFileSync } from "node:fs";
import { test, expect } from "./support.js";

const links = JSON.parse(readFileSync(new URL("./fixtures/localized-links.json", import.meta.url), "utf8"));

async function open(page, link) {
  await page.goto(link);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
}

const pick = (page, row) => page.locator(`[data-row="${row}"] input[data-event="answered"]`);

test("a link in a translated language shows the translation and names its language", async ({ page }) => {
  await open(page, links.french);
  await expect(page.locator("#survey-title")).toHaveText("Enquête traduite");
  await expect(page.locator("main")).toHaveAttribute("lang", "fr");
  await expect(page.locator('[data-row="s0"]')).toContainText("Le travail");
  await expect(page.locator('[data-row="0"]')).toContainText("À quelle fréquence l'équipe revoit-elle son travail ?");
  await expect(page.locator('[data-row="0-0"]')).toContainText("Jamais");
  // An untranslated label keeps its source text.
  await expect(page.locator('[data-row="0-3"]')).toContainText("Often");
  await expect(page.locator('[data-row="1-0"]')).toContainText("Un tableau");
});

test("the template's accessibility text names and describes the question", async ({ page }) => {
  await open(page, links.french);
  const input = pick(page, "0-0");
  // The accessible description is announced with the radio buttons.
  await expect(input).toHaveAccessibleDescription(/Fréquence des revues.*Cinq niveaux, de jamais à toujours\./);
  // The visible prompt is replaced for assistive technology, not hidden on screen.
  await expect(page.locator('[data-row="0"] strong')).toBeVisible();
  await expect(page.locator('[data-row="0"] strong')).toHaveAttribute("aria-hidden", "true");

  // A question without accessibility text is described by its prompt as before.
  await expect(pick(page, "1-0")).toHaveAccessibleDescription(/Quel outil l'équipe utilise-t-elle le plus \?/);
});

test("a language without a translation shows the source text", async ({ page }) => {
  await open(page, links.untranslated);
  await expect(page.locator("#survey-title")).toHaveText("Translated survey");
  await expect(page.locator("main")).toHaveAttribute("lang", "de-DE");
  await expect(page.locator('[data-row="0"]')).toContainText("How often does the team review its work?");
});

test("answering in the translated page follows the link and survives a reload", async ({ page }) => {
  await open(page, links.french);
  await pick(page, "0-3").check();
  await pick(page, "1-0").check();
  await expect(page.locator("#progress")).toHaveText("2 of 2 answered");
  await expect.poll(() => page.url()).not.toBe(new URL(links.french, page.url()).href);

  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(page.locator("main")).toHaveAttribute("lang", "fr");
  await expect(pick(page, "0-3")).toBeChecked();
  await expect(pick(page, "1-0")).toBeChecked();
});
