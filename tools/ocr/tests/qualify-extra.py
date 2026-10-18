"""Public raster-PDF rotation/crop/text-layer and mixed-handwriting checks; no runtime network."""
from pathlib import Path
import argparse,importlib.util,json
parser=argparse.ArgumentParser();parser.add_argument('--assets',type=Path,required=True);parser.add_argument('--fixtures',type=Path,required=True);parser.add_argument('--handwriting',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
target=args.output.resolve();target.mkdir(parents=True,exist_ok=False);assets=args.assets.resolve()
spec=importlib.util.spec_from_file_location('ocr',assets/'local_ocr_worker.py');w=importlib.util.module_from_spec(spec);spec.loader.exec_module(w);w.offline()
from PIL import Image
from pypdf import PdfWriter,PdfReader
from pypdf.generic import DictionaryObject,NameObject,DecodedStreamObject,ArrayObject,NumberObject
writer=PdfWriter();p=writer.add_blank_page(width=320,height=220)
font=DictionaryObject({NameObject('/Type'):NameObject('/Font'),NameObject('/Subtype'):NameObject('/Type1'),NameObject('/BaseFont'):NameObject('/Helvetica')})
p[NameObject('/Resources')]=DictionaryObject({NameObject('/Font'):DictionaryObject({NameObject('/F1'):writer._add_object(font)})})
stream=DecodedStreamObject();stream.set_data(b'BT /F1 18 Tf 30 120 Td (PUBLIC TEXT LAYER 2026) Tj ET');p[NameObject('/Contents')]=writer._add_object(stream)
p.cropbox.lower_left=(10,10);p.cropbox.upper_right=(310,210)
second=writer.add_blank_page(width=320,height=220)
second[NameObject('/Resources')]=p[NameObject('/Resources')];second[NameObject('/Contents')]=p[NameObject('/Contents')];second[NameObject('/CropBox')]=ArrayObject([NumberObject(10),NumberObject(10),NumberObject(310),NumberObject(210)]);second.rotate(90)
third=writer.add_blank_page(width=320,height=220);third[NameObject('/Resources')]=p[NameObject('/Resources')];third[NameObject('/Contents')]=p[NameObject('/Contents')];third.rotate(90)
scan=PdfReader(args.fixtures/'scan-two-pages.pdf');writer.add_page(scan.pages[0])
pdf=target/'public-mixed-rotated.pdf';writer.write(pdf)
checks=[];results=[]
for index in range(4):
 if index in (1,2):
  try:w.process_page(pdf,index,'PDF_DOCUMENT',assets/'models');raise AssertionError('Renderer source CropBox mismatch must reject')
  except w.OcrError as e:assert e.code==('PDF_RENDER_BOX_MISMATCH' if index==1 else 'PDF_RENDER_ROTATION_MISMATCH')
  checks.append({'page':index,'status':'rejected','error_code':'PDF_RENDER_BOX_MISMATCH' if index==1 else 'PDF_RENDER_ROTATION_MISMATCH','hidden_cropped_content_not_recognized':True});continue
 result=w.process_page(pdf,index,'PDF_DOCUMENT',assets/'models');result.update(input_sha256=w.digest(pdf),status='complete',external_ai='deny',network_attempts=len(w.NETWORK_ATTEMPTS))
 assert result['source_method']=='ocr'
 space=result['coordinate_system'];assert space['units']=='pdf_display_points'
 for span in result['spans']:
  x,y,x2,y2=span['bounding_box'];assert 0<=x<=x2<=space['width']+1 and 0<=y<=y2<=space['height']+1
 if index==0:assert space['page_rotation_degrees']==0 and 'PUBLICTEXTLAYER2026' in result['text'].replace(' ','').replace('\n','')
 if index==2:assert space['page_rotation_degrees']==90 and abs(space['width']-220)<1 and abs(space['height']-320)<1
 checks.append({'page':index,'coordinates_in_bounds':True,'source_method':'ocr','rotation':space.get('page_rotation_degrees'),'text_layer_semantics_verified':False})
 (target/f'page-{index}.json').write_text(json.dumps(result,ensure_ascii=False),encoding='utf-8');results.append(result)
printed=Image.open(args.fixtures/'printed-ja.png').convert('RGB');hand=Image.open(args.handwriting/'kmnist-ten-public.png').convert('RGB');mixed=Image.new('RGB',(max(printed.width,hand.width),printed.height+hand.height+32),'white');mixed.paste(printed,(0,0));mixed.paste(hand,(0,printed.height+16));mixed.save(target/'public-printed-historical-handwriting.png')
r=w.process_page(target/'public-printed-historical-handwriting.png',0,'HANDWRITING',assets/'models');assert r['needs_review'] and any('EXPERIMENTAL_HANDWRITING' in v for v in r['warnings'])
(target/'mixed-handwriting-result.json').write_text(json.dumps(r,ensure_ascii=False),encoding='utf-8')
assert not w.NETWORK_ATTEMPTS
report={'status':'PASS_PUBLIC_EXTRA_FORMAT_COORDINATE_CHECKS','checks':checks,'mixed_print_historical_handwriting_tested':True,'modern_handwriting_qualified':False,'photo_limit':'Synthetic camera-like JPG only; genuine camera-image quality not qualified.','network_attempts':0,'worker_sha256':w.digest(assets/'local_ocr_worker.py'),'attribution':'KMNIST historical handwritten kana, rois-codh/kmnist, CC-BY-SA4.0; composite with generated public print. Preserve source attribution/manifest.'}
(target/'extra-proof.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(report))
