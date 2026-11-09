// Tests for the deployed-site check, against an assembled site served locally,
// plus adversarial copies with a broken asset, a missing start-up screen and an
// unapproved mark. Run after `npm run build:wasm` is not required: only the
// static pages and brand assets are fetched.
import { test } from "node:test";
import assert from "node:assert/strict";
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync, mkdirSync } from "node:fs";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { extname, join } from "node:path";
import { verify, pageProblems, identityProblems } from "./verify-deployed.mjs";
import { pages, publishPage } from "./assemble-site.mjs";

const repo = new URL("../../", import.meta.url).pathname;
const site = JSON.parse(readFileSync(join(repo, "deploy/pages/site.json"), "utf8"));

// The published pages and brand assets, as assemble-site lays them out.
const buildSite = () => {
  const out = mkdtempSync(join(tmpdir(), "signal-site-"));
  cpSync(join(repo, "web"), join(out, "web"), { recursive: true });
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
      response.writeHead(200, { "content-type": extname(file) === ".svg" ? "image/svg+xml" : "text/html" }).end(body);
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

test("an unapproved mark is reported", () => {
  assert.deepEqual(identityProblems({ status: "approved", approved: true, assets: { favicon: "a", markDark: "b" } }), []);
  assert.ok(identityProblems({ status: "provisional", approved: false, assets: { favicon: "a", markDark: "b" } }).length > 0);
  assert.ok(identityProblems({ status: "approved", approved: true, assets: { favicon: "a" } }).includes("identity.json: no dark mark variant"));
});

test("a non-200 page is reported without further checks", () => {
  assert.deepEqual(pageProblems("web/", { status: 500, body: "" }), ["web/: HTTP 500"]);
});
