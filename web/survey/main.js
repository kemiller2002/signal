// Limen kernel side for the survey page: starts the kernel
// (web-kernel/limen-wasm.js) against the engine's survey export. Nothing else.
import { startPage } from "../../web-kernel/limen-wasm.js";

await startPage("DispatchSurvey");
