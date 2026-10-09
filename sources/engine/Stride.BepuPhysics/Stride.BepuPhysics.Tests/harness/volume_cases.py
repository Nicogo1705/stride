"""Lays out the case shots: one sheet per subject, a row per case, columns from above, then OpacityThickness off, on, and with SeparateVolumeSegments."""
import os, sys
from PIL import Image, ImageDraw, ImageFont
d = sys.argv[1]
try:
    font = ImageFont.truetype('arial.ttf', 16)
    title = ImageFont.truetype('arialbd.ttf', 20)
except OSError:
    font = title = ImageFont.load_default()
columns = [('stage', 0, 'From above (cone = camera)'), ('nostage', 1, 'OpacityThickness = 0 (alpha blend)'),
           ('stage', 1, 'OpacityThickness > 0'), ('segments', 1, 'OpacityThickness > 0, SeparateVolumeSegments')]
subjects = {'A': 'Subject: OpacityThickness, Medium = Absorbing (wedge 5 cm to 1 m)',
            'S': 'Subject: OpacityThickness, Medium = Scattering (wedge 5 cm to 1 m)',
            'W': 'Subject: OpacityThickness, Medium = Scattering (box sunk into the ground, ends at the pit floor)',
            'G': 'Subject: OpacityThickness, Medium = Scattering, Density = compute node (box through a wall and the ground)',
            'X': 'Shadow maps'}
others = {'opaque': 'opaque material', 'alpha': 'MaterialTransparencyBlendFeature (OpacityThickness = 0)',
          'additive': 'MaterialTransparencyAdditiveFeature', 'glass': 'MaterialTransparencyGlassFeature',
          'absorbing': 'OpacityThickness, Medium = Absorbing', 'scattering': 'OpacityThickness, Medium = Scattering'}

def row_label(name):
    rest = name[2:]
    for key, text in others.items():
        for where in ('front', 'behind'):
            if rest == f'{key}-{where}':
                return f'+ {text}, {"in front" if where == "front" else "behind"}'
            if rest == f'camera-inside-{key}-{where}':
                return f'camera inside + {text}, {"in front" if where == "front" else "behind"}'
    return {'alone': 'alone', 'alone-orthographic': 'alone, orthographic camera', 'camera-inside': 'camera inside',
            'noise': 'Density node on a dark wall', 'shadows': 'Absorbing and Scattering casting shadows'}.get(rest, rest)

names = sorted({f.rsplit('-', 2)[0] for f in os.listdir(os.path.join(d, 'stage')) if f.endswith('-0-top.png')})
for kind in 'ASWGX':
    rows = [n for n in names if n.startswith(kind + '-')]
    rows.sort(key=lambda n: (n != f'{kind}-alone', n))
    w, h, top = 384, 240, 24
    head = 64
    sheet = Image.new('RGB', (len(columns) * w, head + len(rows) * (h + top)), 'white')
    g = ImageDraw.Draw(sheet)
    g.text((6, 6), subjects[kind], fill='black', font=title)
    for c, (_, _, label) in enumerate(columns):
        g.text((c * w + 6, 38), label, fill='black', font=font)
    for r, n in enumerate(rows):
        y = head + r * (h + top)
        g.text((6, y + 4), row_label(n), fill='black', font=font)
        for c, (sub, idx, _) in enumerate(columns):
            p = os.path.join(d, sub, f'{n}-{idx}-{"top" if idx == 0 else "view"}.png')
            if os.path.exists(p):
                sheet.paste(Image.open(p).convert('RGB').resize((w, h)), (c * w, y + top))
    sheet.save(os.path.join(d, f'cases-{kind}.png'))
