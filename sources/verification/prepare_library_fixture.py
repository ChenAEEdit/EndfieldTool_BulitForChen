"""Create isolated motion-pack tasks from a preserved model and authored verification clips."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import sys
import uuid
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
from index_game_resources import digest, write_json
from prepare_unity_adaptation import prepare_unity_package


def prepare(package, bootstrap, baseline_plan, model_directory, project, reports):
    import prepare_unity_adaptation as adapter
    adapter.ROOT = package.resolve()
    workspace = reports / 'workspace'; workspace.mkdir(parents=True, exist_ok=True)
    write_json(workspace / 'character-workspace.json', {'schema': 'endfield-character-workspace-v1'})
    original = json.loads(baseline_plan.read_text(encoding='utf-8-sig'))
    data = json.loads(bootstrap.read_text(encoding='utf-8-sig'))
    assembly = json.loads((model_directory / 'model-assembly.json').read_text(encoding='utf-8-sig'))
    plans = []
    for name, index in [('first', 0), ('second', 1), ('strict-failure', 2)]:
        task_id, run_id, bundle_run = str(uuid.uuid4()), uuid.uuid4().hex, uuid.uuid4().hex
        task = workspace / 'tasks' / uuid.UUID(task_id).hex
        run = task / 'objects' / run_id
        shutil.copytree(model_directory, run / 'exports/model', ignore=shutil.ignore_patterns('*.meta'))
        plan = dict(original, id=task_id, options=dict(original['options'], workspaceDirectory=str(workspace), unityProject=str(project)))
        write_json(task / 'plan.json', plan)
        write_json(task / 'result.json', {'schema': 'endfield-bundle-extraction-v1', 'taskId': task_id, 'runId': bundle_run,
            'status': 'bundles_extracted', 'planSha256': digest(task / 'plan.json')})
        fbx = next((run / 'exports/model').glob('*.fbx'))
        write_json(run / 'model-report.json', {'fbx': str(fbx), 'fbx_bytes': fbx.stat().st_size})
        source = run / 'exports/animations/fixture.anim'; source.parent.mkdir()
        shutil.copy2(data['sources'][index], source)
        clip = {'key': 'A' + str(index), 'name': 'LibraryFixture_' + name, 'sourceAsset': 'fixture', 'sourceBundle': 'fixture.ab', 'pathId': str(index)}
        write_json(run / 'animation-report.json', {'exports': [{'source_asset_file': 'fixture', 'source_bundle': 'fixture.ab',
            'm_PathID': index, 'output': str(source)}]})
        state = {'task': task, 'run': run, 'result': {'schema': 'endfield-object-operation-v1', 'taskId': task_id,
            'runId': run_id, 'bundleRunId': bundle_run, 'mode': 'prepare', 'files': [], 'failures': [], 'warnings': [],
            'modelRevision': 'preserved-ardelia-model-fixture'}}
        rules = {'modelImportScale': 100, 'positionScale': 1, 'inferUnits': False, 'dropUnmatched': False,
            'dropSceneTranslation': False, 'forceLoop': True, 'normalizeRoot': '', 'excludedDescendants': [], 'excludedNodes': [], 'createPreviewScene': True}
        pack = prepare_unity_package(state, {'adaptation': rules}, {'key': assembly['bodyKey']}, [clip])
        state['result']['files'] = [{'kind': 'UnityPackage', 'path': str(p), 'bytes': p.stat().st_size, 'sha256': digest(p)} for p in pack.rglob('*') if p.is_file()]
        write_json(task / 'objects/preparation.json', state['result'])
        write_json(run / 'operation.json', state['result'])
        plans.append({'name': name, 'path': str(task / 'plan.json')})
    write_json(reports / 'fixture-plans.json', plans)
    print('Prepared three isolated fixture tasks: two valid motions and one strict rejection.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for arg in ('package', 'bootstrap', 'baseline_plan', 'model_directory', 'project', 'reports'):
        parser.add_argument(arg, type=Path)
    prepare(**vars(parser.parse_args()))
