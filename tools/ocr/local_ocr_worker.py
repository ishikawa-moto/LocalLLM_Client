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
# JIS X 0213 retains Japanese kanji (including extended Japanese names), kana,
# punctuation and ASCII. Shared Japanese/Chinese ideographs cannot be classified
# by language from a code point alone. Never delete the whole Han Unicode block.
LANGUAGE_POLICY = 'ja-jis0213-ascii-v1'
EXCLUDED_MARKER = '[対象外文字あり]'

def target_character(character):
    if len(character) != 1:
        return False
    code = ord(character)
    if (0x1100 <= code <= 0x11ff or 0x3130 <= code <= 0x318f or
        0xa960 <= code <= 0xa97f or 0xac00 <= code <= 0xd7ff or
        0x3100 <= code <= 0x312f or 0x31a0 <= code <= 0x31bf):
        return False
    try:
        character.encode('euc_jis_2004')
        return True
    except UnicodeEncodeError:
        return False

class JapaneseDecode:
    """Exclude non-target labels before CTC decoding, without guessing a substitute.

    Keep blank competitive with the excluded maximum. Simply zeroing foreign
    scores would force a weaker Japanese guess and inflate apparent confidence.
    Mark affected lines explicitly; no excluded text is retained as a hypothesis.
    """
    def __init__(self, decoder):
        if not decoder.character or decoder.character[0] != 'blank':
            raise OcrError('LANGUAGE_DECODER_INCOMPATIBLE')
        self.decoder = decoder
        self.character = tuple(decoder.character)
        self.blocked = tuple(i for i, c in enumerate(self.character)
                             if i and not target_character(c))
        self.excluded_steps = 0

    def __call__(self, preds, return_word_box=False, **kwargs):
        import numpy as np
        if return_word_box or preds.ndim != 3 or preds.shape[2] != len(self.character):
            raise OcrError('LANGUAGE_DECODER_INCOMPATIBLE')
        if not self.blocked:
            return self.decoder(preds, return_word_box=False, **kwargs)
        winners = preds.argmax(axis=2)
        excluded = np.isin(winners, self.blocked)
        self.excluded_steps += int(excluded.sum())
        limited = preds.copy()
        limited[:, :, 0] = np.where(excluded, preds[:, :, self.blocked].max(axis=2), preds[:, :, 0])
        limited[:, :, self.blocked] = 0
        lines, words = self.decoder(limited, return_word_box=False, **kwargs)
        # The marker survives plain-text / table / Slack attachment exports.
        lines = [(text + EXCLUDED_MARKER if excluded[i].any() else text, score)
                 for i, (text, score) in enumerate(lines)]
        return lines, words

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
    params={'Global.log_level':'critical','Global.text_score':0.0,'Global.max_side_len':1280,'Global.return_word_box':False,
      'EngineConfig.onnxruntime.intra_op_num_threads':2,'EngineConfig.onnxruntime.inter_op_num_threads':1,
      'EngineConfig.onnxruntime.use_cuda':False,'EngineConfig.onnxruntime.use_dml':False,
      'Det.model_path':str(model_root/det),'Det.ocr_version':version,'Det.model_type':ModelType.MOBILE,
      'Rec.model_path':str(model_root/rec),'Rec.ocr_version':version,'Rec.model_type':ModelType.MOBILE,'Rec.lang_type':'ch' if variant=='v5' else 'japan',
      'Cls.model_path':str(model_root/cls),'Cls.ocr_version':OCRVersion.PPOCRV4,'Cls.model_type':ModelType.MOBILE,'Rec.rec_batch_num':2}
    engine=RapidOCR(params=params)
    engine.text_rec.postprocess_op=JapaneseDecode(engine.text_rec.postprocess_op)
    providers={name:getattr(engine,name).session.session.get_providers() for name in ('text_det','text_rec','text_cls')}
    if any(p!=['CPUExecutionProvider'] for p in providers.values()):raise OcrError('CPU_PROVIDER_REQUIRED')
    return engine,dict(engine='RapidOCR',version=importlib.metadata.version('rapidocr'),recognition_model=rec,detection_model=det,model_hashes={name:manifest['files'][name]['sha256'] for name in [det,rec,cls]},runtime='onnxruntime',runtime_version=importlib.metadata.version('onnxruntime'),requested_providers=['CPUExecutionProvider'],actual_providers=providers,threads=2,language_policy=LANGUAGE_POLICY)

# Geometry uses a bounded preview; recognition keeps original pixels in bounded tiles.
# No page-wide OpenCV masks or ONNX tensors are created for a large photograph.
TILE_CORE=960
TILE_HALO=128
LAYOUT_SIDE=1600

