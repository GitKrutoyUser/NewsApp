import gzip
import json
from PIL import Image, ImageDraw

W, H = 2048, 1024
water = (20, 43, 58, 255)
border = (184, 201, 174, 255)
image = Image.new('RGBA', (W, H), water)
draw = ImageDraw.Draw(image)
geo = json.load(open('Assets/countries.geojson', encoding='utf-8'))

def code_of(p):
    code = p.get('ISO_A2')
    if not code or code == '-99': code = p.get('ISO_A2_EH')
    return code or p.get('ADMIN', '')

def country_color(g, code):
    pts=[]
    def collect(v):
        if isinstance(v,list) and len(v)>=2 and isinstance(v[0],(int,float)): pts.append(v)
        elif isinstance(v,list):
            for item in v: collect(item)
    collect(g)
    lat=sum(p[1] for p in pts)/len(pts) if pts else 0
    c=code.upper()
    if c in {'AQ','ATA','GL','GRL'}: return (217,228,226,255)
    if lat > 58 or lat < -48: return (85,115,95,255)
    if c in {'SA','AE','EG','LY','DZ','MA','NA','BW','MN','AU'}: return (165,150,108,255)
    if 20 < lat < 42: return (120,144,100,255)
    return (99,129,96,255)

def xy(lon, lat): return ((lon+180)/360*W, (90-lat)/180*H)
def unwrap(ring):
    out=[]; prev=None
    for lon,lat in ring:
        lon=float(lon)
        if prev is not None:
            while lon-prev>180: lon-=360
            while lon-prev<-180: lon+=360
        out.append((lon,float(lat))); prev=lon
    return out

def wrapped_points(ring):
    p=unwrap(ring)
    return [[xy(lon+s,lat) for lon,lat in p] for s in (-360,0,360)]

def draw_ring(ring, fill=None, outline=None, width=1):
    for pts in wrapped_points(ring):
        draw.polygon(pts, fill=fill)
        if outline: draw.line(pts+[pts[0]], fill=outline, width=width, joint='curve')

for feature in geo['features']:
    props=feature['properties']; g=feature['geometry']; code=code_of(props); polys=[g['coordinates']] if g['type']=='Polygon' else g['coordinates']
    for rings in polys:
        if not rings: continue
        draw_ring(rings[0], fill=country_color(g,code), outline=border, width=2)
        for hole in rings[1:]: draw_ring(hole, fill=water, outline=border, width=2)

image.save('Assets/earth-countries.png', optimize=True)
raw=bytearray(image.tobytes())
for i in range(0,len(raw),4): raw[i],raw[i+2]=raw[i+2],raw[i]
with gzip.open('Assets/earth-countries.bgra.gz','wb',compresslevel=9) as f: f.write(raw)
print('saved',image.size)
