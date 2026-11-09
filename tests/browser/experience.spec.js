// Application identity, landing page and pre-runtime start-up
// (EDS-BRAND-APP-001, EDS-UX-LANDING-001, EDS-UX-WAIT-001; Forma .forma-app-identity
// and .forma-startup). The start-up screen is painted from each page's HTML
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
    await expect(screen.locator(".forma-app-identity__name")).toHaveText("Signal");
    await expect(screen.locator(".forma-spinner__indicator")).toBeVisible();
    await expect(screen.locator(".forma-startup__problem")).toBeHidden();
    release();
    await expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: 30_000 });
    await expect(screen).toBeHidden();
  });

  test(`${path}: a failed engine download ends the activity and explains the failure`, async ({ page }) => {
    await page.route(FRAMEWORK, (route) => route.abort());
    await page.goto(path);
    const screen = page.locator("#app-startup");
    await expect(screen).toHaveAttribute("data-forma-state", "failed", { timeout: 30_000 });
    await expect(screen.getByRole("alert")).toContainText("Signal could not start.");
    await expect(screen.locator(".forma-startup__status")).toBeHidden();
  });

  test(`${path}: a page module that fails to load ends the start-up screen as failed`, async ({ page }) => {
    await page.route("**/web-kernel/limen-wasm.js", (route) => route.fulfill({ status: 404, body: "" }));
    await page.goto(path);
    const screen = page.locator("#app-startup");
    await expect(screen).toHaveAttribute("data-forma-state", "failed", { timeout: 15_000 });
    await expect(screen.getByRole("alert")).toBeVisible();
    await expect(screen.locator(".forma-startup__status")).toBeHidden();
  });

  test(`${path}: a start that never finishes times out after 60 seconds`, async ({ page }) => {
    await page.clock.install();
    await page.route(FRAMEWORK, () => new Promise(() => {}));
    await page.goto(path);
    const screen = page.locator("#app-startup");
    await expect(screen).toHaveAttribute("data-forma-state", "starting");
    await page.clock.runFor(59_000);
    await expect(screen).toHaveAttribute("data-forma-state", "starting");
    await page.clock.runFor(2_000);
    await expect(screen).toHaveAttribute("data-forma-state", "timed-out");
    await expect(screen.getByRole("alert")).toContainText("taking too long to start");
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

// Forma 1.x is brand-neutral; the Echelon theme (loaded after Forma, applied
// by data-forma-brand on <html>) supplies Echelon colours and fonts
// (EDS-ADR-002). Signal's own mark stays Signal's.
for (const path of [...PAGES, "/web/home/index.html"]) {
  test(`${path}: the Echelon theme brands the page over Forma's neutral palette`, async ({ page }) => {
    await page.route(FRAMEWORK, () => new Promise(() => {}));
    await page.goto(path);
    await expect(page.locator("html")).toHaveAttribute("data-forma-brand", "echelon");
    const look = () => page.evaluate(() => {
      const body = getComputedStyle(document.body);
      return { surface: body.backgroundColor, font: body.fontFamily };
    });
    const branded = await look();
    await page.evaluate(() => document.documentElement.removeAttribute("data-forma-brand"));
    const neutral = await look();
    expect(branded.font).toMatch(/^Manrope\b/);
    expect(neutral.font).not.toMatch(/Manrope/);
    expect(branded.surface).not.toBe(neutral.surface);
    await expect(page.locator(".forma-app-identity__name").first()).toHaveText("Signal");
    await expect(page.locator('[src*="logos/echelon-foundry/"], [href*="logos/echelon-foundry/"]')).toHaveCount(0);
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

test("the mark's approval is recorded, not assumed", async ({ request }) => {
  const identity = await (await request.get("/web/brand/identity.json")).json();
  expect(identity.status).toBe("approved");
  expect(identity.approved).toBe(true);
  expect(identity.approval.by).toBeTruthy();
  expect(identity.approval.record).toContain("echelon-design-system");
});
