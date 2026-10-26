"""Read-only source/imported-prefab local direction comparison."""
import json
import math
from pathlib import Path
import re
import sys

root = Path(sys.argv[1])
config = json.loads((root / 'adaptation.json').read_text(encoding='utf-8-sig'))
rig = json.loads((root / config['clips'][0]['sourceRig']).read_text(encoding='utf-8-sig'))
text = (root / 'Generated/Character.prefab').read_text(encoding='utf-8-sig')
names, transforms = {}, {}
for kind, identity, block in re.findall(r'^--- !u!(\d+) &(-?\d+)\s*\n(.*?)(?=^--- !u!|\Z)', text, re.S | re.M):
    if kind == '1':
        names[identity] = re.search(r'^  m_Name: (.*)$', block, re.M)[1]
    elif kind == '4':
        go = re.search(r'm_GameObject: \{fileID: (-?\d+)\}', block)[1]
        position = [float(v) for v in re.search(r'm_LocalPosition: \{x: (.*?), y: (.*?), z: (.*?)\}', block).groups()]
        transforms[identity] = {'go': go, 'position': position}
targets = {names[t['go']]: t['position'] for t in transforms.values() if names[t['go']].startswith('Bip001_')}
nodes = {n['path']: n for n in rig['nodes']}
rows = []
for path, node in nodes.items():
    name = path.rsplit('/', 1)[-1]
    if name not in targets or not any(v in name for v in ('Spine', 'Neck', 'Clavicle', 'UpperArm', 'Thigh')):
        continue
    a = list(node['serializedPosition'].values())
    b = targets[name]
    length = math.sqrt(sum(v*v for v in a) * sum(v*v for v in b))
    if length < 1e-10:
        continue
    angle = math.degrees(math.acos(max(-1, min(1, sum(x*y for x, y in zip(a, b)) / length))))
    rows.append({'bone': name, 'source': a, 'target': b, 'localDirectionAngle': angle})
print(json.dumps({'sourceRig': rig['name'], 'directions': rows}, ensure_ascii=False, indent=2))
