"""Composite preview of the ballistic rack with a body standing in it (not shipped in the mod).

Usage:
    python state/preview_rack_assembled.py [body]     # body = Minuteman | V1 | Moskit | Grison

Simulates what DMSE.CompMissileBodyRenderer draws on top of DMSE_Building_BallisticRack:
the rack graphic is drawn at drawSize 2.3 and the body graphic at drawSize 2.2, both on the
same 512x512 canvas that represents the 2x2 building tile footprint.
"""
from __future__ import annotations

import os
import sys

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RACK = os.path.join(ROOT, '1.7', 'Textures', 'Things', 'Building', 'BallisticRack', 'building.png')

BODIES = {
    'Minuteman': 'Minuteman/skyFaller_incoming.png',
    'V1': 'V1/skyFaller_incoming.png',
    'V2': 'V2/skyFaller_incoming.png',
    'Moskit': 'Moskit/skyFaller_incoming.png',
    'Grison': 'Grison/skyFaller_incoming.png',
}

NAME = sys.argv[1] if len(sys.argv) > 1 else 'Minuteman'
body_path = os.path.join(ROOT, '1.7', 'Textures', 'Things', 'Missile', BODIES[NAME])

CENTER = 512  # canvas representing the 2x2 footprint
RACK_SCALE = 2.3
BODY_SCALE = 2.2

rack = Image.open(RACK).convert('RGBA')
rack_draw = int(CENTER * RACK_SCALE / 2.0)          # 2.3 tiles in a 2-tile canvas
body_draw = int(CENTER * BODY_SCALE / 2.0)

canvas = Image.new('RGBA', (CENTER * 2, CENTER * 2), (58, 66, 60, 255))  # grass-ish backdrop

rack_img = rack.resize((rack_draw, rack_draw), Image.LANCZOS)
body_img = Image.open(body_path).convert('RGBA').resize((body_draw, body_draw), Image.LANCZOS)

ox = (canvas.width - rack_draw) // 2
oy = (canvas.height - rack_draw) // 2
canvas.alpha_composite(rack_img, (ox, oy))

bx = (canvas.width - body_draw) // 2
by = (canvas.height - body_draw) // 2
canvas.alpha_composite(body_img, (bx, by))

out = os.path.join(ROOT, 'state', f'preview_rack_{NAME}.png')
canvas.save(out, 'PNG')
print('wrote', os.path.relpath(out, ROOT))
