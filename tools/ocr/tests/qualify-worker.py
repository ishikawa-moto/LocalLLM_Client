from pathlib import Path
import importlib.util,json,hashlib,unicodedata,sys,time,argparse
from PIL import Image
import pypdf
parser=argparse.ArgumentParser();parser.add_argument('--assets',type=Path,required=True);parser.add_argument('--fixtures',type=Path,required=True);parser.add_argument('--handwriting',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);args=parser.parse_args();target=args.output.resolve();target.mkdir(parents=True,exist_ok=False);assets=args.assets.resolve()
spec=importlib.util.spec_from_file_location('ocr',Path(__file__).resolve().parents[1]/'local_ocr_worker.py');w=importlib.util.module_from_spec(spec);spec.loader.exec_module(w);w.offline()
checks=[]
def check(label,value,detail=None):
 checks.append({'test':label,'passed':bool(value),'detail':detail})
 if not value:raise AssertionError(label)
def norm(t):return ''.join(unicodedata.normalize('NFKC',t).split())
def edit(a,b):
 row=list(range(len(b)+1))
 for i,x in enumerate(a,1):
  new=[i]
  for j,y in enumerate(b,1):new.append(min(row[j]+1,new[-1]+1,row[j-1]+(x!=y)))
  row=new
 return row[-1]
f=args.fixtures.resolve();m=json.loads((f/'manifest.json').read_text(encoding='utf-8'))
for name,code in [('corrupt.png','UnidentifiedImageError'),('corrupt.pdf','PdfReadError')]:
 try:w.inspect(f/name,w.digest(f/name));raise AssertionError(name)
 except Exception as e:check('reject_'+name,type(e).__name__==code,type(e).__name__)
try:w.checked_input(f/'printed-ja.png','0'*64);raise AssertionError('hash')
except w.OcrError as e:check('input_hash_binding',e.code=='INPUT_HASH_MISMATCH')
Image.new('1',(10001,2),'white').save(target/'wide.png')
try:w.inspect(target/'wide.png',w.digest(target/'wide.png'));raise AssertionError('pixels')
except w.OcrError as e:check('pixel_limit',e.code=='PIXEL_LIMIT')
writer=pypdf.PdfWriter();writer.add_blank_page(100,100);writer.encrypt('PUBLIC');writer.write(target/'encrypted.pdf')
try:w.inspect(target/'encrypted.pdf',w.digest(target/'encrypted.pdf'));raise AssertionError('encrypt')
except w.OcrError as e:check('encrypted_pdf_rejected',e.code=='PDF_ENCRYPTED')
for orientation in range(1,9):
 im=Image.new('RGB',(110,70),'white');exif=im.getexif();exif[274]=orientation;path=target/f'exif{orientation}.jpg';im.save(path,exif=exif)
 raster,space,transform=w.load_page(path,0)
 points=[transform(x,y) for x,y in [(0,0),(raster.width,0),(raster.width,raster.height),(0,raster.height)]]
 check('exif_'+str(orientation),sorted(set(round(p[0]) for p in points))==[0,110] and sorted(set(round(p[1]) for p in points))==[0,70])
quality=[]
for name,profile in [('printed-ja.webp','PRINTED_TEXT'),('table-ruled.png','TABLE_LAYOUT'),('table-merged.png','TABLE_LAYOUT'),('table-unruled.png','TABLE_LAYOUT'),('scan-two-pages.pdf','PDF_DOCUMENT')]:
 page=0;start=time.monotonic();r=w.process_page(f/name,page,profile,assets/'models');r.update(input_sha256=w.digest(f/name),external_ai='deny',status='complete',network_attempts=len(w.NETWORK_ATTEMPTS));(target/(name+'.json')).write_text(json.dumps(r,ensure_ascii=False),encoding='utf-8')
 for span in r['spans']:
  space=r['coordinate_system'];box=span['bounding_box'];check('position_'+name+'_'+span['span_id'],0<=box[0]<=box[2]<=space['width']+1 and 0<=box[1]<=box[3]<=space['height']+1)
 record={'fixture':name,'elapsed_ms':int((time.monotonic()-start)*1000),'text':r['text'],'tables':len(r['tables']),'needs_review':r['needs_review']}
 if name=='printed-ja.webp':
  truth=m['ground_truth'][name];record.update(normalized_CER=edit(norm(truth),norm(r['text']))/len(norm(truth)),raw_WER=edit(truth.split(),r['text'].split())/len(truth.split()));check('printed_webp_exact_normalized',record['normalized_CER']==0)
 if name=='table-ruled.png':
  cells=r['tables'][0]['cells'];truth=m['table_ground_truth'][name];correct=sum(norm(c['text'])==norm(truth[c['row_index']][c['column_index']]) for c in cells)
  record.update(cell_text_accuracy=correct/12,cell_structure_accuracy=sum(c['row_span']==c['column_span']==1 for c in cells)/12)
  check('ruled_4_by_3_structure',r['tables'][0]['rows']==4 and r['tables'][0]['columns']==3 and len(cells)==12)
 if name=='table-merged.png':check('merged_header_span',any(c['row_index']==0 and c['column_index']==0 and c['column_span']==2 for c in r['tables'][0]['cells']))
 if name=='table-unruled.png':check('unruled_explicit_unqualified',not r['tables'] and r['needs_review'])
 if name.endswith('.pdf'):check('pdf_display_point_coordinates',r['coordinate_system']['units']=='pdf_display_points')
 quality.append(record)
h=args.handwriting.resolve();meta=json.loads((h/'manifest.json').read_text(encoding='utf-8'));r=w.process_page(h/'kmnist-ten-public.png',0,'HANDWRITING',assets/'models');(target/'handwriting-result.json').write_text(json.dumps(r,ensure_ascii=False),encoding='utf-8')
quality.append({'fixture':'KMNIST-10 historical handwriting','truth':meta['truth'],'text':r['text'],'normalized_CER':edit(norm(meta['truth']),norm(r['text']))/len(norm(meta['truth'])),'modern_handwriting_qualified':False,'needs_review':r['needs_review']})
check('handwriting_always_experimental',r['needs_review'] and any('EXPERIMENTAL_HANDWRITING' in v for v in r['warnings']))
check('no_runtime_network_attempt',len(w.NETWORK_ATTEMPTS)==0)
(target/'worker-quality.json').write_text(json.dumps({'checks':checks,'quality':quality,'network_attempts':len(w.NETWORK_ATTEMPTS),'private_data_used':False},ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'status':'PASS_WORKER_PUBLIC_QUALIFICATION','checks':len(checks),'handwriting_qualified':False,'network_attempts':len(w.NETWORK_ATTEMPTS)}))
