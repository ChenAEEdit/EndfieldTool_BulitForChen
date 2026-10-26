"""Bounded source-rig discovery from the same verified game manifest as the task."""
from pathlib import Path
import re
import zlib

from extract_plan_bundles import require, extract_one, valid_existing
from index_game_resources import normalize_game, digest, read_indexes, read_manifest, assert_unchanged, emit


def add_source_rig_inputs(plan, bundle_result, inventory, run, inputs, copied, read):
    known = {0}
    for row in inventory['objects']:
        if row.get('rigEvidencePath'):
            for node in read(row['rigEvidencePath'])['nodes']:
                known.add(zlib.crc32(node['path'].encode('utf-8')) if node['path'] else 0)
        if row.get('avatarTosPath'):
            known.update(map(int, read(row['avatarTosPath'])['avatar']['m_TOS']))
    missing = {}
    for row in inventory['objects']:
        unresolved = set(row.get('bindingPathHashes', [])) - known
        if unresolved:
            missing.setdefault(Path(row['sourceBundle']).name.casefold(), set()).update(unresolved)
    summary = {'schema': 'endfield-source-rig-discovery-v1', 'addedFiles': [], 'seeds': [], 'skipped': []}
    if not missing:
        return summary
    game = normalize_game(Path(plan['options']['gameDirectory']))
    require(digest(game.parent / 'GameAssembly.dll') == plan['gameAssemblySha256'].upper(), '源骨架调查时游戏版本已变化。')
    metadata = run / 'source-rig-metadata'
    metadata.mkdir()
    vfs = game / 'StreamingAssets/VFS'
    records, signatures, _ = read_indexes(vfs, metadata)
    manifest, manifest_md5, _ = read_manifest(records, vfs, metadata)
    require(manifest_md5 == bundle_result['manifestMd5'], '源骨架调查的资源清单与原提取不一致。')
    by_path = {}
    for row in records:
        name = row['name'].replace('\\', '/').casefold().split('/windows/', 1)[-1]
        by_path.setdefault(name, []).append(row)
    # Only exact seed identities already present in the verified task snapshot.
    names = {Path(row['path']).name.casefold(): row['virtualPath'].replace('\\', '/').casefold().split('/windows/', 1)[-1]
             for row in bundle_result['files']}
    bundles = {b.name.casefold(): b for b in manifest.bundles}
    selected = set()
    for basename in sorted(missing):
        seed = bundles.get(names.get(basename, ''))
        if seed is None:
            summary['skipped'].append({'bundle': basename, 'reason': 'not-an-exact-task-manifest-seed'})
            continue
        if len(seed.direct_reverse_dependencies) > 16:
            summary['skipped'].append({'bundle': seed.name, 'reason': 'reverse-reference-limit', 'count': len(seed.direct_reverse_dependencies)})
            continue
        closure, pending = set(), list(seed.direct_reverse_dependencies)
        while pending:
            index = pending.pop()
            if index in closure:
                continue
            closure.add(index)
            pending.extend(manifest.bundles[index].direct_dependencies)
        if len(closure | selected) > 128:
            summary['skipped'].append({'bundle': seed.name, 'reason': 'dependency-closure-limit', 'count': len(closure)})
            continue
        selected.update(closure)
        summary['seeds'].append({'bundle': seed.name, 'unknownHashes': sorted(missing[basename]),
                                 'directReferences': [manifest.bundles[i].name for i in seed.direct_reverse_dependencies]})
    rows = []
    for index in sorted(selected):
        name = manifest.bundles[index].name.casefold()
        matches = by_path.get(name, [])
        require(len(matches) == 1, '源骨架依赖缺失或不唯一：' + name)
        rows.append(matches[0])
    if sum(r['length'] for r in rows) > 512 * 1024 * 1024:
        summary['skipped'].append({'reason': 'source-rig-byte-limit', 'limit': 512 * 1024 * 1024})
        return summary
    for row in rows:
        name = row['name'].replace('\\', '/').rsplit('/', 1)[-1]
        require(re.fullmatch(r'[a-zA-Z0-9][a-zA-Z0-9_.-]{0,160}\.ab', name) and '..' not in name, '源骨架包文件名无效。')
        if name.casefold() in copied:
            require(copied[name.casefold()]['md5'].upper() == row['data_md5'].upper(), '源骨架候选与当前快照存在同名不同内容的包。')
            continue
        target = inputs / name
        emit('补充源骨架证据：' + name, 36)
        chunk = vfs / row['group'] / (row['chunk_md5'] + '.chk')
        extract_one(chunk, row, target, lambda _: None)
        require(valid_existing(target, row), '源骨架补充包 MD5 不符。')
        copied[name.casefold()] = {'path': str(target), 'md5': row['data_md5'], 'length': row['length']}
        summary['addedFiles'].append({'path': str(target), 'virtualPath': row['name'], 'group': row['group'], 'md5': row['data_md5'], 'length': row['length']})
    assert_unchanged(vfs, signatures, game, plan['gameAssemblySha256'].upper())
    return summary
