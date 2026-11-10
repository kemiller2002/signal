// Limen kernel side for the administrator page: starts the kernel
// (web-kernel/limen-wasm.js) against the administrator engine, with the
// schedule pack (Arca's back-off waits) and Signal's host pack
// (web-kernel/host.js: what Fides' sign-in client needs from the browser),
// the files pack for saving report exports (WI-0075), and the focus pack,
// so focus moves to the status of an operation whose control went away
// (SIG-VIS-PENDING-002). Nothing else.
import { startPage } from "../../web-kernel/limen-wasm.js";
import { scheduleCapability } from "../../node_modules/@echelon-foundry/limen/dist/capabilities/schedule/index.js";
import { filesCapability } from "../../node_modules/@echelon-foundry/limen/dist/capabilities/files/index.js";
import { focusCapability } from "../../node_modules/@echelon-foundry/limen/dist/capabilities/focus/index.js";
import { hostCapability } from "../../web-kernel/host.js";

await startPage("DispatchAdmin", [scheduleCapability(), hostCapability(), filesCapability(), focusCapability()]);
