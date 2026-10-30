"""Immutable, workspace-local exports keyed by actual source revision, never by role name."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import uuid
from contextlib import contextmanager

from extract_plan_bundles import require
from index_game_resources import digest, within, write_json, emit


def fingerprint(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=False).encode()).hexdigest().upper()


def model_revision(plan, bundle_result, inventory, root, avatar):
    def identity(row):
        source = Path(row['sourceBundle']).resolve()
        files = [f for f in inventory['inputFiles'] if Path(f['path']).resolve() == source]
        require(len(files) == 1, '模型来源不在已核对的解析快照中。')
        return [source.name.casefold(), files[0]['md5'].upper(), row['sourceAsset'], row['pathId']]
    return fingerprint({'schema': 'endfield-model-export-v1', 'exporter': '0.16.0',
                        'game': plan['gameAssemblySha256'], 'manifest': bundle_result['manifestMd5'],
                        'body': identity(root), 'avatar': identity(avatar) if avatar else None})


@contextmanager
def locked_cache(workspace, revision):
    base = Path(workspace).resolve() / 'models'
    base.mkdir(parents=True, exist_ok=True)
    lock = (base / (revision + '.lock')).open('a+b')
    try:
        if os.name == 'nt':
            import msvcrt
            if lock.seek(0, 2) == 0:
                lock.write(b'0'); lock.flush()
            lock.seek(0)
            try:
                msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
            except OSError as error:
                raise ValueError('同一模型正在导出，请稍后重试；已有结果保留。') from error
        yield base / revision
    finally:
        lock.close()


def checked_cache(cache):
    record = json.loads((cache / 'model-cache.json').read_text(encoding='utf-8-sig'))
    require(record.get('schema') == 'endfield-model-cache-v1' and record.get('revision') == cache.name, '模型缓存归属无效。')
    expected = set()
    for row in record['files']:
        path = (cache / 'files' / row['path']).resolve()
        require(within(path, (cache / 'files').resolve()) and path.is_file() and not path.is_symlink()
                and path.stat().st_size == row['bytes'] and digest(path) == row['sha256'], '模型缓存缺失或已修改；没有覆盖原缓存。')
        expected.add(path)
    require(expected == {p.resolve() for p in (cache / 'files').rglob('*') if p.is_file()}, '模型缓存出现额外文件。')
    return record


def restore(cache, output, report_path):
    if not cache.exists():
        return False
    record = checked_cache(cache)
    require(not output.exists(), '模型输出目录已存在。')
    shutil.copytree(cache / 'files', output)
    def relocate(value):
        if isinstance(value, str):
            return value.replace(record['originalOutput'], str(output))
        if isinstance(value, dict):
            return {k: relocate(v) for k, v in value.items()}
        if isinstance(value, list):
            return [relocate(v) for v in value]
        return value
    write_json(report_path, relocate(record['report']))
    emit('复用已核对的角色模型导出；本次只解码所选动作。', 40)
    return True


def publish(cache, output, report):
    require(not cache.exists(), '模型缓存已存在，不能覆盖。')
    stage = cache.with_name(cache.name + '.pending-' + uuid.uuid4().hex)
    shutil.copytree(output, stage / 'files')
    rows = [{'path': p.relative_to(output).as_posix(), 'bytes': p.stat().st_size, 'sha256': digest(p)}
            for p in sorted(output.rglob('*')) if p.is_file()]
    write_json(stage / 'model-cache.json', {'schema': 'endfield-model-cache-v1', 'revision': cache.name,
        'originalOutput': str(output), 'files': rows, 'report': report})
    stage.rename(cache)
    checked_cache(cache)
