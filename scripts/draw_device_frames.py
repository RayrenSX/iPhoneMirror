"""Render the original model-specific SVG frames declared in device-frame-drawings.json.

Requires requests, Pillow, numpy, scipy and resvg-py==0.5.0. No network or source photos
are needed. Official dimensions constrain the outline/panel; estimated visual
details are explicitly identified in the manifest and generated catalog.
"""
from pathlib import Path
from html import escape
import hashlib
import json
import math
import numpy as np
from PIL import Image
from scipy.ndimage import label, find_objects
import resvg_py

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / 'src/App/Assets/DeviceFrames'
VECTORS = ROOT / 'src/App/Assets/DeviceFrameDrawings'

# Housing colors are illustrative RGB values, never numeric Lockdown mappings.
METALS = {'silver': '#bfc2c6', 'starlight': '#dad6cc', 'space-gray': '#70747a',
          'black': '#383a3e', 'white': '#d4d6d9', 'midnight': '#30353d',
          'gold': '#d8c3a5', 'rose-gold': '#d8b5ae', 'red': '#a51c32',
          'soft-pink': '#e4ced0'}


def mix(color, other, amount):
    a, b = [tuple(int(c[i:i+2], 16) for i in (1, 3, 5)) for c in (color, other)]
    return '#' + ''.join(f'{round(x*(1-amount)+y*amount):02x}' for x, y in zip(a, b))


