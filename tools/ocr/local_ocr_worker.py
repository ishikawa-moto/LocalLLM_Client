"""Fixed offline CPU OCR worker. Private documents never become prompts or tools.

Provisioning is a separate operation. One invocation handles exactly one page and
exits, releasing native sessions. Only Windows Host starts production invocations.
"""
from __future__ import annotations
import os
for _key in ('OMP_NUM_THREADS','OPENBLAS_NUM_THREADS','MKL_NUM_THREADS','NUMEXPR_NUM_THREADS'):
    os.environ[_key]='2'
os.environ.update(DO_NOT_TRACK='1',HF_HUB_OFFLINE='1',TRANSFORMERS_OFFLINE='1',HF_HUB_DISABLE_TELEMETRY='1')
import argparse, csv, hashlib, importlib.metadata, io, json, math, socket, sys, time, warnings
from pathlib import Path

MAX_INPUT_BYTES=25*1024*1024
MAX_PIXELS=20_000_000
MAX_RENDER_PIXELS=16_000_000
MAX_PAGES=200
MAX_RESULT_BYTES=8*1024*1024
PROFILES={'AUTO','PRINTED_TEXT','HANDWRITING','TABLE_LAYOUT','PDF_DOCUMENT'}
NETWORK_ATTEMPTS=[]
class OcrError(Exception):
    def __init__(self,code):self.code=code
def block_network(*args,**kwargs):
    NETWORK_ATTEMPTS.append('blocked_socket_operation')
    raise OcrError('NETWORK_FORBIDDEN')
def offline():
    socket.socket.connect=block_network
    socket.socket.connect_ex=block_network
    socket.create_connection=block_network
    socket.getaddrinfo=block_network
    socket.socket.sendto=block_network
    import urllib.request
    urllib.request.urlopen=block_network
    import onnxruntime
    onnxruntime.disable_telemetry_events()
def digest(path):
    h=hashlib.sha256()
    with Path(path).open('rb') as f:
        while chunk:=f.read(1024*1024):h.update(chunk)
    return h.hexdigest()
def atomic(path,value):
    data=json.dumps(value,ensure_ascii=False,allow_nan=False).encode('utf-8')
    if len(data)>MAX_RESULT_BYTES:raise OcrError('RESULT_TOO_LARGE')
    path=Path(path);temporary=path.with_name(path.name+'.tmp')
    with temporary.open('xb') as f:f.write(data);f.flush();os.fsync(f.fileno())
    temporary.replace(path)
def checked_input(path,expected):
    path=Path(path)
    if not path.is_file() or path.is_symlink():raise OcrError('INPUT_UNAVAILABLE')
    if path.stat().st_size>MAX_INPUT_BYTES:raise OcrError('INPUT_TOO_LARGE')
    if digest(path)!=expected:raise OcrError('INPUT_HASH_MISMATCH')
    return path
def inspect(path,expected):
    path=checked_input(path,expected)
    if path.suffix.lower()=='.pdf':
        import pypdf
        reader=pypdf.PdfReader(path,strict=True)
        if reader.is_encrypted:raise OcrError('PDF_ENCRYPTED')
        pages=len(reader.pages)
        if not 1<=pages<=MAX_PAGES:raise OcrError('PAGE_LIMIT')
        return dict(page_count=pages,input_kind='pdf')
    from PIL import Image
    Image.MAX_IMAGE_PIXELS=MAX_PIXELS
    with warnings.catch_warnings():
        warnings.simplefilter('error',Image.DecompressionBombWarning)
        with Image.open(path) as image:
            if image.format not in ('PNG','JPEG','WEBP'):raise OcrError('UNSUPPORTED_IMAGE')
            if image.width*image.height>MAX_PIXELS or max(image.size)>10000:raise OcrError('PIXEL_LIMIT')
            if getattr(image,'n_frames',1)!=1:raise OcrError('ANIMATED_IMAGE_NOT_SUPPORTED')
            image.verify()
    return dict(page_count=1,input_kind='image')
