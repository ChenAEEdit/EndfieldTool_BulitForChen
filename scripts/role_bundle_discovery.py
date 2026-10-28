"""Bounded model-entry investigation; name hits are candidates, never associations."""
import bisect
import hashlib
import json
from pathlib import Path
import re
import shutil
import uuid

from extract_plan_bundles import require, extract_one, valid_existing
from index_game_resources import (normalize_game, digest, read_indexes, read_manifest,
                                  assert_unchanged, emit, write_json)


def add_role_model_inputs(plan, bundle_result, inventory, run, inputs, copied):
    role = plan['characterId']
    require(re.fullmatch(r'[a-z0-9_]+', role), '角色标识无效。')
    # Existing independent bodies require no additional byte search.
    if any(o.get('hierarchyRoot') and o.get('boneReferenceCount', 0) > 0
           and re.fullmatch(r'chr_[0-9]+_' + re.escape(role) + r'_(?:ui|post)?model', o['name'], re.I)
           for o in inventory['objects']):
        return {'schema': 'endfield-role-model-discovery-v1', 'reason': 'body-already-present', 'addedFiles': []}
    seeds = sorted(set(match for r in plan['resources'] for match in
                       re.findall(r'[0-9A-F]{8}/[0-9A-F]{32}\.chk', r.get('evidence', ''))))
    summary = {'schema': 'endfield-role-model-discovery-v1', 'role': role, 'chunks': [], 'addedFiles': [],
               'associationEstablished': False, 'reason': 'bounded-name-candidates-only'}
    if not seeds:
        summary['reason'] = 'no-indexed-chunk-entry-points'
        return summary
    game = normalize_game(Path(plan['options']['gameDirectory']))
    require(digest(game.parent / 'GameAssembly.dll') == plan['gameAssemblySha256'].upper(), '调查模型时游戏版本已变化。')
    vfs = game / 'StreamingAssets/VFS'
    if sum((vfs / seed).stat().st_size for seed in seeds) > 8 * 1024**3:
        summary['reason'] = 'indexed-chunk-byte-limit'
        return summary
    metadata = run / 'role-model-metadata'
    metadata.mkdir()
    records, signatures, _ = read_indexes(vfs, metadata)
    manifest, md5, _ = read_manifest(records, vfs, metadata)
    require(md5 == bundle_result['manifestMd5'], '模型调查清单与提取记录不一致。')
    by_path = {}
    for bundle in manifest.bundles:
        by_path.setdefault(bundle.name.casefold(), []).append(bundle)
    available = {}
    for row in records:
        available.setdefault(row['name'].replace('\\', '/').casefold().split('/windows/', 1)[-1], []).append(row)
    cache = Path(plan['options']['workspaceDirectory']) / 'index' / plan['gameAssemblySha256'] / 'role-name-investigation-v1'
    cache.mkdir(parents=True, exist_ok=True)
    matches = set()
    needle = role.encode('ascii').lower()
    for seed in seeds:
        path = vfs / seed
        before = path.stat()
        signature = hashlib.sha256(json.dumps([role, seed, before.st_size, before.st_mtime_ns,
            signatures[seed.split('/')[0]], md5]).encode()).hexdigest()
        cache_file = cache / (signature + '.json')
        rows = sorted((r for r in records if r['group'] + '/' + r['chunk_md5'] + '.chk' == seed
                       and r['name'].endswith('.ab') and not r['use_encrypt']), key=lambda r: r['offset'])
        starts = [r['offset'] for r in rows]
        if cache_file.is_file():
            require(cache_file.stat().st_size <= 2 * 1024**2, '角色调查缓存大小异常：' + str(cache_file))
            cached = json.loads(cache_file.read_text(encoding='utf-8'))
            hits = cached.get('hits')
            require(cached.get('signature') == signature and isinstance(hits, list) and all(isinstance(h, str) for h in hits),
                    '角色调查缓存损坏：' + str(cache_file))
            allowed = {r['name'].replace('\\', '/').casefold().split('/windows/', 1)[-1] for r in rows}
            require(set(hits) <= allowed, '角色调查缓存包含其他 CHK 的记录：' + str(cache_file))
            emit('复用角色模型调查缓存：' + seed, 39)
        else:
            emit('调查索引所在 CHK 的其他模型名称：' + seed, 39)
            found = set()
            consumed, previous = 0, b''
            with path.open('rb') as stream:
                while block := stream.read(4 * 1024**2):
                    data = previous + block
                    base = consumed - len(previous)
                    lower = data.lower()
                    offset = lower.find(needle)
                    while offset >= 0:
                        absolute = base + offset
                        index = bisect.bisect_right(starts, absolute) - 1
                        if index >= 0:
                            row = rows[index]
                            if absolute + len(needle) <= row['offset'] + row['length']:
                                found.add(row['name'].replace('\\', '/').casefold().split('/windows/', 1)[-1])
                        offset = lower.find(needle, offset + 1)
                    consumed += len(block)
                    previous = data[-max(1, len(needle) - 1):]
            after = path.stat()
            require((before.st_size, before.st_mtime_ns) == (after.st_size, after.st_mtime_ns), 'CHK 在模型调查期间发生变化。')
            hits = sorted(found)
            write_json(cache_file, {'signature': signature, 'hits': hits})
        matches.update(hits)
        summary['chunks'].append({'path': seed, 'signature': signature, 'candidateBundles': hits})
    require(len(matches) <= 512, '模型名称调查候选过多，需要缩小入口。')
    selected, pending = set(), list(matches)
    while pending:
        name = pending.pop()
        if name in selected:
            continue
        require(len(by_path.get(name, [])) == 1, '候选包不在当前资源清单中或不唯一：' + name)
        selected.add(name)
        pending.extend(manifest.bundles[i].name.casefold() for i in by_path[name][0].direct_dependencies)
    chosen = []
    for name in sorted(selected):
        found = available.get(name, [])
        require(len(found) == 1, '模型调查资源缺失或不唯一：' + name)
        chosen.append(found[0])
    require(sum(r['length'] for r in chosen) <= 2 * 1024**3, '模型调查候选依赖超过 2 GiB，需要缩小入口。')
    previous_inputs = sorted(p / 'input' for p in (run.parent).iterdir() if p.is_dir() and p != run and (p / 'inventory.json').is_file())
    for index, row in enumerate(chosen):
        name = row['name'].replace('\\', '/').rsplit('/', 1)[-1]
        require(re.fullmatch(r'[a-zA-Z0-9][a-zA-Z0-9_.-]{0,160}\.ab', name) and '..' not in name, '模型包文件名无效。')
        if name.casefold() in copied:
            require(copied[name.casefold()]['md5'].upper() == row['data_md5'].upper(), '同名模型包内容不一致。')
            continue
        target = inputs / name
        if index % 25 == 0:
            emit(f'核对模型调查候选依赖 {index + 1}/{len(chosen)}', 39)
        cached = next((p / name for p in previous_inputs if (p / name).is_file() and not (p / name).is_symlink() and valid_existing(p / name, row)), None)
        if cached:
            shutil.copy2(cached, target)
        elif not row['use_encrypt']:
            temporary = target.with_name(target.name + '.partial-' + uuid.uuid4().hex)
            try:
                with (vfs / row['group'] / (row['chunk_md5'] + '.chk')).open('rb') as source, temporary.open('wb') as destination:
                    source.seek(row['offset'])
                    remaining = row['length']
                    while remaining:
                        block = source.read(min(4 * 1024**2, remaining))
                        require(block, '模型调查候选包不完整。')
                        destination.write(block)
                        remaining -= len(block)
                require(valid_existing(temporary, row), '模型调查候选包 MD5 不符。')
                temporary.replace(target)
            finally:
                temporary.unlink(missing_ok=True)
        else:
            extract_one(vfs / row['group'] / (row['chunk_md5'] + '.chk'), row, target, lambda _: None)
        require(valid_existing(target, row), '模型调查候选包 MD5 不符。')
        copied[name.casefold()] = {'path': str(target), 'md5': row['data_md5'], 'length': row['length']}
        summary['addedFiles'].append({'path': str(target), 'virtualPath': row['name'], 'md5': row['data_md5'], 'length': row['length']})
    assert_unchanged(vfs, signatures, game, plan['gameAssemblySha256'].upper())
    return summary
