"""Reference integrity and completeness checks, independent of game object IDs."""
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
from model_assembly import resolve_assembly


class AssemblyChecks(unittest.TestCase):
    def setUp(self):
        self.temp_base = Path(__file__).resolve().parents[3] / 'outputs/model-assembly-test-temp'
        self.temp_base.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix='assembly-', dir=self.temp_base)
        self.directory = Path(self.temp.name)
        self.rows = [self.node('body', '', None, ['hand']), self.node('hand', 'Hand', 'body', ['prop']),
                     self.node('prop', 'Prop', 'hand', [], renderer=True)]
        self.inventory = {'objects': [{'key': 'body'}]}

    def tearDown(self):
        self.directory.resolve().relative_to(self.temp_base.resolve())
        self.temp.cleanup()

    def node(self, key, name, parent, children, renderer=False):
        return {'key': key, 'name': name, 'kind': 'GameObject', 'error': None, 'data': {
            'transformKey': key + '-transform', 'parent': {'key': parent + '-transform'} if parent else None,
            'children': [{'key': child + '-transform'} for child in children], 'active': key != 'prop',
            'components': [], 'mesh': {'key': 'mesh'} if renderer else None,
            'materials': [{'key': 'material'}] if renderer else None,
            'renderer': {'key': key + '-renderer', 'kind': 'MeshRenderer', 'enabled': True} if renderer else None,
            'localPosition': {'x': 0, 'y': 1, 'z': 0}, 'localRotation': {'x': 0, 'y': 0, 'z': 0, 'w': 1},
            'localScale': {'x': 1, 'y': 1, 'z': 1}}}

    def save(self):
        path = self.directory / 'serialized-evidence.json'
        path.write_text(json.dumps({'schema': 'endfield-serialized-evidence-v1', 'objects': self.rows}), encoding='utf-8')
        self.inventory.update(serializedEvidencePath=str(path), serializedEvidenceSha256=hashlib.sha256(path.read_bytes()).hexdigest().upper())

    def resolve(self, keys=(), clips=None):
        self.save()
        return resolve_assembly(self.inventory, self.directory / 'inventory.json', 'body', keys, clips)

    def test_embedded_static_prop_without_avatar(self):
        result = self.resolve(['prop'])
        self.assertTrue(result['visualDependenciesResolved'])
        self.assertEqual(result['parts'][0]['path'], 'Hand/Prop')
        self.assertFalse(result['nodes'][-1]['active'])
        self.assertEqual(result['parts'][0]['scale'], {'x': 1, 'y': 1, 'z': 1})

    def test_single_model_compatible(self):
        self.rows = [self.node('body', '', None, [])]
        self.assertEqual(len(self.resolve()['nodes']), 1)

    def test_missing_child_ref_rejected(self):
        self.rows.pop()
        with self.assertRaisesRegex(ValueError, '子节点引用未解析'):
            self.resolve()

    def test_contradictory_parent_rejected(self):
        self.rows[-1]['data']['parent']['key'] = 'body-transform'
        with self.assertRaisesRegex(ValueError, '相互矛盾'):
            self.resolve()

    def test_ambiguous_path_rejected(self):
        self.rows.append(self.node('other', 'Prop', 'hand', []))
        self.rows[1]['data']['children'].append({'key': 'other-transform'})
        with self.assertRaisesRegex(ValueError, '重复层级路径'):
            self.resolve()

    def test_unproven_independent_prop_rejected(self):
        self.rows.append(self.node('other', 'Prop', None, [], renderer=True))
        with self.assertRaisesRegex(ValueError, '没有可验证的本体挂点'):
            self.resolve(['other'])

    def test_unknown_initial_visibility_rejected(self):
        self.rows[-1]['data']['active'] = None
        with self.assertRaisesRegex(ValueError, '初始显隐证据'):
            self.resolve()

    def test_tampered_evidence_rejected(self):
        self.save()
        Path(self.inventory['serializedEvidencePath']).write_text('{}', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, '摘要已变化'):
            resolve_assembly(self.inventory, self.directory / 'inventory.json', 'body')

    def test_particles_cannot_be_claimed_complete(self):
        self.rows[-1]['data']['components'].append({'key': 'particles', 'kind': 'ParticleSystem'})
        result = self.resolve()
        self.assertFalse(result['visualDependenciesResolved'])
        self.assertIn('Hand/Prop:ParticleSystem', result['unsupportedVisualComponents'])

    def test_unresolved_component_cannot_disappear(self):
        self.rows[-1]['data']['components'].append({'key': None, 'pathId': '123', 'kind': None})
        with self.assertRaisesRegex(ValueError, '组件引用未解析'):
            self.resolve()

    def test_lod_uses_actual_renderer_references(self):
        self.rows[0]['data']['components'].append({'key': 'lod', 'kind': 'LODGroup'})
        self.rows.append({'key': 'lod', 'kind': 'LODGroup', 'data': {'serialized': {
            'm_Size': 1, 'm_LocalReferencePoint': {'x': 0, 'y': .5, 'z': 0}, 'm_Enabled': True,
            'm_FadeMode': 1, 'm_AnimateCrossFading': True, 'm_LastLODIsBillboard': False,
            'm_LODs': [{'screenRelativeHeight': .1, 'fadeTransitionWidth': .5, 'renderers': [{'renderer': {'m_PathID': 123}}]}]},
            'references': [{'path': '/m_GameObject', 'key': 'body'}, {'path': '/m_LODs/0/renderers/0/renderer', 'key': 'prop-renderer'}]}})
        result = self.resolve()
        self.assertEqual(result['lodGroups'][0]['levels'][0]['renderers'][0]['path'], 'Hand/Prop')
        self.assertEqual(result['lodGroups'][0]['size'], 1)
        self.rows[-1]['data']['references'][-1]['key'] = 'external-renderer'
        with self.assertRaisesRegex(ValueError, 'LOD 引用未解析'):
            self.resolve()

    def test_controller_visual_effect_retains_evidence_and_stops(self):
        self.inventory['objects'][0]['linkedControllerKey'] = 'controller'
        self.rows += [
            {'key': 'controller', 'kind': 'AnimatorController', 'data': {'references': [{'path': '/m_StateMachineBehaviours/0', 'key': 'behaviour'}]}},
            {'key': 'behaviour', 'kind': 'MonoBehaviour', 'data': {'script': {'name': 'AnimatorBehaviourPlayEffect'},
                'serialized': {'_effects': [{'effectName': 'PropEffect', 'mountPoint': 'Hand'}]}}}]
        result = self.resolve()
        self.assertFalse(result['visualDependenciesResolved'])
        self.assertEqual(result['externalEffects'][0]['serializedSettings']['mountPoint'], 'Hand')
        self.assertEqual(result['externalEffects'][0]['candidateModelKeys'], [])

    def test_effects_are_limited_to_selected_controller_states(self):
        self.inventory['objects'][0].update(linkedControllerKey='controller', controllerClipKeys=['overview', 'special'])
        def state(identity, clip):
            return {'data': {'m_FullPathID': identity, 'm_BlendTreeConstantArray': [{'data': {'m_NodeArray': [{'data': {'m_ClipID': clip}}]}}]}}
        self.rows += [
            {'key': 'overview', 'kind': 'AnimationClip', 'data': {'events': []}},
            {'key': 'special', 'kind': 'AnimationClip', 'data': {'events': []}},
            {'key': 'controller', 'kind': 'AnimatorController', 'data': {'references': [
                {'path': '/m_StateMachineBehaviours/0', 'key': 'behaviour'},
                {'path': '/m_AnimationClips/0', 'key': 'overview'}, {'path': '/m_AnimationClips/1', 'key': 'special'}],
                'serialized': {'m_Controller': {'m_LayerArray': [{'data': {'m_StateMachineIndex': 0}}],
                    'm_StateMachineArray': [{'data': {'m_StateConstantArray': [state(100, 0), state(200, 1)]}}]},
                    'm_StateMachineBehaviourVectorDescription': {'m_StateMachineBehaviourIndices': [0],
                        'm_StateMachineBehaviourRanges': [{'Key': {'m_StateID': 200, 'm_LayerIndex': 0}, 'Value': {'m_StartIndex': 0, 'm_Count': 1}}]}}}},
            {'key': 'behaviour', 'kind': 'MonoBehaviour', 'data': {'script': {'name': 'AnimatorBehaviourPlayEffect'},
                'serialized': {'_effects': [{'effectName': 'PropEffect', 'mountPoint': 'Hand'}]}}}]
        self.assertTrue(self.resolve(clips=['overview'])['visualDependenciesResolved'])
        result = self.resolve(clips=['special'])
        self.assertFalse(result['visualDependenciesResolved'])
        self.assertEqual(result['externalEffects'][0]['stateBindings'][0]['clipKeys'], ['special'])

    def test_runtime_event_cannot_be_silently_discarded(self):
        self.rows.append({'key': 'clip', 'kind': 'AnimationClip', 'data': {'events': [{'time': .5, 'functionName': 'SpawnProp'}]}})
        result = self.resolve(clips=['clip'])
        self.assertFalse(result['visualDependenciesResolved'])
        self.assertEqual(result['animationEvents'][0]['status'], 'unsupported-runtime-animation-event')

    def test_audio_event_is_explicitly_outside_visual_preview(self):
        self.rows.append({'key': 'clip', 'kind': 'AnimationClip', 'data': {'events': [{'time': .5, 'functionName': 'PostAudioEvent'}]}})
        result = self.resolve(clips=['clip'])
        self.assertTrue(result['visualDependenciesResolved'])
        self.assertEqual(result['animationEvents'][0]['status'], 'audio-outside-visual-preview')


if __name__ == '__main__':
    unittest.main()
