// Tests for the deployed-site check, against an assembled site served locally,
// plus adversarial copies with a broken asset, a missing start-up screen, an
// unapproved mark, a page that is not branded, a missing or misordered Echelon
// theme and an Echelon Foundry logo in place of Signal's mark. Run after
// `npm ci`; `npm run build:wasm` is not required: only the static pages, their
// stylesheets and the brand assets are fetched.
import { test } from "node:test";
import assert from "node:assert/strict";
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync, mkdirSync } from "node:fs";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { extname, join } from "node:path";
import { verify, pageProblems, identityProblems, stylesheetProblems, themeProblems } from "./verify-deployed.mjs";
import { pages, publishPage } from "./assemble-site.mjs";

const repo = new URL("../../", import.meta.url).pathname;
const site = JSON.parse(readFileSync(join(repo, "deploy/pages/site.json"), "utf8"));
// Forma 0.x markup lives only in the guards' fixture file (WI-0109), so this
// file holds no Forma 0.x name and the namespace guard scans it too.
const zero = JSON.parse(readFileSync(join(repo, "tests/fixtures/forma-zero-names.json"), "utf8")).verifyDeployed;

// The published pages, their stylesheets (Forma and the Echelon theme from
// node_modules) and brand assets, as assemble-site lays them out.
const styles = ["node_modules/@echelon-foundry/design-system/dist", "node_modules/@echelon-foundry/echelon-theme/dist"];
const buildSite = () => {
  const out = mkdtempSync(join(tmpdir(), "signal-site-"));
  cpSync(join(repo, "web"), join(out, "web"), { recursive: true });
  styles.forEach((path) => cpSync(join(repo, path), join(out, path), { recursive: true }));
  pages.forEach(({ path, pagesCss }) =>
    writeFileSync(join(out, path), publishPage(readFileSync(join(repo, path), "utf8"), site, pagesCss)));
  return out;
};

const serve = (root) => new Promise((resolve) => {
  const server = createServer((request, response) => {
    const path = decodeURIComponent(new URL(request.url, "http://x").pathname);
    const file = join(root, path.endsWith("/") ? `${path}index.html` : path);
    try {
      const body = readFileSync(file);
      const type = { ".svg": "image/svg+xml", ".css": "text/css", ".json": "application/json" }[extname(file)] ?? "text/html";
      response.writeHead(200, { "content-type": type }).end(body);
    } catch {
      response.writeHead(404).end("not found");
    }
  }).listen(0, "127.0.0.1", () => resolve(server));
});

const withSite = async (mutate, check) => {
  const root = buildSite();
  mutate(root);
  const server = await serve(root);
  try {
    await check(`http://127.0.0.1:${server.address().port}/`);
  } finally {
    server.close();
    rmSync(root, { recursive: true, force: true });
  }
};

test("the published site passes", () =>
  withSite(() => {}, async (base) => assert.deepEqual(await verify(base), [])));

test("a missing favicon asset is reported", () =>
  withSite((root) => rmSync(join(root, "web/brand/signal-mark.svg")), async (base) => {
    const problems = await verify(base);
    assert.ok(problems.some((p) => /signal-mark\.svg: HTTP 404/.test(p)), problems.join("\n"));
  }));

test("a missing dark mark variant is reported", () =>
  withSite((root) => rmSync(join(root, "web/brand/signal-mark-dark.svg")), async (base) => {
    const problems = await verify(base);
    assert.ok(problems.some((p) => /signal-mark-dark\.svg: HTTP 404/.test(p)), problems.join("\n"));
  }));

test("an engine page without its start-up screen is reported", () =>
  withSite((root) => {
    const file = join(root, "web/survey/index.html");
    writeFileSync(file, readFileSync(file, "utf8").replace('id="app-startup"', 'id="removed"'));
  }, async (base) => {
    const problems = await verify(base);
    assert.ok(problems.some((p) => p.startsWith("web/survey/: no pre-runtime start-up screen")), problems.join("\n"));
  }));

