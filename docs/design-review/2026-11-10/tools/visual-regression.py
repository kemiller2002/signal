"""Compare candidate screenshots against an explicitly selected reference.

Reference approval is an owner decision, never inferred by this tool. Pixel
differences flag review work; a pixel match does not prove usability.
Requires Pillow. Both directories must use the same filenames and browser.
"""
import argparse
import json
from pathlib import Path
from PIL import Image, ImageChops

def compare(reference, candidate, max_fraction=0):
    rows=[]
    names=sorted({p.name for p in reference.glob('*.png')} | {p.name for p in candidate.glob('*.png')})
    for name in names:
        a,b=reference/name,candidate/name
        if not a.exists() or not b.exists():
            rows.append({'file':name,'pass':False,'reason':'missing screenshot'})
            continue
        with Image.open(a) as left, Image.open(b) as right:
            if left.size!=right.size:
                rows.append({'file':name,'pass':False,'reason':'different dimensions','reference':left.size,'candidate':right.size})
                continue
            diff=ImageChops.difference(left.convert('RGB'),right.convert('RGB'))
            red,green,blue=diff.split()
            mask=ImageChops.lighter(ImageChops.lighter(red,green),blue)
            changed=left.width*left.height-mask.histogram()[0]
            fraction=changed/(left.width*left.height)
            rows.append({'file':name,'pass':fraction<=max_fraction,'changedPixels':changed,'fraction':fraction})
    return {'kind':'pixel regression screen, not visual acceptance','screens':rows,'pass':bool(rows) and all(row['pass'] for row in rows)}

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('reference',type=Path)
    parser.add_argument('candidate',type=Path)
    parser.add_argument('--max-fraction',type=float,default=0)
    parser.add_argument('--output',type=Path)
    args=parser.parse_args()
    if not 0<=args.max_fraction<=1: parser.error('--max-fraction must be between 0 and 1')
    result=compare(args.reference,args.candidate,args.max_fraction)
    body=json.dumps(result,indent=2)+'\n'
    if args.output: args.output.write_text(body)
    else: print(body,end='')
    raise SystemExit(0 if result['pass'] else 1)
