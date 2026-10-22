import importlib.util,unittest,types,json,math
from pathlib import Path
from PIL import Image,ImageDraw
from tempfile import TemporaryDirectory
from unittest.mock import patch
spec=importlib.util.spec_from_file_location('worker',Path(__file__).resolve().parents[1]/'local_ocr_worker.py');w=importlib.util.module_from_spec(spec);spec.loader.exec_module(w)
class BoundedLayoutTests(unittest.TestCase):
 def test_all_pixels_owned_once_and_tiles_bounded(self):
  for size in [(20,30),(1280,1280),(3060,4080),(10000,100)]:
   image=Image.new('RGB',size,'white');area=0
   for tile,offset,core in w.recognition_regions(image):
    self.assertLessEqual(tile.width,1280);self.assertLessEqual(tile.height,1280)
    self.assertGreaterEqual(core[0],offset[0]);self.assertGreaterEqual(core[1],offset[1]);area+=(core[2]-core[0])*(core[3]-core[1])
   self.assertEqual(area,size[0]*size[1])
 def test_multiple_tables_and_merged_cells(self):
  image=Image.new('RGB',(900,700),'white');d=ImageDraw.Draw(image)
  for y0 in (40,400):
   for y in (y0,y0+80,y0+160):d.line((40,y,640,y),fill='black',width=2)
   for x in (40,340,640):d.line((x,y0+80 if x==340 and y0==400 else y0,x,y0+160),fill='black',width=2)
  cells,_=w.grid_cells(image)
  self.assertEqual(len(set(c['table_id'] for c in cells)),2)
  self.assertEqual(len(cells),7);self.assertTrue(any(c['column_span']==2 for c in cells))
 def test_unruled_alignment_is_a_reviewable_hypothesis(self):
  spans=[]
  for row in range(4):
   for col,text in enumerate(['科目','金額','比率'] if row==0 else ['売上高','1,234','60.5%']):
    x=[30,400,800][col];y=50+row*80;spans.append(dict(text=text,recognition_raster_box=[x,y,x+100,y+30],unreadable=False))
  cells=w.aligned_cells(spans)
  self.assertEqual(len(cells),12);self.assertTrue(all(c['needs_review'] and c['structure_confidence'] is None for c in cells))
  self.assertTrue(all(c['row_span']==c['column_span']==1 for c in cells))
  self.assertEqual(w.aligned_cells(spans[:3]),[])
 def test_onnx_failure_classification_never_exports_message(self):
  klass=type('RuntimeException',(Exception,),{'__module__':'onnxruntime.capi.onnxruntime_pybind11_state'})
  self.assertEqual(w.failure_code(klass('failed to allocate memory PRIVATE')),'ONNX_MEMORY_ALLOCATION')
  self.assertEqual(w.failure_code(klass('PRIVATE invalid node')),'ONNX_EXECUTION_FAILED')
  wrapped=type('ONNXRuntimeError',(Exception,),{'__module__':'rapidocr.inference_engine.onnxruntime.main'})
  self.assertEqual(w.failure_code(wrapped('Traceback: Failed to allocate memory PRIVATE')),'ONNX_MEMORY_ALLOCATION')
  self.assertEqual(w.failure_code(wrapped('PRIVATE invalid node')),'ONNX_EXECUTION_FAILED')
  self.assertEqual(w.failure_code(wrapped('std::bad allocation')),'ONNX_MEMORY_ALLOCATION')
  self.assertEqual(w.failure_code(MemoryError('PRIVATE')),'WORKER_MEMORY_LIMIT')
  self.assertEqual(w.failure_code(ValueError('PRIVATE')),'WORKER_FAILURE')
 def test_japanese_shared_han_not_removed(self):
  self.assertTrue(all(w.target_character(c) for c in '日本語売上金額韓国中国123%'))
  self.assertFalse(w.target_character('한'))
 def test_transformed_tile_coordinates(self):
  with TemporaryDirectory() as directory:
   path=Path(directory)/'fixture.png';Image.new('RGB',(2000,300),'white').save(path)
   engine=lambda im:types.SimpleNamespace(txts=['公開'] if im.shape[1]>=300 else [],scores=[.99],boxes=[[[150,30],[250,30],[250,60],[150,60]]])
   engine.text_rec=types.SimpleNamespace(postprocess_op=types.SimpleNamespace(excluded_steps=0))
   with patch.object(w,'load_engine',return_value=(engine,{})):
    result=w.process_page(path,0,'PRINTED_TEXT',directory)
   self.assertEqual(len(result['spans']),2)
   self.assertEqual(result['spans'][1]['bounding_box'],[982,30,1082,60])
   self.assertTrue(all(s['bounding_box'][2]<=2000 for s in result['spans']))
if __name__=='__main__':unittest.main(verbosity=2)
