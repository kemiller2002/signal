// Visual-review accessibility audit (SIG-ECHELON-APP-001.7), run by the capture
// (capture.spec.js) on every screen it photographs, so the checks the
// "foundry ledger" adoption must evidence are measured on the real built app
// and reproducible from main. Evidence, not a test: findings are recorded in
// the screen's fragment and the manifest, never thrown.
//
// - contrast: every visible text node's colour against the nearest opaque
//   background, in light and dark, against WCAG 2.x AA (4.5:1, or 3:1 for
//   large text). Text on an image or gradient, disabled controls and visually
//   hidden text are counted as not measurable, not as passing.
// - keyboard: the first 30 focus stops reached with Tab from the top of the page, whether each draws a
//   visible indicator (outline or box-shadow) and whether the order follows
//   the document.
// - text200: 200% text at 320 CSS px (the root font size doubled through the
//   CSSOM, which the pages' style-src 'self' allows) and the horizontal
//   overflow it causes, with the widest offenders outside scroll containers.
// - reducedMotion: running infinite animations under prefers-reduced-motion,
//   by name (the Echelon skeleton shimmer must be none).
// - forcedColors: a viewport screenshot with forced-colors: active.
//
// The pure helpers are exported for the planning test.

export const AA_NORMAL = 4.5;
export const AA_LARGE = 3;

const channel = (v) => {
  const s = v / 255;
  return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
};

export const luminance = ([r, g, b]) => 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);

export const contrastRatio = (a, b) => {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
};

// Alpha-composite a colour over an opaque background.
export const over = ([r, g, b, a = 1], [br, bg, bb]) =>
  [r * a + br * (1 - a), g * a + bg * (1 - a), b * a + bb * (1 - a)];

export const isLarge = (px, weight) => px >= 24 || (px >= 18.66 && weight >= 700);

export const required = (px, weight) => (isLarge(px, weight) ? AA_LARGE : AA_NORMAL);

// In the page: every visible text-bearing element's colours and size. Runs in
// the browser, so it cannot close over the helpers above.
const measureText = () => {
  const parse = (c) => {
    const m = (c.match(/[\d.]+/g) ?? []).map(Number);
    return m.length >= 3 ? [m[0], m[1], m[2], m.length > 3 ? m[3] : 1] : null;
  };
  const hidden = (el) => {
    const r = el.getBoundingClientRect();
    const s = getComputedStyle(el);
    return r.width <= 1 || r.height <= 1 || s.visibility === "hidden" || s.clipPath === "inset(50%)" || s.clip === "rect(0px, 0px, 0px, 0px)";
  };
  const background = (el) => {
    const layers = [];
    for (let e = el; e; e = e.parentElement) {
      const s = getComputedStyle(e);
      if (s.backgroundImage && s.backgroundImage !== "none") return { image: true };
      const c = parse(s.backgroundColor);
      if (c && c[3] > 0) layers.push(c);
      if (c && c[3] >= 0.99) return { layers };
    }
    return { layers: [...layers, [255, 255, 255, 1]] };
  };
  const describe = (el) => `${el.tagName.toLowerCase()}${el.id ? `#${el.id}` : ""}${typeof el.className === "string" && el.className ? `.${el.className.trim().split(/\s+/).slice(0, 2).join(".")}` : ""}`;
  return [...document.querySelectorAll("body *")]
    .filter((el) => [...el.childNodes].some((n) => n.nodeType === 3 && n.textContent.trim()))
    .filter((el) => el.getClientRects().length > 0 && !el.closest("[hidden], template, script, style, svg"))
    .map((el) => {
      const s = getComputedStyle(el);
      const bg = background(el);
      return {
        element: describe(el),
        text: el.textContent.trim().replace(/\s+/g, " ").slice(0, 60),
        fg: parse(s.color),
        bg: bg.image ? null : bg.layers,
        px: parseFloat(s.fontSize),
        weight: Number(s.fontWeight) || 400,
        exempt: hidden(el) || !!el.closest(":disabled, [aria-disabled='true'], .forma-sr-only, .forma-visually-hidden"),
        opacity: Number(s.opacity)
      };
    });
};

// Compose the measured layers (topmost first) into one opaque background.
export const compose = (layers) =>
  [...layers].reverse().reduce((under, layer) => over(layer, under), [255, 255, 255]);

export const contrastFindings = (measured) => {
  const rows = measured.filter((m) => !m.exempt && m.fg);
  const unmeasurable = rows.filter((m) => !m.bg);
  const checked = rows.filter((m) => m.bg).map((m) => {
    const bg = compose(m.bg);
    const ratio = contrastRatio(over(m.fg, bg), bg);
    return { element: m.element, text: m.text, ratio: Math.round(ratio * 100) / 100, required: required(m.px, m.weight) };
  });
  const failures = checked.filter((c) => c.ratio < c.required);
  return { checked: checked.length, unmeasurable: unmeasurable.length, failures: dedupe(failures) };
};

const dedupe = (rows) => [...new Map(rows.map((r) => [`${r.element}|${r.text}|${r.ratio}`, r])).values()].slice(0, 25);

