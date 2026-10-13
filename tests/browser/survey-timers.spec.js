// Section and page timers on the survey page (DF-SIGNAL-2026-0015,
// SIG-LINK-016): a page timer starts when its page is first shown and is
// stamped into the link; a reload keeps it; an expired enforced timer holds
// that page's answers; an edited start is refused.
import { readFileSync } from "node:fs";
import { test, expect } from "./support.js";

const links = JSON.parse(readFileSync(new URL("./fixtures/timer-links.json", import.meta.url), "utf8"));
const pick = (page, row) => page.locator(`[data-row="${row}"] input[data-event="answered"]`);
const payloadOf = (url) => Buffer.from(new URL(url).hash.split("#r=")[1].split("&")[0].replaceAll("-", "+").replaceAll("_", "/"), "base64");

async function open(page, link) {
  await page.goto(link);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
}

test("a page timer starts when its page is shown, and a reload keeps its start", async ({ page }) => {
  await open(page, links.scoped);
  await expect(page.locator("#timer-notice")).toContainText("Answer this page by");
  // Format 2 with the timer-starts flag (bit 3).
  await expect.poll(() => payloadOf(page.url())[0]).toBe(2);
  await expect.poll(() => payloadOf(page.url())[42] & 8).toBe(8);
  const stamped = payloadOf(page.url()).subarray(42, 51).toString("hex");
  await pick(page, "0-3").check();
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(pick(page, "0-3")).toBeChecked();
  expect(payloadOf(page.url()).subarray(42, 51).toString("hex")).toBe(stamped);
});

test("an expired page timer holds its page, and an edited start is refused", async ({ page }) => {
  await open(page, links.scopedExpired);
  await expect(page.locator("#timer-notice")).toContainText("Time is up for this page");
  await expect(pick(page, "0-3")).toBeDisabled();

  const [path, payload] = links.scopedExpired.split("#r=");
  const bytes = Buffer.from(payload.replaceAll("-", "+").replaceAll("_", "/"), "base64");
  bytes[50] = bytes[50] + 1;
  await page.goto(`${path}#r=${bytes.toString("base64").replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "")}`);
  await expect(page.locator("#survey-refused-message")).toBeVisible();
});
