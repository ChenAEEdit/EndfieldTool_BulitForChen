"""Find accessory entry points through mesh bundle reverse references, without attaching candidates."""
from pathlib import Path
import re

from extract_plan_bundles import require, extract_one, valid_existing
from index_game_resources import normalize_game, digest, read_indexes, read_manifest, assert_unchanged, emit


def add_prop_reference_inputs(plan, bundle_result, inventory, run, inputs, copied):
    role = plan['characterId']
    require(re.fullmatch(r'[a-z0-9_]+', role), '角色标识无效。')
    # This is a discovery hint only; an accessory's role ownership/mount is still unproven.
    seeds = sorted({Path(o['sourceBundle']).name.casefold() for o in inventory['objects']
                    if o['kind'] == 'Mesh' and re.search(r'(?:widget|item|prop)', o['name'], re.I)
                    and re.search(r'(?:^|_)' + re.escape(role) + r'(?:_|$)', o['name'], re.I)})
    summary = {'schema': 'endfield-prop-reference-discovery-v1', 'associationEstablished': False,
               'seeds': [], 'addedFiles': [], 'skipped': []}
    if not seeds:
        return summary
    game = normalize_game(Path(plan['options']['gameDirectory']))
    require(digest(game.parent / 'GameAssembly.dll') == plan['gameAssemblySha256'].upper(), '调查附件时游戏版本已变化。')
    metadata = run / 'prop-reference-metadata'; metadata.mkdir()
    vfs = game / 'StreamingAssets/VFS'
    records, signatures, _ = read_indexes(vfs, metadata)
    manifest, md5, _ = read_manifest(records, vfs, metadata)
    require(md5 == bundle_result['manifestMd5'], '附件调查清单与当前任务提取记录不同。')
    by_basename = {}
    for b in manifest.bundles:
        by_basename.setdefault(Path(b.name).name.casefold(), []).append(b)
    available = {}
    for row in records:
        available.setdefault(row['name'].replace('\\', '/').casefold().split('/windows/', 1)[-1], []).append(row)
    selected = set()
    for name in seeds:
        matches = by_basename.get(name, [])
        require(len(matches) == 1, '附件网格清单入口缺失或不唯一：' + name)
        seed = matches[0]
        if len(seed.direct_reverse_dependencies) > 32:
            summary['skipped'].append({'meshBundle': seed.name, 'reason': 'reverse-reference-limit'})
            continue
        closure, pending = set(), list(seed.direct_reverse_dependencies)
        while pending:
            i = pending.pop()
            if i in closure:
                continue
            closure.add(i)
            pending.extend(manifest.bundles[i].direct_dependencies)
        new = {i for i in closure | selected if Path(manifest.bundles[i].name).name.casefold() not in copied}
        if len(new) > 128:
            summary['skipped'].append({'meshBundle': seed.name, 'reason': 'new-dependency-limit', 'count': len(new)})
            continue
        selected.update(closure)
        summary['seeds'].append({'meshBundle': seed.name, 'directReferences': [manifest.bundles[i].name for i in seed.direct_reverse_dependencies]})
    rows = []
    for i in sorted(selected):
        matches = available.get(manifest.bundles[i].name.casefold(), [])
        require(len(matches) == 1, '附件依赖缺失或不唯一：' + manifest.bundles[i].name)
        rows.append(matches[0])
    require(sum(r['length'] for r in rows if Path(r['name']).name.casefold() not in copied) <= 512 * 1024**2,
            '新增附件依赖超过 512 MiB，需要缩小入口。')
    for row in rows:
        name = row['name'].replace('\\', '/').rsplit('/', 1)[-1]
        require(re.fullmatch(r'[a-zA-Z0-9][a-zA-Z0-9_.-]{0,160}\.ab', name) and '..' not in name, '附件包文件名无效。')
        if name.casefold() in copied:
            require(copied[name.casefold()]['md5'].upper() == row['data_md5'].upper(), '同名附件包内容不符。')
            continue
        emit('沿网格实际反向引用补充附件候选：' + name, 41)
        target = inputs / name
        extract_one(vfs / row['group'] / (row['chunk_md5'] + '.chk'), row, target, lambda _: None)
        require(valid_existing(target, row), '附件候选包 MD5 不符。')
        copied[name.casefold()] = {'path': str(target), 'md5': row['data_md5'], 'length': row['length']}
        summary['addedFiles'].append({'path': str(target), 'virtualPath': row['name'], 'md5': row['data_md5'], 'length': row['length']})
    assert_unchanged(vfs, signatures, game, plan['gameAssemblySha256'].upper())
    return summary
