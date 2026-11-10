# Signal visual baseline

Visual-review evidence (WI-0110), not functional test evidence: these images show what was on screen and prove nothing about correctness.

Source: kemiller2002/signal `311f90d1efe4a1b6931d52874656bcbfafe40203` (visual/echelon-app-layer), run https://github.com/kemiller2002/signal/actions/runs/42761757163.
Browser: 153.0.8010.12. 3 screens, 36 screenshots. Details: [manifest.json](manifest.json).

| Screen | Site | Sign-in | desktop 1440x900 | tablet 834x1112 | mobile 390x844 |
|---|---|---|---|---|---|
| `landing` Landing page (web/home/) — repository-root server (python http.server), as the browser suite serves it | checkout | no | [light](screens/landing/desktop-light-full.png) [dark](screens/landing/desktop-dark-full.png) | [light](screens/landing/tablet-light-full.png) [dark](screens/landing/tablet-dark-full.png) | [light](screens/landing/mobile-light-full.png) [dark](screens/landing/mobile-dark-full.png) |
| `landing-pages-site` Landing page (web/home/) — the assembled GitHub Pages site (tools/pages/assemble-site.mjs) under /signal/, with the demo banner and Content-Security-Policy | pages | no | [light](screens/landing-pages-site/desktop-light-full.png) [dark](screens/landing-pages-site/desktop-dark-full.png) | [light](screens/landing-pages-site/tablet-light-full.png) [dark](screens/landing-pages-site/tablet-dark-full.png) | [light](screens/landing-pages-site/mobile-light-full.png) [dark](screens/landing-pages-site/mobile-dark-full.png) |
| `pages-site-root` Pages site root (redirects to web/home/) | pages | no | [light](screens/pages-site-root/desktop-light-full.png) [dark](screens/pages-site-root/desktop-dark-full.png) | [light](screens/pages-site-root/tablet-light-full.png) [dark](screens/pages-site-root/tablet-dark-full.png) | [light](screens/pages-site-root/mobile-light-full.png) [dark](screens/pages-site-root/mobile-dark-full.png) |

## Not captured

Every planned screen was captured.
