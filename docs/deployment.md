# Deploying Signal

## Deployment: GitHub Pages

Signal is published to GitHub Pages by `.github/workflows/pages.yml` on
every push to `main` (and on demand, with *Run workflow*). Pull requests
build and verify the site but never deploy it.

- **Address:** <https://signal.echelonfoundry.com/web/> (the administrator
  page is at `web/admin/`). The older addresses,
  `https://kemiller2002.github.io/signal/` and `https://kevinmmiller.us/signal/`,
  redirect there. The site root sends the browser to the assessment page
  at `web/`.
- **What is published:** the repository's layout (`web/`, `web-kernel/`,
  `build/wasm/wwwroot/` from `npm run build:wasm` in Release, and the
  `dist`/`src` folders of the Limen, Forma and Folio packages), assembled by
  `tools/pages/assemble-site.mjs`. Every address in the page is relative,
  so the site works under `/signal/` or at a domain's root alike; no
  `<base href>` is set. The administrator page's routes live in the
  fragment (`web/admin/#/groups/…`, SIG-LINK-002), so the server only ever
  sees `web/admin/` and no `404.html` fallback is needed. The browser suite
  opens deep links cold against the assembled site served under a sub-path
  (Playwright project `pages`). `.nojekyll` is added. The `.br`/`.gz`
  copies are left out: Pages compresses on its own and serves `.wasm` as
  `application/wasm`.
- **Demo mode.** Signal has no sign-in and no storage yet. The page is the
  assessment as it runs locally: answers are held by the engine in the
  browser tab and are gone on reload. Nothing is sent anywhere, and there
  is no offline queue (it is off by requirement). The page shows the banner
  "Demo — data stays in this browser; GitHub sign-in not configured". The
  repository holds no secret for Pages, and needs none: the site is public.
- **Configuration is file-driven.** `deploy/pages/site.json` holds the
  banner and the Content-Security-Policy. When Signal gains a deployment
  configuration (sign-in through Fides, a data location), the site names it
  there as `"deployment": { "from": "deploy/pages/<file>", "as": "<name>" }`
  and the assembly publishes it beside the page.

**Turning on real sign-in later** changes `deploy/pages/` only: add the
deployment configuration as above (a public GitHub App client id and the
Fides exchange's origin; never a secret), add the exchange's origin and
`https://api.github.com` to the policy's `connect-src`, and remove or reword
`banner`. Push to `main`; the workflow republishes.

**Security on Pages.** Pages cannot set response headers, so the policy is
a `<meta http-equiv="Content-Security-Policy">` that the assembly inserts
first in `<head>` of every published page (`web/` and `web/admin/`,
`pages` in `tools/pages/assemble-site.mjs`, WI-0068): scripts, styles and connections from the site's own
origin only, `'wasm-unsafe-eval'` for the .NET WebAssembly runtime, no
inline script or style, `object-src 'none'`, `base-uri 'self'`.
Known deviation: `frame-ancestors` (and `X-Frame-Options`) cannot be
delivered by a meta element, so on Pages another site can frame the page.
With no session or stored data that is low risk; before real sign-in, host
where `frame-ancestors 'none'` can be sent as a header, or accept the risk
with a recorded decision.

**Enabling Pages.** The deploy job runs `actions/configure-pages` with
`enablement: true`. If Pages is not enabled and the workflow's token may not
enable it, the job fails saying so; a repository admin then opens
**Settings → Pages → Build and deployment → Source → GitHub Actions** once
and re-runs the workflow.
