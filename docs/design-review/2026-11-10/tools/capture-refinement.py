import json, os
from pathlib import Path
from urllib.parse import urlparse
from playwright.sync_api import sync_playwright, expect

root=Path(__file__).resolve().parents[4]
evidence=Path(__file__).resolve().parents[1]
out=evidence/'after'; out.mkdir(exist_ok=True)
links=json.loads((root/'tests/browser/fixtures/survey-links.json').read_text())
checks=[]
origin=os.environ.get('SIGNAL_REVIEW_ORIGIN','https://signal.echelonfoundry.com')
with sync_playwright() as p:
 b=p.chromium.launch(executable_path='/usr/bin/chromium',args=['--no-sandbox'],proxy=({'server':os.environ['HTTPS_PROXY']} if origin.startswith('https:') else None))
 context=b.new_context(viewport={'width':1440,'height':900})
 def local_presentation(route):
  path=urlparse(route.request.url).path
  if path=='/review-text.css':
   route.fulfill(content_type='text/css',body='html {font-size:200% !important}')
   return
  file={'/web/':'web/index.html','/web/survey/':'web/survey/index.html'}.get(path,path.lstrip('/'))
  allowed=file in ['web/index.html','web/survey/index.html','web/styles.css','web-kernel/page.css'] or file.startswith('node_modules/')
  target=root/file
  if allowed and target.is_file(): route.fulfill(path=str(target))
  else: route.continue_()
 context.route(origin+'/**',local_presentation)
 page=context.new_page(); errors=[]
 page.on('pageerror',lambda e:errors.append(str(e)))
 def capture(name):
  for scheme in ['light','dark']:
   page.emulate_media(color_scheme=scheme)
   for label,w,h in [('desktop',1440,900),('tablet',834,1112),('mobile',390,844),('narrow',320,900)]:
    page.set_viewport_size({'width':w,'height':h})
    page.mouse.move(0,0)
    page.evaluate('() => document.fonts.ready.then(() => Promise.all(document.getAnimations().filter(a => a.effect?.getTiming().iterations !== Infinity).map(a => a.finished.catch(() => {}))))')
    page.screenshot(path=str(out/(name+'-'+label+'-'+scheme+'.png')),full_page=True)
    overflow=page.evaluate('document.documentElement.scrollWidth>innerWidth')
    checks.append({'screen':name,'scheme':scheme,'viewport':label,'pageOverflow':overflow})
    assert not overflow, (name,scheme,label,'page overflow')
  page.emulate_media(color_scheme='light',reduced_motion='reduce')
  page.set_viewport_size({'width':320,'height':900})
  page.evaluate("()=>{let s=document.createElement('link');s.id='review-text';s.rel='stylesheet';s.href='/review-text.css';document.head.append(s)}")
  page.wait_for_function("() => getComputedStyle(document.documentElement).fontSize==='32px'")
  page.screenshot(path=str(out/(name+'-320-text200.png')),full_page=True)
  overflow=page.evaluate('document.documentElement.scrollWidth>innerWidth')
  checks.append({'screen':name,'viewport':'320-text200','pageOverflow':overflow})
  assert not overflow,(name,'text200 overflow')
  page.locator('#review-text').evaluate('(s)=>s.remove()')
  page.emulate_media(forced_colors='active')
  page.screenshot(path=str(out/(name+'-forced-colors.png')),full_page=True)
  page.emulate_media(forced_colors='none')
 page.goto(origin+'/web/',wait_until='networkidle')
 expect(page.get_by_role('button',name='See results',exact=True)).to_be_visible(timeout=60000)
 capture('assessment')
 page.get_by_role('button',name='See results',exact=True).click()
 expect(page.locator('#refusal')).to_be_visible()
 checks.append({'flow':'unanswered assessment refused','pass':True})
 names=page.locator('input[type=radio]').evaluate_all('(a)=>[...new Set(a.map(x=>x.name))]')
 for name in names:
  page.locator('input[type=radio]').evaluate_all('(a,n)=>{let x=a.find(x=>x.name===n && x.value==="2"); if(x) x.click()}',name)
 page.get_by_role('button',name='See results',exact=True).click()
 expect(page.locator('#results')).to_be_visible()
 expect(page.locator('#results-body tr')).to_have_count(3)
 assert page.locator('#results-body').inner_text().count('50.0')==3
 assert page.locator('#methodology').inner_text() in ((evidence/'live-before/results-text.txt').read_text())
 capture('results')
 page.get_by_role('button',name='Edit answers',exact=True).click()
 expect(page.locator('#questions')).to_be_visible()
 checks.append({'flow':'assessment answers, scores 50.0 x 3, methodology and edit answers','pass':True})
 page.goto(origin+'/'+links['identified'],wait_until='networkidle')
 expect(page.get_by_role('button',name='Submit answers',exact=True)).to_be_visible(timeout=60000)
 capture('respondent')
 # Keyboard access retains native inputs and a visible focus indicator.
 page.set_viewport_size({'width':1440,'height':900})
 page.keyboard.press('Tab')
 focused=page.evaluate('({tag:document.activeElement.tagName,outline:getComputedStyle(document.activeElement).outlineStyle})')
 checks.append({'keyboard':focused})
 assert focused['tag']=='INPUT' and focused['outline']!='none'
 page.screenshot(path=str(out/'respondent-keyboard.png'),full_page=True)
 for row in ['0-3','1-4','2-6','3-1','4-2']:
  page.locator('[data-row="'+row+'"] input[data-event="answered"]').check()
 expect(page.locator('#progress')).to_have_text('5 of 5 answered')
 page.get_by_role('button',name='Submit answers',exact=True).click()
 # The fixture may show review first; the same domain button submits it.
 if page.locator('#review-notice').is_visible(): page.get_by_role('button',name='Submit answers',exact=True).click()
 expect(page.locator('#submitted')).to_be_visible(timeout=10000)
 checks.append({'flow':'generic survey answers and submission','pass':True})
 capture('submitted')
 checks.append({'origin':origin,'runtime':'locally built' if origin.startswith('http://127.0.0.1') else 'deployed','pageErrors':errors}); assert not errors
 (out/'functional-checks.json').write_text(json.dumps(checks,indent=2))
 b.close()