def load_page(path,index):
    from PIL import Image,ImageOps
    if path.suffix.lower()=='.pdf':
        import pypdfium2 as pdfium
        with pdfium.PdfDocument(path) as document:
            if not 0<=index<len(document):raise OcrError('PAGE_INDEX')
            page=document[index]
            try:
                # Compare native source boxes independently: some rotated/cropped PDFs are
                # interpreted differently by PDFium. Never silently OCR outside the source crop.
                import pypdf
                source_page=pypdf.PdfReader(path,strict=True).pages[index]
                source_crop=[float(v) for v in source_page.cropbox]
                source_media=[float(v) for v in source_page.mediabox]
                if any(abs(a-b)>0.01 for a,b in zip(source_crop,page.get_cropbox())) or any(abs(a-b)>0.01 for a,b in zip(source_media,page.get_mediabox())):raise OcrError('PDF_RENDER_BOX_MISMATCH')
                if source_page.rotation%360!=page.get_rotation():raise OcrError('PDF_RENDER_ROTATION_MISMATCH')
                width,height=page.get_size()
                if min(width,height)<=0 or max(width,height)>20000:raise OcrError('PDF_PAGE_DIMENSION')
                scale=min(2.0,math.sqrt(MAX_RENDER_PIXELS/(width*height)))
                if scale<0.2:raise OcrError('PDF_PAGE_DIMENSION')
                bitmap=page.render(scale=scale)
                try: image=bitmap.to_pil().convert('RGB')
                finally:bitmap.close()
                space=dict(units='pdf_display_points',origin='top_left',width=width,height=height,page_rotation_degrees=page.get_rotation(),crop_box_pdf_native=list(page.get_cropbox()),media_box_pdf_native=list(page.get_mediabox()),raster_width=image.width,raster_height=image.height)
                return image,space,lambda x,y:(x*width/image.width,y*height/image.height)
            finally:page.close()
    if index!=0:raise OcrError('PAGE_INDEX')
    with Image.open(path) as original:
        width,height=original.size;orientation=int(original.getexif().get(274,1))
        image=ImageOps.exif_transpose(original).convert('RGB')
    # Edge coordinates (continuous bounding boxes), inverse of EXIF transpose.
    maps={1:lambda x,y:(x,y),2:lambda x,y:(width-x,y),3:lambda x,y:(width-x,height-y),4:lambda x,y:(x,height-y),5:lambda x,y:(y,x),6:lambda x,y:(y,height-x),7:lambda x,y:(width-y,height-x),8:lambda x,y:(width-y,x)}
    if orientation not in maps:raise OcrError('EXIF_ORIENTATION')
    return image,dict(units='original_image_pixels',origin='top_left',width=width,height=height,exif_orientation=orientation,raster_width=image.width,raster_height=image.height),maps[orientation]
def load_engine(model_root,variant):
    from rapidocr import RapidOCR,OCRVersion,ModelType
    model_root=Path(model_root)
    manifest=json.loads((model_root/'models-manifest.json').read_text(encoding='utf-8'))
    if manifest.get('schema_version')!=1:raise OcrError('MODEL_MANIFEST_INVALID')
    choices={'v5':('ch_PP-OCRv5_det_mobile.onnx','ch_PP-OCRv5_rec_mobile.onnx'),'japan_v4':('ch_PP-OCRv4_det_mobile.onnx','japan_PP-OCRv4_rec_mobile.onnx')}
    if variant not in choices:raise OcrError('ENGINE_VARIANT')
    det,rec=choices[variant];cls='ch_ppocr_mobile_v2.0_cls_mobile.onnx'
    for name in [det,rec,cls]:
        item=manifest['files'].get(name)
        if not item or not (model_root/name).is_file() or digest(model_root/name)!=item['sha256']:raise OcrError('MODEL_HASH_MISMATCH')
    version=OCRVersion.PPOCRV5 if variant=='v5' else OCRVersion.PPOCRV4
    params={'Global.log_level':'critical','Global.text_score':0.0,'Global.max_side_len':2400,'Global.return_word_box':False,
      'EngineConfig.onnxruntime.intra_op_num_threads':2,'EngineConfig.onnxruntime.inter_op_num_threads':1,
      'EngineConfig.onnxruntime.use_cuda':False,'EngineConfig.onnxruntime.use_dml':False,
      'Det.model_path':str(model_root/det),'Det.ocr_version':version,'Det.model_type':ModelType.MOBILE,
      'Rec.model_path':str(model_root/rec),'Rec.ocr_version':version,'Rec.model_type':ModelType.MOBILE,'Rec.lang_type':'ch' if variant=='v5' else 'japan',
      'Cls.model_path':str(model_root/cls),'Cls.ocr_version':OCRVersion.PPOCRV4,'Cls.model_type':ModelType.MOBILE,'Rec.rec_batch_num':2}
    engine=RapidOCR(params=params)
    providers={name:getattr(engine,name).session.session.get_providers() for name in ('text_det','text_rec','text_cls')}
    if any(p!=['CPUExecutionProvider'] for p in providers.values()):raise OcrError('CPU_PROVIDER_REQUIRED')
    return engine,dict(engine='RapidOCR',version=importlib.metadata.version('rapidocr'),recognition_model=rec,detection_model=det,model_hashes={name:manifest['files'][name]['sha256'] for name in [det,rec,cls]},runtime='onnxruntime',runtime_version=importlib.metadata.version('onnxruntime'),requested_providers=['CPUExecutionProvider'],actual_providers=providers,threads=2)
