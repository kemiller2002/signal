import json,os
from pathlib import Path
from playwright.sync_api import sync_playwright
root=Path('/workspace/signal-current')
out=Path('/workspace/signal-review/baseline')
links=json.loads((root/'tests/browser/fixtures/survey-links.json').read_text())
with sync_playwright() as p:
 b=p.chromium.launch(executable_path='/usr/bin/chromium',args=['--no-sandbox'],proxy={'server':os.environ['HTTPS_PROXY']})
 page=b.new_page(viewport={'width':1440,'height':900})
 records=[]
 for name,path in [('assessment','web/'),('respondent',links['identified']),('assessment-detail','web/admin/#/assessments/SDRA/versions/0.1.0-draft')]:
  page.goto('https://signal.echelonfoundry.com/'+path,wait_until='networkidle',timeout=60000)
  try: page.wait_for_function("document.documentElement.dataset.kernel === 'running'",timeout=60000)
  except: pass
  page.wait_for_timeout(1000)
  records.append({'screen':name,'url':page.url,'text':page.locator('body').inner_text(),'buttons':page.get_by_role('button').all_text_contents()})
  for label,w,h in [('desktop',1440,900),('tablet',834,1112),('mobile',390,844)]:
   page.set_viewport_size({'width':w,'height':h})
   page.screenshot(path=str(out/(name+'-'+label+'.png')),full_page=True)
 (out/'public-flows.json').write_text(json.dumps(records,indent=2))
 b.close()
