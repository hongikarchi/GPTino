# Phase 1b closure: append analytic torsion (It) and warping (Iw) columns to both
# shipped section catalogs. Doubly-symmetric I formulas: It = sum(b*t^3)/3 (fillet-less,
# catalog values run a few % higher), Iw = Iy_weak * (H - tf)^2 / 4.
import json

def add_torsion(path, get):
    d = json.load(open(path, encoding='utf-8'))
    for s in d['sections']:
        H, B, tw, tf, Iy = get(s)
        J = ((2 * B * tf ** 3) + ((H - 2 * tf) * tw ** 3)) / 3.0 / 1e4   # cm4
        Iw = Iy * ((H - tf) / 10.0) ** 2 / 4.0                            # cm6
        s['It_cm4'] = round(J, 1)
        s['Iw_cm6'] = round(Iw)
    d['meta']['torsion_note'] = (
        'It = sum(b*t^3)/3, Iw = Iy_weak*(H-tf)^2/4 (doubly-symmetric I, fillet-less '
        'analytic; catalog It runs a few % higher from fillets - treat LTB checks with '
        '+-10% caution until table-sourced values land)')
    json.dump(d, open(path, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
    print(path, 'updated:', len(d['sections']), 'rows')

add_torsion('assets/data/structural/sections-ks.json',
            lambda s: (s['H'], s['B'], s['tw'], s['tf'], s['Iy']))
add_torsion('assets/data/structural/sections.json',
            lambda s: (s['h'], s['b'], s['tw'], s['tf'], s['Iz']))
