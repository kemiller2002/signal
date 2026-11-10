// Visual-review evidence capture (WI-0110): an init script that runs in the
// page before its own scripts. It records, on every DOM change, a snapshot of
// everything that can tell a person to wait, so the manifest can say what was
// on screen while each asynchronous operation ran. It reads the DOM only and
// changes nothing. Microtasks, not timers, coalesce the snapshots, so a test
// that installs Playwright's fake clock does not stop the recording.
(() => {
  const text = (element) => (element.innerText ?? element.textContent ?? "").replace(/\s+/g, " ").trim().slice(0, 200);
  const visible = (element) =>
    typeof element.checkVisibility === "function"
      ? element.checkVisibility({ visibilityProperty: true, opacityProperty: true })
      : element.getClientRects().length > 0;
  const outsideStartup = (element) => !element.closest("#app-startup");
  const texts = (selector) =>
    Array.from(document.querySelectorAll(selector)).filter(outsideStartup).filter(visible).map(text);

  const snapshot = () => {
    const startup = document.getElementById("app-startup");
    const body = document.body;
    return {
      t: performance.now(),
      kernel: document.documentElement.dataset.kernel ?? null,
      blank: body ? (body.innerText ?? "").trim().length === 0 : true,
      startup: startup
        ? {
            visible: !startup.hidden && visible(startup),
            state: startup.dataset.formaState ?? null,
            spinner: Array.from(startup.querySelectorAll(".forma-spinner__indicator")).some(visible),
            text: Array.from(startup.querySelectorAll("[role=status], [role=alert], .forma-startup__slow"))
              .filter(visible)
              .map(text)
              .filter(Boolean)
              .join(" | ")
          }
        : null,
      spinners: texts(".forma-spinner"),
      busy: texts('[aria-busy="true"]'),
      statuses: texts('[role="status"], #loading'),
      alerts: texts('[role="alert"], [role="alertdialog"]'),
      disabledButtons: Array.from(document.querySelectorAll("button:disabled, .forma-button[aria-disabled='true']"))
        .filter(outsideStartup)
        .filter(visible).length
    };
  };

  const state = { doc: Math.random().toString(36).slice(2), log: [] };
  window.__visualCapture = state;
  let scheduled = false;
  const record = () => {
    scheduled = false;
    try {
      state.log.push(snapshot());
      if (state.log.length > 5000) state.log.splice(0, state.log.length - 5000);
    } catch {
      // A snapshot taken mid-teardown can throw; the next mutation records again.
    }
  };
  const schedule = () => {
    if (scheduled) return;
    scheduled = true;
    queueMicrotask(record);
  };
  new MutationObserver(schedule).observe(document, { subtree: true, childList: true, attributes: true, characterData: true });
  // A state a stylesheet reveals on a timer (Forma's start-up slow note is a
  // delayed zero-length animation) changes no DOM, so animations and
  // transitions are recorded too, as are stylesheets finishing loading.
  for (const type of ["animationstart", "animationend", "transitionend", "load"]) document.addEventListener(type, schedule, true);
  document.addEventListener("DOMContentLoaded", schedule);
  window.addEventListener("load", schedule);
})();
