"""Verify all catalog artwork, regenerate accurate masks and selected GPU fixtures.
Requires Pillow, numpy, scipy. Use --fixtures to include every asset (large).
"""
from pathlib import Path
import argparse, hashlib, json, struct
from PIL import Image
import numpy as np
from scipy.ndimage import label, find_objects
ROOT=Path(__file__).resolve().parents[1]
ASSETS=ROOT/"src/App/Assets/DeviceFrames"
REVIEW=ROOT/"work/phone-frame-review"
REVIEW_MODELS={'iphone-12-mini','iphone-17','iphone-8','iphone-14-pro','iphone-16-pro-max',
    'iphone-18-pro','iphone-18-pro-max','ipad-air-4th-generation','ipad-pro-11-inch-m4',
    'ipad-mini-6th-generation','ipad-mini-a20-pro','iphone-duo','iphone-duo-inner',
    'iphone-5','iphone-6','iphone-6-plus','ipad-pro-12-9-inch','ipad-pro-11-inch-m5',
    'ipad-10th-generation'}
def verify_artwork(row, path):
    if hashlib.sha256(path.read_bytes()).hexdigest()!=row['sha256']:raise ValueError(f'Artwork hash mismatch: {path}')
    im=Image.open(path).convert('RGBA')
    if im.size!=(row['frameWidth'],row['frameHeight']):raise ValueError(f'Artwork dimensions mismatch: {path}')
    if max(im.size)>4096:raise ValueError(f'Artwork exceeds GPU size limit: {path}')
    alpha=np.asarray(im.getchannel('A'));regions,n=label(alpha<255);center=regions[im.height//2,im.width//2]
    if center==0:raise ValueError(f'Opaque screen center: {path}')
    ys,xs=find_objects(regions)[center-1]
    measured=(xs.start,ys.start,xs.stop-xs.start,ys.stop-ys.start)
    declared=tuple(row[key] for key in ('screenX','screenY','screenWidth','screenHeight'))
    if measured!=declared:raise ValueError(f'Screen geometry mismatch: {path}: measured {measured}, catalog {declared}')
    mask=np.where(regions==center,255-alpha,0).astype('uint8')
    if mask[0,:].any() or mask[-1,:].any() or mask[:,0].any() or mask[:,-1].any():raise ValueError(f'Screen connects to exterior: {path}')
    return im,mask

def verify_screen_mask(path, expected):
    actual=np.asarray(Image.open(path).convert('L'))
    if actual.shape!=expected.shape or not np.array_equal(actual,expected):
        raise ValueError(f'Screen mask differs from artwork transparency: {path}')

def main():
    args=argparse.ArgumentParser()
    args.add_argument('--fixtures',action='store_true')
    args.add_argument('--check-only',action='store_true',help='Validate without writing masks or fixtures')
    options=args.parse_args()
    models=json.loads((ROOT/'config/apple-device-frames.json').read_text(encoding='utf-8'))['frames']
    models=[profile for model in models for profile in [model,*model.get('displayModes',[])]]
    rows=[]
    for model in models:
        default=next(c for c in model['colors'] if c['id']==model['defaultColorId'])
        for key in ('sha256','frameWidth','frameHeight','screenX','screenY','screenWidth','screenHeight'):
            if model[key]!=default[key]:raise ValueError(f'Default finish geometry/hash mismatch: {model["id"]}/{key}')
        rows.append(model)
        for color in model.get('colors',[]):
            if color['assetId']!=model['id']:rows.append({**model,**color,'id':color['assetId']})
    review_models=REVIEW_MODELS|{m['id'] for m in models if m.get('artworkKind')=='self-drawn'}
    if not options.check_only:REVIEW.mkdir(parents=True,exist_ok=True)
    for row in rows:
        im,mask=verify_artwork(row,ASSETS/(row['id']+'.png'))
        if options.check_only:
            verify_screen_mask(ASSETS/(row['id']+'-screen.png'),mask)
            continue
        Image.fromarray(mask).save(ASSETS/(row['id']+'-screen.png'))
        if options.fixtures or row['id'] in review_models:
            width,height=im.size
            pixels=np.asarray(im.convert('RGBa'))[:,:,[2,1,0,3]].tobytes()
            header=struct.pack('<IIffff',width,height,row['screenX']/width,row['screenY']/height,row['screenWidth']/width,row['screenHeight']/height)
            (REVIEW/(row['id']+'.bin')).write_bytes(header+pixels+mask.tobytes())
    print(f'Verified {len(models)} frame/display profiles and {len(rows)} finish assets.')

if __name__=='__main__':main()
