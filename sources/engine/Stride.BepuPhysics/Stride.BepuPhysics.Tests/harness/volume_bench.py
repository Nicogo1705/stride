"""Measured bench of opacity by thickness: compares sample points of every tile with the analytic colour.
usage: python volume_bench.py <dir> <variant>   -> <variant>-report.md, <variant>-samples.png

Ground truth along each sample's ray, composited from the background to the camera:
  absorbing medium, optical depth tau:  dst *= (1 - alpha (1 - tint))^(tau / -ln(1 - alpha))   (Beer-Lambert per channel)
  scattering medium, segment length L:  dst = lerp(dst, C, 1 - exp(-sigma L))
  plain pane:                           dst = lerp(dst, C, alpha)
  water (W) is a closed slab from its surface to behind the background, so it ends at the background; wedges displace it, panes sit in it.
  sigma = -ln(1 - alpha) / thickness. Lit colours C and the background are measured on opaque reference tiles.
Rays are intersected with the exact geometry the test wrote; the camera is at the origin looking down -z.
"""
import json, math, os, sys
from PIL import Image, ImageDraw

d, variant = sys.argv[1], sys.argv[2]
cfg = json.load(open(os.path.join(d, variant + '.json')))
img = Image.open(os.path.join(d, variant + '.png')).convert('RGB')
W, H = cfg['width'], cfg['height']
tanX, tanY = cfg['tan']
SAMPLES = [-0.8, -0.4, 0.0, 0.4, 0.8]  # across the tile, in fractions of the front face's half size

def s2l(c):
    c /= 255
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4

def l2s(c):
    c = min(max(c, 0), 1)
    return 255 * (c * 12.92 if c <= 0.0031308 else 1.055 * c ** (1 / 2.4) - 0.055)

dot = lambda a, b: sum(x * y for x, y in zip(a, b))
add = lambda a, b: [x + y for x, y in zip(a, b)]
mul = lambda a, s: [x * s for x in a]
norm = lambda a: mul(a, 1 / math.sqrt(dot(a, a)))

def to_pixel(p):
    return int(round((p[0] / -p[2] / tanX + 1) / 2 * W)), int(round((1 - p[1] / -p[2] / tanY) / 2 * H))

def from_pixel(x, y):
    return norm([((x + 0.5) / W * 2 - 1) * tanX, (1 - (y + 0.5) / H * 2) * tanY, -1])