def draw(model, finish):
    body, features, style = model['bodyMm'], model['features'], model['style']
    w, h = body['width'], body['height']
    panel = model['panelPixels']
    factor = min(2, max(1, math.ceil(1600 / panel['height'])))
    scale = model['ppi'] / 25.4 * factor
    pad = 1.2
    width, height = round((w + 2*pad)*scale), round((h + 2*pad)*scale)
    # Use a physical viewBox with exact pixel-derived screen size. The SVG has
    # its own alpha opening; the renderer never stretches a different model.
    sw, sh = panel['width']/model['ppi']*25.4, panel['height']/model['ppi']*25.4
    x, y = pad+(w-sw)/2, pad+(h-sh)/2
    cx = pad+w/2
    r = features['corner']
    is_ipad = style.startswith('ipad')
    chrome = style == 'iphone-chrome'
    flat = style == 'iphone-flat'
    notch = style == 'iphone-notch'
    white_glass = (is_ipad and style != 'ipad-original' and finish not in ('black', 'space-gray')) or (flat and finish == 'white')
    face = '#f3f2f0' if white_glass else '#0b0c0e'
    metal = METALS[finish]
    if is_ipad and finish in ('black', 'white'): metal = METALS['silver']
    if chrome or flat: metal = '#aeb2b7'
    if features.get('warmGold') and finish == 'gold': metal = '#d9b8a7'
    edge = 1.55 if chrome else .65 if style == 'ipad-original' else .48 if is_ipad else .65 if flat else .75
    screen_r = features.get('screenCorner', 0)
    hole = f'<rect x="{x}" y="{y}" width="{sw}" height="{sh}" rx="{screen_r}" fill="black"/>'
    notch_path = ''
    if notch:
        nw, nd = features['notchWidth'], features['notchDepth']
        left, right, bottom = cx-nw/2, cx+nw/2, y+nd
        notch_path = (f'M {left-1.4} {y-.5} H {right+1.4} V {y} '
                      f'Q {right} {y} {right} {y+1.4} V {bottom-1.8} '
                      f'Q {right} {bottom} {right-1.8} {bottom} H {left+1.8} '
                      f'Q {left} {bottom} {left} {bottom-1.8} V {y+1.4} '
                      f'Q {left} {y} {left-1.4} {y} Z')
        hole += f'<path d="{notch_path}" fill="white"/>'
    svg = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {w+2*pad} {h+2*pad}">',
        f'<title>{escape(model["name"])} — {finish}, original vector frame</title>',
        '<desc>Official body and panel dimensions; visual estimates for sensors, buttons, radii and finish shading. Not physically calibrated.</desc>',
        '<defs>',
        f'<linearGradient id="metal" x1="0" y1="0" x2="1" y2=".16" gradientUnits="objectBoundingBox"><stop stop-color="{mix(metal,"#000000",.38)}"/><stop offset=".08" stop-color="{mix(metal,"#ffffff",.6)}"/><stop offset=".17" stop-color="{metal}"/><stop offset=".5" stop-color="{mix(metal,"#ffffff",.25)}"/><stop offset=".86" stop-color="{metal}"/><stop offset=".97" stop-color="{mix(metal,"#ffffff",.5)}"/><stop offset="1" stop-color="{mix(metal,"#000000",.4)}"/></linearGradient>',
        f'<linearGradient id="glass" x2=".3" y2="1"><stop stop-color="{mix(face,"#ffffff",.025)}"/><stop offset=".55" stop-color="{face}"/><stop offset="1" stop-color="{mix(face,"#000000",.035)}"/></linearGradient>',
        '<radialGradient id="lens"><stop stop-color="#172f43"/><stop offset=".42" stop-color="#0b1824"/><stop offset=".65" stop-color="#192b42"/><stop offset=".85" stop-color="#05090d"/><stop offset="1" stop-color="#30343a"/></radialGradient>',
        f'<mask id="opening" maskUnits="userSpaceOnUse" x="0" y="0" width="{w+2*pad}" height="{h+2*pad}"><rect width="100%" height="100%" fill="white"/>{hole}</mask>',
        '</defs>', '<g mask="url(#opening)">']

    def rect(rx, ry, rw, rh, radius, fill, stroke=None, weight=.08):
        extra = f' stroke="{stroke}" stroke-width="{weight}"' if stroke else ''
        svg.append(f'<rect x="{rx:.5f}" y="{ry:.5f}" width="{rw:.5f}" height="{rh:.5f}" rx="{radius:.5f}" fill="{fill}"{extra}/>')

    def circle(rx, ry, radius, fill, stroke=None, weight=.08):
        extra = f' stroke="{stroke}" stroke-width="{weight}"' if stroke else ''
        svg.append(f'<circle cx="{rx}" cy="{ry}" r="{radius}" fill="{fill}"{extra}/>')

    def side(side, pos, length, protrusion=.38):
        if side == 'top': rect(pad+pos, pad-protrusion, length, protrusion+.6, .23, 'url(#metal)', mix(metal,'#000000',.45))
        elif side == 'left': rect(pad-protrusion, pad+pos, protrusion+.6, length, .25, 'url(#metal)', mix(metal,'#000000',.45))
        else: rect(pad+w-.6, pad+pos, protrusion+.6, length, .25, 'url(#metal)', mix(metal,'#000000',.45))

    # Buttons are behind the body with distinct layouts rather than applied to
    # a universal silhouette. Positions are photo-derived visual estimates.
    if is_ipad:
        side('top', w-26, 10)
        if style in ('ipad-original', 'ipad-classic'):
            side('right',22,4.3,.32)
            side('right',33,16,.32)
        else:
            if features.get('mute'): side('right', 22, 4.3, .32)
            side('right', 33 if features.get('mute') else 27, 7.5, .32)
            side('right', 42 if features.get('mute') else 36, 7.5, .32)
    elif chrome:
        side('top', w-19, 10, .45)
        side('left', 15.7, 4.7, .4)
        side('left', 28.4, 17, .42)
    elif flat:
        side('top', w-20, 10, .35)
        side('left', 16.4 if features['antenna']=='gsm' else 14.4, 4.5)
        side('left', 29, 4.8); side('left', 39, 4.8)
    elif notch:
        side('left', 22.6, 5.2); side('left', 35, 10.3); side('left', 49, 10.3)
        side('right', 39, 17.5)
    else:
        side('left', 23, 4.7); side('left', 34.8, 9.4); side('left', 47, 9.4)
        side('right', 34.8, 11)
    if chrome and finish == 'white':
        rect(pad-.12, pad-.12, w+.24, h+.24, r+.12, '#e5e5e4', '#a1a3a5')
    rect(pad, pad, w, h, r, 'url(#metal)', mix(metal,'#000000',.58), .16)
    rect(pad+.23, pad+.23, w-.46, h-.46, r-.23, 'none', mix(metal,'#ffffff',.65), .10)
    rect(pad+edge, pad+edge, w-2*edge, h-2*edge, max(0,r-edge), 'url(#glass)', '#26282b' if not white_glass else '#b8b8b7', .09)
    if notch:
        # Narrow display surround remains opaque even at the rounded corners.
        rect(x-.25, y-.25, sw+.5, sh+.5, screen_r+.25, '#020203')
        svg.append(f'<path d="{notch_path}" fill="#08090b"/>')
        rect(cx-4.4, pad+.82, 8.8, .45, .22, '#222429', '#050507', .08)
        for i in range(22):
            circle(cx-4.1+i*.39, pad+1.04, .045, '#45474a')
        circle(cx+7.7, y+2.55, 1.12, '#030405')
        circle(cx+7.7, y+2.55, .82, 'url(#lens)')
        circle(cx+7.46, y+2.3, .19, '#243d56')
        circle(cx-6.5, y+2.65, .75, '#101015')
        rect(cx-2.6,y+2.05,3.7,1.1,.5,'#101115')
    else:
        rect(x-.17, y-.17, sw+.34, sh+.34, .12, '#232629' if not white_glass else '#b4b6b7')
        if is_ipad:
            camera_y = pad+(y-pad)*.51
            if features.get('camera'):
                circle(cx,camera_y,1.12,'#28292c' if white_glass else '#16181b')
                circle(cx,camera_y,.77,'url(#lens)')
                circle(cx-.23,camera_y-.22,.18,'#31465d')
            if features.get('ambientSensor'): circle(cx+5.5,camera_y,.36,'#353639')
        else:
            speaker_y = y-9.2 if style == 'iphone-rounded' else pad+9.0
            rect(cx-5.8,speaker_y,11.6,1.4,.65,'#393b3c' if chrome else '#323336')
            rect(cx-5.5,speaker_y+.22,11,1.0,.45,'#111315')
            for i in range(26):
                circle(cx-5.1+i*.4,speaker_y+.53,.065,'#57595c')
                circle(cx-5.1+i*.4,speaker_y+.91,.065,'#454749')
            if features.get('camera'):
                cam_x = cx-12.9
                circle(cam_x,speaker_y+.7,1.3,'#242629' if white_glass else '#17191d')
                circle(cam_x,speaker_y+.7,.93,'url(#lens)')
                circle(cam_x-.25,speaker_y+.43,.2,'#263e55')
                rect(cx-2.9,speaker_y-4.1,5.8,1.45,.7,'#282a2e' if white_glass else '#141519')
    if features.get('home'):
        home_y = y+sh+(pad+h-y-sh)*.52
        radius = 5.25 if is_ipad else 5.4
        touch = features['home']=='touch-id'
        circle(cx,home_y,radius,'url(#metal)' if touch else '#bebfbe' if white_glass else '#050607')
        circle(cx,home_y,radius-(.34 if touch else .16),face,'#d4d3d0' if white_glass else '#25272a',.09)
        if not touch:
            # Older Home buttons carry a rounded square; Touch ID does not.
            rect(cx-1.65,home_y-1.65,3.3,3.3,.65,'none','#8f9294' if not white_glass else '#a5a6a6',.22)
    if flat:
        antenna_y = [pad+12,pad+h-12] if features['antenna']=='cdma' else [pad+h-12]
        for ay in antenna_y:
            rect(pad-.015,ay,.65,.65,0,'#393a3c')
            rect(pad+w-.63,ay,.65,.65,0,'#393a3c')
        if features['antenna']=='gsm': rect(pad+16,pad-.015,.65,.65,0,'#393a3c')
    elif style in ('iphone-rounded','iphone-notch'):
        for ay in (pad+13,pad+h-13):
            for ax in (pad,pad+w-edge):
                rect(ax,ay,edge,.65,0,mix(metal,'#ffffff',.5))
    if features.get('smartConnector'):
        for offset in (-2.5,0,2.5): circle(pad+.27,pad+h/2+offset,.18,mix(metal,'#000000',.5))
    svg.extend(['</g>','</svg>'])
    return '\n'.join(svg)+'\n'


