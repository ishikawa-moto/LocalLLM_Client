"""Host-invoked local reference bundle preparation; never knowledge adoption."""
import hashlib,json,os,pathlib,uuid
from reference_pdf_prepare import prepare

def digest(raw):return hashlib.sha256(raw).hexdigest()
def match_original(existing,record):
    if existing['original_pdf_sha256']!=record['original_pdf_sha256'] or existing['source_id']!=record['source_id']:
        raise ValueError('Existing bundle belongs to a different original PDF')
    if existing['source_hash']!=record['source_hash']:
        raise ValueError('Same original has different extraction; explicit revision required')
def validate(folder):
    folder=pathlib.Path(folder)
    manifest=json.loads((folder/'manifest.json').read_text(encoding='utf-8'))
    if manifest['schema']!='localbrain.external-reference.v1':raise ValueError('Unknown reference bundle schema')
    if manifest['review_state']!='unverified' or manifest['allow_fact_promotion'] is not False:raise ValueError('Reference is not an unverified bundle')
    original=(folder/'original.pdf').read_bytes();text=(folder/'extracted.md').read_bytes()
    if digest(original)!=manifest['original_pdf_sha256'] or digest(text)!=manifest['source_hash']:raise ValueError('Immutable reference bytes differ')
    if manifest['source_id']!='source:'+digest(original):raise ValueError('Original reference identity differs')
    end=0
    for number,page in enumerate(manifest['page_map'],1):
        if page['page']!=number:raise ValueError('Page numbers differ')
        if page['start_byte']!=end or not end<page['end_byte']<=len(text):raise ValueError('Page map differs')
        if digest(text[end:page['end_byte']])!=page['text_sha256']:raise ValueError('Page text hash differs')
        end=page['end_byte']
    if end!=len(text):raise ValueError('Incomplete page map')
    return manifest

def persist(root,raw,source_uri,retrieved_at):
    record=prepare(raw,source_uri,retrieved_at)
    root=pathlib.Path(root).resolve();root.mkdir(parents=True,exist_ok=True)
    final=root/record['original_pdf_sha256']
    if final.exists():
        existing=validate(final)
        match_original(existing,record)
        return {'path':str(final),'duplicate':True,'source_id':existing['source_id'],'knowledge_adopted':False}
    stage=root/('.pending-'+uuid.uuid4().hex)
    if stage.resolve().parent!=root or final.resolve().parent!=root:raise ValueError('Bundle location differs from selected corpus')
    stage.mkdir()
    manifest={k:v for k,v in record.items() if k!='text'}
    manifest.update(schema='localbrain.external-reference.v1',review_state='unverified',allow_fact_promotion=False,
                    original_file='original.pdf',derived_file='extracted.md',explicit_claim_verification_required=True)
    files={'original.pdf':raw,'extracted.md':record['text'].encode('utf-8'),
           'manifest.json':(json.dumps(manifest,ensure_ascii=True,indent=2)+'\n').encode('utf-8')}
    # A failed preparation remains a .pending directory, never an eligible bundle.
    for name,content in files.items():
        with (stage/name).open('xb') as stream:stream.write(content);stream.flush();os.fsync(stream.fileno())
    validate(stage)
    try:os.rename(stage,final)
    except FileExistsError:
        existing=validate(final)
        match_original(existing,record)
        return {'path':str(final),'duplicate':True,'source_id':existing['source_id'],'knowledge_adopted':False}
    return {'path':str(final),'duplicate':False,'source_id':manifest['source_id'],'knowledge_adopted':False}
