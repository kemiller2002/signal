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
  await expect(page.locator("#draft-questions li")).toHaveText([/^S1 · Q1: We ship every week\./, /^S1 · Q2: We fix bugs fast\./]);

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
