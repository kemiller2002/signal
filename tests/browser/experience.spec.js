// Application identity, landing page and pre-runtime start-up
// (EDS-BRAND-APP-001, EDS-UX-LANDING-001, EDS-UX-WAIT-001; Forma .ef-app-identity
// and .ef-startup). The start-up screen is painted from each page's HTML
// before the WebAssembly engine loads; web-kernel/limen-wasm.js hides it once
// the kernel runs and marks a failed start.
import { test, expect } from "@playwright/test";

const PAGES = ["/web/index.html", "/web/survey/index.html", "/web/admin/index.html"];
const FRAMEWORK = "**/build/wasm/wwwroot/_framework/dotnet.js";

for (const path of PAGES) {
  test(`${path}: the start-up screen is visible before the engine loads and hidden after`, async ({ page }) => {
    let release;
    const held = new Promise((resolve) => { release = resolve; });
    await page.route(FRAMEWORK, async (route) => { await held; await route.continue(); });
    await page.goto(path);
    const screen = page.locator("#app-startup");
    await expect(screen).toBeVisible();
    await expect(screen.getByRole("status")).toContainText("Starting Signal");
    await expect(screen.locator(".ef-app-identity__name")).toHaveText("Signal");
    await expect(screen.locator(".ef-spinner__indicator")).toBeVisible();
    await expect(screen.locator(".ef-startup__problem")).toBeHidden();
    release();
    await expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: 30_000 });
    await expect(screen).toBeHidden();
  });

  test(`${path}: a failed engine download ends the activity and explains the failure`, async ({ page }) => {
    await page.route(FRAMEWORK, (route) => route.abort());
    await page.goto(path);
    const screen = page.locator("#app-startup");
    await expect(screen).toHaveAttribute("data-ef-state", "failed", { timeout: 30_000 });
    await expect(screen.getByRole("alert")).toContainText("Signal could not start.");
    await expect(screen.locator(".ef-startup__status")).toBeHidden();
  });

  test(`${path}: the favicon is Signal's mark, not an empty icon`, async ({ page, request }) => {
    await page.goto(path);
    const href = await page.locator('link[rel="icon"]').evaluate((link) => link.href);
    expect(href).toMatch(/\/web\/brand\/signal-mark\.svg$/);
    const response = await request.get(href);
    expect(response.ok()).toBe(true);
    expect(await response.text()).toContain("<svg");
  });
}

test("the landing page explains Signal and leads to the administrator console", async ({ page }) => {
  await page.goto("/web/home/index.html");
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Surveys whose answers live in the link");
  await expect(page.getByRole("link", { name: "Signal", exact: true })).toBeVisible();
  const console = page.getByRole("link", { name: "Open the administrator console" });
  await expect(console).toBeVisible();
  await console.click();
  await expect(page).toHaveURL(/\/web\/admin\/$/);
});

test("the landing page shows no sample records and runs no script", async ({ page }) => {
  await page.goto("/web/home/index.html");
  expect(await page.locator("script").count()).toBe(0);
  await expect(page.locator("table, [data-each]")).toHaveCount(0);
});

for (const width of [320, 390, 768, 1280]) {
  test(`the landing page does not scroll sideways at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.goto("/web/home/index.html");
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow).toBeLessThanOrEqual(0);
  });
}

test("the landing page works in a dark color scheme", async ({ page }) => {
  await page.emulateMedia({ colorScheme: "dark" });
  await page.goto("/web/home/index.html");
  const [text, surface] = await page.evaluate(() => {
    const style = getComputedStyle(document.body);
    return [style.color, style.backgroundColor];
  });
  expect(text).not.toBe(surface);
});

test("the provisional mark is declared provisional, not approved", async ({ request }) => {
  const identity = await (await request.get("/web/brand/identity.json")).json();
  expect(identity.status).toBe("proposed");
  expect(identity.approved).toBe(false);
});