const focusState = () => {
  const el = document.activeElement;
  if (!el || el === document.body) return null;
  const s = getComputedStyle(el);
  const outline = s.outlineStyle !== "none" && parseFloat(s.outlineWidth) > 0;
  const shadow = s.boxShadow && s.boxShadow !== "none";
  const r = el.getBoundingClientRect();
  return {
    element: `${el.tagName.toLowerCase()}${el.id ? `#${el.id}` : ""}`,
    name: (el.getAttribute("aria-label") || el.textContent || el.value || "").trim().replace(/\s+/g, " ").slice(0, 40),
    visible: outline || shadow,
    onScreen: r.width > 0 && r.height > 0,
    index: [...document.querySelectorAll("*")].indexOf(el)
  };
};

export const keyboardFindings = (stops) => ({
  stops: stops.length,
  withoutIndicator: stops.filter((s) => !s.visible).map((s) => `${s.element} "${s.name}"`),
  outOfDocumentOrder: stops.slice(1).filter((s, i) => s.index < stops[i].index).map((s) => `${s.element} "${s.name}"`),
  sequence: stops.map((s) => s.name || s.element)
});

const overflowAt200 = () => {
  const root = document.documentElement;
  const width = root.clientWidth;
  const scrolls = (el) => { for (let e = el.parentElement; e; e = e.parentElement) { const o = getComputedStyle(e).overflowX; if (o === "auto" || o === "scroll") return true; } return false; };
  const offenders = [...document.querySelectorAll("body *")]
    .filter((el) => el.getClientRects().length > 0 && el.getBoundingClientRect().right > width + 1 && !scrolls(el))
    .filter((el) => ![...el.children].some((c) => c.getBoundingClientRect().right > width + 1))
    .slice(0, 8)
    .map((el) => `${el.tagName.toLowerCase()}${el.id ? `#${el.id}` : ""} right=${Math.round(el.getBoundingClientRect().right)} "${(el.textContent || "").trim().slice(0, 30)}"`);
  return { overflow: root.scrollWidth - width, offenders };
};

const infiniteAnimations = () =>
  document.getAnimations()
    .filter((a) => a.playState === "running" && a.effect?.getTiming().iterations === Infinity)
    .map((a) => a.animationName ?? "unnamed");

// Runs every check on the page as it stands and restores its viewport and
// media. `shotFile` is where the forced-colours screenshot goes.
export const auditPage = async (page, { screenshot }) => {
  const result = {};
  for (const scheme of ["light", "dark"]) {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.emulateMedia({ colorScheme: scheme });
    // Colour transitions (Forma's buttons have them) must finish first, or a
    // half-way colour is measured.
    await page.evaluate(() => Promise.all(document.getAnimations().filter((a) => a.effect?.getTiming().iterations !== Infinity).map((a) => a.finished.catch(() => {}))));
    await page.waitForTimeout(100);
    result[`contrast${scheme === "light" ? "Light" : "Dark"}`] = contrastFindings(await page.evaluate(measureText));
  }
  await page.emulateMedia({ colorScheme: "light" });

  const stops = [];
  // Start the walk at the top of the page, not where focus last was: a
  // temporary anchor first in <body> (tabindex -1, so never a stop itself)
  // takes focus and is removed; the next Tab reaches the page's first stop.
  await page.evaluate(() => {
    document.activeElement?.blur?.();
    window.scrollTo(0, 0);
    const anchor = document.createElement("span");
    anchor.tabIndex = -1;
    document.body.prepend(anchor);
    anchor.focus();
    anchor.remove();
  });
  await page.keyboard.press("Tab").catch(() => {});
  for (let i = 0; i < 30; i += 1) {
    const state = await page.evaluate(focusState);
    if (!state || stops.some((s) => s.index === state.index)) break;
    stops.push(state);
    await page.keyboard.press("Tab").catch(() => {});
  }
  await page.evaluate(() => document.activeElement?.blur?.());
  result.keyboard = keyboardFindings(stops);

  await page.setViewportSize({ width: 320, height: 900 });
  await page.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
  await page.waitForTimeout(150);
  result.text200at320 = await page.evaluate(overflowAt200);
  await page.evaluate(() => { document.documentElement.style.removeProperty("font-size"); });
  await page.setViewportSize({ width: 1440, height: 900 });

  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.waitForTimeout(100);
  const names = await page.evaluate(infiniteAnimations);
  result.reducedMotion = { infinite: names, shimmer: names.filter((n) => /shimmer/.test(n)).length };
  await page.emulateMedia({ reducedMotion: "no-preference", forcedColors: "active" });
  await page.waitForTimeout(100);
  result.forcedColors = await screenshot().then((file) => ({ file }), (error) => ({ error: String(error).slice(0, 200) }));
  await page.emulateMedia({ forcedColors: "none" });
  return result;
};

// One line per screen for the README and the PR.
export const auditSummary = (audit) => !audit ? "not audited" : [
  `contrast ${audit.contrastLight.failures.length}/${audit.contrastLight.checked} light, ${audit.contrastDark.failures.length}/${audit.contrastDark.checked} dark below AA`,
  `focus ${audit.keyboard.stops - audit.keyboard.withoutIndicator.length}/${audit.keyboard.stops} visible`,
  `200% text @320 overflow ${audit.text200at320.overflow}px`,
  `reduced-motion shimmer ${audit.reducedMotion.shimmer}`
].join("; ");
