// node --test tools/pages/ : the page transforms of the Pages assembly (WI-0065, WI-0068).
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { pages, publishPage, rootPage } from "./assemble-site.mjs";

const site = JSON.parse(readFileSync(new URL("../../deploy/pages/site.json", import.meta.url), "utf8"));
const page = readFileSync(new URL("../../web/index.html", import.meta.url), "utf8");

test("the policy comes first in <head>, before anything it governs", () => {
  const published = publishPage(page, site);
  const policy = published.indexOf('http-equiv="Content-Security-Policy"');
  assert.ok(policy > 0);
  assert.ok(policy < published.indexOf("<link"));
  assert.ok(policy < published.indexOf("<script"));
});

test("the policy allows WebAssembly and no inline script", () => {
  assert.match(site.contentSecurityPolicy, /script-src 'self' 'wasm-unsafe-eval'(;|$)/);
  assert.doesNotMatch(site.contentSecurityPolicy, /'unsafe-inline'|'unsafe-eval'/);
});

test("the published page has no inline script", () => {
  const scripts = [...publishPage(page, site).matchAll(/<script\b[^>]*>/g)].map((m) => m[0]);
  assert.ok(scripts.length > 0);
  scripts.forEach((tag) => assert.match(tag, /\bsrc=/));
});

test("the banner is the first thing in <body>, with its stylesheet", () => {
  const published = publishPage(page, site);
  assert.match(published, /<body[^>]*>\n<p class="pages-demo-banner" role="note" data-pages-banner>Demo/);
  assert.match(published, /<link rel="stylesheet" href="\.\/pages\.css" \/>/);
});

test("no banner is added when the site names none", () => {
  const published = publishPage(page, { ...site, banner: undefined });
  assert.doesNotMatch(published, /pages-demo-banner|pages\.css/);
});

test("a page that already carries a policy keeps its own", () => {
  const own = page.replace(/<meta charset="utf-8" \/>/, '$&\n<meta http-equiv="Content-Security-Policy" content="default-src \'none\'" />');
  const published = publishPage(own, site);
  assert.equal([...published.matchAll(/http-equiv="Content-Security-Policy"/g)].length, 1);
  assert.match(published, /default-src 'none'/);
});

test("the root page sends the browser to web/ by a relative address, without script", () => {
  const root = rootPage(site);
  assert.match(root, /<meta http-equiv="refresh" content="0; url=web\/" \/>/);
  assert.doesNotMatch(root, /<script/);
});

test("the Pages site configures no sign-in or data location", () => {
  assert.equal(site.deployment, undefined);
});

// WI-0068: the administrator page was published without the policy.
test("every HTML page in web/ is published, and each gets the policy and the banner", async () => {
  const { readdirSync, statSync } = await import("node:fs");
  const { join, relative } = await import("node:path");
  const root = new URL("../../", import.meta.url).pathname;
  const html = (dir) => readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? html(path) : name.endsWith(".html") ? [relative(root, path)] : [];
  });
  assert.deepEqual(html(join(root, "web")).sort(), pages.map((p) => p.path).sort());

  pages.forEach(({ path, pagesCss }) => {
    const published = publishPage(readFileSync(new URL(`../../${path}`, import.meta.url), "utf8"), site, pagesCss);
    const policy = published.indexOf('http-equiv="Content-Security-Policy"');
    assert.ok(policy > 0 && policy < published.indexOf("<link") && policy < published.indexOf("<script"), path);
    assert.match(published, /data-pages-banner/, path);
    assert.ok(published.includes(`<link rel="stylesheet" href="${pagesCss}" />`), path);
  });
});
