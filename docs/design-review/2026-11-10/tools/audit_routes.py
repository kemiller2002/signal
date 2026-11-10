import json, os
from pathlib import Path
from playwright.sync_api import sync_playwright
out=Path('/workspace/signal-review/baseline')
with sync_playwright() as p:
 b=p.chromium.launch(executable_path='/usr/bin/chromium',args=['--no-sandbox'],proxy={'server':os.environ['HTTPS_PROXY']})
 page=b.new_page(viewport={'width':1440,'height':1000})
 errors=[]
 page.on('pageerror',lambda e:errors.append(str(e)))
 page.goto('https://signal.echelonfoundry.com/web/admin/',wait_until='networkidle')
 try: page.get_by_text('Loading the deployment',exact=False).wait_for(state='hidden',timeout=45000)
 except: pass
 results=[]
 for route in ['','groups','compare','assessments','reports','administrators','storage']:
  page.goto('https://signal.echelonfoundry.com/web/admin/#/'+route,wait_until='networkidle')
  page.wait_for_timeout(1200)
  name=route or 'overview'
  results.append({'route':route,'text':page.locator('body').inner_text(),'links':page.locator('a').evaluate_all('(a)=>a.map(x=>({text:x.textContent,href:x.href}))')})
  for label,w,h in [('desktop',1440,1000),('tablet',768,1024),('mobile',390,844)]:
   page.set_viewport_size({'width':w,'height':h})
   page.screenshot(path=str(out/(name+'-'+label+'.png')),full_page=True)
 (out/'screens.json').write_text(json.dumps({'screens':results,'errors':errors},indent=2))
 b.close()
