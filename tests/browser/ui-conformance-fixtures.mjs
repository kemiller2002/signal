// Shared fixtures for the UI conformance specs (UI-LAYOUT-INTERACTION@1.0.0,
// signal#104, signal#113): the pages' fixed links and names, the bounded
// kernel start-up wait, and the signed-in administrator's fakes at the
// network boundary (the deployment, GitHub's authorize redirect and Fides'
// exchange). Test-only.
import { readFileSync } from "node:fs";
import { test, expect, expectConsoleError } from "./support.js";
import { FakeGitHubStore } from "./fakes/github-store.js";
import {
  expectPeerAlignment as expectPeerAlignmentMeasured,
  inventoryVisibleActions
} from "./ui-conformance-helpers.mjs";


// Every measured group is attached to the test result (JSON reporter, trace)
// as conformance evidence: rule, group, edge, delta and each peer's coordinate.
export async function expectPeerAlignment(page, group, options) {
  const report = await expectPeerAlignmentMeasured(page, group, options);
  await test.info().attach(`alignment ${report.group} @${page.viewportSize()?.width}`, {
    body: JSON.stringify({ viewport: page.viewportSize()?.width, ...report }), contentType: "application/json" });
  return report;
}

export const links = JSON.parse(readFileSync(new URL("./fixtures/survey-links.json", import.meta.url), "utf8"));
export const KERNEL_START_MS = 30_000;

// An anonymous invitation (as in assessment.spec.js): it can be submitted.
export const INVITATION = "/web/index.html#r=AQSV3vb9UTM28AoLDA0ODxAREhMUFRYXGBlkZWZnaGlqa2xtbm9wcXJzAA8AAAAAAAAAAGf0i7E";
export const FIRST_PROMPT = "Major requirements are expected to be substantially defined before implementation begins.";
export const ORDINAL = ["Never or almost never", "Rarely", "Sometimes", "Often", "Almost always"];
export const SPECIAL = ["Don't know", "Not observed", "Not applicable"];
export const AREAS = [
  ["Overview", "#/"],
  ["Groups", "#/groups"],
  ["Compare", "#/compare"],
  ["Assessments", "#/assessments"],
  ["Reports", "#/reports"],
  ["Administrators", "#/administrators"],
  ["Storage", "#/storage"]
];

export async function open(page, url, width = 1280) {
  await page.setViewportSize({ width, height: 900 });
  await page.goto(url);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: KERNEL_START_MS });
}

export const question = (page, prompt) => page.getByRole("group", { name: prompt, exact: true });

// Answers every assessment question with the same visible choice, by label.
export async function answerAll(page, choice) {
  const groups = page.locator("fieldset.ef-ordinal-scale");
  const count = await groups.count();
  for (let i = 0; i < count; i++) await groups.nth(i).getByRole("radio", { name: choice, exact: true }).check();
  await expect(page.locator("#progress")).toHaveText(`${count} of ${count} answered`);
}

// The enabled, rendered, non-field actions on the page, by name, in order.
export const actionNames = async (page) => (await inventoryVisibleActions(page)).map((action) => action.nameHint);

export const origin = `http://127.0.0.1:${process.env.SIGNAL_TEST_PORT ?? 4321}`;
export const redirectUri = `${origin}/web/admin/index.html`;
export const deployment = {
  environment: "production",
  environmentName: "production",
  identity: { exchange: "https://fides.test", application: "signal-admin", provider: "github", clientId: "Iv23liTEST", redirectUri },
  profiles: [{ id: "primary", label: "Survey data", provider: "github",
    location: { owner: "acme", repository: "signal-data", branch: "main", basePath: "prod" } }],
  datasets: [{ id: "ds_engagement", label: "Engagement", profile: "primary", administrators: ["583231"] }]
};
const inHours = (hours) => new Date(Date.now() + hours * 3600 * 1000).toISOString().replace(/\.\d+Z$/, "Z");

// Signs in through the real page: GitHub's authorize step and Fides'
// exchange are answered locally; the store is whatever the caller routed.
export async function routeSignIn(page) {
  await page.route("**/web/admin/signal.deployment.json", (route) =>
    route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(deployment) }));
  await page.route("https://github.com/**", (route) => {
    const state = new URL(route.request().url()).searchParams.get("state");
    return route.fulfill({ status: 302, headers: { location: `${redirectUri}?code=good-code&state=${state}` } });
  });
  const cors = { "access-control-allow-origin": origin, "access-control-allow-methods": "POST", "access-control-allow-headers": "content-type" };
  await page.route("https://fides.test/v1/**", (route) =>
    route.request().method() === "OPTIONS"
      ? route.fulfill({ status: 204, headers: cors })
      : route.fulfill({ status: 200, contentType: "application/json", headers: cors,
          body: JSON.stringify({ accessToken: "gho_BROWSERTESTTOKEN0123456789", accessTokenExpiresAt: inHours(8),
            refreshToken: "ghr_BROWSERTESTREFRESH0123456789", refreshTokenExpiresAt: inHours(24 * 180),
            identity: { provider: "github", subject: "583231", login: "octocat", name: "The Octocat" } }) }));
}

export async function signIn(page, width = 1280) {
  await open(page, "/web/admin/index.html", width);
  await expect(page.getByRole("heading", { name: "Sign in with GitHub" })).toBeVisible();
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.waitForURL(/\/web\/admin\/index\.html/);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: KERNEL_START_MS });
}

// Signs in against a fresh, test-only fake of the GitHub store
// (fakes/github-store.js) and waits until the dataset is open and writable.
// The store answers a missing file with 404, which the browser logs as a
// failed resource load: those lines are expected, nothing else is.
export async function signInWithStore(page, { width = 1280 } = {}) {
  const store = new FakeGitHubStore({ origin });
  expectConsoleError(page, /Failed to load resource: the server responded with a status of 404/);
  await routeSignIn(page);
  await store.install(page);
  await signIn(page, width);
  await expect(page.getByRole("status").filter({ hasText: "Signed in; changes can be saved." })).toBeVisible({ timeout: KERNEL_START_MS });
  return store;
}

const DATASET = "prod/signal/datasets/ds_engagement";
export const recordsOf = (store, type) =>
  store.files().filter((path) => path.startsWith(`${DATASET}/records/${type}/`));

// Creates a group through the Groups area's form and returns its key.
export async function createGroup(page, { expected = "2", minimum = "1" } = {}) {
  await page.getByRole("navigation", { name: "Administration" }).getByRole("link", { name: "Groups", exact: true }).click();
  const form = page.locator("#new-group");
  await form.getByRole("spinbutton", { name: "Expected surveys" }).fill(expected);
  await form.getByRole("spinbutton", { name: "Expected surveys" }).press("Tab");
  await form.getByRole("spinbutton", { name: "Minimum reportable count" }).fill(minimum);
  await form.getByRole("spinbutton", { name: "Minimum reportable count" }).press("Tab");
  await form.getByRole("button", { name: "Create group" }).click();
  await expect(page.getByRole("heading", { level: 2, name: /^Group [0-9a-f]{32}$/ })).toBeVisible({ timeout: 15_000 });
  return (await page.locator("#group-key").textContent()).trim();
}
