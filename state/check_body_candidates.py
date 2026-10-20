"""List every DMSE.MissileBodyDef and the bodies a ballistic silo can select."""
import glob
import xml.etree.ElementTree as ET

rows = []
for path in glob.glob('1.7/Defs/**/*.xml', recursive=True):
    try:
        root = ET.parse(path).getroot()
    except Exception as exc:  # noqa: BLE001 - report and continue
        print('PARSE FAIL', path, exc)
        continue
    for node in root:
        if node.tag != 'DMSE.MissileBodyDef':
            continue
        rows.append((node.findtext('defName'), node.findtext('sizeClass'),
                     node.findtext('launchMode'), node.findtext('customizeLocation')))

print('{:<42} {:<10} {:<9} customize'.format('body', 'sizeClass', 'launch'))
for body, size, launch, custom in sorted(rows):
    print('{:<42} {:<10} {:<9} {}'.format(body, size, launch, custom))

silo = [r for r in rows if r[3] == 'Launcher' and r[1] == 'Ballistic'
        and r[2] in ('Vertical', 'Both')]
print()
print('silo candidates (Launcher + Ballistic + Vertical/Both):', len(silo))
for r in silo:
    print('  -', r[0])
