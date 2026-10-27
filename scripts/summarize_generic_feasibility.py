"""Read actual experiment evidence; do not rewrite task playback state or source files."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path


def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def summarize(reports, formal_package):
    units = read(reports / 'unit-verification.json')
    guards = read(reports / 'association-guards.json')
    assert units['passed'] and guards['passed']
    baseline = read(formal_package / 'release-manifest.json')
    for row in baseline['runtimeFiles'] + baseline['sourceFiles'] + baseline['documentationFiles']:
        assert digest(formal_package / row['path']) == row['sha256'], 'Formal package changed'
    cases = []
    evidence = ['unit-verification.json', 'association-guards.json', 'shared-source-boundary.json', 'discovery-rejection.json']
    for unit in units['cases']:
        role = unit['role']
        source = read(reports / role / 'case.json')
        associated = read(reports / role / 'association.json')
        generated = read(reports / role / 'generation.json')
        played = read(reports / role / 'play/play-verification.json')
        assert associated['passed'] and generated['passed'] and played['passed']
        assert played['actualPlay'] and played['switchClip'] and played['nonFiniteVertices'] == 0
        assert all(played[k] for k in ('savedPoseMatchesPrefab', 'sceneSaveReloadVerified', 'originalSceneUnchanged', 'seek', 'restart', 'pause', 'speed'))
        assert len(played['clips']) == 2
        assert all(c['avatarMatches'] and c['timeAdvanced'] and c['runtimeLoopCrossed'] for c in played['clips'])
        assert unit['passed'] and unit['measuredSegments'] >= 15 and unit['maxCorrectedRelativeError'] <= .01
        assert all(a['status'] == 'measured' and a['segments'] >= 8 and a['maxRatioDeviation'] <= .01 for a in unit['animations'])
        assert associated['association']['modelKey'] == source['model']['key']
        assert set(associated['association']['clipKeys']) == {c['key'] for c in source['clips']}
        for row in source['exported']['files']:
            assert digest(row['path']) == row['sha256'], 'Original export changed'
        config_path = Path(generated['generated']['result']['unityReportPath']).parent.parent / 'adaptation.json'
        config = read(config_path)
        for row in config['inputs']:
            assert digest(config_path.parent / row['path']) == row['sha256'], 'Unity source changed'
        native = read(generated['generated']['result']['unityReportPath'])
        project = Path(read(source['filePath'])['options']['unityProject'])
        assert (project / 'ProjectSettings/ProjectVersion.txt').is_file(), 'Cannot resolve validation project'
        for row in native['outputs']:
            # Generated output paths are project-relative Unity asset paths.
            assert digest(project / row['path']) == row['sha256'], 'Generated output changed'
        track_counts = []
        for clip in native['clips']:
            tracks = clip['tracks']
            count = Counter(t['decision'] for t in tracks)
            assert len(tracks) == count['retain'] + count['omit']
            track_counts.append({'name': clip['name'], 'total': len(tracks), 'written': count['retain'], 'omitted': count['omit'],
                                 'omissionReasons': dict(Counter(t['reason'] for t in tracks if t['decision'] != 'retain'))})
        cases.append({'role': role, 'controllerReferencedClips': associated['association']['referencedClips'], 'selectedClips': len(played['clips']),
                      'modelImportScale': unit['modelImportScale'], 'scaleSegments': unit['measuredSegments'],
                      'maxScaleRelativeError': unit['maxCorrectedRelativeError'], 'actualPlay': True, 'trackCounts': track_counts,
                      'scene': generated['generated']['result']['unityPreviewScene']})
        evidence.extend(role + '/' + p for p in ['case.json', 'association.json', 'generation.json', 'play/play-verification.json'])
    negative = [c for c in guards['checks'] if 'rejected' in c]
    anonymous = [c for c in guards['checks'] if c['name'] == 'anonymous-role-and-reversed-inventory']
    assert len(cases) == 2 and len(negative) == 14 and all(c['rejected'] for c in negative)
    assert len(anonymous) == 2 and all(c['passed'] for c in anonymous)
    dapan = read(reports / 'dapan/failure.json')
    assert 'No model name candidates' in dapan['error']
    discovery = read(reports / 'discovery-rejection.json')
    assert discovery['passed'] and discovery['actualExit'] == 1
    assert {p.name for p in (reports / 'negative-discovery/dapan').iterdir()} == {'failure.json'}
    shared = read(reports / 'shared-source-boundary.json')
    assert shared['sourceRigAutomaticallyDetermined'] is False
    return {'schema': 'endfield-generic-feasibility-v1', 'experimentCompleted': True, 'dedicatedIdleAutomationFeasible': True,
            'allCharactersAutomatic': False, 'formalManifestFilesUnchanged': True, 'originalExportsAndUnityFilesUnchanged': True,
            'cases': cases, 'rejectedInvalidAssociationCases': len(negative), 'anonymousRoleOrderChecks': len(anonymous),
            'unresolved': ['dapan has no model candidate in current name index', 'female_walk original shared rig has no proven controller reference in inspected snapshot',
                           'Lossy scalar-track omissions remain', 'No GUI integration, override-controller support or all-character acceptance'],
            'evidence': [{'path': name, 'sha256': digest(reports / name)} for name in evidence]}


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--reports', required=True, type=Path)
    parser.add_argument('--formal-package', required=True, type=Path)
    args = parser.parse_args()
    result = summarize(args.reports, args.formal_package)
    destination = args.reports / 'feasibility-summary.json'
    destination.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'experimentCompleted': result['experimentCompleted'], 'cases': len(result['cases']), 'summary': str(destination)}))