def grid_cells(image):
    import cv2,numpy as np
    from PIL import Image
    preview=image.copy();preview.thumbnail((LAYOUT_SIDE,LAYOUT_SIDE))
    sx=image.width/preview.width;sy=image.height/preview.height
    gray=cv2.cvtColor(np.asarray(preview),cv2.COLOR_RGB2GRAY)
    binary=cv2.threshold(gray,0,255,cv2.THRESH_BINARY_INV|cv2.THRESH_OTSU)[1]
    height,width=gray.shape
    horizontal=cv2.morphologyEx(binary,cv2.MORPH_OPEN,cv2.getStructuringElement(cv2.MORPH_RECT,(max(25,width//30),1)))
    vertical=cv2.morphologyEx(binary,cv2.MORPH_OPEN,cv2.getStructuringElement(cv2.MORPH_RECT,(1,max(25,height//30))))
    grid=cv2.bitwise_or(horizontal,vertical)
    count,labels,stats,_=cv2.connectedComponentsWithStats(cv2.dilate(grid,np.ones((3,3),np.uint8)))
    def clusters(values):
        groups=[]
        for value in values:
            if groups and value-groups[-1][-1]<4:groups[-1].append(value)
            else:groups.append([value])
        return [int(round(sum(g)/len(g))) for g in groups]
    cells=[];table_number=0
    for region in sorted(stats[1:].tolist(),key=lambda z:(z[1],z[0])):
        x,y,w,h,area=region
        if w<60 or h<40 or area<80:continue
        xs=clusters((np.flatnonzero((vertical[y:y+h,x:x+w]>0).sum(axis=0)>h*.35)+x).tolist())
        ys=clusters((np.flatnonzero((horizontal[y:y+h,x:x+w]>0).sum(axis=1)>w*.35)+y).tolist())
        if len(xs)<2 or len(ys)<2 or len(xs)*len(ys)>1000:continue
        # Closed spaces bounded by detected ruling; disconnected tables get separate axes.
        contours,_=cv2.findContours(255-grid[y:y+h,x:x+w],cv2.RETR_LIST,cv2.CHAIN_APPROX_SIMPLE)
        found=[];occupied=set()
        for contour in contours:
            cx,cy,cw,ch=cv2.boundingRect(contour);cx+=x;cy+=y
            if cw<8 or ch<8:continue
            c=min(range(len(xs)),key=lambda i:abs(xs[i]-cx));r=min(range(len(ys)),key=lambda i:abs(ys[i]-cy))
            c2=min(range(len(xs)),key=lambda i:abs(xs[i]-(cx+cw)));r2=min(range(len(ys)),key=lambda i:abs(ys[i]-(cy+ch)))
            if c2<=c or r2<=r or max(abs(xs[c]-cx),abs(xs[c2]-(cx+cw)),abs(ys[r]-cy),abs(ys[r2]-(cy+ch)))>5:continue
            if cx<xs[0]-2 or cy<ys[0]-2 or cx+cw>xs[-1]+3 or cy+ch>ys[-1]+3:continue
            cover={(a,b) for a in range(r,r2) for b in range(c,c2)}
            if occupied.intersection(cover):continue
            occupied.update(cover)
            found.append(dict(table_id=f'table_{table_number}',cell_id=f'p_cell_{table_number}_{r}_{c}',row_index=r,column_index=c,row_span=r2-r,column_span=c2-c,bounding_box=[xs[c]*sx,ys[r]*sy,xs[c2]*sx,ys[r2]*sy],text='',confidence=None,structure_confidence=None,needs_review=True,unreadable=False,structure_method='bounded_ruled_grid_not_general_layout_model'))
        if found:
            cells.extend(sorted(found,key=lambda c:(c['row_index'],c['column_index'])));table_number+=1
        if table_number>32 or len(cells)>4096:raise OcrError('TABLE_LIMIT')
    if not cells:return [],image
    if sx==sy==1:
        clean=np.asarray(image).copy();clean[cv2.dilate(grid,np.ones((3,3),np.uint8))>0]=255
        return cells,Image.fromarray(clean)
    return cells,image

def aligned_cells(spans):
    """Conservative unruled alignment hypothesis, never a verified table structure.

    Require three consecutive data rows and at least one stable numeric right
    edge. Text is copied from evidence; no totals, labels, or merged cells guessed.
    """
    import statistics,re,unicodedata
    if len(spans)<6:return []
    boxes=[s['recognition_raster_box'] for s in spans]
    height=statistics.median(b[3]-b[1] for b in boxes)
    if height<=0:return []
    rows=[]
    for span in sorted(spans,key=lambda s:(s['recognition_raster_box'][1],s['recognition_raster_box'][0])):
        b=span['recognition_raster_box'];cy=(b[1]+b[3])/2
        if rows and abs(rows[-1][0]-cy)<height*.65:rows[-1][1].append(span)
        else:rows.append([cy,[span]])
    numeric=lambda text:re.fullmatch(r'[\s¥￥$€+−\-()0-9,.%円]+',unicodedata.normalize('NFKC',text)) is not None
    candidates=[row for row in rows if len(row[1])>=2 and any(numeric(s['text']) for s in row[1])]
    groups=[]
    for row in candidates:
        if groups and row[0]-groups[-1][-1][0]<height*5:groups[-1].append(row)
        else:groups.append([row])
    cells=[];table_index=0
    for group in groups:
        if len(group)<3:continue
        # Restrict inference to rows with a consistent number of separated spans.
        counts=[len(r[1]) for r in group];columns=max(set(counts),key=counts.count)
        if not 2<=columns<=12 or any(n!=columns for n in counts):continue
        data=[sorted(row[1],key=lambda s:s['recognition_raster_box'][0]) for row in group]
        lefts=[];rights=[];consistent=True
        for col in range(columns):
            column=[row[col] for row in data];bs=[v['recognition_raster_box'] for v in column]
            anchors=[b[2] if numeric(v['text']) else b[0] for v,b in zip(column,bs)]
            if max(anchors)-min(anchors)>height*1.5:consistent=False;break
            lefts.append(min(b[0] for b in bs));rights.append(max(b[2] for b in bs))
        if not consistent or any(lefts[i+1]-rights[i]<height for i in range(columns-1)):continue
        boundaries=[lefts[0]-height*.25]+[(rights[i]+lefts[i+1])/2 for i in range(columns-1)]+[rights[-1]+height*.25]
        selected=group[:]
        above=[row for row in rows if 0<group[0][0]-row[0]<height*5 and len(row[1])==columns]
        if above:
            header=max(above,key=lambda row:row[0]);parts=sorted(header[1],key=lambda z:z['recognition_raster_box'][0])
            if all(boundaries[c]<=sum(z['recognition_raster_box'][::2])/2<=boundaries[c+1] for c,z in enumerate(parts)):selected.insert(0,header)
        ys=[min(s['recognition_raster_box'][1] for s in selected[0][1])-height*.25]+[(selected[i][0]+selected[i+1][0])/2 for i in range(len(selected)-1)]+[max(s['recognition_raster_box'][3] for s in selected[-1][1])+height*.25]
        for row_index,row in enumerate(selected):
            for col,span in enumerate(sorted(row[1],key=lambda z:z['recognition_raster_box'][0])):
                table_id=f'table_{table_index}';span.update(table_id=table_id,row_index=row_index,column_index=col)
                cells.append(dict(table_id=table_id,row_index=row_index,column_index=col,row_span=1,column_span=1,bounding_box=[max(0,boundaries[col]),max(0,ys[row_index]),boundaries[col+1],ys[row_index+1]],text='',confidence=None,structure_confidence=None,needs_review=True,unreadable=span['unreadable'],structure_method='experimental_aligned_text_not_verified_layout'))
        table_index+=1
    return cells

def recognition_regions(image):
    """Half-open ownership cores prevent overlap duplicates; halos preserve edge glyphs.

    Wide lines may cross a core. They are retained as separate OCR spans, never
    asserted to be an exact paragraph. Coordinates always refer to source raster.
    """
    if image.width<=1280 and image.height<=1280:
        yield image,(0,0),(0,0,image.width,image.height);return
    for y in range(0,image.height,TILE_CORE):
        for x in range(0,image.width,TILE_CORE):
            left=max(0,x-TILE_HALO);top=max(0,y-TILE_HALO)
            right=min(image.width,x+TILE_CORE+TILE_HALO);bottom=min(image.height,y+TILE_CORE+TILE_HALO)
            yield image.crop((left,top,right,bottom)),(left,top),(x,y,min(x+TILE_CORE,image.width),min(y+TILE_CORE,image.height))

def process_page(path,index,profile,models,variant='v5'):
    import cv2,numpy as np
    cv2.setNumThreads(1)
    image,space,transform=load_page(path,index)
    tables=[];warnings_out=[]
    if profile in ('TABLE_LAYOUT','AUTO','PDF_DOCUMENT'):
        tables,image=grid_cells(image)
        if profile=='TABLE_LAYOUT' and not tables:warnings_out.append('NO_CONFIDENT_RULED_GRID_UNRULED_LAYOUT_UNQUALIFIED')
    engine,identity=load_engine(models,variant)
    spans=[];tiles=0
    if image.width>1280 or image.height>1280:warnings_out.append('TILED_RECOGNITION_VERIFY_SMALL_TEXT_AND_SEAMS')
    for tile,(left,top),core in recognition_regions(image):
        tiles+=1
        result=engine(cv2.cvtColor(np.asarray(tile),cv2.COLOR_RGB2BGR))
        if result.txts is None:continue
        for box,candidate,confidence in zip(result.boxes,result.txts,result.scores):
            if len(box)!=4:raise OcrError('INVALID_COORDINATES')
            local=[[float(x),float(y)] for x,y in box]
            if not all(math.isfinite(v) for point in local for v in point) or any(x < -2 or y < -2 or x > tile.width+2 or y > tile.height+2 for x,y in local):raise OcrError('INVALID_COORDINATES')
            raw=[[min(tile.width,max(0,x))+left,min(tile.height,max(0,y))+top] for x,y in local]
            center=[sum(p[0] for p in raw)/4,sum(p[1] for p in raw)/4]
            if not(core[0]<=center[0]<core[2] and core[1]<=center[1]<core[3]):continue
            confidence=float(confidence)
            if not math.isfinite(confidence) or not 0<=confidence<=1:raise OcrError('INVALID_CONFIDENCE')
            if len(candidate)>4096:raise OcrError('SPAN_TEXT_LIMIT')
            if len(spans)>=2500:raise OcrError('SPAN_LIMIT')
            poly=[list(transform(x,y)) for x,y in raw]
            bbox=[min(p[0] for p in poly),min(p[1] for p in poly),max(p[0] for p in poly),max(p[1] for p in poly)]
            raw_width=max(p[0] for p in raw)-min(p[0] for p in raw);raw_height=max(p[1] for p in raw)-min(p[1] for p in raw)
            vertical=raw_height>max(1,raw_width)*2
            if vertical and 'VERTICAL_TEXT_UNQUALIFIED' not in warnings_out:warnings_out.append('VERTICAL_TEXT_UNQUALIFIED')
            seam=(left>0 and min(p[0] for p in raw)<=left+2) or (top>0 and min(p[1] for p in raw)<=top+2) or (left+tile.width<image.width and max(p[0] for p in raw)>=left+tile.width-2) or (top+tile.height<image.height and max(p[1] for p in raw)>=top+tile.height-2)
            uncertain=confidence<0.85 or vertical or EXCLUDED_MARKER in candidate or seam;unreadable=confidence<0.50
            i=len(spans)
            span=dict(recognition_raster_box=[min(p[0] for p in raw),min(p[1] for p in raw),max(p[0] for p in raw),max(p[1] for p in raw)],page=index,block=None,paragraph=None,line=i,span_id=f'p{index}_s{i}',text='[判読不能]' if unreadable else candidate,hypothesis=candidate if unreadable else None,polygon=poly,bounding_box=bbox,confidence=confidence,confidence_kind='uncalibrated_recognizer_score_not_probability_of_correctness',needs_review=uncertain,unreadable=unreadable,table_id=None,row_index=None,column_index=None)
            matching=[cell for cell in tables if cell['bounding_box'][0]<=center[0]<=cell['bounding_box'][2] and cell['bounding_box'][1]<=center[1]<=cell['bounding_box'][3]]
            if len(matching)==1:
                cell=matching[0];cell['text']+=(' ' if cell['text'] else '')+span['text'];cell['unreadable']|=unreadable
                span.update(table_id=cell['table_id'],row_index=cell['row_index'],column_index=cell['column_index'])
            spans.append(span)
        del result
        if tile is not image:tile.close()
    if engine.text_rec.postprocess_op.excluded_steps:warnings_out.append('NON_JAPANESE_CANDIDATE_EXCLUDED_VERIFY_SOURCE')
    spans.sort(key=lambda s:(s['bounding_box'][1],s['bounding_box'][0]))
    if not tables and profile in ('AUTO','TABLE_LAYOUT','PDF_DOCUMENT'):
        tables=aligned_cells(spans)
        if tables:warnings_out.append('EXPERIMENTAL_UNRULED_ALIGNMENT_REQUIRES_SOURCE_VERIFICATION')
    for cell in tables:
        x,y,x2,y2=cell['bounding_box'];cell['bounding_box']=[max(0,x),max(0,y),min(image.width,x2),min(image.height,y2)]
        members=[s for s in spans if s['table_id']==cell['table_id'] and s['row_index']==cell['row_index'] and s['column_index']==cell['column_index']]
        cell['text']=' '.join(s['text'] for s in members)
        cell['confidence']=min((s['confidence'] for s in members),default=None)
        cell['hypotheses']=[s['hypothesis'] for s in members if s['hypothesis']]
        cell['cell_id']=f"p{index}_{cell['table_id']}_r{cell['row_index']}_c{cell['column_index']}"
        x,y,x2,y2=cell['bounding_box'];poly=[list(transform(px,py)) for px,py in [(x,y),(x2,y),(x2,y2),(x,y2)]]
        cell['polygon']=poly;cell['bounding_box']=[min(p[0] for p in poly),min(p[1] for p in poly),max(p[0] for p in poly),max(p[1] for p in poly)]
    table_output=[]
    for table_id in dict.fromkeys(c['table_id'] for c in tables):
        cells=[c for c in tables if c['table_id']==table_id]
        rows=max(c['row_index']+c['row_span'] for c in cells);cols=max(c['column_index']+c['column_span'] for c in cells)
        matrix=[['']*cols for _ in range(rows)]
        for cell in cells:matrix[cell['row_index']][cell['column_index']]=cell['text']
        buf=io.StringIO();writer=csv.writer(buf)
        for row in matrix:writer.writerow(["'"+v if v.lstrip().startswith(('=','+','-','@')) or v.startswith(('\t','\r')) else v for v in row])
        lines=['| '+' | '.join(v.replace('&','&amp;').replace('<','&lt;').replace('>','&gt;').replace('|','\\|').replace('\n',' ') for v in row)+' |' for row in matrix]
        lines.insert(1,'| '+' | '.join(['---']*cols)+' |')
        box=[min(c['bounding_box'][0] for c in cells),min(c['bounding_box'][1] for c in cells),max(c['bounding_box'][2] for c in cells),max(c['bounding_box'][3] for c in cells)]
        table_output.append(dict(table_id=table_id,page=index,rows=rows,columns=cols,bounding_box=box,cells=cells,csv=buf.getvalue(),csv_formula_escaped=True,markdown='\n'.join(lines),needs_review=True,structure_confidence=None,structure_status='aligned_text_inferred_verify_before_use' if cells[0]['structure_method'].startswith('experimental') else 'ruled_grid_detected_verify_before_use'))
    if any(not c['structure_method'].startswith('experimental') for c in tables):warnings_out.append('RULED_TABLE_STRUCTURE_REQUIRES_VERIFICATION')
    if profile=='HANDWRITING':warnings_out.append('EXPERIMENTAL_HANDWRITING_NOT_QUALIFIED_FOR_NAMES_ADDRESSES_OR_FORMS')
    if not spans:warnings_out.append('NO_TEXT_DETECTED_NOT_PROOF_OF_BLANK_PAGE')
    space['recognition_mapping']=dict(method='source_raster_tiles_v1',tile_core=TILE_CORE,tile_halo=TILE_HALO,tiles=tiles,scaling=1)
    return dict(page=index,source_method='ocr',coordinate_system=space,spans=spans,tables=table_output,text='\n'.join(s['text'] for s in spans),needs_review=not spans or any(s['needs_review'] for s in spans) or bool(warnings_out),warnings=warnings_out,engine=identity)

def failure_code(error):
    if isinstance(error,OcrError):return error.code
    name=type(error).__name__
    # Classify locally; never persist the exception text, path or OCR content.
    if name in ('MemoryError','_ArrayMemoryError'):return 'WORKER_MEMORY_LIMIT'
    if type(error).__module__.startswith('onnxruntime'):
        message=str(error).lower()
        return 'ONNX_MEMORY_ALLOCATION' if any(v in message for v in ('allocate memory','memory allocation','bad_alloc','out of memory','failed to allocate')) else 'ONNX_EXECUTION_FAILED'
    return {'UnidentifiedImageError':'IMAGE_DECODE_FAILED','DecompressionBombError':'PIXEL_LIMIT','DecompressionBombWarning':'PIXEL_LIMIT','PdfReadError':'PDF_CORRUPT','PdfiumError':'PDF_CORRUPT'}.get(name,'WORKER_FAILURE')

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
        code=failure_code(error)
        print(json.dumps(dict(status='failed',error_code=code,error_type={'ONNX_MEMORY_ALLOCATION':'OnnxMemory','ONNX_EXECUTION_FAILED':'OnnxExecution','WORKER_MEMORY_LIMIT':'Memory','IMAGE_DECODE_FAILED':'ImageDecode'}.get(code,'Other'),network_attempts=len(NETWORK_ATTEMPTS))))
        sys.exit(2)
if __name__=='__main__':main()
