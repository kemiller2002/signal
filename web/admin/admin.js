// Limen kernel side for the administrator page: starts the kernel
// (web-kernel/limen-wasm.js) against the administrator engine, with the
// schedule pack (Arca's back-off waits) and Signal's host pack
// (web-kernel/host.js: what Fides' sign-in client needs from the browser).
// Nothing else.
import { startPage } from "../../web-kernel/limen-wasm.js";
import { scheduleCapability } from "../../node_modules/@echelon-foundry/limen/dist/capabilities/schedule/index.js";
import { hostCapability } from "../../web-kernel/host.js";

await startPage("DispatchAdmin", [scheduleCapability(), hostCapability()]);