def measure(x0, y0, r=2, image=None):
    image = image or img
    chans = [[], [], []]
    for y in range(y0 - r, y0 + r + 1):
        for x in range(x0 - r, x0 + r + 1):
            for i, c in enumerate(image.getpixel((x, y))):
                chans[i].append(c)
    return [sorted(c)[len(c) // 2] for c in chans]

def local(layer, ray):
    """Ray (from the origin, unit direction) in the layer's frame: origin and direction."""
    o = mul(layer['origin'], -1)
    axes = layer['x_axis'], layer['y_axis'], layer['z_axis']
    return [dot(o, a) for a in axes], [dot(ray, a) for a in axes]

def hit_plane(o, dv, a, b):
    """Distance to the plane z + a x + b = 0 in the local frame, and the local hit point."""
    s = -(o[2] + a * o[0] + b) / (dv[2] + a * dv[0])
    return s, add(o, mul(dv, s))

def surface_hit(layer, ray):
    o, dv = local(layer, ray)
    h, tilt = layer['h'], layer.get('tilt', 0)
    a = tilt / (2 * h)  # z = -tilt (x + h) / 2h  <=>  z + a x + a h = 0
    s, p = hit_plane(o, dv, a, a * h)
    inside = abs(p[0]) <= h and abs(p[1]) <= h
    return s, inside, (p[0] + h) / (2 * h)

def wedge_hits(layer, ray):
    o, dv = local(layer, ray)
    h, thin, thick = layer['h'], layer['thin'], layer['thick']
    s1, p1 = hit_plane(o, dv, 0, 0)
    a = (thick - thin) / (2 * h)  # back: z = -(thin + a (x + h))
    s2, p2 = hit_plane(o, dv, a, thin + a * h)
    ok = all(abs(c) <= h for c in (p1[0], p1[1], p2[0], p2[1]))
    return s1, s2, ok

kinds = cfg['kinds']
# The engine converts the tint to linear space before using it
for v in kinds.values():
    v['colour'] = [s2l(c * 255) for c in v['colour']]
sigma = {k: (-math.log(1 - v['alpha']) / v['thickness'] if v['thickness'] > 0 else 0) for k, v in kinds.items()}
inside_kind = cfg['inside']
model = 'physical' if cfg['stage'] else 'nostage'

tiles = {t['name']: t for t in cfg['tiles']}
def tile_centre(t):
    return int(round((t['ndc'][0] + 1) / 2 * W)), int(round((1 - t['ndc'][1]) / 2 * H))
lin = lambda srgb: [s2l(c) for c in srgb]
# Calibrate on a render with nothing around the camera: the 'stage' variant has the same reference tiles
calibration = Image.open(os.path.join(d, 'stage.png')).convert('RGB') if cfg['inside'] else img
background = lin(measure(*tile_centre(tiles['-']), r=4, image=calibration))
lit = {k: lin(measure(*tile_centre(tiles['O:' + k]), r=4, image=calibration)) for k in 'SWP'}
lit['U'] = lit['W']

def absorb(dst, k, tau):
    # Beer-Lambert per channel: each channel lets 1 - alpha (1 - tint) through per OpacityThickness
    a = kinds[k]['alpha']
    return [dst[i] * math.exp(tau * math.log(1 - a * (1 - kinds[k]['colour'][i])) / -math.log(1 - a)) for i in range(3)]

def scatter(dst, k, tau):
    o = 1 - math.exp(-tau)
    return [dst[i] * (1 - o) + lit[k][i] * o for i in range(3)]

def pane(dst):
    a = kinds['P']['alpha']
    return [dst[i] * (1 - a) + lit['P'][i] * a for i in range(3)]

def expected(t, ray):
    """Returns (colour, notes); notes flag samples whose geometry left the shape or whose media overlap."""
    s_bg = cfg['background_z'] / ray[2]
    ops, notes = [], []
    wedges, panes, water = [], [], None
    for l in t['layers']:
        k = l['kind']
        if k in 'AS':
            s1, s2, ok = wedge_hits(l, ray)
            if not ok: notes.append('edge')
            wedges.append((s1, s2, k))
        elif k == 'P':
            s, ok, _ = surface_hit(l, ray)
            if not ok: notes.append('edge')
            panes.append(s)
        else:
            s, ok, _ = surface_hit(l, ray)
            if not ok: notes.append('edge')
            water = (s, s_bg, k) if k == 'W' else (0.0, s, k)
    box = (0.0, cfg['inside_back_z'] / ray[2], inside_kind) if inside_kind else None

    if model == 'nostage':
        # Closed volumes blend with their alpha once, open surfaces still span to the scene
        items = [(s1, ('wedge', k, None)) for s1, s2, k in wedges] + [(s, ('pane',)) for s in panes]
        if water: items.append((water[0], ('wedge', 'W', None)))
        dst = list(background)
        for s, op in sorted(items, key=lambda x: -x[0]):
            if op[0] == 'wedge':
                k, a = op[1], kinds[op[1]]['alpha']
                dst = [dst[i] * ((1 - a) + a * kinds[k]['colour'][i]) for i in range(3)] if k == 'A' else [dst[i] * (1 - a) + lit[k][i] * a for i in range(3)]
            elif op[0] == 'pane':
                dst = pane(dst)
            else:
                dst = scatter(dst, op[1], sigma[op[1]] * op[2])
        return dst, notes

    # Physical: split the water around wedges and panes, then composite every piece back to front
    for s1, s2, k in wedges:
        ops.append((s1, s2, 'A' if k == 'A' else 'S', k))
    for s in panes:
        ops.append((s, s, 'P', 'P'))
    if water:
        cuts = sorted({water[0], water[1]} | {c for s1, s2, _ in wedges for c in (s1, s2) if water[0] < c < water[1]}
                      | {s for s in panes if water[0] < s < water[1]})
        for lo, hi in zip(cuts, cuts[1:]):
            mid = (lo + hi) / 2
            if not any(s1 <= mid <= s2 for s1, s2, _ in wedges):
                ops.append((lo, hi, 'water', water[2]))
    if box:
        ops.append((box[0], box[1], 'box', box[2]))
        if any(op[2] != 'box' and op[0] < box[1] for op in ops):
            notes.append('overlap')
    dst = list(background)
    for lo, hi, what, k in sorted(ops, key=lambda o: (-o[1], -o[0])):
        L = hi - lo
        if what == 'P':
            dst = pane(dst)
        elif k == 'A':
            dst = absorb(dst, k, sigma[k] * L)
        else:
            dst = scatter(dst, k, sigma[k] * L)
    return dst, notes

report, swatches = [], []
for t in cfg['tiles']:
    if t['name'] == '-' or t['name'].startswith('O:'):
        continue
    front = t['layers'][0]
    errs, cells, notes_all = [], [], set()
    for f in SAMPLES:
        # Sample point on the front layer's centre row, at fraction f of its half size
        p = add(front['origin'], mul(front['x_axis'], f * front['h']))
        x, y = to_pixel(p)
        ray = from_pixel(x, y)
        exp, notes = expected(t, ray)
        notes_all |= set(notes)
        got = measure(x, y)
        exp_s = [l2s(c) for c in exp]
        errs.append(max(abs(got[i] - exp_s[i]) for i in range(3)))
        cells.append((exp_s, got))
    worst = max(errs)
    verdict = 'overlap' if 'overlap' in notes_all else 'OK' if worst <= 3 else 'close' if worst <= 10 else 'WRONG'
    report.append(f"| `{t['name']}` | {' '.join(f'{e:.0f}' for e in errs)} | {worst:.0f} | {verdict}{' (edge)' if 'edge' in notes_all else ''} |")
    swatches.append((t['name'], cells, verdict))

with open(os.path.join(d, variant + '-report.md'), 'w', encoding='utf-8') as f:
    f.write(f"# {variant} ({model})\n\nCalibration (sRGB): background {tuple(round(l2s(c)) for c in background)}, "
            + ", ".join(f"lit {k} {tuple(round(l2s(c)) for c in lit[k])}" for k in 'SWP') + "\n\n")
    f.write("Error = max channel difference in sRGB levels (0-255), at 5 points across the tile (thin to thick wedge / shallow to deep water). OK ≤ 3, close ≤ 10.\n\n")
    f.write("| layers (front to back) | error per point | worst | verdict |\n|---|---|---|---|\n" + "\n".join(report) + "\n")

# Per tile: top row expected, bottom row measured, one square per sample point
cols, cw, ch = 4, 300, 64
sheet = Image.new('RGB', (cols * cw, ((len(swatches) + cols - 1) // cols) * ch), 'white')
g = ImageDraw.Draw(sheet)
for i, (name, cells, verdict) in enumerate(swatches):
    x, y = (i % cols) * cw, (i // cols) * ch
    for j, (e, m) in enumerate(cells):
        g.rectangle([x + 4 + j * 28, y + 4, x + 30 + j * 28, y + 30], fill=tuple(round(c) for c in e))
        g.rectangle([x + 4 + j * 28, y + 32, x + 30 + j * 28, y + 58], fill=tuple(m))
    g.text((x + 150, y + 12), name, fill='black')
    g.text((x + 150, y + 32), verdict, fill={'OK': 'green', 'close': 'darkorange', 'WRONG': 'red'}.get(verdict, 'gray'))
sheet.save(os.path.join(d, variant + '-samples.png'))

