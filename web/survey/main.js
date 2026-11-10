// Limen kernel side for the survey page: starts the kernel
// (web-kernel/limen-wasm.js) against the engine's survey export, with the
// focus pack, so focus moves to the top of a new page when the control that
// moved the respondent there is gone (SIG-ECHELON-APP-002 (d)). Nothing else.
import { startPage } from "../../web-kernel/limen-wasm.js";
import { focusCapability } from "../../node_modules/@echelon-foundry/limen/dist/capabilities/focus/index.js";

await startPage("DispatchSurvey", [focusCapability()]);
