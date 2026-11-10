# Signal visual baseline

Visual-review evidence (WI-0110), not functional test evidence: these images show what was on screen and prove nothing about correctness.

Source: kemiller2002/signal `a9a09fd00ec98f1e11576ed6e20b5928d9b7dcff` (visual/echelon-admin-shell), run https://github.com/kemiller2002/signal/actions/runs/42786285961.
Browser: 153.0.8010.12. 5 screens, 30 screenshots. Details: [manifest.json](manifest.json).

| Screen | Site | Sign-in | desktop 1440x900 | tablet 834x1112 | mobile 390x844 |
|---|---|---|---|---|---|
| `admin-loading-configuration` Console while its deployment configuration loads (fetch held) | checkout | no | [light](screens/admin-loading-configuration/desktop-light-full.png) [dark](screens/admin-loading-configuration/desktop-dark-full.png) | [light](screens/admin-loading-configuration/tablet-light-full.png) [dark](screens/admin-loading-configuration/tablet-dark-full.png) | [light](screens/admin-loading-configuration/mobile-light-full.png) [dark](screens/admin-loading-configuration/mobile-dark-full.png) |
| `admin-unconfigured` Console with no data store configured (the demo deployment; empty state) | checkout | no | [light](screens/admin-unconfigured/desktop-light-full.png) [dark](screens/admin-unconfigured/desktop-dark-full.png) | [light](screens/admin-unconfigured/tablet-light-full.png) [dark](screens/admin-unconfigured/tablet-dark-full.png) | [light](screens/admin-unconfigured/mobile-light-full.png) [dark](screens/admin-unconfigured/mobile-dark-full.png) |
| `admin-misconfigured` Console with a deployment configuration it cannot use (error) | checkout | no | [light](screens/admin-misconfigured/desktop-light-full.png) [dark](screens/admin-misconfigured/desktop-dark-full.png) | [light](screens/admin-misconfigured/tablet-light-full.png) [dark](screens/admin-misconfigured/tablet-dark-full.png) | [light](screens/admin-misconfigured/mobile-light-full.png) [dark](screens/admin-misconfigured/mobile-dark-full.png) |
| `admin-sign-in` Console sign-in screen (a configured deployment, signed out) | checkout | no | [light](screens/admin-sign-in/desktop-light-full.png) [dark](screens/admin-sign-in/desktop-dark-full.png) | [light](screens/admin-sign-in/tablet-light-full.png) [dark](screens/admin-sign-in/tablet-dark-full.png) | [light](screens/admin-sign-in/mobile-light-full.png) [dark](screens/admin-sign-in/mobile-dark-full.png) |
| `admin-pages-site-demo` Console on the assembled Pages site (the published demo: no store, no sign-in) | pages | no | [light](screens/admin-pages-site-demo/desktop-light-full.png) [dark](screens/admin-pages-site-demo/desktop-dark-full.png) | [light](screens/admin-pages-site-demo/tablet-light-full.png) [dark](screens/admin-pages-site-demo/tablet-dark-full.png) | [light](screens/admin-pages-site-demo/mobile-light-full.png) [dark](screens/admin-pages-site-demo/mobile-dark-full.png) |

## Accessibility audit (tests/visual/audit.mjs)

Measured on each screen as captured: WCAG AA text contrast (light, dark; text below AA / text measured), keyboard focus stops with a visible indicator, horizontal overflow with 200% text at 320 CSS px, and running shimmer animations under reduced motion. A forced-colors screenshot is linked. Findings are in manifest.json under each screen's `audit`.

- `admin-loading-configuration`: contrast 0/11 light, 0/11 dark below AA; focus 8/8 visible; 200% text @320 overflow 0px; reduced-motion shimmer 0; text narrower than its words 0; forced colors 0/11 below AA; lowest ratio 4.6:1 ([forced colors](screens/admin-loading-configuration/desktop-forced-colors-viewport.png))
- `admin-unconfigured`: contrast 0/13 light, 0/13 dark below AA; focus 8/8 visible; 200% text @320 overflow 0px; reduced-motion shimmer 0; text narrower than its words 0; forced colors 0/13 below AA; lowest ratio 4.6:1 ([forced colors](screens/admin-unconfigured/desktop-forced-colors-viewport.png))
- `admin-misconfigured`: contrast 0/12 light, 0/12 dark below AA; focus 8/8 visible; 200% text @320 overflow 0px; reduced-motion shimmer 0; text narrower than its words 0; forced colors 0/12 below AA; lowest ratio 4.6:1 ([forced colors](screens/admin-misconfigured/desktop-forced-colors-viewport.png))
- `admin-sign-in`: contrast 0/16 light, 0/16 dark below AA; focus 10/10 visible; 200% text @320 overflow 0px; reduced-motion shimmer 0; text narrower than its words 0; forced colors 0/16 below AA; lowest ratio 4.6:1 ([forced colors](screens/admin-sign-in/desktop-forced-colors-viewport.png))
- `admin-pages-site-demo`: contrast 0/17 light, 0/17 dark below AA; focus 10/10 visible; 200% text @320 overflow 0px; reduced-motion shimmer 0; text narrower than its words 0; forced colors 0/17 below AA; lowest ratio 4.6:1 ([forced colors](screens/admin-pages-site-demo/desktop-forced-colors-viewport.png))

## Not captured

Every planned screen was captured.
