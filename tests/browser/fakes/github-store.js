// TEST-ONLY. A deterministic, in-memory stand-in for the GitHub REST API that
// Arca's GitHub provider (EchelonFoundry.Arca.GitHub 0.5.0) uses as the
// administrator's data store, installed at the network boundary with
// Playwright route interception. Nothing here ships: the page, the WASM
// engine, Fides' client and Arca's provider run unchanged and talk to
// https://api.github.com as in production; only the responses are local.
//
// It implements exactly the requests Arca 0.5.0 sends (src/Arca.GitHub,
// commit 5dd00d9): GET /user, the repository, the branch and its rules, the
// branch ref, contents at a commit (file or folder), a commit's tree, and
// the Git data writes (POST trees and commits, a fast-forward-only PATCH of
// the ref), plus commit history and compare for reconciliation. Blob SHAs
// are real Git blob SHA-1s, because Arca compares them with the revisions it
// computes itself; tree and commit SHAs are deterministic SHA-1s of their
// content. State is per instance, so every test starts from an empty store.
//
// Failure injection: `failNext(method, pathPattern, response)` answers the
// next matching request with the given response (default 503), or aborts it
// when response is "abort", so a test can prove a failure is reported
// honestly and that the next attempt recovers.
import { createHash } from "node:crypto";

const sha1 = (data) => createHash("sha1").update(data).digest("hex");
const blobSha = (text) => {
  const bytes = Buffer.from(text, "utf8");
  return sha1(Buffer.concat([Buffer.from(`blob ${bytes.length}\0`), bytes]));
};

export class FakeGitHubStore {
  constructor({ owner = "acme", repository = "signal-data", branch = "main", login = "octocat", userId = 583231, origin }) {
    this.owner = owner;
    this.repository = repository;
    this.branch = branch;
    this.user = { id: userId, login, type: "User" };
    this.origin = origin;
    this.blobs = new Map(); // sha -> text
    this.trees = new Map(); // sha -> Map(name -> { type: "blob" | "tree", sha })
    this.commits = new Map(); // sha -> { tree, parents, message }
    this.failures = [];
    this.requests = [];
    const emptyTree = this.#storeTree(new Map());
    this.head = this.#storeCommit({ tree: emptyTree, parents: [], message: "Initial commit" });
  }

  get repoPath() {
    return `/repos/${this.owner}/${this.repository}`;
  }

  // The number of commits on the branch, the root included.
  get commitCount() {
    let count = 0;
    for (let sha = this.head; sha; sha = this.commits.get(sha).parents[0]) count++;
    return count;
  }

  // Every file path at the branch head, as "path/to/file".
  files(treeSha = this.commits.get(this.head).tree, prefix = "") {
    const out = [];
    for (const [name, entry] of this.trees.get(treeSha)) {
      const path = prefix ? `${prefix}/${name}` : name;
      if (entry.type === "tree") out.push(...this.files(entry.sha, path));
      else out.push(path);
    }
    return out.sort();
  }

  // The text of a file at the branch head, or undefined.
  read(path) {
    const entry = this.#lookup(this.commits.get(this.head).tree, path.split("/"));
    return entry?.type === "blob" ? this.blobs.get(entry.sha) : undefined;
  }

  failNext(method, pattern, response = { status: 503, body: { message: "Service Unavailable" } }) {
    this.failures.push({ method, pattern, response });
  }

  async install(page) {
    await page.route("https://api.github.com/**", (route) => this.#handle(route));
  }

  // ---------------------------------------------------------------------------

  #storeTree(entries) {
    const canonical = JSON.stringify([...entries].sort(([a], [b]) => (a < b ? -1 : 1)));
    const sha = sha1(`tree ${canonical}`);
    this.trees.set(sha, entries);
    return sha;
  }

