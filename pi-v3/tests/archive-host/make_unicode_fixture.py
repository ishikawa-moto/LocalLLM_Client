import io,tarfile
from pathlib import Path
path=Path(__file__).parent/'obj'/'unicode.tar'
path.parent.mkdir(exist_ok=True)
with path.open('wb') as output, tarfile.open(fileobj=output,mode='w|') as archive:
    item=tarfile.TarInfo('日本語.txt');item.size=2;item.mtime=0
    archive.addfile(item,io.BytesIO(b'ok'))
