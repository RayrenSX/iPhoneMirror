"""Import pinned artwork and original SVG drawings; generate a coverage report.
Requires requests, Pillow, numpy, scipy, resvg-py. Never selects a frame by screen resolution alone.
"""
import base64, copy, hashlib, json, re, sys
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from urllib.parse import quote, unquote, urlparse
import requests
from PIL import Image
import numpy as np
from scipy.ndimage import label, find_objects
ROOT=Path(__file__).resolve().parents[1]
RESEARCH=ROOT/"work/phone-frame-research"
ASSETS=ROOT/"src/App/Assets/DeviceFrames"
CACHE=RESEARCH/"downloaded"
CACHE.mkdir(parents=True,exist_ok=True)
def read(p):
    if p.exists():return json.loads(p.read_text(encoding="utf-8-sig"))
    sources=json.loads((ROOT/'config/device-frame-source-index.json').read_text(encoding='utf-8'))
    return sources[p.name]
products=read(ROOT/"config/apple-display-catalog.json")["products"]
existing=read(ROOT/"config/apple-device-frames.json")
# Every association is explicit. Similar displays do not imply identical bodies.
fast={
"iPhone 5c":["iPhone5,3","iPhone5,4"],"iPhone 5s":["iPhone6,1","iPhone6,2"],
"iPhone 6s":["iPhone8,1"],"iPhone 6s Plus":["iPhone8,2"],"iPhone 7":["iPhone9,1","iPhone9,3"],"iPhone 7 Plus":["iPhone9,2","iPhone9,4"],
"iPhone 8":["iPhone10,1","iPhone10,4"],"iPhone 8 Plus":["iPhone10,2","iPhone10,5"],"iPhone SE":["iPhone12,8"],
"iPhone X":["iPhone10,3","iPhone10,6"],"iPhone XS":["iPhone11,2"],"iPhone XS Max":["iPhone11,4","iPhone11,6"],"iPhone XR":["iPhone11,8"],
"iPhone 11":["iPhone12,1"],"iPhone 11 Pro":["iPhone12,3"],"iPhone 11 Pro Max":["iPhone12,5"],
"iPhone 12":["iPhone13,2"],"iPhone 12 Mini":["iPhone13,1"],"iPhone 12 Pro":["iPhone13,3"],"iPhone 12 Pro Max":["iPhone13,4"],
"iPhone 13":["iPhone14,5"],"iPhone 13 Mini":["iPhone14,4"],"iPhone 13 Pro":["iPhone14,2"],"iPhone 13 Pro Max":["iPhone14,3"],
"iPhone 14":["iPhone14,7"],"iPhone 14 Plus":["iPhone14,8"],"iPhone 14 Pro":["iPhone15,2"],"iPhone 14 Pro Max":["iPhone15,3"],
"iPhone 16":["iPhone17,3"],"iPhone 16 Plus":["iPhone17,4"],"iPhone 16 Pro":["iPhone17,1"],"iPhone 16 Pro Max":["iPhone17,2"],
"iPhone 17":["iPhone18,3"],"iPhone 17 Pro":["iPhone18,1"],"iPhone 17 Pro Max":["iPhone18,2"],
"iPad 10.2":["iPad7,11","iPad7,12","iPad11,6","iPad11,7","iPad12,1","iPad12,2"],
"iPad Air 2":["iPad5,3","iPad5,4"],
"iPad Air (2019)":["iPad11,3","iPad11,4"],
"iPad Mini 4":["iPad5,1","iPad5,2"],
"iPad Mini (2019)":["iPad11,1","iPad11,2"],
"iPad Air (2019) 2020":["iPad13,1","iPad13,2"],
"iPad Pro (11-inch)":["iPad8,1","iPad8,2","iPad8,3","iPad8,4","iPad8,9","iPad8,10"],
"iPad Pro (12.9-inch) (4th generation)":["iPad8,11","iPad8,12"],
"iPad Pro":["iPad7,1","iPad7,2"],
}
# Media assets supplement missing modern iPhones and iPads; variants ending in 2
# are landscape. Pencil/shadow variants are never selected.
media={
("apple-iphone","15"):["iPhone15,4"],("apple-iphone","15-pro-max"):["iPhone16,2"],("apple-iphone","air"):["iPhone18,4"],
# The third-party M1 directory actually contains fourth-generation finishes.
# Use Apple's March 2022 archive for Air 5 instead.
("apple-ipad","ipad-air-11-m2-m3"):["iPad14,8","iPad14,9","iPad15,3","iPad15,4"],
("apple-ipad","ipad-air-13-m2-m3"):["iPad14,10","iPad14,11","iPad15,5","iPad15,6"],
("apple-ipad","ipad-pro-11-a12x-to-m2"):["iPad13,4","iPad13,5","iPad13,6","iPad13,7","iPad14,3","iPad14,4"],
("apple-ipad","ipad-pro-13-a12x-to-m2"):["iPad8,5","iPad8,6","iPad8,7","iPad8,8","iPad13,8","iPad13,9","iPad13,10","iPad13,11","iPad14,5","iPad14,6"],
("apple-ipad","ipad-pro-11-m4-m5"):["iPad16,3","iPad16,4"],
("apple-ipad","ipad-pro-13-m4-m5"):["iPad16,5","iPad16,6"],
("apple-ipad","ipad-mini-8-3-a17-pro"):["iPad16,1","iPad16,2"],
}
def slug(s):return re.sub(r"[^a-z0-9]+","-",s.replace("ʀ","r").lower()).strip("-")
def fetch(item):
    if item.get('local'):return item,Path(item['local'])
    out=CACHE/(hashlib.sha256(item["url"].encode()).hexdigest()+".png")
    if out.exists():return item,out
    err=None
    for _ in range(3):
        try:
            r=requests.get(item["url"],timeout=35)
            r.raise_for_status()
            data=r.content
            if not data.startswith(b"\x89PNG"):
                data=base64.b64decode(r.json()["content"])
            Image.open(__import__('io').BytesIO(data)).verify()
            out.write_bytes(data);return item,out
        except Exception as e:err=e
    raise RuntimeError(f"Download failed {item['url']}: {err}")

