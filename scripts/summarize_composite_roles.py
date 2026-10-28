"""Summarize source evidence and native failures without promoting fixture Play to role acceptance."""
import argparse
import json
from pathlib import Path
import re

from index_game_resources import digest, write_json
from model_assembly import resolve_assembly


def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def summarize(reports, project):
    result = {'schema': 'endfield-composite-idle-acceptance-v1', 'fullIdleVerified': False, 'roles': []}
    for role in ('laevat', 'jsspsi'):
        directory = reports / role
        discovered = read(directory / 'discovery.json')
        inventory_path = Path(discovered['inventoryPath'])
        inventory = read(inventory_path)
        plan = read(discovered['filePath'])
        if inventory['taskId'] != plan['id']:
            raise ValueError('Inventory belongs to another task.')
        roots = [o for o in inventory['objects'] if o.get('hierarchyRoot') and o.get('isModelCandidate')
                 and re.fullmatch(r'chr_[0-9]+_' + role + r'_(?:ui|post)?model', o['name'])]
        row = {'role': role, 'taskId': plan['id'], 'planPath': discovered['filePath'],
               'gameBuild': plan['gameBuild'], 'inventoryPath': str(inventory_path),
               'inventorySha256': digest(inventory_path), 'snapshotInputs': len(inventory['inputFiles']),
               'reusedInventoryPath': inventory.get('reusedInventoryPath'),
               'independentBodyCandidates': [{k: o.get(k) for k in ('key', 'name', 'pathId', 'sourceAsset', 'sourceBundle',
                    'rendererCount', 'boneReferenceCount', 'linkedAvatarKey', 'linkedControllerKey')} for o in roots],
               'fullIdleVerified': False, 'actualRolePlay': 'not-run', 'status': 'stopped'}
        failure = directory / 'failure.json'
        if failure.is_file():
            row['failure'] = read(failure)
        association = directory / 'association.json'
        if association.is_file():
            saved_association = read(association)
            if Path(saved_association['inventoryPath']).resolve() != inventory_path.resolve():
                raise ValueError('Association belongs to a different inventory snapshot; rerun adaptation.')
            association = saved_association['association']
            keys = [clip['key'] for clip in association['clips']]
            row['selectedClips'] = [clip['name'] for clip in association['clips']]
            composition = resolve_assembly(inventory, inventory_path, association['model']['key'],
                                           [model['key'] for model in association['relatedModels']], keys)
            assembly_path = directory / 'model-assembly-inspection.json'
            write_json(assembly_path, composition)
            row.update(assemblyEvidencePath=str(assembly_path), sourceNodes=len(composition['nodes']),
                       sourceRenderedParts=len(composition['parts']), visualDependenciesResolved=composition['visualDependenciesResolved'],
                       sourceLodGroups=len(composition['lodGroups']),
                       externalEffects=composition['externalEffects'], independentAttachments=composition['independentAttachments'],
                       independentModelCandidates=composition['independentModelCandidates'])
        prepared = directory / 'prepared.json'
        if prepared.is_file():
            prepared = read(prepared)
            base = project / prepared['unityAssetRoot']
            native_path = base / 'Generated/adaptation-result.json'
            if native_path.is_file():
                native = read(native_path)
                if native['configSha256'] != prepared['unityConfigSha256'] or digest(base / 'adaptation.json') != prepared['unityConfigSha256']:
                    raise ValueError('Native report/config do not belong to the prepared run.')
                row.update(nativeReportPath=str(native_path), nativeReportSha256=digest(native_path),
                           nativeStatus=native['status'], adapted=native['adapted'], nativeError=native.get('error'),
                           unitInference=native.get('unitInference'))
                row['status'] = 'generated-unverified' if native['adapted'] else 'generation-stopped'
        result['roles'].append(row)
    for name, filename in [('fixturePlay', 'fixture-play.json'), ('associationGuards', 'association-guards.json'),
                           ('singleModelRegression', 'regression/ardelia/baseline-regression.json')]:
        path = reports / filename
        result[name] = {'path': str(path), 'result': read(path)} if path.is_file() else {'status': 'not-run'}
    write_json(reports / 'acceptance-summary.json', result)
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('reports', type=Path)
    parser.add_argument('project', type=Path)
    args = parser.parse_args()
    summary = summarize(args.reports.resolve(), args.project.resolve())
    for role in summary['roles']:
        print(role['role'] + ': ' + role['status'] + '; fullIdleVerified=false; actualRolePlay=not-run')
