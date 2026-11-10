// Every editing action of the survey draft editor against the test-only fake
// GitHub store (UI-LAYOUT-INTERACTION@1.0.0 ULI-A01, A02, A07, A10;
// signal#113). Before GH-113 the editor rendered as its heading alone, so
// none of these controls could be reached in a browser. Each action adds a
// visible entry and its Remove button takes that entry away again; an answer
// the question does not have is refused with the answers it does have.
import { test, expect } from "./support.js";
import { signInWithStore, STORE_STEP_MS } from "./ui-conformance-fixtures.mjs";

// Every store request crosses Playwright's route interception, so a signed-in
// flow makes dozens of local round trips per action; on a loaded runner an
// action can take tens of seconds. The bounds are generous, never sleeps.
test.describe.configure({ timeout: 300_000 });

async function startDraft(page) {
  await page.getByRole("navigation", { name: "Administration" }).getByRole("link", { name: "Assessments", exact: true }).click();
  const start = page.locator("#new-draft");
  await start.getByRole("textbox", { name: "Survey identifier" }).fill("PULSE");
  await start.getByRole("textbox", { name: "Survey identifier" }).press("Tab");
  await start.getByRole("textbox", { name: "Title" }).fill("Team pulse");
  await start.getByRole("textbox", { name: "Title" }).press("Tab");
  await start.getByRole("button", { name: "Start draft" }).click();
  const draft = page.locator("#draft");
  await expect(draft.getByRole("heading", { name: "Team pulse (PULSE, draft)" })).toBeVisible();
  return draft;
}

test("ULI-A02/A07: each draft editing action adds its entry, refuses an impossible answer, and removes again", async ({ page }) => {
  await signInWithStore(page);
  const draft = await startDraft(page);
  const type = async (name, value, nth = 0) => {
    const box = draft.getByRole("textbox", { name, exact: true }).nth(nth);
    await box.fill(value);
    await box.press("Tab");
  };
  const alert = draft.getByRole("alert");

  await type("New section title", "Delivery");
  await draft.getByRole("button", { name: "Add section" }).click();
  for (const prompt of ["We ship every week.", "We fix bugs fast."]) {
    await type("New question", prompt);
    await draft.getByRole("button", { name: "Add the question below here" }).click();
  }
  // Structured rows (signal#137 review): the question, then its section and
  // kind as tags, then Remove named for the question.
  await expect(page.locator("#draft-questions li")).toHaveText([/^Q1: We ship every week\.\s*Section S1/, /^Q2: We fix bugs fast\.\s*Section S1/]);
  await expect(page.locator("#draft-questions li").first().getByRole("button")).toHaveAccessibleName("Remove question Q1: We ship every week.");

  // A condition on an answer the question does not have is refused, and says which answers exist.
  await type("Show question", "Q2");
  await type("only when question", "Q1");
  await type("has the answer", "4");
  await draft.getByRole("button", { name: "Add condition" }).click();
  await expect(alert).toContainText("'4' is not an answer to Q1. Its answers are: Never or almost never");
  await expect(page.locator("#draft-rules li")).toHaveCount(0);

  const entries = [
    ["#draft-rules", "Add condition", async () => { await type("has the answer", "Often"); }, "Show Q2 only when Q1 is 'Often'"],
    ["#draft-facts", "Add fact", async () => { await type("A yes/no fact, true when question or fact", "Q1"); await type("has the answer", "Often", 1); }, "F1: true when Q1 is 'Often'"],
    ["#draft-recommendations", "Add recommendation", async () => {
      await type("Recommend when question or fact", "Q1"); await type("has the answer", "Rarely", 2);
      await type("Title", "Ship more", 1); await type("What to do", "Release smaller batches.");
    }, "R1 (Suggested): \"Ship more\" when Q1 is 'Rarely'"],
    ["#draft-validation", "Add validation rule", async () => {
      await type("Question", "Q1"); await type("Lowest", "1"); await type("Highest", "3"); await type("Message the respondent sees", "Pick 1 to 3");
    }, "V1: Q1 must be from 1 to 3 (\"Pick 1 to 3\")"]
  ];
  for (const [list, button, fill, text] of entries) {
    await fill();
    await draft.getByRole("button", { name: button }).click();
    await expect(page.locator(`${list} li`)).toHaveText(new RegExp(`^${text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}`));
  }

  // Presentation: a time limit, a question bank and a translation each appear as an entry.
  await type("Time limit (minutes)", "20");
  await type("Warn when this many minutes are left", "5");
  await draft.getByRole("button", { name: "Add time limit" }).click();
  const timer = draft.getByRole("listitem").filter({ hasText: "timer-1: 20 minutes for the whole survey, warning at 5 min left" });
  await expect(timer).toHaveCount(1);
  await type("Bank of questions, separated by commas", "Q1, Q2");
  await type("Each respondent answers", "1");
  await draft.getByRole("button", { name: "Add question bank" }).click();
  const bank = draft.getByRole("listitem").filter({ hasText: "bank-1: each respondent answers 1 of Q1, Q2" });
  await expect(bank).toHaveCount(1);
  await type("Language tag", "fr-CA");
  await type("Text to translate (for example question/Q1/prompt)", "question/Q1/prompt");
  await type("Translation", "Nous livrons chaque semaine.");
  await draft.getByRole("button", { name: "Set translation" }).click();
  const translation = draft.getByRole("listitem").filter({ hasText: "fr-CA · question/Q1/prompt: Nous livrons chaque semaine." });
  await expect(translation).toHaveCount(1);
  await type("Question", "Q1", 1);
  await type("Name read instead of the prompt (optional)", "Weekly shipping");
  await draft.getByRole("button", { name: "Set accessibility text" }).click();
  await expect(draft.getByRole("listitem").filter({ hasText: "Q1: Weekly shipping" })).toHaveCount(1);
  await draft.getByRole("checkbox", { name: "Number the questions" }).check();
  await expect(draft.getByRole("checkbox", { name: "Number the questions" })).toBeChecked();
  await expect(draft).toContainText("Unsaved changes");

  // Every Remove takes its entry away.
  for (const entry of [timer, bank, translation]) {
    await entry.getByRole("button", { name: "Remove" }).click();
    await expect(entry).toHaveCount(0);
  }
  for (const [list] of entries) {
    await page.locator(`${list} li`).getByRole("button", { name: "Remove" }).click();
    await expect(page.locator(`${list} li`)).toHaveCount(0);
  }
  await page.locator("#draft-questions li").filter({ hasText: "Q2:" }).getByRole("button", { name: "Remove" }).click();
  await expect(page.locator("#draft-questions li")).toHaveCount(1);
  await page.locator("#draft-sections li").getByRole("button", { name: "Remove section" }).click();
  await expect(page.locator("#draft-sections li")).toHaveCount(0);
  await expect(page.locator("#draft-findings")).toContainText("STRUCT-NO-SECTIONS");
});

