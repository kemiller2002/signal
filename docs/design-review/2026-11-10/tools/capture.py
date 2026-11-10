import json, os
from pathlib import Path
from playwright.sync_api import sync_playwright

OUT = Path('/workspace/signal-review/baseline')
with sync_playwright() as p:
    browser = p.chromium.launch(executable_path='/usr/bin/chromium', headless=True, args=['--no-sandbox'], proxy={'server':os.environ['HTTPS_PROXY']})
    context = browser.new_context()
    page = context.new_page()
    evidence = []
    for name, url in [('home','https://signal.echelonfoundry.com/web/home/'), ('admin','https://signal.echelonfoundry.com/web/admin/')]:
        page.goto(url, wait_until='networkidle', timeout=60000)
        page.screenshot(path=str(OUT / (name+'-desktop.png')),full_page=True)
        evidence.append({'screen':name,'url':page.url,'text':page.locator('body').inner_text(),'links':page.locator('a').evaluate_all('(links)=>links.map(a=>({text:a.textContent,href:a.href}))')})
        for label, width, height in [('tablet',768,1024),('mobile',390,844)]:
            page.set_viewport_size({'width':width,'height':height})
            page.screenshot(path=str(OUT / (name+'-'+label+'.png')),full_page=True)
        page.set_viewport_size({'width':1440,'height':1000})
    (OUT/'observations.json').write_text(json.dumps(evidence,indent=2))
    browser.close()
