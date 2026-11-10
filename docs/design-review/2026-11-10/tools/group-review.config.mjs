import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const {default:suite}=await import(resolve(root,'playwright.config.js'));
export default {
 ...suite,
 testDir:resolve(root,'tests/visual'),
 testMatch:'capture-refinement-results.spec.js',
 outputDir:resolve(root,'test-results/group-review'),
 timeout:300000,
 projects:[{name:'group-results-review',use:{...suite.projects[0].use,launchOptions:{executablePath:'/usr/bin/chromium'}}}],
 webServer:suite.webServer.map(server=>({...server,cwd:root}))
};
