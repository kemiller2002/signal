// A survey choice by its row key ("<question>-<choice>"), as the engine
// projects it. A scale question shows its choices on its own row, as Forma's
// ordinal scale (SIG-ECHELON-APP-002), each input's value the choice's index,
// and its choice rows are hidden; any other question's choice is its own row.
// Each row holds every input kind, hidden but the one it shows, so the input
// is named by its event.
export const pick = (page, key) => {
  const [question, choice] = key.split("-");
  return page.locator(
    `[data-row="${key}"]:not([hidden]) input[data-event="answered"], ` +
      `[data-row="${question}"] fieldset:not([hidden]) input[data-event="answered"][value="${choice}"]`
  );
};