// Authoring in the "foundry ledger" direction (SIG-ECHELON-APP-002 (c)): the
// draft's title with its lifecycle chip, each part of the editor on its own
// panel in the editor's order, at most one primary action (Publish), and
// danger emphasis on the destructive confirmation.
test("SIG-ECHELON-APP-002: the draft editor's parts sit on panels in order, with one primary action and a danger confirmation", async ({ page }) => {
  await signInWithStore(page);
  const draft = await startDraft(page);
  await expect(draft.locator("#draft-lifecycle")).toHaveClass(/\bechelon-chip\b/);
  const panels = draft.locator(".draft-editor > .echelon-panel > .echelon-panel__header h3");
  await expect(panels).toHaveText(["Details", "Sections", "Questions", "Conditions", "Facts", "Recommendations", "Completion",
    "Validation rules", "Presentation", "Scoring", "Translations", "Accessibility text", "Test fixtures", "Validation", "Review"]);
  const primaries = draft.locator('[data-echelon-emphasis="primary"]:visible');
  expect(await primaries.count()).toBeLessThanOrEqual(1);
  if (await primaries.count() === 1) await expect(primaries).toHaveText("Publish");
  // Deleting is offered once the draft is stored.
  await draft.getByRole("button", { name: "Save draft", exact: true }).click();
  await expect(page.locator("#notice-message")).toHaveText("The draft of PULSE was saved.", { timeout: STORE_STEP_MS });
  await draft.getByRole("button", { name: "Delete draft…", exact: true }).click();
  await expect(draft.locator("#delete-draft-confirm").getByRole("button", { name: "Delete draft", exact: true })).toHaveAttribute("data-echelon-emphasis", "danger");
  await draft.getByRole("button", { name: "Keep it", exact: true }).click();
  await expect(draft.locator("#delete-draft-confirm")).toHaveCount(0);
});