def render_frames(existing=()):
    manifest = json.loads((ROOT/'config/device-frame-drawings.json').read_text(encoding='utf-8'))
    products = json.loads((ROOT/'config/apple-display-catalog.json').read_text(encoding='utf-8'))['products']
    covered = {i for frame in existing for i in frame['productTypes']}
    catalog_path = ROOT/'config/apple-device-frames.json'
    previous = json.loads(catalog_path.read_text(encoding='utf-8'))['frames'] if catalog_path.exists() else []
    cached = {c['assetId']:c for f in previous if f.get('artworkKind')=='self-drawn' for c in f['colors']}
    ASSETS.mkdir(parents=True, exist_ok=True)
    VECTORS.mkdir(parents=True, exist_ok=True)
    frames = []
    for model in manifest['models']:
        assert not covered.intersection(model['productTypes']), 'Drawing overrides existing model artwork'
        product = next(p for p in products if model['productTypes'][0] in p['identifiers'])
        assert set(model['productTypes']).issubset(product['identifiers']), 'Incorrect drawing ProductType'
        assert model['panelPixels'] == dict(width=product['width'],height=product['height']), 'Drawing panel differs from official catalog'
        assert model['evidence']['specSha256'] == product['appleSpecSha256'], 'Drawing specification provenance changed'
        colors = []
        for index, finish in enumerate(model['finishes']):
            asset = model['id'] if index == 0 else model['id']+'-'+finish
            svg_path = VECTORS/(asset+'.svg')
            svg_path.write_text(draw(model,finish),encoding='utf-8',newline='\n')
            svg_hash = hashlib.sha256(svg_path.read_bytes()).hexdigest()
            path = ASSETS/(asset+'.png')
            prior = cached.get(asset,{})
            if path.exists() and prior.get('svgSha256') == svg_hash and hashlib.sha256(path.read_bytes()).hexdigest() == prior.get('sha256'):
                png = path.read_bytes()
            else:
                png = resvg_py.svg_to_bytes(svg_path=str(svg_path),skip_system_fonts=True)
                path.write_bytes(png)
            im = Image.open(path).convert('RGBA')
            alpha = np.asarray(im.getchannel('A'))
            regions,_ = label(alpha<255)
            center = regions[im.height//2,im.width//2]
            assert center != 0, 'Drawn screen is opaque'
            ys,xs = find_objects(regions)[center-1]
            assert 0<xs.start<xs.stop<im.width and 0<ys.start<ys.stop<im.height, 'Drawing screen opens to exterior'
            screen_w,screen_h = xs.stop-xs.start,ys.stop-ys.start
            assert abs(screen_w/screen_h-product['width']/product['height'])<.004, 'Drawing screen aspect is incorrect'
            Image.fromarray(np.where(regions==center,255-alpha,0).astype('uint8')).save(ASSETS/(asset+'-screen.png'))
            geom = dict(frameWidth=im.width,frameHeight=im.height,screenX=xs.start,screenY=ys.start,screenWidth=screen_w,screenHeight=screen_h)
            colors.append(dict(id=finish,assetId=asset,tokens=list(dict.fromkeys([finish,finish.replace('-',' ')])),
                source=svg_path.relative_to(ROOT).as_posix(),svgSha256=svg_hash,
                sha256=hashlib.sha256(png).hexdigest(),artworkKind='self-drawn',**geom))
        first = colors[0]
        frame = dict(id=model['id'],name=model['name'],productTypes=model['productTypes'],
            **{key:first[key] for key in ['frameWidth','frameHeight','screenX','screenY','screenWidth','screenHeight','source','sha256']},
            defaultColorId=first['id'],colors=colors,artworkKind='self-drawn',
            geometryAccuracy=manifest['precision'],bodyMm=model['bodyMm'],panelPixels=model['panelPixels'],ppi=model['ppi'],evidence=model['evidence'])
        frames.append(frame)
        covered.update(model['productTypes'])
        print(f'Drew {model["name"]} ({model["id"]}): {len(colors)} finishes',flush=True)
    return frames


if __name__ == '__main__':
    # A direct invocation must update hashes/geometry along with the PNGs.
    # Leaving the old embedded catalog would make a subsequent build inconsistent.
    from import_device_frames import refresh_drawings
    refresh_drawings()
