// A survey choice by its row key ("<question>-<choice>"), as the engine
// projects it. A scale question shows its choices on its own row, as Forma's
// ordinal scale (SIG-ECHELON-APP-002), each input's value the choice's index,
// and its choice rows are hidden; any other question's choice is its own row.
// Each row holds every input kind, hidden but the one it shows (a choice
// row also holds a hidden scale), so the choice is its row's own radio,
// directly under the row, and a scale's point is in its question row's scale
// (its shown slots only: a hidden point slot may carry a special answer's
// index as its value).
export const pick = (page, key) => {
  const [question, choice] = key.split("-");
  return page.locator(
    `[data-row="${key}"]:not([hidden]) > label > input[data-event="answered"], ` +
      `[data-row="${question}"] > fieldset:not([hidden]) label:not([hidden]) > input[data-event="answered"][value="${choice}"]`
  );
};
