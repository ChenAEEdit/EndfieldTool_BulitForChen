"""Stage decoded Chen idle sources with provenance checks for the existing Unity project."""
from pathlib import Path
import argparse,hashlib,importlib.util,json,shutil,sys

def digest(path,algorithm='sha256'):
    h=hashlib.new(algorithm)
    with Path(path).open('rb') as stream:
        for block in iter(lambda:stream.read(1024*1024),b''):h.update(block)
    return h.hexdigest().upper()

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--search',required=True,type=Path)
    parser.add_argument('--workspace',required=True,type=Path)
    parser.add_argument('--game-assembly',required=True,type=Path)
    parser.add_argument('--avatar',required=True,type=Path)
    args=parser.parse_args()
    root=Path(__file__).resolve().parents[1]
    profile=json.loads((root/'profiles/chen.json').read_text(encoding='utf-8'))
    if digest(args.game_assembly)!=profile['game_assembly_sha256']:raise ValueError('Unsupported game build')
    spec=importlib.util.spec_from_file_location('idle_pipeline',root/'pipeline.py')
    pipeline=importlib.util.module_from_spec(spec);spec.loader.exec_module(pipeline)
    records=json.loads((args.search/'候选动作清单.json').read_text(encoding='utf-8'))
    expected={b['name']:b['md5'].upper() for b in profile['bundles']}
    expected['f96650b645132f8ad52047d6.ab']='5248072BBFD485DAE6AA29F9E47A9085'
    stage=args.workspace/'unity/ChenIdle'
    stage.mkdir(parents=True,exist_ok=True)
    marker=stage/'tool-generated.json'
    if any(stage.iterdir()) and not marker.is_file():raise ValueError('Unowned staging directory')
    marker.write_text(json.dumps({'tool':'EndfieldChenIdle','game_build':profile['build']}),encoding='utf-8')
    (stage/'Animations').mkdir(exist_ok=True);(stage/'Editor').mkdir(exist_ok=True)
    config=[];provenance=[];supplements=[]
    mapping={'A_actor_chen_ui_overview_loop':('Overview','Overview idle'),
             'A_actor_chen_ui_relax_loop':('Relax','Relax idle')}
    mapping.update({f'A_actor_chen_uiteam_idle_{n:02}':(f'Team{n:02}',f'Team idle {n:02}') for n in range(1,5)})
    for record in records:
        bundle=Path(record['source_bundle'])
        actual=digest(bundle,'md5')
        if actual!=expected.get(bundle.name):raise ValueError('Bundle MD5 mismatch: '+str(bundle))
        record=dict(record,bundle_md5_verified=actual,source_sha256=digest(record['exported_file']))
        if record['category']=='additive_idle':
            record['application_status']='not_applied_requires_base_pose_and_blending'
            supplements.append(record);continue
        ident,label=mapping[record['name']]
        source=f'Assets/ChenIdle/Animations/Chen_Idle_{ident}_Source.anim'
        target=stage/'Animations'/Path(source).name
        pipeline.resolve_animation(record['exported_file'],args.avatar,target,args.workspace/f'{ident}_paths.json')
        config.append(dict(id=ident,label=label,source=source,clip=f'Assets/ChenIdle/Animations/Chen_Idle_{ident}.anim',
                           scene=f'Assets/ChenIdle/Chen_Idle_{ident}.unity',controller=f'Assets/ChenIdle/Chen_Idle_{ident}.controller'))
        provenance.append(record)
    config.sort(key=lambda c:['Overview','Relax','Team01','Team02','Team03','Team04'].index(c['id']))
    for source,target in [(root/'templates/idle/IdleControls.cs',stage/'IdleControls.cs'),
                          (root/'templates/idle/IdleSceneBuilder.cs',stage/'Editor/IdleSceneBuilder.cs')]:shutil.copy2(source,target)
    (stage/'candidates.json').write_text(json.dumps({'candidates':config},ensure_ascii=False,indent=2),encoding='utf-8')
    (args.workspace/'provenance.json').write_text(json.dumps({'build':profile['build'],'game_sha256_verified':profile['game_assembly_sha256'],
        'candidates':provenance,'supplemental_clips':supplements},ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'stage':str(stage),'candidates':len(config),'supplements_not_applied':len(supplements)}))

if __name__=='__main__':main()
