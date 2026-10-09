// Assembles the static site GitHub Pages serves (WI-0065), from a checkout in
// which `npm ci` and `npm run build:wasm` have run.
//
//   node tools/pages/assemble-site.mjs [output directory, default dist-pages]
//
// The page loads everything by relative address (../web-kernel,
// ../node_modules, ../build/wasm), so the site keeps the repository's layout
// and works at any base path: a custom domain's root or /signal/ alike. No
// <base href> is needed, and none is added (it would also redirect the
// page's #fragment links). The site root sends the browser to web/.
//
// What the Pages deployment is, is file-driven: deploy/pages/site.json names
// the Content-Security-Policy the page carries, the banner it shows and,
// once Signal has one, the deployment configuration published beside the
// page. Changing the deployment means changing those files only.
//
// Pure functions build the plan; the IO at the bottom carries it out.
import { cpSync, mkdirSync, readFileSync, rmSync, writeFileSync, readdirSync, statSync } from "node:fs";
import { join, dirname } from "node:path";

const escapeHtml = (text) =>
  text.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);

// The directories the page reaches, copied at the same relative paths.
export const copies = [
  "web",
  "web-kernel",
  // The published survey versions the survey page reads (DF-SIGNAL-2026-0005):
  // committed by the operator from the console's "For the respondent site".
  "published-templates",
  "build/wasm/wwwroot",
  "node_modules/@echelon-foundry/limen/dist",
  "node_modules/@echelon-foundry/design-system/dist",
  "node_modules/@echelon-foundry/print-components/src"
];

// Every page the site publishes, each with the relative address of the
// banner's stylesheet. Every one carries the policy and the banner (WI-0068).
export const pages = [
  { path: "web/index.html", pagesCss: "./pages.css" },
  { path: "web/admin/index.html", pagesCss: "../pages.css" },
  { path: "web/survey/index.html", pagesCss: "../pages.css" }
];

const cspMeta = (policy) => `<meta http-equiv="Content-Security-Policy" content="${escapeHtml(policy)}" />`;

const bannerHtml = (text) => `<p class="pages-demo-banner" role="note" data-pages-banner>${escapeHtml(text)}</p>`;

const insertAfter = (html, pattern, addition) => {
  const match = html.match(pattern);
  if (match === null) throw new Error(`assemble-site: the page has no ${pattern}`);
  const at = match.index + match[0].length;
  return html.slice(0, at) + "\n" + addition + html.slice(at);
};

// The published page: the policy first in <head> (a meta policy covers only
// what follows it), the banner's stylesheet after the page's own first
// stylesheet, the banner first in <body>. A page that already carries a
// policy keeps it.
export const publishPage = (html, site, pagesCss = "./pages.css") => {
  const withPolicy = /http-equiv="Content-Security-Policy"/i.test(html)
    ? html
    : insertAfter(html, /<meta charset="[^"]*"\s*\/?>/i, cspMeta(site.contentSecurityPolicy));
  return site.banner
    ? insertAfter(
        insertAfter(withPolicy, /<link rel="stylesheet" href="[^"]+"\s*\/?>/i, `<link rel="stylesheet" href="${pagesCss}" />`),
        /<body[^>]*>/i,
        bannerHtml(site.banner)
      )
    : withPolicy;
};

export const rootPage = (site) => `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
${cspMeta(site.contentSecurityPolicy)}
<meta http-equiv="refresh" content="0; url=web/" />
<link rel="icon" href="data:," />
<title>${escapeHtml(site.title)}</title>
</head>
<body>
<p><a href="web/">Open ${escapeHtml(site.title)}</a></p>
</body>
</html>
`;

// Precompressed copies are for servers that negotiate them; Pages compresses
// on its own and would only publish them as dead weight.
const isPrecompressed = (path) => /\.(br|gz)$/.test(path);

const files = (dir) =>
  readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? files(path) : [path];
  });

const main = (root, out) => {
  const site = JSON.parse(readFileSync(join(root, "deploy/pages/site.json"), "utf8"));
  rmSync(out, { recursive: true, force: true });
  copies.forEach((relative) => {
    const from = join(root, relative);
    statSync(from); // fails loudly when the build or `npm ci` has not run
    mkdirSync(dirname(join(out, relative)), { recursive: true });
    cpSync(from, join(out, relative), { recursive: true, filter: (path) => !isPrecompressed(path) });
  });
  pages.forEach(({ path, pagesCss }) => {
    const page = join(out, path);
    writeFileSync(page, publishPage(readFileSync(page, "utf8"), site, pagesCss));
  });
  if (site.deployment) cpSync(join(root, site.deployment.from), join(out, "web", site.deployment.as));
  cpSync(join(root, "deploy/pages/pages.css"), join(out, "web/pages.css"));
  writeFileSync(join(out, "index.html"), rootPage(site));
  writeFileSync(join(out, ".nojekyll"), "");
  const published = files(out);
  console.log(`assemble-site: ${published.length} files in ${out}`);
};

if (import.meta.url === `file://${process.argv[1]}`) {
  main(process.cwd(), process.argv[2] ?? "dist-pages");
}