// The validation findings table never sets a word as a column of letters
// (WI-0121; the capture audit's collapse check found "Severity", "Blocker"
// and the finding codes broken at 390 px and at 320 px with 200% text). Its
// cells keep whole words and the table scrolls in its labelled region; the
// page itself does not scroll sideways. Same rule as tests/visual/audit.mjs:
// three or more lines with fewer than four characters a line is collapsed.
test("SIG-ECHELON-APP-002 (WI-0121): the draft's validation findings keep whole words at 834, 390 and 320 px with 200% text", async ({ page }) => {
  await signInWithStore(page);
  await startDraft(page);
  const findings = page.locator("#draft-findings");
  await expect(findings).toContainText("STRUCT-NO-SECTIONS");
  const collapsed = () => findings.evaluate((table) =>
    [...table.querySelectorAll("th, td")].flatMap((cell) => {
      const text = cell.textContent.trim();
      if (!text) return [];
      const range = document.createRange();
      range.selectNodeContents(cell);
      const lines = new Set([...range.getClientRects()].filter((r) => r.width > 0).map((r) => Math.round(r.top))).size;
      const perLine = text.replace(/\s/g, "").length / Math.max(lines, 1);
      return lines >= 3 && perLine < 4 ? [`${text} (${lines} lines)`] : [];
    }));
  const pageOverflow = () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  for (const width of [834, 390]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await collapsed(), `at ${width} px`).toEqual([]);
    expect(await pageOverflow(), `at ${width} px`).toBeLessThanOrEqual(0);
  }
  await page.setViewportSize({ width: 320, height: 900 });
  await page.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
  expect(await collapsed(), "at 320 px with 200% text").toEqual([]);
  expect(await pageOverflow(), "at 320 px with 200% text").toBeLessThanOrEqual(0);
  // The table scrolls inside its region, which says what it holds and can take focus.
  const region = page.locator(".admin-table-scroll").filter({ has: findings });
  await expect(region).toHaveAttribute("role", "region");
  await expect(region).toHaveAttribute("tabindex", "0");
  await expect(region).toHaveAccessibleName("Validation");
});

