from pathlib import Path
from PIL import Image, ImageDraw
root=Path.cwd()
a=root/'src/Assets'
# Two interlocking bands form an angular S; same geometry for vector and icon.
polys=[([(43,17),(25,17),(15,27),(15,34),(33,34),(41,26),(24,26),(27,23),(43,23)],'#83BDAA'), ([(21,47),(39,47),(49,37),(49,30),(31,30),(23,38),(40,38),(37,41),(21,41)],'#478D7D')]
svg='<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><rect x="1" y="1" width="62" height="62" rx="16" fill="#172522" stroke="#35534A"/>'
for points,color in polys: svg+='<polygon points="'+ ' '.join(f'{x},{y}' for x,y in points)+'" fill="'+color+'"/>'
svg+='</svg>'
(a/'Switchcraft.svg').write_text(svg)
im=Image.new('RGBA',(1024,1024)); d=ImageDraw.Draw(im)
d.rounded_rectangle((16,16,1008,1008),radius=256,fill='#172522',outline='#35534A',width=16)
for points,color in polys: d.polygon([(x*16,y*16) for x,y in points],fill=color)
im.resize((256,256),Image.Resampling.LANCZOS).save(a/'Switchcraft.png')
im.save(a/'Switchcraft.ico',sizes=[(s,s) for s in [16,20,24,32,40,48,64,128,256]])

