// The administrator page (web/admin/) end to end: DOM event -> Limen kernel
// -> WASM shim -> F# engine (Signal.Admin) -> view -> DOM, with Signal's
// host pack and Limen's schedule pack negotiated. The F# suite drives the
// engine through every flow against Arca's in-memory provider; this suite
// proves the page reaches the engine and the engine reaches the browser.
import { test, expect } from "./support.js";

const configured = {
  environment: "production",
  environmentName: "production",
  identity: {
    exchange: "https://fides.test",
    application: "signal-admin",
    provider: "github",
    clientId: "Iv23liTEST",
    redirectUri: `http://127.0.0.1:${process.env.SIGNAL_TEST_PORT ?? 4321}/web/admin/index.html`
  },
  profiles: [{ id: "primary", label: "Survey data", provider: "github",
    location: { owner: "acme", repository: "signal-data", branch: "main", basePath: "prod" } }],
  datasets: [{ id: "ds_engagement", label: "Engagement", profile: "primary", administrators: ["583231"] }]
};

async function open(page) {
  await page.goto("/web/admin/index.html");
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
}

test("the page negotiates its packs and says when no store is configured", async ({ page }) => {
  await open(page);
  await expect(page.getByRole("heading", { name: "No data store is configured" })).toBeVisible();
});

test("a configured deployment signs in with GitHub through Fides, memory-only by default", async ({ page }) => {
  await page.route("**/web/admin/signal.deployment.json", (route) =>
    route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(configured) }));
  let authorize = null;
  await page.route("https://github.com/**", (route) => {
    authorize = route.request().url();
    return route.fulfill({ status: 200, contentType: "text/html", body: "<p>GitHub</p>" });
  });

  await open(page);
  await expect(page.getByRole("heading", { name: "Sign in with GitHub" })).toBeVisible();
  await expect(page.locator("#retention-page")).toBeChecked();
  await expect(page.locator("#retention-disclosure")).toContainText("only on this page");

  await page.locator("#sign-in-button").click();
  await page.waitForURL(/github\.com\/login\/oauth\/authorize/);
  const url = new URL(authorize);
  expect(url.searchParams.get("client_id")).toBe("Iv23liTEST");
  expect(url.searchParams.get("code_challenge_method")).toBe("S256");
  expect(url.searchParams.get("redirect_uri")).toBe(configured.identity.redirectUri);
});
