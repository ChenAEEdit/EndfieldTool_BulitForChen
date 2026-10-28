"""Inspect exact reverse references of selected mesh assets; never auto-attach name matches."""
import argparse
import json
from pathlib import Path
import re
import subprocess

import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
import index_game_resources as index
import extract_plan_bundles as extraction


def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def run(package, plan_path, inventory_path, output, pattern):
    index.ROOT = extraction.ROOT = package.resolve()
    plan, inventory = read(plan_path), read(inventory_path)
    extraction.require(inventory['taskId'] == plan['id'], 'Inventory is not part of this plan.')
    extraction.require(not output.exists(), 'Use a new evidence directory.')
    output.mkdir(parents=True)
    game = index.normalize_game(Path(plan['options']['gameDirectory']))
    extraction.require(index.digest(game.parent / 'GameAssembly.dll') == plan['gameAssemblySha256'], 'Game version changed.')
    metadata = output / 'metadata'; metadata.mkdir()
    vfs = game / 'StreamingAssets/VFS'
    records, signatures, _ = index.read_indexes(vfs, metadata)
    manifest, md5, _ = index.read_manifest(records, vfs, metadata)
    extraction.require(md5 == read(Path(plan_path).parent / 'result.json')['manifestMd5'], 'Manifest changed.')
    by_name = {}
    for bundle in manifest.bundles:
        by_name.setdefault(Path(bundle.name).name.casefold(), []).append(bundle)
    seeds = sorted({Path(o['sourceBundle']).name.casefold() for o in inventory['objects']
                    if o['kind'] == 'Mesh' and re.search(pattern, o['name'])})
    extraction.require(seeds, 'No selected source meshes.')
    selected, pending, evidence = set(), [], []
    for name in seeds:
        extraction.require(len(by_name.get(name, [])) == 1, 'Mesh bundle missing or ambiguous.')
        bundle = by_name[name][0]
        extraction.require(len(bundle.direct_reverse_dependencies) <= 32, 'Too many reverse references; narrow the query.')
        pending.extend(bundle.direct_reverse_dependencies)
        evidence.append({'meshBundle': bundle.name, 'directReferences': [manifest.bundles[i].name for i in bundle.direct_reverse_dependencies]})
    while pending:
        i = pending.pop()
        if i in selected:
            continue
        selected.add(i)
        pending.extend(manifest.bundles[i].direct_dependencies)
    extraction.require(len(selected) <= 128, 'Reference closure exceeds 128 bundles.')
    available = {}
    for record in records:
        available.setdefault(record['name'].replace('\\', '/').casefold().split('/windows/', 1)[-1], []).append(record)
    rows = []
    for i in sorted(selected):
        matches = available.get(manifest.bundles[i].name.casefold(), [])
        extraction.require(len(matches) == 1, 'Reference dependency missing or ambiguous.')
        rows.append(matches[0])
    extraction.require(sum(r['length'] for r in rows) <= 512 * 1024**2, 'Reference closure exceeds 512 MiB.')
    inputs = output / 'input'; inputs.mkdir()
    for row in rows:
        name = row['name'].replace('\\', '/').rsplit('/', 1)[-1]
        extraction.require(re.fullmatch(r'[a-zA-Z0-9][a-zA-Z0-9_.-]{0,160}\.ab', name) and '..' not in name, 'Unsafe bundle name.')
        target = inputs / name
        extraction.extract_one(vfs / row['group'] / (row['chunk_md5'] + '.chk'), row, target, lambda _: None)
        extraction.require(extraction.valid_existing(target, row), 'Extracted mesh reference failed MD5.')
    process = subprocess.run([str(package / 'runtime/object-probe/BundleObjectProbe.exe'), '--input', str(inputs), '--report', str(output / 'inventory.json')],
                             capture_output=True, text=True, encoding='utf-8', errors='replace', creationflags=subprocess.CREATE_NO_WINDOW)
    (output / 'probe.log').write_text(process.stdout + process.stderr, encoding='utf-8')
    extraction.require(process.returncode == 0, 'Reference object probe failed.')
    index.assert_unchanged(vfs, signatures, game, plan['gameAssemblySha256'])
    probed = read(output / 'inventory.json')
    summary = {'schema': 'endfield-prop-reference-investigation-v1', 'sourceInventory': str(inventory_path), 'meshPattern': pattern,
               'associationEstablished': False, 'bundleReferences': evidence, 'bundles': len(rows),
               'models': [o for o in probed['objects'] if o.get('isModelCandidate')], 'inventoryPath': str(output / 'inventory.json')}
    index.write_json(output / 'reference-summary.json', summary)
    print(json.dumps({'bundles': len(rows), 'models': [(o['key'], o['name']) for o in summary['models']]}, ensure_ascii=False))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--package', type=Path, required=True)
    parser.add_argument('--plan', type=Path, required=True)
    parser.add_argument('--inventory', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--mesh-pattern', required=True)
    args = parser.parse_args()
    run(args.package.resolve(), args.plan.resolve(), args.inventory.resolve(), args.output.resolve(), args.mesh_pattern)
