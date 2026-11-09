// Checks the deployed Signal site, not the source checkout (EDS-ADR-002
// requirement 5, EDS-BRAND-APP-001, SIG-FORMA1-001): every published entry
// page answers 200, carries Signal's own favicon, is branded Echelon
// (data-forma-brand="echelon" on <html>) and loads the Echelon theme's CSS
// after Forma's; every engine page carries its pre-runtime start-up screen
// (Forma 1.x .forma-startup); the landing page carries Signal's own
// identity, never the Echelon Foundry logos; and every brand asset the pages
// and web/brand/identity.json reference resolves at the deployed URL.
//
//   node tools/pages/verify-deployed.mjs https://signal.example.org/
//
// Exit 0 when every check passes, 1 otherwise. Pure checks over fetched
// responses; only `main` touches the network.

export const ENGINE_PAGES = ["web/", "web/survey/", "web/admin/"];
export const LANDING = "web/home/";
export const IDENTITY = "web/brand/identity.json";

// The Forma 1.x bundle and the Echelon theme, as each page's stylesheet
// imports them from the published node_modules.
export const FORMA_CSS = /node_modules\/@echelon-foundry\/design-system\/dist\/all\.css$/;
export const THEME_CSS = /node_modules\/@echelon-foundry\/echelon-theme\/dist\/echelon\.css$/;

const iconHref = (html) => html.match(/<link rel="icon" href="([^"]+)"/)?.[1] ?? null;

// The page's own stylesheet: the first one it links (the Pages banner's
// stylesheet is inserted after it).
export const stylesheetHref = (html) => html.match(/<link rel="stylesheet" href="([^"]+)"/)?.[1] ?? null;

const htmlTag = (html) => html.match(/<html\b[^>]*>/i)?.[0] ?? "";

const hasStartup = (html) =>
  /class="forma-startup"[^>]*id="app-startup"|id="app-startup"[^>]*class="forma-startup"/.test(html);

export const pageProblems = (path, response) => [
  ...(response.status === 200 ? [] : [`${path}: HTTP ${response.status}`]),
  ...(response.status !== 200 ? [] : [
    ...(iconHref(response.body) && iconHref(response.body) !== "data:," ? [] : [`${path}: no favicon (or the empty data: icon)`]),
    ...(/\sdata-forma-brand="echelon"/.test(htmlTag(response.body)) ? [] : [`${path}: <html> is not branded data-forma-brand="echelon"`]),
    ...(stylesheetHref(response.body) ? [] : [`${path}: no stylesheet`]),
    ...(ENGINE_PAGES.includes(path) && !hasStartup(response.body)
      ? [`${path}: no pre-runtime start-up screen (#app-startup.forma-startup)`] : []),
    ...(path === LANDING && !/class="forma-app-identity"[\s\S]*?class="forma-app-identity__name">Signal</.test(response.body)
      ? [`${path}: no Signal application identity`] : []),
    // The theme brings Echelon colours and fonts only; Signal keeps its own mark.
    ...(/logos\/echelon-foundry\//.test(response.body) ? [`${path}: uses an Echelon Foundry logo instead of Signal's mark`] : [])
  ])
];

const importsOf = (css) => [...css.matchAll(/@import\s+(?:url\()?\s*"([^"]+)"/g)].map((m) => m[1]);

// A page's stylesheet must import Forma, then the Echelon theme after it.
export const stylesheetProblems = (url, response) => {
  if (response.status !== 200) return [`${url}: HTTP ${response.status}`];
  const imports = importsOf(response.body);
  const forma = imports.findIndex((href) => FORMA_CSS.test(href));
  const theme = imports.findIndex((href) => THEME_CSS.test(href));
  return [
    ...(forma >= 0 ? [] : [`${url}: does not import Forma's all.css`]),
    ...(theme >= 0 ? [] : [`${url}: does not import the Echelon theme's echelon.css`]),
    ...(forma >= 0 && theme >= 0 && theme < forma ? [`${url}: imports the Echelon theme before Forma`] : [])
  ];
};

export const themeImportUrl = (url, css) => {
  const href = importsOf(css).find((candidate) => THEME_CSS.test(candidate));
  return href ? new URL(href, url).href : null;
};

// The served theme is the Echelon brand for Forma 1.x, not an empty or stale file.
export const themeProblems = (url, response) =>
  response.status !== 200 ? [`${url}: HTTP ${response.status}`]
  : /\[data-forma-brand="echelon"\]/.test(response.body) && /--forma-color-/.test(response.body) ? []
  : [`${url}: not the Echelon theme for Forma 1.x`];

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

const unique = (values) => [...new Set(values)];

export const verify = async (base, fetcher = fetchText) => {
  const root = base.endsWith("/") ? base : `${base}/`;
  const pages = await Promise.all([...ENGINE_PAGES, LANDING].map(async (path) => {
    const response = await fetcher(new URL(path, root).href);
    return { path, url: new URL(path, root).href, response };
  }));
  const served = pages.filter(({ response }) => response.status === 200);
  const pageIssues = pages.flatMap(({ path, response }) => pageProblems(path, response));

  const stylesheetUrls = unique(served
    .filter(({ response }) => stylesheetHref(response.body))
    .map(({ url, response }) => new URL(stylesheetHref(response.body), url).href));
  const stylesheets = await Promise.all(stylesheetUrls.map(async (url) => ({ url, response: await fetcher(url) })));
  const stylesheetIssues = stylesheets.flatMap(({ url, response }) => stylesheetProblems(url, response));
  const themeUrls = unique(stylesheets
    .filter(({ response }) => response.status === 200)
    .map(({ url, response }) => themeImportUrl(url, response.body))
    .filter((url) => url !== null));
  const themeIssues = (await Promise.all(themeUrls.map(async (url) => themeProblems(url, await fetcher(url))))).flat();

  const favicons = served
    .filter(({ response }) => iconHref(response.body))
    .map(({ url, response }) => new URL(iconHref(response.body), url).href);
  const identityResponse = await fetcher(new URL(IDENTITY, root).href);
  const identity = identityResponse.status === 200 ? (() => { try { return JSON.parse(identityResponse.body); } catch { return null; } })() : null;
  const assetUrls = unique([...favicons, ...identityAssetPaths(identity).map((path) => new URL(path, root).href)]);
  const assetIssues = (await Promise.all(assetUrls.map(async (url) => assetProblems(url, await fetcher(url), "svg")))).flat();
  return [
    ...pageIssues,
    ...stylesheetIssues,
    ...themeIssues,
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
