// Limen kernel side - browser interop only, for the assessment page (web/).
//
// Loads the published .NET WebAssembly runtime, hands each Limen message, as
// JSON, to the [JSExport] in the Echelon.Signal.Browser shim, and starts
// Limen's BrowserKernel. It never inspects a message: every application
// decision is made by the F# engine on the far side of the transport
// (src/Echelon.Signal.Engine, src/Echelon.Signal.Application).
//
// Folio's print components are registered here too, so the print surface
// upgrades; they are inert light-DOM elements with no behaviour.
import "../node_modules/@echelon-foundry/print-components/src/components/register.js";
import { BrowserKernel } from "../node_modules/@echelon-foundry/limen/dist/kernel/browser-kernel.js";

// Relative to this module, so it resolves the same in the checkout and over
// HTTP (the repository root is what is served).
const FRAMEWORK = "../build/wasm/wwwroot/_framework";

class WasmEngineTransport {
  #dispatch = null;
  #exportName;

  constructor(exportName) {
    this.#exportName = exportName;
  }

  async start() {
    const { dotnet } = await import(`${FRAMEWORK}/dotnet.js`);
    const runtime = await dotnet.withDiagnosticTracing(false).create();
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    const dispatch = exports.SignalWasm?.[this.#exportName];
    if (typeof dispatch !== "function") {
      throw new Error(`SignalWasm.${this.#exportName} export not found; run \`npm run build:wasm\` to republish the engine.`);
    }
    this.#dispatch = dispatch;
  }

  async dispatch(message) {
    if (this.#dispatch === null) throw new Error("dispatch() called before start()");
    return JSON.parse(this.#dispatch(JSON.stringify(message)));
  }
}

// Limen reports a bridge failure to its diagnostics sink rather than
// throwing, so the default no-op sink would make a broken engine look like an
// empty page. Failures are loud; everything else is debug output.
const diagnostics = {
  report(event) {
    if (event.kind === "BridgeError") {
      console.error(`[limen] bridge error during ${event.phase}: ${event.detail}`);
    } else if (event.kind === "Handshake" && event.verdict.kind === "Incompatible") {
      console.error(`[limen] engine and kernel are incompatible: ${JSON.stringify(event.verdict.reason)}`);
    } else {
      console.debug("[limen]", event.kind);
    }
  }
};

// Starts the kernel for a page. `exportName` is the SignalWasm export;
// `capabilities` are the optional Limen packs the page's engine may select.
// The kernel's status is published on <html data-kernel> as soon as it
// leaves "starting", so the page (and its browser tests) can tell "running"
// from a failed start even while start-up effects are still completing.
// The pre-runtime start-up screen (#app-startup, Forma .ef-startup) is in each
// page's HTML. It is presentation only: hidden as soon as the kernel reports
// "running" (start-up effects may still be completing, and the application is
// already usable), and marked failed when the kernel ends in any other state.
// web-kernel/startup-watchdog.js owns the time-out and module-load failures,
// because this module cannot run when one of its own imports fails.
const startupOutcome = (status) =>
  status === "running" ? "mounted" : status === "unstarted" || status === "starting" ? "pending" : "failed";

const presentStartup = (screen, outcome) => {
  if (!screen || screen.hidden) return;
  if (outcome === "mounted") {
    screen.hidden = true;
  } else if (outcome === "failed") {
    screen.dataset.efState = "failed";
  }
};

export async function startPage(exportName, capabilities = []) {
  const screen = document.getElementById("app-startup");
  const kernel = new BrowserKernel(new WasmEngineTransport(exportName), document, diagnostics, {
    requireHandshake: true,
    capabilities
  });
  const publish = () => {
    document.documentElement.dataset.kernel = kernel.status;
  };
  const watch = setInterval(() => {
    if (kernel.status !== "unstarted" && kernel.status !== "starting") {
      publish();
      presentStartup(screen, startupOutcome(kernel.status));
      clearInterval(watch);
    }
  }, 50);
  try {
    await kernel.start();
  } catch (error) {
    presentStartup(screen, "failed");
    throw error;
  } finally {
    clearInterval(watch);
    publish();
  }
  presentStartup(screen, startupOutcome(kernel.status));
}
