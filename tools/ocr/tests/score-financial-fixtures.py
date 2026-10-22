from pathlib import Path
import json,unicodedata,sys
root=Path(sys.argv[1]);truth=json.loads((root/'public-fixtures/truth.json').read_text(encoding='utf8'));report=[]
def norm(t):return ''.join(unicodedata.normalize('NFKC',t).split())
for path in sorted((root/'bounded-native').glob('*.json')):
 if path.name.endswith('.receipt.json'):continue
 v=json.loads(path.read_text(encoding='utf8'));record=dict(fixture=path.name,tables=len(v['tables']),cells=0,cell_text_correct=0,structure_correct=True,review_explicit=v['needs_review'],coordinates_valid=True)
 for span in v['spans']:
  x,y,r,b=span['bounding_box'];space=v['coordinate_system'];record['coordinates_valid']&=0<=x<r<=space['width']+1 and 0<=y<b<=space['height']+1
 if 'exif' in path.name:
  record.update(semantic_rotation_qualified=False,reason='90 degree visual table remains rotated; review required');report.append(record);continue
 for t in v['tables']:
  record['structure_correct']&=t['rows']==4 and t['columns']==3 and len(t['cells']) in (11,12)
  for c in t['cells']:
   record['cells']+=1;r=c['row_index'];col=c['column_index'];record['cell_text_correct']+=norm(c['text'])==norm(truth['simple_table'][r][col])
 record['cell_text_accuracy']=record['cell_text_correct']/max(record['cells'],1);record['structure_correct']&=record['tables']==(4 if 'large' in path.name else 2)
 report.append(record)
(root/'public-quality-scored.json').write_text(json.dumps(report,indent=2),encoding='utf8');print(json.dumps(report))
assert all(r['coordinates_valid'] and r['review_explicit'] for r in report)
assert all(r.get('semantic_rotation_qualified') is False or (r['structure_correct'] and r['cell_text_accuracy']==1) for r in report)