  #storeCommit(commit) {
    const sha = sha1(`commit ${JSON.stringify(commit)} ${this.commits.size}`);
    this.commits.set(sha, commit);
    return sha;
  }

  #lookup(treeSha, segments) {
    const tree = this.trees.get(treeSha);
    if (!tree) return undefined;
    const [first, ...rest] = segments.filter((s) => s !== "");
    if (first === undefined) return { type: "tree", sha: treeSha };
    const entry = tree.get(first);
    if (!entry || rest.length === 0) return entry;
    return entry.type === "tree" ? this.#lookup(entry.sha, rest) : undefined;
  }

  // A new tree: the base tree with each path set to content, or removed.
  #applyTree(baseSha, changes) {
    const write = (treeSha, segments, change) => {
      const entries = new Map(treeSha ? this.trees.get(treeSha) : []);
      const [name, ...rest] = segments;
      if (rest.length === 0) {
        if (change.content === null) entries.delete(name);
        else {
          const sha = blobSha(change.content);
          this.blobs.set(sha, change.content);
          entries.set(name, { type: "blob", sha });
        }
      } else {
        const child = entries.get(name);
        const next = write(child?.type === "tree" ? child.sha : null, rest, change);
        if (this.trees.get(next).size === 0) entries.delete(name);
        else entries.set(name, { type: "tree", sha: next });
      }
      return this.#storeTree(entries);
    };
    return changes.reduce((tree, change) => write(tree, change.path.split("/"), change), baseSha);
  }

  #ancestors(sha) {
    const seen = [];
    for (let at = sha; at; at = this.commits.get(at)?.parents[0]) seen.push(at);
    return seen;
  }

  #headers(request) {
    return {
      "access-control-allow-origin": this.origin,
      "access-control-allow-methods": "GET, POST, PATCH, PUT, DELETE, OPTIONS",
      "access-control-allow-headers": request.headers()["access-control-request-headers"] ?? "authorization, accept, content-type, x-github-api-version, if-none-match",
      "access-control-expose-headers": "etag, retry-after, x-ratelimit-limit, x-ratelimit-remaining, x-ratelimit-reset, x-ratelimit-resource",
      "x-ratelimit-limit": "5000",
      "x-ratelimit-remaining": "4999",
      "x-ratelimit-reset": "4102444800",
      "x-ratelimit-resource": "core"
    };
  }

  async #handle(route) {
    const request = route.request();
    const method = request.method();
    const headers = this.#headers(request);
    if (method === "OPTIONS") return route.fulfill({ status: 204, headers });

    const url = new URL(request.url());
    const path = decodeURIComponent(url.pathname);
    this.requests.push(`${method} ${path}${url.search}`);

    const failure = this.failures.findIndex((f) => f.method === method && f.pattern.test(path));
    if (failure >= 0) {
      const [{ response }] = this.failures.splice(failure, 1);
      if (response === "abort") return route.abort("connectionrefused");
      return route.fulfill({ status: response.status, headers, contentType: "application/json", body: JSON.stringify(response.body ?? {}) });
    }

    const reply = (status, body) => route.fulfill({ status, headers, contentType: "application/json", body: JSON.stringify(body) });
    if (!/^Bearer \S+/.test(request.headers()["authorization"] ?? "")) return reply(401, { message: "Requires authentication" });

    const repo = this.repoPath;
    const json = () => JSON.parse(request.postData() ?? "{}");

    if (method === "GET" && path === "/user") return reply(200, this.user);
    if (method === "GET" && path === repo) {
      return reply(200, { id: 7001, name: this.repository, owner: { login: this.owner }, visibility: "private", private: true, archived: false,
        permissions: { pull: true, push: true, admin: false } });
    }
    if (method === "GET" && path === `${repo}/branches/${this.branch}`) return reply(200, { name: this.branch, commit: { sha: this.head } });
    if (method === "GET" && path === `${repo}/rules/branches/${this.branch}`) return reply(200, []);
    if (method === "GET" && path === `${repo}/git/ref/heads/${this.branch}`) {
      return reply(200, { ref: `refs/heads/${this.branch}`, object: { type: "commit", sha: this.head } });
    }
    if (method === "GET" && path.startsWith(`${repo}/contents/`)) {
      const at = url.searchParams.get("ref") ?? this.branch;
      const commit = this.commits.get(at === this.branch ? this.head : at);
      if (!commit) return reply(404, { message: "No commit found for the ref" });
      const filePath = path.slice(`${repo}/contents/`.length);
      const entry = this.#lookup(commit.tree, filePath.split("/"));
      if (!entry) return reply(404, { message: "Not Found" });
      if (entry.type === "blob") {
        const text = this.blobs.get(entry.sha);
        return reply(200, { type: "file", name: filePath.split("/").pop(), path: filePath, sha: entry.sha,
          size: Buffer.byteLength(text, "utf8"), encoding: "base64", content: Buffer.from(text, "utf8").toString("base64") });
      }
      return reply(200, [...this.trees.get(entry.sha)].map(([name, child]) => ({
        name, path: filePath ? `${filePath}/${name}` : name, sha: child.sha,
        size: child.type === "blob" ? Buffer.byteLength(this.blobs.get(child.sha), "utf8") : 0,
        type: child.type === "blob" ? "file" : "dir" })));
    }
    const commitMatch = path.match(new RegExp(`^${repo}/git/commits/([0-9a-f]{40})$`));
    if (method === "GET" && commitMatch) {
      const commit = this.commits.get(commitMatch[1]);
      return commit ? reply(200, { sha: commitMatch[1], tree: { sha: commit.tree }, parents: commit.parents.map((sha) => ({ sha })), message: commit.message })
        : reply(404, { message: "Not Found" });
    }
    if (method === "POST" && path === `${repo}/git/trees`) {
      const body = json();
      if (!this.trees.has(body.base_tree)) return reply(422, { message: "Invalid tree info" });
      const changes = body.tree.map((entry) => ({ path: entry.path, content: entry.content ?? null }));
      return reply(201, { sha: this.#applyTree(body.base_tree, changes) });
    }
    if (method === "POST" && path === `${repo}/git/commits`) {
      const body = json();
      if (!this.trees.has(body.tree) || body.parents.some((p) => !this.commits.has(p))) return reply(422, { message: "Invalid commit" });
      return reply(201, { sha: this.#storeCommit({ tree: body.tree, parents: body.parents, message: body.message }) });
    }
    if (method === "PATCH" && path === `${repo}/git/refs/heads/${this.branch}`) {
      const body = json();
      const commit = this.commits.get(body.sha);
      if (!commit) return reply(422, { message: "Object does not exist" });
      if (!this.#ancestors(body.sha).includes(this.head)) return reply(422, { message: "Update is not a fast forward" });
      this.head = body.sha;
      return reply(200, { ref: `refs/heads/${this.branch}`, object: { type: "commit", sha: this.head } });
    }
    if (method === "GET" && path === `${repo}/commits`) {
      const from = url.searchParams.get("sha");
      const start = from === this.branch || !from ? this.head : from;
      const only = url.searchParams.get("path");
      const list = this.#ancestors(start).filter((sha) => {
        if (!only) return true;
        const commit = this.commits.get(sha);
        const parent = commit.parents[0] ? this.commits.get(commit.parents[0]) : null;
        const here = this.#lookup(commit.tree, only.split("/"))?.sha;
        const before = parent ? this.#lookup(parent.tree, only.split("/"))?.sha : undefined;
        return here !== before;
      }).slice(0, 100).map((sha) => ({ sha, commit: { message: this.commits.get(sha).message } }));
      return reply(200, list);
    }
    const compare = path.match(new RegExp(`^${repo}/compare/([0-9a-f]{40})\\.\\.\\.([0-9a-f]{40})$`));
    if (method === "GET" && compare) {
      const [, base, tip] = compare;
      if (!this.commits.has(base) || !this.commits.has(tip)) return reply(404, { message: "Not Found" });
      const status = base === tip ? "identical" : this.#ancestors(tip).includes(base) ? "ahead" : this.#ancestors(base).includes(tip) ? "behind" : "diverged";
      return reply(200, { status });
    }
    return reply(404, { message: `The fake GitHub store does not implement ${method} ${path}` });
  }
}