// The template header and layout (signal#137 review): one action group at the
// top with the outcome under it; the editor's parts to move to, each moving
// focus to its panel's heading and changing nothing else; fields as wide as
// their column, up to a measure.
test("SIG-ECHELON-APP-002 (WI-0130): the draft's actions are one group in its header, and its parts move focus to their headings", async ({ page }) => {
  await signInWithStore(page);
  const draft = await startDraft(page);
  const head = draft.locator("header.draft-head");
  await expect(head.getByRole("heading", { level: 2 })).toHaveText("Team pulse (PULSE, draft)");
  await expect(head.locator("#draft-lifecycle")).toHaveAttribute("data-echelon-tone", "draft");
  const actions = head.getByRole("group", { name: "Draft actions" });
  // In the mockup's order, of the actions the draft offers now.
  const ORDER = ["Delete draft…", "Submit for review", "Approve as reviewed", "Save draft", "Publish"];
  const offered = async () => {
    const names = (await actions.getByRole("button").allTextContents()).map((name) => name.trim());
    expect(names).toEqual(ORDER.filter((name) => names.includes(name)));
    return names;
  };
  expect(await offered()).toEqual(expect.arrayContaining(["Save draft", "Publish"]));
  await expect(actions.getByRole("button", { name: "Publish", exact: true })).toHaveAttribute("data-echelon-emphasis", "primary");
  // Every draft action is in that group: none is left in a panel below.
  for (const name of ["Save draft", "Publish", "Submit for review", "Approve as reviewed", "Delete draft…"]) {
    await expect(draft.locator(".draft-panel").getByRole("button", { name, exact: true })).toHaveCount(0);
  }
  await actions.getByRole("button", { name: "Save draft", exact: true }).click();
  await expect(head.locator(".admin-outcome")).toContainText("The draft of PULSE was saved.", { timeout: STORE_STEP_MS });
  expect(await offered()).toEqual(expect.arrayContaining(["Delete draft…", "Save draft", "Publish"]));
  await expect(actions.getByRole("button", { name: "Delete draft…", exact: true })).toHaveAttribute("data-echelon-emphasis", "danger");

  // The parts: each moves focus to its heading, in view; the address stays.
  const parts = head.getByRole("navigation", { name: "Draft parts" }).getByRole("button");
  await expect(parts).toHaveText(["Questions", "Conditions", "Scoring", "Presentation", "Translations", "Test fixtures", "Validation", "Review"]);
  const address = page.url();
  for (const [part, heading] of [["Scoring", "Scoring"], ["Test fixtures", "Test fixtures"], ["Validation", "Validation"], ["Review", "Review"], ["Questions", "Questions"]]) {
    await parts.filter({ hasText: part }).click();
    const target = draft.getByRole("heading", { level: 3, name: heading, exact: true });
    await expect(target).toBeFocused();
    await expect(target).toBeInViewport();
  }
  expect(page.url()).toBe(address);
  // By keyboard too.
  await parts.filter({ hasText: "Conditions" }).focus();
  await page.keyboard.press("Enter");
  await expect(draft.getByRole("heading", { level: 3, name: "Conditions", exact: true })).toBeFocused();

  // A question row keeps whole words at 320 px with 200% text: Remove goes
  // under the question (the audit found the question four letters a line).
  await draft.getByRole("textbox", { name: "New section title", exact: true }).fill("Delivery");
  await draft.getByRole("textbox", { name: "New section title", exact: true }).press("Tab");
  await draft.getByRole("button", { name: "Add section" }).click();
  await draft.getByRole("textbox", { name: "New question", exact: true }).fill("We ship every week.");
  await draft.getByRole("textbox", { name: "New question", exact: true }).press("Tab");
  await draft.getByRole("button", { name: "Add the question below here" }).click();
  await expect(page.locator("#draft-questions li")).toHaveCount(1);

  // The warnings' acknowledgement names them, from the findings themselves,
  // and leads to them under Validation (signal#137 review).
  const warningCodes = await page.locator("#draft-findings tbody tr").filter({ has: page.locator("td", { hasText: /^Warning$/ }) }).locator("td:nth-child(2)").allTextContents();
  expect(warningCodes.length).toBeGreaterThan(0);
  const summary = warningCodes.length === 1 ? `1 warning: ${warningCodes[0]}` : `${warningCodes.length} warnings: ${warningCodes.join(", ")}`;
  const ack = head.locator(".draft-ack");
  await expect(ack.getByRole("checkbox", { name: "I have read the warnings and accept them" })).toBeVisible();
  await expect(ack.locator("#draft-warning-summary")).toHaveText(summary);
  await ack.getByRole("button", { name: "See them under Validation", exact: true }).click();
  const validation = draft.getByRole("heading", { level: 3, name: "Validation", exact: true });
  await expect(validation).toBeFocused();
  await expect(validation).toBeInViewport();
  await expect(page.locator("#draft-findings")).toContainText(warningCodes[0]);
  expect(page.url()).toBe(address);

  await page.setViewportSize({ width: 320, height: 900 });
  await page.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
  const collapsed = await page.locator("#draft-questions").evaluate((list) => {
    const found = [];
    const walker = document.createTreeWalker(list, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const text = node.textContent.trim();
      const parent = node.parentElement;
      if (!text || parent.closest("code, .forma-visually-hidden") || parent.getClientRects().length === 0) continue;
      const range = document.createRange();
      range.selectNodeContents(node);
      const lines = new Set([...range.getClientRects()].filter((r) => r.width > 0).map((r) => Math.round(r.top))).size;
      if (lines >= 3 && text.replace(/\s/g, "").length / lines < 4) found.push(`${text} (${lines} lines)`);
    }
    return found;
  });
  expect(collapsed).toEqual([]);
  await page.evaluate(() => { document.documentElement.style.removeProperty("font-size"); });

  // Fields fill their column, up to a 40rem measure (Title was about 155 px).
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 900 });
    const fit = await draft.getByRole("textbox", { name: "Title", exact: true }).first().evaluate((input) => {
      const body = input.closest(".echelon-panel__body");
      const style = getComputedStyle(body);
      const room = body.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
      const measure = 40 * parseFloat(getComputedStyle(document.documentElement).fontSize);
      return { width: input.getBoundingClientRect().width, expected: Math.min(room, measure) };
    });
    expect(Math.abs(fit.width - fit.expected), `at ${width} px`).toBeLessThanOrEqual(1);
  }
});
