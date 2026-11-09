// Start-up watchdog for the pre-runtime start-up screen (#app-startup, Forma
// .ef-startup). A classic script, deliberately outside the module graph, so it
// still runs when a module the page imports (main.js, the Limen kernel, Folio,
// this repository's loader) fails to load: in that case startPage never runs
// and only this script can end the start-up screen. It never decides anything
// about the application; it only ends an indicator that would otherwise spin
// forever (EDS-UX-WAIT-001). web-kernel/limen-wasm.js hides the screen when
// the kernel runs and marks it failed when the kernel cannot start.
(() => {
  const DEADLINE_MS = 60000;
  const screen = document.getElementById("app-startup");
  if (!screen) return;

  const pending = () => !screen.hidden && !["failed", "timed-out"].includes(screen.dataset.efState);

  const present = (state, title) => {
    if (!pending()) return;
    screen.dataset.efState = state;
    const heading = document.getElementById("app-startup-problem-title");
    if (heading && title) heading.textContent = title;
  };

  setTimeout(() => present("timed-out", "Signal is taking too long to start."), DEADLINE_MS);

  // A module script fires `error` when it, or any module it imports, cannot
  // be fetched or parsed. This script is placed after the page's module
  // script, so the element exists now; module scripts are deferred, so the
  // listener is attached before any such error can fire.
  Array.from(document.querySelectorAll('script[type="module"]')).forEach((script) =>
    script.addEventListener("error", () => present("failed", "Signal could not start."), { once: true }));
})();
