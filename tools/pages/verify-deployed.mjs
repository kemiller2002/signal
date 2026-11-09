// Checks the deployed Signal site, not the source checkout (EDS-ADR-002
// requirement 5, EDS-BRAND-APP-001): every published entry page answers 200,
// carries Signal's own favicon and its pre-runtime start-up screen, the
// landing page carries the application identity, and every brand asset the
// pages and web/brand/identity.json reference resolves at the deployed URL.
//
//   node tools/pages/verify-deployed.mjs https://signal.example.org/
//
// Exit 0 when every check passes, 1 otherwise. Pure checks over fetched
// responses; only `main` touches the network.

export const ENGINE_PAGES = ["web/", "web/survey/", "web/admin/"];
export const LANDING = "web/home/";
export const IDENTITY = "web/brand/identity.json";

const iconHref = (html) => html.match(/<link rel="icon" href="([^"]+)"/)?.[1] ?? null;

export const pageProblems = (path, response) => [
  ...(response.status === 200 ? [] : [`${path}: HTTP ${response.status}`]),
  ...(response.status !== 200 ? [] : [
    ...(iconHref(response.body) && iconHref(response.body) !== "data:," ? [] : [`${path}: no favicon (or the empty data: icon)`]),
    ...(ENGINE_PAGES.includes(path) && !/class="ef-startup"[^>]*id="app-startup"|id="app-startup"[^>]*class="ef-startup"/.test(response.body)
      ? [`${path}: no pre-runtime start-up screen (#app-startup.ef-startup)`] : []),
    ...(path === LANDING && !/class="ef-app-identity"/.test(response.body) ? [`${path}: no application identity`] : [])
  ])
];

export const assetProblems = (url, response, kind) =>
  response.status !== 200 ? [`${url}: HTTP ${response.status}`]
  : kind === "svg" && !/<svg[\s>]/.test(response.body) ? [`${url}: not an SVG`]
  : [];

export const identityProblems = (identity) => [
  ...(identity && typeof identity === "object" ? [] : ["identity.json: not an object"]),
  ...(identity?.approved === true && identity?.status === "approved" ? [] : ["identity.json: the mark is not recorded as approved"]),
  ...(identity?.assets?.favicon ? [] : ["identity.json: no favicon asset"]),
  ...(identity?.assets?.markDark ? [] : ["identity.json: no dark mark variant"])
];

// Asset paths in identity.json are repository-relative ("web/brand/x.svg"),
// which is also their path under the published site root.
export const identityAssetPaths = (identity) =>
  [identity?.assets?.favicon, identity?.assets?.markDark].filter((path) => typeof path === "string");

const fetchText = async (url) => {
  const response = await fetch(url, { redirect: "follow" });
  return { status: response.status, body: await response.text() };
};

export const verify = async (base, fetcher = fetchText) => {
  const root = base.endsWith("/") ? base : `${base}/`;
  const pages = await Promise.all([...ENGINE_PAGES, LANDING].map(async (path) => {
    const response = await fetcher(new URL(path, root).href);
    return { path, response };
  }));
  const pageIssues = pages.flatMap(({ path, response }) => pageProblems(path, response));
  const favicons = pages
    .filter(({ response }) => response.status === 200 && iconHref(response.body))
    .map(({ path, response }) => new URL(iconHref(response.body), new URL(path, root)).href);
  const identityResponse = await fetcher(new URL(IDENTITY, root).href);
  const identity = identityResponse.status === 200 ? (() => { try { return JSON.parse(identityResponse.body); } catch { return null; } })() : null;
  const assetUrls = [...new Set([...favicons, ...identityAssetPaths(identity).map((path) => new URL(path, root).href)])];
  const assetIssues = (await Promise.all(assetUrls.map(async (url) => assetProblems(url, await fetcher(url), "svg")))).flat();
  return [
    ...pageIssues,
    ...(identityResponse.status === 200 ? identityProblems(identity) : [`${IDENTITY}: HTTP ${identityResponse.status}`]),
    ...assetIssues
  ];
};

if (import.meta.url === `file://${process.argv[1]}`) {
  const base = process.argv[2];
  if (!base) { console.error("usage: verify-deployed.mjs BASE_URL"); process.exit(2); }
  const problems = await verify(base);
  problems.forEach((problem) => console.error(`::error::${problem}`));
  console.log(problems.length === 0 ? `verify-deployed: ${base} passes` : `verify-deployed: ${problems.length} problem(s)`);
  process.exit(problems.length === 0 ? 0 : 1);
}
