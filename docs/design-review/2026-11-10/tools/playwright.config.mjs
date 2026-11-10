import suite from '/workspace/signal-current/playwright.config.js';
export default {
 ...suite,
 testDir:'/workspace/signal-current/tests/browser',
 outputDir:'/workspace/signal-current/test-results',
 projects:suite.projects.map(project=>({...project,use:{...project.use,launchOptions:{executablePath:'/usr/bin/chromium'}}})),
 webServer:suite.webServer.map(server=>({...server,cwd:'/workspace/signal-current'}))
};