test("an empty favicon and a missing landing page are reported", () =>
  withSite((root) => {
    const file = join(root, "web/index.html");
    writeFileSync(file, readFileSync(file, "utf8").replace(/<link rel="icon" href="[^"]+"/, '<link rel="icon" href="data:,"'));
    rmSync(join(root, "web/home"), { recursive: true });
  }, async (base) => {
    const problems = await verify(base);
    assert.ok(problems.includes("web/: no favicon (or the empty data: icon)"), problems.join("\n"));
    assert.ok(problems.includes("web/home/: HTTP 404"), problems.join("\n"));
  }));

const edit = (root, path, change) => {
  const file = join(root, path);
  writeFileSync(file, change(readFileSync(file, "utf8")));
};

test("an engine page still on Forma 0.x's start-up screen is reported", () =>
  withSite((root) => edit(root, "web/admin/index.html", (html) => html.replace('class="forma-startup"', zero.zeroStartupClass)), async (base) => {
    const problems = await verify(base);
    assert.ok(problems.includes("web/admin/: no pre-runtime start-up screen (#app-startup.forma-startup)"), problems.join("\n"));
  }));

test("a page that is not branded Echelon is reported", () =>
  withSite((root) => edit(root, "web/home/index.html", (html) => html.replace(' data-forma-brand="echelon"', "")), async (base) => {
    const problems = await verify(base);
    assert.ok(problems.includes('web/home/: <html> is not branded data-forma-brand="echelon"'), problems.join("\n"));
  }));

test("an Echelon theme that is not served is reported", () =>
  withSite((root) => rmSync(join(root, "node_modules/@echelon-foundry/echelon-theme"), { recursive: true }), async (base) => {
    const problems = await verify(base);
    assert.ok(problems.some((p) => /echelon-theme\/dist\/echelon\.css: HTTP 404/.test(p)), problems.join("\n"));
  }));

test("a stylesheet without the theme, or with it before Forma, is reported", () =>
  withSite((root) => {
    edit(root, "web/home/home.css", (css) => css.replace(/@import "[^"]*echelon-theme[^"]*";\n/, ""));
    edit(root, "web/admin/admin.css", (css) => {
      const theme = css.match(/@import "[^"]*echelon-theme[^"]*";\n/)[0];
      return theme + css.replace(theme, "");
    });
  }, async (base) => {
    const problems = await verify(base);
    assert.ok(problems.some((p) => /web\/home\/home\.css: does not import the Echelon theme/.test(p)), problems.join("\n"));
    assert.ok(problems.some((p) => /web\/admin\/admin\.css: imports the Echelon theme before Forma/.test(p)), problems.join("\n"));
  }));

test("an Echelon Foundry logo in place of Signal's identity is reported", () =>
  withSite((root) => edit(root, "web/home/index.html", (html) => html
    .replace(/<a class="forma-app-identity"[\s\S]*?<\/a>/, '<img src="../brand/echelon/logos/echelon-foundry/mark.svg" alt="Echelon Foundry" />')), async (base) => {
    const problems = await verify(base);
    assert.ok(problems.includes("web/home/: no Signal application identity"), problems.join("\n"));
    assert.ok(problems.includes("web/home/: uses an Echelon Foundry logo instead of Signal's mark"), problems.join("\n"));
  }));

test("the stylesheet and theme checks are pure", () => {
  const forma = '@import "../node_modules/@echelon-foundry/design-system/dist/all.css";';
  const theme = '@import "../node_modules/@echelon-foundry/echelon-theme/dist/echelon.css";';
  assert.deepEqual(stylesheetProblems("s.css", { status: 200, body: `${forma}\n${theme}` }), []);
  assert.deepEqual(stylesheetProblems("s.css", { status: 200, body: `${theme}\n${forma}` }), ["s.css: imports the Echelon theme before Forma"]);
  assert.deepEqual(stylesheetProblems("s.css", { status: 404, body: "" }), ["s.css: HTTP 404"]);
  assert.deepEqual(themeProblems("t.css", { status: 200, body: '[data-forma-brand="echelon"] { --forma-color-text-primary: #171a18; }' }), []);
  // A Forma 0.x theme (the old brand attribute and properties) is not the theme for Forma 1.x.
  assert.deepEqual(themeProblems("t.css", { status: 200, body: zero.zeroTheme }), ["t.css: not the Echelon theme for Forma 1.x"]);
});

test("an unapproved mark is reported", () => {
  assert.deepEqual(identityProblems({ status: "approved", approved: true, assets: { favicon: "a", markDark: "b" } }), []);
  assert.ok(identityProblems({ status: "provisional", approved: false, assets: { favicon: "a", markDark: "b" } }).length > 0);
  assert.ok(identityProblems({ status: "approved", approved: true, assets: { favicon: "a" } }).includes("identity.json: no dark mark variant"));
});

test("a non-200 page is reported without further checks", () => {
  assert.deepEqual(pageProblems("web/", { status: 500, body: "" }), ["web/: HTTP 500"]);
});
