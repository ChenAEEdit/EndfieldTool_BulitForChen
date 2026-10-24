"""Summarize explicitly exported Chen idle candidates without importing or playing them."""
from pathlib import Path
import argparse,json,re

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('directory',type=Path,help='Directory containing candidate export report.json files')
    args=parser.parse_args()
    categories={
        'ui_overview':'角色总览循环；用途根据资源命名判断',
        'ui_relax':'界面放松待机循环；用途根据资源命名判断',
        'team_idle':'编队待机变体；用途根据资源命名判断',
        'additive_idle':'叠加或布料片段；需要正确的基础动画与混合设置，不作为独立待机交付',
    }
    items=[]
    for report_path in sorted(args.directory.glob('*/report.json')):
        report=json.loads(report_path.read_text(encoding='utf-8-sig'))
        for entry in report.get('exports',[]):
            source=Path(entry['output'])
            text=source.read_text(encoding='utf-8-sig')
            def number(key):
                match=re.search(r'^\s+'+key+r': ([^\n]+)',text,re.M)
                return float(match[1]) if match else None
            begin,end=number('m_StartTime'),number('m_StopTime')
            items.append({
                'name':entry['m_Name'],'category':report_path.parent.name,'description':categories.get(report_path.parent.name,''),
                'duration_seconds':round(end-begin,6) if begin is not None and end is not None else None,
                'sample_rate':number('m_SampleRate'),'loop_flag':number('m_LoopTime'),
                'curves':entry['curves'],'keys':entry['keys'],'unknown_paths':entry['unknown_paths'],
                'source_bundle':entry['source_bundle'],'exported_file':str(source),
                'decode_status':'passed' if entry['curves']>0 and entry['keys']>0 and entry['unknown_paths']==0 else 'needs_review',
                'unity_play_status':'not_requested',
            })
    (args.directory/'候选动作清单.json').write_text(json.dumps(items,ensure_ascii=False,indent=2),encoding='utf-8')
    lines=['# 陈千语待机动作候选','',
        '以下片段已用当前解码器导出并检查实际曲线和关键帧。用途根据资源命名判断，尚未进行 Unity 动作画面对照，不能把解码成功当作播放姿势正确。','',
        '| 动画 | 时长/秒 | 曲线数 | 关键帧数 | 分类 |',
        '| --- | ---: | ---: | ---: | --- |']
    for item in items:
        lines.append(f"| `{item['name']}` | {item['duration_seconds']} | {item['curves']} | {item['keys']} | {item['description']} |")
    lines+=['','## 使用范围','',
        '- `ui_overview` 和 `ui_relax` 是界面待机的主要候选。','- `team_idle` 包含四个编队待机变体。',
        '- `additive_idle` 中的两条不应直接当作普通独立动作播放，需要基础姿势、叠加参考及布料流程。',
        '- 所有导出均保留在当前目录的相应子目录，源 Bundle 路径及统计见 JSON 清单。',
        '- 本次只查找与导出，未修改现有 Unity 场景、控制器或工具的支持范围。','']
    (args.directory/'待机动作说明.md').write_text('\n'.join(lines),encoding='utf-8')
    print(json.dumps({'clips':len(items),'decoded':sum(i['decode_status']=='passed' for i in items),'summary':str(args.directory/'待机动作说明.md')},ensure_ascii=False))

if __name__=='__main__':main()