def geometry(path):
    im=Image.open(path).convert("RGBA");a=np.asarray(im.getchannel("A"))
    regions,n=label(a<255)
    center=int(regions[im.height//2,im.width//2])
    if center==0:raise ValueError(f"Screen center is opaque: {path}")
    obj=find_objects(regions)[center-1];y,x=obj
    if x.start==0 or y.start==0 or x.stop==im.width or y.stop==im.height:raise ValueError(f"Open screen contour: {path}")
    mask=np.where(regions==center,255-a,0).astype('uint8')
    return im,mask,(x.start,y.start,x.stop-x.start,y.stop-y.start)

def all_profiles(frames):
    return [profile for frame in frames for profile in [frame,*frame.get('displayModes',[])]]

def add_reviewed_shared_fronts(frames):
    """Apply explicit front-view associations; never infer them from resolution."""
    manifest=read(ROOT/'config/device-frame-shared-fronts.json')
    for group in manifest['groups']:
        source=next(f for f in frames if f['id']==group['sourceProfileId'])
        assert group['sourceProductType'] in source['productTypes'], 'Shared-front source model changed'
        product=next(p for p in products if p['name']==group['productName'])
        source_product=next(p for p in products if group['sourceProductType'] in p['identifiers'])
        assert set(product['identifiers'])==set(group['productTypes']), 'Shared-front target identifiers changed'
        assert all(not set(f['productTypes']).intersection(group['productTypes']) for f in frames), 'Shared-front target already has artwork'
        assert group['displayPixels']==dict(width=product['width'],height=product['height']), 'Shared-front panel changed'
        assert (source_product['width'],source_product['height'])==(product['width'],product['height']), 'Shared-front source panel differs'
        assert len(group['evidence'])>=2, 'Missing reviewed front-view evidence'
        frame=copy.deepcopy(source)
        frame.update(id=slug(product['name']),name=product['name'],productTypes=group['productTypes'],
                     artworkKind='reviewed-shared-front',sharedFrontSource=source['id'],
                     sharedFrontEvidence=group['evidence'],sharedFrontScope=group['scope'])
        frame.pop('displayModes',None)
        frame['colors']=[c for c in frame['colors'] if c['id'] in group['finishes']]
        assert len(frame['colors'])==len(group['finishes']), 'Missing shared-front finishes'
        assert frame['defaultColorId'] in group['finishes'], 'Shared-front default finish is excluded'
        for color in frame['colors']:
            original=color['assetId']
            color['assetId']=frame['id'] if color['id']==frame['defaultColorId'] else frame['id']+'-'+color['id']
            # Numeric housing encodings are model-specific, even if artwork is shared.
            color['tokens']=list(dict.fromkeys([color['id'],color['id'].replace('-',' ')]))
            for suffix in ['.png','-screen.png']:
                (ASSETS/(color['assetId']+suffix)).write_bytes((ASSETS/(original+suffix)).read_bytes())
        frames.append(frame)

def main():
    if '--drawings-only' in sys.argv:
        refresh_drawings()
        return
    tree=read(RESEARCH/"frameit-tree.json")["tree"]
    items=[]
    for x in tree:
        if not x["path"].startswith("1772014847/Apple ") or not x["path"].endswith(".png"):continue
        stem=Path(x["path"]).stem.removeprefix("Apple ")
        keys=[k for k in fast if stem.startswith(k+" ")]
        if not keys:continue
        key=max(keys,key=len)
        if key=="iPad Air (2019)":continue
        color=stem[len(key)+1:].removesuffix(" Portrait")
        items.append(dict(group=key,identifiers=fast[key],color=color,url="https://fastlane.github.io/frameit-frames/"+quote(x["path"]),source="https://github.com/fastlane/frameit-frames/blob/gh-pages/"+quote(x["path"])))
    history=read(RESEARCH/"current-frameit-tree.json")["tree"]
    for x in history:
        if not x['path'].startswith('latest/Apple iPad Pro (10.5-inch) ') or not x['path'].endswith('.png'):continue
        color=Path(x['path']).stem.removeprefix('Apple iPad Pro (10.5-inch) ')
        items.append(dict(group='iPad Pro (10.5-inch)',identifiers=['iPad7,3','iPad7,4'],color=color,url=x['url'],source='https://github.com/fastlane/frameit-frames/blob/'+read(RESEARCH/'current-frameit-tree.json')['sha']+'/'+quote(x['path'])))
    for key,version in [("iPad Air 2","1607001240"),("iPad Air (2019)","1629411840"),("iPad Mini 4","1607001240"),("iPhone SE","1509566139")]:
        for x in history:
            if not x['path'].startswith(version+'/Apple '+key+' ') or not x['path'].endswith('.png'):continue
            color=Path(x['path']).stem.removeprefix('Apple '+key+' ')
            if color not in ['Gold','Silver','Space Gray','Rose Gold']:continue
            items.append(dict(group='iPhone SE (1st generation)' if key=='iPhone SE' else key,identifiers=['iPhone8,4'] if key=='iPhone SE' else fast[key],color=color,url='https://fastlane.github.io/frameit-frames/'+quote(x['path']),source='https://github.com/fastlane/frameit-frames/blob/gh-pages/'+quote(x['path'])))
    index=read(RESEARCH/"device-frames-media-index.json")
    tree={x["path"]:x for x in read(RESEARCH/"device-frames-media-tree.json")["tree"]}
    for (family,key),identifiers in media.items():
        for color,entry in index[family][key].items():
            if any(t in color for t in ['landscape','pencil','shadow']):continue
            if entry["screen"]["width"]>entry["screen"]["height"]:continue
            path=unquote(urlparse(entry['frame']).path).split('/main/',1)[1]
            items.append(dict(group=key,identifiers=identifiers,color=re.sub(r'[- ]?2$','',color).removeprefix('portrait-').replace('-',' '),url=tree[path]['url'],source=entry['frame']))
    supplemental=read(ROOT/'work/phone-color-research/appleframer.com-tree.json')['tree']
    for x in supplemental:
        path=x['path']
        if not path.endswith('Portrait.png'):continue
        group=None
        if path=='public/frames/iPad/mini/2021/iPad mini 2021 Portrait.png':
            group='iPad mini (6th generation)';identifiers=['iPad14,1','iPad14,2'];color='Starlight'
        if group:items.append(dict(group=group,identifiers=identifiers,color=color,url=x['url'],source='https://github.com/TimBroddin/appleframer.com/blob/'+read(ROOT/'work/phone-color-research/appleframer.com-tree.json')['sha']+'/'+quote(path)))
    # These older, model-specific vector renderings have opaque rectangular
    # screens. Clear only the publisher's measured screen rectangle; preserve
    # all bezel/sensor pixels and record both original and derived hashes.
    for group in read(ROOT/'config/device-frame-supplements.json')['groups']:
        for finish in group['finishes']:
            items.append(dict(group=group['name'],identifiers=group['productTypes'],color=finish['color'],
                url=finish['url'],source=finish['source'],screen=finish['screen'],
                geometrySource=group['geometrySource'],artworkKind='model-specific-vector-rendering'))
    # Direct, unmodified PNGs extracted from Apple's disk images take priority.
    direct_groups={
        'iPad (A16)': ['iPad15,7','iPad15,8'],
        'iPad Air 11_ (M4)': ['iPad16,8','iPad16,9'],
        'iPad Air 13_ (M4)': ['iPad16,10','iPad16,11'],
        'iPhone 15': ['iPhone15,4'], 'iPhone 15 Plus': ['iPhone15,5'],
        'iPhone 15 Pro': ['iPhone16,1'], 'iPhone 15 Pro Max': ['iPhone16,2'],
        'iPad Air': ['iPad13,16','iPad13,17'],
        'iPhone 18 Pro': ['iPhone19,2'], 'iPhone 18 Pro Max': ['iPhone19,3','iPhone19,7'],
        'iPad mini (A20 Pro)': ['iPad19,1','iPad19,2'],
        'iPad Pro (M5) 11_': ['iPad17,1','iPad17,2'],
        'iPad Pro (M5) 13_': ['iPad17,3','iPad17,4'],
        'iPhone Duo': ['iPhone19,4'],
    }
    local_items=[]
    for path in (RESEARCH/'apple-dmg').rglob('* - *Portrait.png'):
        if '__MACOSX' in str(path) or path.name.startswith('._'):continue
        parts=path.stem.rsplit(' - ',2)
        if len(parts)!=3 or parts[0] not in direct_groups:continue
        group,color,orientation=parts
        if orientation not in ['Portrait','Inner Open Portrait','Outer Closed Portrait']:continue
        archive={'ipad-a16':'Bezel-iPad-(A16)','ipad-air-m4':'Bezel-iPad-Air-(M4)','iphone-15':'Bezel-iPhone-15',
                 'ipad-air':'Bezel-iPad-Air','iphone-18':'Bezel-iPhone-18','ipad-mini-a20':'Bezel-iPad-mini-(A20-Pro)',
                 'ipad-pro-m5':'Bezel-iPad-Pro-(M5)','iphone-duo':'Bezel-iPhone-Duo'}[path.relative_to(RESEARCH/'apple-dmg').parts[0]]
        item=dict(group=group,identifiers=direct_groups[group],color=color,local=str(path),url='',source='https://devimages-cdn.apple.com/design/resources/download/'+archive+'.dmg#'+quote(path.name))
        if orientation=='Inner Open Portrait':item.update(group='iPhone Duo Inner',displayMode='inner',panelWidth=1878,panelHeight=2670)
        local_items.append(item)
    replaced={i for item in local_items for i in item['identifiers']}
    items=[item for item in items if not set(item['identifiers']).intersection(replaced)]+local_items
    downloads=list(ThreadPoolExecutor(max_workers=5).map(fetch,items))
    groups={}
    for item,path in downloads:
        groups.setdefault(item['group'],[]).append((item,path))
    old={f['productTypes'][0]:f for f in existing['frames']}
    frames=[]
    aliases={'space-grey':'space-gray','grey':'space-gray','stardust':'starlight','lavender':'purple'}
    for key,variants in groups.items():
        variants.sort(key=lambda row:(not any(t in row[0]['color'].lower() for t in ['black','gray','grey','graphite','midnight']),row[0]['color']))
        ids=variants[0][0]['identifiers'];product=next(p for p in products if ids[0] in p['identifiers'])
        fid='iphone-duo-inner' if key=='iPhone Duo Inner' else old[ids[0]]['id'] if ids[0] in ['iPhone13,1','iPhone18,3'] else slug(product['name'])
        colors=[];default=None
        for idx,(item,path) in enumerate(variants):
            cid=aliases.get(slug(item['color']),slug(item['color']))
            if key=='iPhone 17' and cid=='purple':cid='lavender'
            if key=='iPhone 5c' and cid=='red':cid='pink'
            if key=='iPhone XR':cid={'silver':'white','space-gray':'black'}.get(cid,cid)
            if any(c['id']==cid for c in colors):continue
            asset=fid if default is None else fid+'-'+cid
            original_hash=hashlib.sha256(path.read_bytes()).hexdigest()
            if 'screen' in item:
                im=Image.open(path).convert('RGBA');x,y,w,h=item['screen']
                if min(x,y)<0 or min(w,h)<=0 or x+w>=im.width or y+h>=im.height:raise ValueError('Invalid published screen geometry')
                pixels=np.array(im);pixels[y:y+h,x:x+w]=0
                derived=CACHE/(hashlib.sha256(item['url'].encode()).hexdigest()+'-transparent.png')
                Image.fromarray(pixels).save(derived)
                im,mask,rect=geometry(derived);artwork_path=derived
                if tuple(rect)!=(x,y,w,h):raise ValueError('Published rectangular screen intersects an external transparent region')
            else:
                im,mask,rect=geometry(path);artwork_path=path
            # Pixel aspect is calibrated to the exact panel aspect; external
            # metadata is not trusted for cutouts or offsets.
            ratio=rect[2]/rect[3];expected=item.get('panelWidth',product['width'])/item.get('panelHeight',product['height'])
            if abs(ratio-expected)>0.004:raise ValueError(f"Panel aspect mismatch {key} {cid}: {rect} expected {product['width']}x{product['height']}")
            (ASSETS/(asset+'.png')).write_bytes(artwork_path.read_bytes())
            Image.fromarray(mask).save(ASSETS/(asset+'-screen.png'))
            geom=dict(frameWidth=im.width,frameHeight=im.height,screenX=rect[0],screenY=rect[1],screenWidth=rect[2],screenHeight=rect[3])
            tokens=[item['color'].lower()]
            # Preserve only model-specific numeric evidence already established.
            if ids[0] in old:
                prior=next((c for c in old[ids[0]]['colors'] if c['id']==cid),None)
                if prior:tokens=prior['tokens']
            color=dict(id=cid,assetId=asset,tokens=tokens,source=item['source'],sha256=hashlib.sha256(artwork_path.read_bytes()).hexdigest(),**geom)
            if 'screen' in item:color.update(originalSha256=original_hash,screenCleared=item['screen'],geometrySource=item['geometrySource'],artworkKind=item['artworkKind'])
            colors.append(color)
            if default is None:default=dict(id=fid,name=product['name'],productTypes=ids,**geom,source=color['source'],sha256=color['sha256'],defaultColorId=cid)
        default['colors']=colors;frames.append(default)
        print(f"Imported {key}: {len(colors)} finishes",flush=True)
    duo=next((f for f in frames if f['id']=='iphone-duo'),None)
    inner=next((f for f in frames if f['id']=='iphone-duo-inner'),None)
    if duo and inner:
        inner.update(panelWidth=1878,panelHeight=2670,panelSpecUrl='https://support.apple.com/en-us/148588')
        duo['displayModes']=[inner];frames.remove(inner)
    finalize_frames(frames)

def refresh_drawings():
    # Keep reviewed imported PNGs intact when refining original SVGs.
    finalize_frames([f for f in existing['frames'] if f.get('artworkKind') not in ('self-drawn','reviewed-shared-front')])

def finalize_frames(frames):
    from draw_device_frames import render_frames
    frames.extend(render_frames(frames))
    # Reviewed tokens are checked in separately from the artwork importer.
    token_path=ROOT/'config/apple-device-frame-colors.json'
    if token_path.exists():
        tokens=read(token_path)['models']
        for frame in all_profiles(frames):
            for color in frame['colors']:
                color['tokens']=tokens.get(frame['id'],{}).get(color['id'],color['tokens'])
    for frame in all_profiles(frames):
        for color in frame['colors']:
            color['tokens']=list(dict.fromkeys([color['id'],color['id'].replace('-',' '),*color['tokens']]))
    add_reviewed_shared_fronts(frames)
    all_assets=[c['assetId'] for f in all_profiles(frames) for c in f['colors']]
    assert len(all_assets)==len(set(all_assets)), 'Asset name collision'
    covered={i for f in frames for i in f['productTypes']}
    missing=[p for p in products if not set(p['identifiers']).issubset(covered)]
    out=dict(schema=1,frames=frames,coverage=dict(identifierCount=len(covered),productCount=sum(set(p['identifiers']).issubset(covered) for p in products),missing=[dict(name=p['name'],productTypes=[i for i in p['identifiers'] if i not in covered]) for p in missing]))
    (ROOT/'config/apple-device-frames.json').write_text(json.dumps(out,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    by_identifier={identifier:frame for frame in frames for identifier in frame['productTypes']}
    lines=['# Device frame coverage','',
        f"{out['coverage']['productCount']} / {len(products)} products; {len(covered)} / {sum(len(p['identifiers']) for p in products)} ProductTypes. {len(all_assets)} finish assets.",'',
        'Presence here means model-specific artwork/geometry is included, not that every firmware color token or physical device has been verified. Some models have only a subset of their finishes. iPhone 5/6/6 Plus and first-generation 12.9-inch iPad Pro use credited vector renderings, with only the published rectangular screen cleared; they are not Apple bezel exports. Duo includes separate official inner and outer artwork. iPad 10 uses reviewed A16 front-view artwork; the explicit association, official specifications and limitation are recorded in config/device-frame-shared-fronts.json. This is not a dedicated iPad 10 export.','',
        'Self-drawn entries use original editable SVGs constrained by official body dimensions and panel density. Sensor/button/notch positions, corner radii and finish shading are visual estimates; they are not Apple bezel exports or physically calibrated measurements. See config/device-frame-drawings.json for provenance and limitations.','',
        '| Device | Identifiers | Included finishes | Artwork |','| --- | --- | --- | --- |']
    for product in products:
        frame=by_identifier.get(product['identifiers'][0])
        finishes=', '.join(c['id'] for c in frame['colors']) if frame else '**Missing exact artwork**'
        kind=frame.get('artworkKind',frame['colors'][0].get('artworkKind','Apple bezel')) if frame else 'Missing'
        lines.append(f"| {product['name']} | {', '.join(product['identifiers'])} | {finishes} | {kind} |")
    (ROOT/'docs/device-frame-coverage.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    print('Coverage',out['coverage'],flush=True)
if __name__=='__main__':main()
