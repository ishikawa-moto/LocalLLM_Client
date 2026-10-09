"""Isolated Phase8 preparation. No network, persistence, queue or knowledge adoption."""
from __future__ import annotations
import datetime,hashlib,io,importlib.metadata
from pypdf import PdfReader

MAX_BYTES=4*1024*1024  # Preserve current Brain import bound in this first prototype.
MAX_PAGES=200
def sha(raw:bytes)->str:return hashlib.sha256(raw).hexdigest()
def prepare(raw:bytes,source_uri:str,retrieved_at:str)->dict:
    if not isinstance(raw,bytes) or not raw or len(raw)>MAX_BYTES:raise ValueError('reference input size exceeds current bound')
    if not raw.startswith(b'%PDF-'):raise ValueError('input is not a PDF')
    if not isinstance(source_uri,str) or not source_uri.strip() or len(source_uri)>2000:raise ValueError('source URI required')
    if not isinstance(retrieved_at,str):raise ValueError('retrieval timestamp required')
    try:
        timestamp=datetime.datetime.fromisoformat(retrieved_at.replace('Z','+00:00'))
    except ValueError as exc:raise ValueError('retrieval timestamp must be ISO8601') from exc
    if timestamp.tzinfo is None:raise ValueError('retrieval timestamp must include timezone')
    reader=PdfReader(io.BytesIO(raw),strict=True)
    if reader.is_encrypted:raise ValueError('encrypted PDF requires an explicit separate workflow')
    if not 1<=len(reader.pages)<=MAX_PAGES:raise ValueError('page count exceeds reference bound')
    parts=[];pages=[];offset=0
    for index,page in enumerate(reader.pages,1):
        text=page.extract_text() or ''
        if not text.strip():raise ValueError('page has no extracted text; needs declared OCR or manual review')
        header=f'\n[PDF page {index}]\n';content=header+text.rstrip()+'\n';encoded=content.encode('utf-8')
        pages.append({'page':index,'start_byte':offset,'end_byte':offset+len(encoded),'text_sha256':sha(encoded)})
        parts.append(content);offset+=len(encoded)
        if offset>MAX_BYTES:raise ValueError('extracted text exceeds current bound')
    text=''.join(parts);original=sha(raw);derived=sha(text.encode())
    return {'schema_version':1,'status':'PREPARED_UNREVIEWED_REFERENCE_ONLY','original_pdf_sha256':original,
            'source_id':'source:'+original,'source_uri':source_uri,'retrieved_at':retrieved_at,
            'source_hash':derived,'extracted_text_sha256':derived,'text':text,'page_map':pages,
            'source_type':'pdf','original_bytes':len(raw),'extractor':'pypdf','extractor_version':importlib.metadata.version('pypdf'),
            'independent_document_count':1,'review_required':True,'claims_are_verified':False,
            'current_repository_authority':False,'network_fetch_performed':False,'knowledge_adopted':False}