def grid_cells(image):
    import cv2,numpy as np
    gray=cv2.cvtColor(np.array(image),cv2.COLOR_RGB2GRAY)
    binary=cv2.threshold(gray,0,255,cv2.THRESH_BINARY_INV|cv2.THRESH_OTSU)[1]
    height,width=gray.shape
    horizontal=cv2.morphologyEx(binary,cv2.MORPH_OPEN,cv2.getStructuringElement(cv2.MORPH_RECT,(max(25,width//15),1)))
    vertical=cv2.morphologyEx(binary,cv2.MORPH_OPEN,cv2.getStructuringElement(cv2.MORPH_RECT,(1,max(25,height//15))))
    def clusters(values):
        groups=[]
        for value in values:
            if groups and value-groups[-1][-1]<5:groups[-1].append(value)
            else:groups.append([value])
        return [int(sum(g)/len(g)) for g in groups]
    ys=clusters(np.flatnonzero((horizontal>0).sum(axis=1)>width*.30).tolist())
    xs=clusters(np.flatnonzero((vertical>0).sum(axis=0)>height*.20).tolist())
    if len(xs)<3 or len(ys)<3 or len(xs)*len(ys)>1000:return [],image
    grid=cv2.bitwise_or(horizontal,vertical)
    contours,_=cv2.findContours(255-grid,cv2.RETR_LIST,cv2.CHAIN_APPROX_SIMPLE)
    cells=[]
    for contour in contours:
        x,y,w,h=cv2.boundingRect(contour)
        if w<12 or h<12 or w*h>width*height*.85:continue
        edges=[min(range(len(xs)),key=lambda i:abs(xs[i]-x)),min(range(len(ys)),key=lambda i:abs(ys[i]-y)),min(range(len(xs)),key=lambda i:abs(xs[i]-(x+w))),min(range(len(ys)),key=lambda i:abs(ys[i]-(y+h)))]
        c,r,c2,r2=edges
        if c2<=c or r2<=r or max(abs(xs[c]-x),abs(xs[c2]-(x+w)),abs(ys[r]-y),abs(ys[r2]-(y+h)))>8:continue
        cells.append(dict(table_id='table_0',row_index=r,column_index=c,row_span=r2-r,column_span=c2-c,bounding_box=[xs[c],ys[r],xs[c2],ys[r2]],text='',confidence=None,structure_method='detected_ruled_grid_not_general_layout_model'))
    cells.sort(key=lambda c:(c['row_index'],c['column_index']))
    if not cells:return [],image
    clean=np.array(image).copy();clean[cv2.dilate(grid,np.ones((3,3),np.uint8))>0]=255
    from PIL import Image
    return cells,Image.fromarray(clean)
def process_page(path,index,profile,models,variant='v5'):
    import cv2,numpy as np
    cv2.setNumThreads(1)
    image,space,transform=load_page(path,index)
    tables=[];warnings_out=[]
    if profile in ('TABLE_LAYOUT','AUTO'):
        tables,image=grid_cells(image)
        if profile=='TABLE_LAYOUT' and not tables:warnings_out.append('NO_CONFIDENT_RULED_GRID_UNRULED_LAYOUT_UNQUALIFIED')
    engine,identity=load_engine(models,variant)
    result=engine(cv2.cvtColor(np.array(image),cv2.COLOR_RGB2BGR))
    spans=[]
    if result.txts is not None:
        if len(result.txts)>2500:raise OcrError('SPAN_LIMIT')
        for i,(box,candidate,confidence) in enumerate(zip(result.boxes,result.txts,result.scores)):
            confidence=float(confidence)
            if not math.isfinite(confidence):raise OcrError('INVALID_CONFIDENCE')
            if len(candidate)>4096:raise OcrError('SPAN_TEXT_LIMIT')
            raw=[[float(x),float(y)] for x,y in box];poly=[list(transform(x,y)) for x,y in raw]
            bbox=[min(p[0] for p in poly),min(p[1] for p in poly),max(p[0] for p in poly),max(p[1] for p in poly)]
            raw_width=max(p[0] for p in raw)-min(p[0] for p in raw);raw_height=max(p[1] for p in raw)-min(p[1] for p in raw)
            vertical=raw_height>max(1,raw_width)*2
            if vertical and 'VERTICAL_TEXT_UNQUALIFIED' not in warnings_out:warnings_out.append('VERTICAL_TEXT_UNQUALIFIED')
            uncertain=confidence<0.85 or vertical;unreadable=confidence<0.50
            span=dict(page=index,block=None,paragraph=None,line=i,span_id=f'p{index}_s{i}',text='[判読不能]' if unreadable else candidate,hypothesis=candidate if unreadable else None,polygon=poly,bounding_box=bbox,confidence=confidence,confidence_kind='uncalibrated_recognizer_score_not_probability_of_correctness',needs_review=uncertain,unreadable=unreadable,table_id=None,row_index=None,column_index=None)
            center=[sum(p[0] for p in raw)/4,sum(p[1] for p in raw)/4]
            matching=[cell for cell in tables if cell['bounding_box'][0]<=center[0]<=cell['bounding_box'][2] and cell['bounding_box'][1]<=center[1]<=cell['bounding_box'][3]]
            if len(matching)==1:
                cell=matching[0];cell['text']+=(' ' if cell['text'] else '')+span['text']
                span.update(table_id=cell['table_id'],row_index=cell['row_index'],column_index=cell['column_index'])
            spans.append(span)
    for cell in tables:
        x,y,x2,y2=cell['bounding_box'];poly=[list(transform(px,py)) for px,py in [(x,y),(x2,y),(x2,y2),(x,y2)]]
        cell['polygon']=poly;cell['bounding_box']=[min(p[0] for p in poly),min(p[1] for p in poly),max(p[0] for p in poly),max(p[1] for p in poly)]
    table_output=[]
    if tables:
        warnings_out.append('RULED_TABLE_STRUCTURE_REQUIRES_VERIFICATION')
        rows=max(c['row_index']+c['row_span'] for c in tables);cols=max(c['column_index']+c['column_span'] for c in tables)
        matrix=[['']*cols for _ in range(rows)]
        for cell in tables:matrix[cell['row_index']][cell['column_index']]=cell['text']
        buf=io.StringIO();writer=csv.writer(buf)
        for row in matrix:writer.writerow(["'"+s if s.lstrip().startswith(('=','+','-','@')) or s.startswith(('\t','\r')) else s for s in row])
        lines=['| '+' | '.join(s.replace('&','&amp;').replace('<','&lt;').replace('>','&gt;').replace('|','\\|').replace('\n',' ') for s in row)+' |' for row in matrix]
        lines.insert(1,'| '+' | '.join(['---']*cols)+' |');markdown='\n'.join(lines)
        table_output=[dict(table_id='table_0',rows=rows,columns=cols,cells=tables,csv=buf.getvalue(),csv_formula_escaped=True,markdown=markdown,structure_status='ruled_grid_detected_verify_before_use')]
    if profile=='HANDWRITING':warnings_out.append('EXPERIMENTAL_HANDWRITING_NOT_QUALIFIED_FOR_NAMES_ADDRESSES_OR_FORMS')
    if not spans:warnings_out.append('NO_TEXT_DETECTED_NOT_PROOF_OF_BLANK_PAGE')
    return dict(page=index,source_method='ocr',coordinate_system=space,spans=spans,tables=table_output,text='\n'.join(s['text'] for s in spans),needs_review=not spans or any(s['needs_review'] for s in spans) or bool(warnings_out),warnings=warnings_out,engine=identity)
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--request',required=True);args=parser.parse_args()
    request=json.loads(Path(args.request).read_text(encoding='utf-8-sig'))
    started=time.monotonic();offline()
    try:
        path=checked_input(request['input_path'],request['input_sha256'])
        metadata=inspect(path,request['input_sha256'])
        if request['operation']=='inspect':result=metadata
        elif request['operation']=='page':
            if request['profile'] not in PROFILES:raise OcrError('PROFILE_INVALID')
            result=process_page(path,int(request['page']),request['profile'],request['model_root'],request.get('variant','v5'))
        else:raise OcrError('OPERATION_INVALID')
        result.update(status='complete',input_sha256=request['input_sha256'],external_ai='deny',network_attempts=len(NETWORK_ATTEMPTS),elapsed_ms=int((time.monotonic()-started)*1000))
        atomic(request['result_path'],result)
        print(json.dumps(dict(status='complete',network_attempts=len(NETWORK_ATTEMPTS),elapsed_ms=result['elapsed_ms'])))
    except Exception as error:
        # Never include parser messages, input names, document text or traceback.
        code=error.code if isinstance(error,OcrError) else {'MemoryError':'WORKER_MEMORY_LIMIT','UnidentifiedImageError':'IMAGE_CORRUPT','DecompressionBombError':'PIXEL_LIMIT','DecompressionBombWarning':'PIXEL_LIMIT','PdfReadError':'PDF_CORRUPT','PdfiumError':'PDF_CORRUPT'}.get(type(error).__name__,'WORKER_FAILURE')
        print(json.dumps(dict(status='failed',error_code=code,error_type=type(error).__name__,network_attempts=len(NETWORK_ATTEMPTS))))
        sys.exit(2)
if __name__=='__main__':main()
