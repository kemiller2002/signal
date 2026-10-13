// Markup rules that Limen's kernel relies on but does not enforce, checked on
// all three pages' source (UI-LAYOUT-INTERACTION@1.0.0 ULI-A01, ULI-L08;
// signal#113). Each was a real defect before GH-113:
// - a data-if template mounts only its first root element, so a template
//   with several roots silently dropped the rest (the survey draft editor
//   rendered as its heading alone; two of three snapshot exports never
//   appeared), and a bare nested <template> as the root stayed mounted when
//   the outer condition turned false (the group view and the printable
//   report followed the administrator to other areas);
// - Limen sends no value for a button, so a button whose meaning was its
//   value attribute arrived empty (the lifecycle buttons were refused and
//   the exports did nothing).
import { readFileSync } from "node:fs";
import { test, expect } from "./support.js";

const PAGES = ["web/index.html", "web/survey/index.html", "web/admin/index.html"];
const source = (path) => readFileSync(new URL(`../../${path}`, import.meta.url), "utf8");

for (const path of PAGES) {
  test(`ULI-A01/L08: every conditional and repeated template in ${path} has exactly one root element`, async ({ page }) => {
    const problems = await page.evaluate((html) => {
      const found = [];
      const walk = (root) => {
        for (const template of root.querySelectorAll("template")) {
          const kind = template.hasAttribute("data-if") ? `data-if="${template.getAttribute("data-if")}"`
            : template.hasAttribute("data-each") ? `data-each="${template.getAttribute("data-each")}"` : "template";
          const roots = [...template.content.children];
          const text = [...template.content.childNodes].filter((node) => node.nodeType === Node.TEXT_NODE && node.textContent.trim() !== "");
          if (roots.length !== 1 || text.length > 0) found.push(`${kind}: ${roots.length} root element(s), ${text.length} text node(s)`);
          else if (roots[0].localName === "template") found.push(`${kind}: its root is a bare <template>`);
          walk(template.content);
        }
      };
      walk(new DOMParser().parseFromString(html, "text/html"));
      return found;
    }, source(path));
    expect(problems).toEqual([]);
  });

  test(`ULI-A01: no button in ${path} carries its meaning in a value attribute`, async ({ page }) => {
    const buttons = await page.evaluate((html) => {
      const found = [];
      const walk = (root) => {
        for (const button of root.querySelectorAll("button[data-event][value]")) found.push(`${button.getAttribute("data-event")}=${button.getAttribute("value")}`);
        for (const template of root.querySelectorAll("template")) walk(template.content);
      };
      walk(new DOMParser().parseFromString(html, "text/html"));
      return found;
    }, source(path));
    expect(buttons).toEqual([]);
  });
}
