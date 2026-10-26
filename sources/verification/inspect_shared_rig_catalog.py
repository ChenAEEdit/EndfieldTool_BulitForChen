"""Create a read-only investigation catalog from exact cached manifest references."""
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
from endfield_manifest import EndfieldManifest
from index_formats import parse_blc

baseline, output = map(Path, sys.argv[1:])
output.mkdir(parents=True, exist_ok=True)
scan = json.loads((baseline / 'scan-result.json').read_text(encoding='utf-8-sig'))
metadata = Path(scan['catalogPath']).parents[2] / 'metadata'
manifest = EndfieldManifest.from_path(metadata / 'manifest.bin')
shared = manifest.find_bundles(['883c322914dd6cf6f62424d8.ab'])
if len(shared) != 1:
    raise ValueError('Shared clip Bundle identity is not unique')
seeds = list(shared[0].direct_reverse_dependencies)
if not seeds or len(seeds) > 64:
    raise ValueError('Investigation requires a bounded direct relationship')
selected, pending = set(), seeds[:]
while pending:
    index = pending.pop()
    if index in selected:
        continue
    selected.add(index)
    pending.extend(manifest.bundles[index].direct_dependencies)
by_name = {}
for file in metadata.glob('*.blc.bin'):
    rows = []
    parse_blc(file.read_bytes(), rows.append)
    for row in rows:
        name = row['name'].replace('\\', '/').lower().split('/windows/', 1)[-1]
        by_name.setdefault(name, []).append(dict(row, group=file.name.split('.')[0]))
references, ids = [], {}
for index in sorted(selected):
    bundle = manifest.bundles[index]
    rows = by_name.get(bundle.name.lower(), [])
    if len(rows) != 1:
        raise ValueError('Missing or ambiguous live reference: ' + bundle.name)
    row = rows[0]
    ids[index] = row['group'] + ':' + row['name']
    references.append(dict(id=ids[index], group=row['group'], md5=row['data_md5'], virtualPath=row['name'], dependencyIds=[]))
for index, row in zip(sorted(selected), references):
    row['dependencyIds'] = [ids[i] for i in manifest.bundles[index].direct_dependencies]
catalog = dict(schema='endfield-character-catalog-v1', gameBuild=scan['catalog']['gameBuild'],
    gameAssemblySha256=scan['catalog']['gameAssemblySha256'], sourceDescription='Exact reverse references of shared movement Bundle; source rig unconfirmed',
    bundles=references, characters=[dict(id='shared_rig_investigation', displayName='共享原骨架调查', resources=[dict(
        id='shared:source-rig-references', name='Shared animation direct reverse references', kind='Model', association='NameCandidate',
        sourcePath=shared[0].name, bundleIds=[ids[i] for i in seeds])])])
(output / 'source-rig-catalog.json').write_text(json.dumps(catalog, ensure_ascii=False, indent=2), encoding='utf-8')
print('Exact investigation seeds:', [manifest.bundles[i].name for i in seeds], 'closure:', len(references))
