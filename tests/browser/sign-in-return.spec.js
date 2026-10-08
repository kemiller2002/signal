// The link target survives sign-in (SIG-LINK-006), end to end in a real
// browser: a signed-out person opens a deep link, is sent to
// #/sign-in?returnTo=..., signs in through Fides and the identity provider
// (both faked at the network), and lands on the view the link named. The
// fragment does not survive the provider's redirect, so this proves the
// target crossed it in tab storage.
import { test, expect, expectConsoleError } from "./support.js";

const origin = `http://127.0.0.1:${process.env.SIGNAL_TEST_PORT ?? 4321}`;
const redirectUri = `${origin}/web/admin/index.html`;

const deployment = {
  environment: "production",
  environmentName: "production",
  identity: { exchange: "https://fides.test", application: "signal-admin", provider: "github", clientId: "Iv23liTEST", redirectUri },
  profiles: [{ id: "primary", label: "Survey data", provider: "github",
    location: { owner: "acme", repository: "signal-data", branch: "main", basePath: "prod" } }],
  datasets: [{ id: "ds_engagement", label: "Engagement", profile: "primary", administrators: ["583231"] }]
};

const inHours = (hours) => new Date(Date.now() + hours * 3600 * 1000).toISOString().replace(/\.\d+Z$/, "Z");

test("a signed-out deep link returns to its view after signing in", async ({ page }) => {
  await page.route("**/web/admin/signal.deployment.json", (route) =>
    route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(deployment) }));
  // The provider sends the browser straight back with a code and the state it was given.
  await page.route("https://github.com/**", (route) => {
    const state = new URL(route.request().url()).searchParams.get("state");
    return route.fulfill({ status: 302, headers: { location: `${redirectUri}?code=good-code&state=${state}` } });
  });
  // Fides' exchange, cross-origin: it answers the preflight and the token request.
  const cors = { "access-control-allow-origin": origin, "access-control-allow-methods": "POST", "access-control-allow-headers": "content-type" };
  await page.route("https://fides.test/v1/token", (route) =>
    route.request().method() === "OPTIONS"
      ? route.fulfill({ status: 204, headers: cors })
      : route.fulfill({ status: 200, contentType: "application/json", headers: cors,
          body: JSON.stringify({ accessToken: "gho_BROWSERTESTTOKEN0123456789", accessTokenExpiresAt: inHours(8),
            refreshToken: "ghr_BROWSERTESTREFRESH0123456789", refreshTokenExpiresAt: inHours(24 * 180),
            identity: { provider: "github", subject: "583231", login: "octocat", name: "The Octocat" } }) }));
  // No real store: the dataset cannot be reached, which the page reports.
  await page.route("https://api.github.com/**", (route) => route.abort("connectionrefused"));
  expectConsoleError(page, /Failed to load resource|ERR_CONNECTION_REFUSED|AEGIS|aegis|Aegis/);

  const target = "/groups/0405060708090a0b0c0d0e0f10111213/results?section=D01&display=percent";
  await page.goto(`/web/admin/index.html#${target}`);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");

  await expect.poll(() => new URL(page.url()).hash).toBe(`#/sign-in?returnTo=${encodeURIComponent(target)}`);
  await expect(page.getByRole("heading", { name: "Sign in with GitHub" })).toBeVisible();

  await page.locator("#sign-in-button").click();
  await page.waitForURL(/\/web\/admin\/index\.html/);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");

  // Back at the view the link named, with the provider's callback gone from the address.
  await expect.poll(() => new URL(page.url()).hash).toBe(`#${target}`);
  expect(new URL(page.url()).search).toBe("");
  await expect(page.locator("#principal")).toHaveText("github:583231");
  expect(await page.evaluate(() => sessionStorage.getItem("signal.admin.returnTo"))).toBeNull();
});
