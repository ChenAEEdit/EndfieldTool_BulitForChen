"""Build a complete embedded model assembly from actual Transform references."""
import json
import math
from pathlib import Path

from extract_plan_bundles import require
from index_game_resources import digest, within


def resolve_assembly(inventory, inventory_path, model_key, related_keys=(), clip_keys=None):
    path = Path(inventory.get('serializedEvidencePath') or '').resolve()
    require(path.is_file() and within(path, Path(inventory_path).resolve().parent),
            '完整模型组成需要重新解析对象以取得层级和显隐证据。')
    require(digest(path) == inventory.get('serializedEvidenceSha256'), '模型组成证据摘要已变化。')
    evidence = json.loads(path.read_text(encoding='utf-8-sig'))
    require(evidence.get('schema') == 'endfield-serialized-evidence-v1', '模型组成证据格式无效。')
    all_rows = {o['key']: o for o in evidence['objects']}
    rows = {k: o for k, o in all_rows.items() if o['kind'] == 'GameObject'}
    transforms = {o['data']['transformKey']: o for o in rows.values() if o.get('data') and o['data'].get('transformKey')}
    require(model_key in rows, '模型组成证据没有所选本体。')
    require(len(related_keys) == len(set(related_keys)) and model_key not in related_keys, '关联模型选择重复。')
    nodes, parts, seen, unsupported, lod_sources = [], [], set(), [], []

    def visit(row, target_path, parent_key):
        require(row['key'] not in seen, '模型组成存在循环或重复引用。')
        seen.add(row['key'])
        require(not row.get('error') and row.get('data'), '模型组成层级无法解析：' + row['key'])
        data = row['data']
        for component in data.get('components') or []:
            require(not component or component.get('key'), '模型组件引用未解析：' + target_path)
            if component and component.get('kind') == 'LODGroup':
                lod_sources.append((row['key'], target_path, component['key']))
            if component and component.get('kind') in ('ParticleSystem', 'ParticleSystemRenderer', 'PlayableDirector'):
                unsupported.append(target_path + ':' + component['kind'])
        actual_parent = data.get('parent')
        if parent_key is not None:
            require(actual_parent and actual_parent.get('key') == parent_key, '模型组成父子引用相互矛盾：' + target_path)
        require(type(data.get('active')) is bool, '缺少模型节点初始显隐证据：' + target_path)
        renderer = data.get('renderer')
        if renderer:
            require(type(renderer.get('enabled')) is bool and (data.get('mesh') or {}).get('key'),
                    '缺少模型网格或 Renderer 显隐证据：' + target_path)
            require(all(m is not None and m.get('key') for m in data.get('materials') or []), '模型材质引用未解析：' + target_path)
            parts.append({'modelKey': row['key'], 'path': target_path, 'relationship': 'embedded',
                'rendererKey': renderer['key'], 'rendererType': renderer['kind'],
                'meshKey': data['mesh']['key'], 'materialKeys': [m['key'] for m in data['materials']],
                'parentTransformKey': actual_parent.get('key') if actual_parent else None,
                'position': data['localPosition'], 'rotation': data['localRotation'], 'scale': data['localScale']})
        nodes.append({'key': row['key'], 'path': target_path, 'active': data['active'],
                      'rendererType': renderer['kind'] if renderer else '',
                      'rendererEnabled': renderer['enabled'] if renderer else True})
        children = data.get('children')
        require(isinstance(children, list), '缺少模型子节点证据：' + target_path)
        for child in children:
            require(child and child.get('key') in transforms, '模型子节点引用未解析：' + target_path)
            go = transforms[child['key']]
            require(go['name'] and '/' not in go['name'], '模型节点名称无法唯一表达为路径。')
            visit(go, target_path + '/' + go['name'] if target_path else go['name'], data['transformKey'])

    visit(rows[model_key], '', None)
    require(all(key in seen for key in related_keys), '关联模型没有可验证的本体挂点；不能凭同包或名称装配独立模型。')
    require(len({n['path'] for n in nodes}) == len(nodes), '模型组成有重复层级路径。')
    renderer_paths = {p['rendererKey']: p for p in parts}
    require(len(renderer_paths) == len(parts), '同一 Renderer 被多个模型节点引用。')
    lod_groups = []
    for owner, target_path, key in lod_sources:
        source = all_rows.get(key)
        require(source and not source.get('error') and source.get('data'), '缺少 LOD 显隐证据，请重新解析对象。')
        data = source['data']
        serialized = data.get('serialized') or {}
        refs = {r['path']: r for r in data.get('references') or []}
        require((refs.get('/m_GameObject') or {}).get('key') == owner, 'LODGroup 所属对象引用矛盾。')
        size, point = serialized.get('m_Size'), serialized.get('m_LocalReferencePoint')
        require(type(size) in (int, float) and math.isfinite(size) and size > 0 and isinstance(point, dict)
                and all(type(point.get(axis)) in (int, float) and math.isfinite(point[axis]) for axis in ('x', 'y', 'z')), 'LOD 边界或单位无效。')
        require(type(serialized.get('m_Enabled')) is bool and type(serialized.get('m_AnimateCrossFading')) is bool
                and type(serialized.get('m_FadeMode')) is int and 0 <= serialized['m_FadeMode'] <= 2, 'LOD 显隐设置无效。')
        lods = serialized.get('m_LODs')
        require(isinstance(lods, list) and 1 <= len(lods) <= 8, 'LOD 层级数量无效。')
        levels, previous = [], 1.0
        for i, lod in enumerate(lods):
            height, width = lod.get('screenRelativeHeight'), lod.get('fadeTransitionWidth')
            require(type(height) in (int, float) and math.isfinite(height) and 0 <= height <= previous
                    and type(width) in (int, float) and math.isfinite(width) and 0 <= width <= 1, 'LOD 阈值无效。')
            previous = height
            renderers = []
            for j, entry in enumerate(lod['renderers']):
                if entry['renderer']['m_PathID'] == 0:
                    renderers.append({'isNull': True})
                    continue
                ref = refs.get(f'/m_LODs/{i}/renderers/{j}/renderer')
                require(ref and ref.get('key') in renderer_paths, 'LOD 引用未解析或超出本体层级。')
                part = renderer_paths[ref['key']]
                renderers.append({'path': part['path'], 'rendererType': part['rendererType'], 'isNull': False})
            levels.append({'height': height, 'fadeWidth': width, 'renderers': renderers})
        if serialized.get('m_LastLODIsBillboard') is not False:
            unsupported.append(target_path + ':billboard-LOD')
        lod_groups.append({'path': target_path, 'size': size, 'referencePoint': point,
                           'fadeMode': serialized['m_FadeMode'], 'animateCrossFading': serialized['m_AnimateCrossFading'],
                           'enabled': serialized['m_Enabled'], 'levels': levels, 'unitPolicy': 'source-local-times-inferred-position-scale'})
    model = next(o for o in inventory['objects'] if o['key'] == model_key)
    controller = all_rows.get(model.get('linkedControllerKey'))
    external, events = [], []
    if clip_keys is not None:
        for key in clip_keys:
            clip = all_rows.get(key)
            require(clip and not clip.get('error') and clip.get('kind') == 'AnimationClip'
                    and isinstance((clip.get('data') or {}).get('events'), list), '所选动画缺少事件证据。')
            for event in clip['data']['events']:
                audio = event.get('functionName') == 'PostAudioEvent'
                events.append({'clipKey': key, 'event': event,
                               'status': 'audio-outside-visual-preview' if audio else 'unsupported-runtime-animation-event'})
                if not audio:
                    unsupported.append(key + ':animation-event:' + str(event.get('functionName')))
    if model.get('linkedControllerKey'):
        require(controller and not controller.get('error') and controller.get('data')
                and isinstance(controller['data'].get('references'), list), '控制器缺少完整行为引用证据，不能认定道具完整。')
    if controller and controller.get('data'):
        selected_behaviours = None
        state_proof = {}
        if clip_keys is not None:
            data = controller['data']
            serialized = data.get('serialized') or {}
            constant = serialized.get('m_Controller') or {}
            ranges = (serialized.get('m_StateMachineBehaviourVectorDescription') or {}).get('m_StateMachineBehaviourRanges') or []
            indices = (serialized.get('m_StateMachineBehaviourVectorDescription') or {}).get('m_StateMachineBehaviourIndices') or []
            refs = {r['path']: r for r in data['references']}
            selected_behaviours, found_clips = set(), set()
            controller_clips = set(clip_keys).intersection(model.get('controllerClipKeys') or [])
            machines = constant.get('m_StateMachineArray')
            layers = constant.get('m_LayerArray')
            require(isinstance(machines, list) and isinstance(layers, list), '控制器缺少可验证的片段与状态关系。')
            for layer_index, layer in enumerate(layers):
                machine_index = layer['data']['m_StateMachineIndex']
                require(type(machine_index) is int and 0 <= machine_index < len(machines), '控制器状态机索引无效。')
                for item in machines[machine_index]['data']['m_StateConstantArray']:
                    state = item['data']
                    state_clips = set()
                    for tree in state.get('m_BlendTreeConstantArray') or []:
                        for node in tree['data']['m_NodeArray']:
                            if node['data'].get('m_ChildIndices'):
                                continue
                            ref = refs.get('/m_AnimationClips/' + str(node['data'].get('m_ClipID')))
                            if ref and ref.get('key'):
                                state_clips.add(ref['key'])
                    selected_clips = state_clips.intersection(controller_clips)
                    if not selected_clips:
                        continue
                    found_clips.update(selected_clips)
                    for entry in ranges:
                        if entry['Key']['m_StateID'] != state['m_FullPathID'] or entry['Key']['m_LayerIndex'] != layer_index:
                            continue
                        start, count = entry['Value']['m_StartIndex'], entry['Value']['m_Count']
                        require(type(start) is int and type(count) is int and start >= 0 and count >= 0 and start + count <= len(indices), '控制器状态行为范围无效。')
                        for index in indices[start:start + count]:
                            pointer_path = '/m_StateMachineBehaviours/' + str(index)
                            require(pointer_path in refs and refs[pointer_path].get('key'), '控制器状态行为引用缺失。')
                            selected_behaviours.add(pointer_path)
                            state_proof.setdefault(pointer_path, []).append({'layer': layer_index, 'stateId': state['m_FullPathID'], 'clipKeys': sorted(selected_clips)})
            require(controller_clips <= found_clips, '所选动画缺少可验证的控制器状态归属。')
        for ref in controller['data'].get('references') or []:
            if not ref['path'].startswith('/m_StateMachineBehaviours/'):
                continue
            if selected_behaviours is not None and ref['path'] not in selected_behaviours:
                continue
            behaviour = all_rows.get(ref.get('key'))
            require(behaviour and not behaviour.get('error') and behaviour['kind'] == 'MonoBehaviour' and behaviour.get('data'),
                    '控制器状态行为引用无法解析，不能认定道具完整。')
            data = behaviour['data']
            if (data.get('script') or {}).get('name') != 'AnimatorBehaviourPlayEffect':
                external.append({'controllerKey': model['linkedControllerKey'], 'behaviourKey': behaviour['key'],
                    'supported': False, 'reason': 'unsupported-state-machine-behaviour', 'script': data.get('script')})
                continue
            for effect in (data.get('serialized') or {}).get('_effects') or []:
                name = effect.get('effectName')
                matches = [o['key'] for o in rows.values() if o['name'] == name and o['data'].get('transformKey') and not o['data'].get('parent')]
                external.append({'controllerKey': model['linkedControllerKey'], 'behaviourKey': behaviour['key'],
                    'effectName': name, 'candidateModelKeys': matches, 'serializedSettings': effect,
                    'stateBindings': state_proof.get(ref['path'], []),
                    'supported': False, 'reason': 'runtime-state-behaviour-effect-not-reconstructed'})
    attachments = []
    for effect in external:
        for key in effect.get('candidateModelKeys') or []:
            data = rows[key]['data']
            attachments.append({'modelKey': key, 'relationship': 'independent-symbolic-candidate',
                'sourceBehaviourKey': effect['behaviourKey'], 'sourceEffectName': effect.get('effectName'),
                'sourceMountPoint': effect['serializedSettings'].get('mountPoint'),
                'position': data['localPosition'], 'rotation': data['localRotation'], 'scale': data['localScale'],
                'associationEstablished': False, 'reason': 'missing-runtime-effect-registry-and-behaviour-reconstruction'})
    reference_parents = {Path(name).name.casefold() for seed in (inventory.get('propReferenceDiscovery') or {}).get('seeds', [])
                         for name in seed['directReferences']}
    candidates = [{k: o.get(k) for k in ('key', 'name', 'pathId', 'sourceAsset', 'sourceBundle', 'linkedAvatarKey', 'linkedControllerKey', 'controllerClipKeys')}
                  for o in inventory['objects'] if o.get('hierarchyRoot') and o.get('isModelCandidate')
                  and o['key'] != model_key and Path(o['sourceBundle']).name.casefold() in reference_parents]
    return {'schema': 'endfield-model-assembly-v1', 'bodyKey': model_key,
            'sourceEvidenceSha256': inventory['serializedEvidenceSha256'],
            'evidence': 'actual-transform-parent-and-child-references', 'nodes': nodes, 'parts': parts,
            'selectedRelatedModelKeys': list(related_keys), 'independentAttachments': attachments,
            'independentModelCandidates': candidates, 'candidateAssociationEstablished': False,
            'unsupportedVisualComponents': unsupported, 'externalEffects': external,
            'animationEvents': events,
            'lodGroups': lod_groups,
            'visualDependenciesResolved': not unsupported and not external}
