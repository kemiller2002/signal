# Deploying Signal

## Deployment: GitHub Pages

Signal is published to GitHub Pages by `.github/workflows/pages.yml` on
every push to `main` (and on demand, with *Run workflow*). Pull requests
build and verify the site but never deploy it.

- **Address:** <https://signal.echelonfoundry.com/web/> (the administrator
  page is at `web/admin/`). The older addresses,
  `https://kemiller2002.github.io/signal/` and `https://kevinmmiller.us/signal/`,
  redirect there. The site root sends the browser to the landing page at
  `web/home/` (since #129); respondents' links to the assessment at `web/`
  are unchanged.
- **What is published:** the repository's layout (`web/`, `web-kernel/`,
  `build/wasm/wwwroot/` from `npm run build:wasm` in Release, and the
  `dist`/`src` folders of the Limen, Forma, Echelon theme and Folio
  packages), assembled by
  `tools/pages/assemble-site.mjs`. Every address in the page is relative,
  so the site works under `/signal/` or at a domain's root alike; no
  `<base href>` is set. The administrator page's routes live in the
  fragment (`web/admin/#/groups/…`, SIG-LINK-002), so the server only ever
  sees `web/admin/` and no `404.html` fallback is needed. The browser suite
  opens deep links cold against the assembled site served under a sub-path
  (Playwright project `pages`). `.nojekyll` is added. The `.br`/`.gz`
  copies are left out: Pages compresses on its own and serves `.wasm` as
  `application/wasm`.
- **The Echelon theme is served publicly.** Every page loads
  `node_modules/@echelon-foundry/echelon-theme/dist/echelon.css` (Forma 1.x
  is brand-neutral; the theme supplies Echelon colours and fonts,
  EDS-ADR-002), so the Pages site publishes that package's `dist/` folder:
  `echelon.css`, `echelon-marketing.css`, `asset-manifest.json` and
  `checksums.txt`. The package is `UNLICENSED`: it is the owner's own
  package (`kemiller2002/echelon-design-system`), published by the owner on
  the owner's site, and it grants no licence to anyone else. Its Echelon
  Foundry logos are not in `dist/` and are not published; Signal uses its
  own mark (`web/brand/`).
- **Sign-in (since WI-0148).** The administrator page signs in through
  Fides: the Echelon exchange at `https://auth.echelonfoundry.com`, the shared
  `Echelon (production)` GitHub App, application `echelon-signal-production`,
  redirect `https://signal.echelonfoundry.com/web/admin/`. Its data lives in
  `kemiller2002/echelon-organization-administration` (branch `main`, folder
  `signal`), and the first administrator is GitHub account `737074`
  (`deploy/pages/signal.deployment.production.json`). The assessment page is
  unchanged: respondents' answers stay in the browser tab. Only the owner
  signs in for now (DF-SIGNAL-2026-0027).
- **Configuration is file-driven.** `deploy/pages/site.json` holds the
  banner and the Content-Security-Policy. When Signal gains a deployment
  configuration (sign-in through Fides, a data location), the site names it
  there as `"deployment": { "from": "deploy/pages/<file>", "as": "<name>" }`
  and the assembly publishes it beside the page.

**Turning on real sign-in** changes `deploy/pages/` only: add the
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
Real sign-in is on, and the owner accepted that risk while they are the only
person who signs in (DF-SIGNAL-2026-0027); before anyone else signs in, host
where `frame-ancestors 'none'` can be sent as a header.

**Enabling Pages.** The deploy job runs `actions/configure-pages` with
`enablement: true`. If Pages is not enabled and the workflow's token may not
enable it, the job fails saying so; a repository admin then opens
**Settings → Pages → Build and deployment → Source → GitHub Actions** once
and re-runs the workflow.
