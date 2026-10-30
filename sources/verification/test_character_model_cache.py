import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
from character_model_cache import model_revision, publish, restore


class CacheTests(unittest.TestCase):
    def setUp(self):
        base = Path(__file__).resolve().parents[3] / 'outputs/model-cache-tests'; base.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=base); self.root = Path(self.temp.name); self.base = base
        self.plan = {'gameAssemblySha256': 'A' * 64}
        self.bundle = {'manifestMd5': 'B' * 32}
        self.row = {'sourceBundle': str(self.root / 'one/model.ab'), 'sourceAsset': 'CAB-model', 'pathId': '42'}
        self.inventory = {'inputFiles': [{'path': self.row['sourceBundle'], 'md5': 'C' * 32}]}
    def tearDown(self):
        self.root.resolve().relative_to(self.base.resolve()); self.temp.cleanup()
    def revision(self): return model_revision(self.plan, self.bundle, self.inventory, self.row, None)
    def test_cross_task_identity_does_not_use_snapshot_path_or_animation_selection(self):
        first = self.revision(); self.row['sourceBundle'] = str(self.root / 'two/model.ab'); self.inventory['inputFiles'][0]['path'] = self.row['sourceBundle']
        self.assertEqual(first, self.revision())
    def test_changed_actual_source_creates_new_version(self):
        first = self.revision(); self.inventory['inputFiles'][0]['md5'] = 'D' * 32
        self.assertNotEqual(first, self.revision())
    def test_changed_manifest_creates_new_version(self):
        first = self.revision(); self.bundle['manifestMd5'] = 'E' * 32
        self.assertNotEqual(first, self.revision())
    def test_missing_provenance_refused(self):
        self.inventory['inputFiles'] = []
        with self.assertRaisesRegex(ValueError, '来源不在'): self.revision()
    def make_cache(self):
        source = self.root / 'original/model'; source.mkdir(parents=True)
        (source / 'model.fbx').write_bytes(b'preserved model'); (source / 'material.json').write_text('{}')
        cache = self.root / self.revision(); publish(cache, source, {'fbx': str(source / 'model.fbx'), 'fbx_bytes': 15})
        return cache
    def test_restore_relocates_report_without_reexport(self):
        cache = self.make_cache(); target = self.root / 'new/model'; report = self.root / 'new-report.json'
        self.assertTrue(restore(cache, target, report)); self.assertEqual(json.loads(report.read_text())['fbx'], str(target / 'model.fbx'))
        self.assertEqual((target / 'model.fbx').read_bytes(), b'preserved model')
    def test_tampered_cache_is_not_replaced(self):
        cache = self.make_cache(); (cache / 'files/model.fbx').write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, '已修改'): restore(cache, self.root / 'new', self.root / 'report.json')
        self.assertEqual((cache / 'files/model.fbx').read_bytes(), b'changed')
    def test_extra_cache_file_refused(self):
        cache = self.make_cache(); (cache / 'files/extra').write_text('extra')
        with self.assertRaisesRegex(ValueError, '额外文件'): restore(cache, self.root / 'new', self.root / 'report.json')

if __name__ == '__main__': unittest.main()
