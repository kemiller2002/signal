import json,os
from pathlib import Path
from playwright.sync_api import sync_playwright
out=Path('/workspace/signal-review/baseline')
with sync_playwright() as p:
 b=p.chromium.launch(executable_path='/usr/bin/chromium',args=['--no-sandbox'],proxy={'server':os.environ['HTTPS_PROXY']})
 page=b.new_page(viewport={'width':1440,'height':900})
 page.goto('https://signal.echelonfoundry.com/web/',wait_until='networkidle')
 page.get_by_role('button',name='See results',exact=True).wait_for(timeout=60000)
 names=page.locator('input[type=radio]').evaluate_all('(a)=>[...new Set(a.map(x=>x.name))]')
 for name in names:
  page.locator('input[type=radio]').filter().evaluate_all('(a,n)=>{let x=a.find(x=>x.name===n && x.value==="2"); if(x) x.click()}',name)
 page.get_by_role('button',name='See results',exact=True).click()
 page.wait_for_timeout(1500)
 (out/'results-text.txt').write_text(page.locator('body').inner_text())
 (out/'results-dom.html').write_text(page.content())
 for label,w,h in [('desktop',1440,900),('tablet',834,1112),('mobile',390,844)]:
  page.set_viewport_size({'width':w,'height':h})
  page.screenshot(path=str(out/('results-'+label+'.png')),full_page=True)
 b.close()
