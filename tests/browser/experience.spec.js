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

// The Echelon theme 1.1.0's self-hosted fonts (SIG-ECHELON-APP-001), from the
// assembled Pages site, whose every page carries the strict policy
// (style-src 'self'; font-src 'self'): each family loads from the site's own
// theme files, and the page reports no policy violation.
const pagesSite = (baseURL) => {
  const url = new URL(baseURL);
  return `${url.protocol}//${url.hostname}:${Number(url.port) + 1}/signal/`;
};

for (const path of ["web/", "web/survey/", "web/admin/", "web/home/"]) {
  test(`${path} on the Pages site: the Echelon fonts load from the site under its policy`, async ({ page, baseURL }) => {
    const site = pagesSite(baseURL);
    const fonts = [];
    page.on("response", (response) => { if (/\.woff2$/.test(response.url())) fonts.push({ url: response.url(), status: response.status() }); });
    await page.addInitScript(() => {
      window.__violations = [];
      document.addEventListener("securitypolicyviolation", (event) => window.__violations.push(`${event.violatedDirective} ${event.blockedURI}`));
    });
    await page.route(`${site}${FRAMEWORK.replace("**/", "")}`, () => new Promise(() => {}));
    await page.goto(`${site}${path}`);
    await expect(page.locator('meta[http-equiv="Content-Security-Policy"]')).toHaveAttribute("content", /font-src 'self'/);
    const loaded = await page.evaluate(async () => {
      const families = ["Manrope", "Newsreader", "IBM Plex Mono"];
      const faces = await Promise.all(families.map((family) => document.fonts.load(`1em "${family}"`)));
      await document.fonts.ready;
      return families.map((family, i) => ({ family, faces: faces[i].map((face) => face.status) }));
    });
    for (const { family, faces } of loaded) {
      expect(faces.length, `${family} has no font face`).toBeGreaterThan(0);
      expect(faces.every((status) => status === "loaded"), `${family}: ${faces.join(", ")}`).toBe(true);
    }
    // The body text is set in Manrope, and the fonts came from the site itself.
    expect(await page.evaluate(() => document.fonts.check('1em "Manrope"'))).toBe(true);
    expect(fonts.length).toBeGreaterThan(0);
    for (const font of fonts) {
      expect(font.url.startsWith(`${site}node_modules/@echelon-foundry/echelon-theme/dist/fonts/`), font.url).toBe(true);
      expect(font.status, font.url).toBe(200);
    }
    expect(await page.evaluate(() => window.__violations)).toEqual([]);
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

// The "foundry ledger" landing page (SIG-ECHELON-APP-002): it explains Signal
// and demonstrates no administrator feature (no form, field or figure), with
// one primary action, to the console, and Signal's own mark.
test("the landing page offers one primary action and demonstrates no administrator feature", async ({ page }) => {
  await page.goto("/web/home/index.html");
  const primary = page.locator('[data-echelon-emphasis="primary"]');
  await expect(primary).toHaveCount(1);
  await expect(primary).toHaveText("Open the administrator console");
  await expect(primary).toHaveAttribute("href", "../admin/");
  await expect(page.locator("form, input, select, textarea, button, .echelon-figure, .echelon-chip")).toHaveCount(0);
  await expect(page.getByRole("heading", { level: 1 })).toHaveClass(/\bechelon-display\b/);
  await expect(page.getByRole("complementary", { name: "Answering a survey?" })).toContainText("Signal never asks you to sign in to answer.");
  await expect(page.locator("header .forma-app-identity .forma-app-identity__name")).toHaveText("Signal");
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
