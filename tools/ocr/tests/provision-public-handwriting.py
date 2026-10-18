"""Provision public labelled historical handwriting; never upload an image."""
from pathlib import Path
import urllib.request,json,hashlib,argparse
import numpy as np
from PIL import Image
parser=argparse.ArgumentParser();parser.add_argument('--output',type=Path,required=True);parser.add_argument('--provision-public-data',action='store_true');args=parser.parse_args();assert args.provision_public_data,'Explicit public test-data download permission required';target=args.output.resolve();target.mkdir(parents=True,exist_ok=False)
sources={
 'images.npz':'https://codh.rois.ac.jp/kmnist/dataset/kmnist/kmnist-test-imgs.npz',
 'labels.npz':'https://codh.rois.ac.jp/kmnist/dataset/kmnist/kmnist-test-labels.npz',
 'classmap.csv':'https://codh.rois.ac.jp/kmnist/dataset/kmnist/kmnist_classmap.csv',
 'LICENSE':'https://raw.githubusercontent.com/rois-codh/kmnist/master/LICENSE'}
for name,url in sources.items():
 p=target/name
 if p.exists():continue
 with urllib.request.urlopen(url,timeout=40) as r:data=r.read(5*1024*1024)
 if len(data)>=5*1024*1024:raise ValueError('download limit')
 p.write_bytes(data)
images=np.load(target/'images.npz')['arr_0'];labels=np.load(target/'labels.npz')['arr_0']
mapping={int(line.split(',')[0]):line.split(',')[2] for line in (target/'classmap.csv').read_text(encoding='utf-8').splitlines()[1:]}
indices=[int(np.flatnonzero(labels==i)[0]) for i in range(10)]
canvas=Image.new('RGB',(1400,210),'white')
for i,index in enumerate(indices):canvas.paste(Image.fromarray(255-images[index]).resize((112,112),Image.Resampling.NEAREST).convert('RGB'),(20+i*138,50))
canvas.save(target/'kmnist-ten-public.png')
truth=''.join(mapping[i] for i in range(10))
manifest={'dataset':'Kuzushiji-MNIST test split, ROIS-DS CODH / National Institute of Japanese Literature','source':'https://github.com/rois-codh/kmnist','license':'CC-BY-SA-4.0','derivative':'First labelled sample of each class, inverted and enlarged in a single row','indices':indices,'truth':truth,'limitation':'Historical cursive kana; not a benchmark of modern Japanese forms, names, addresses or free handwriting','files':{p.name:{'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'bytes':p.stat().st_size,'url':sources.get(p.name)} for p in target.iterdir() if p.is_file()}}
(target/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'status':'PUBLIC_HANDWRITING_READY','truth':truth,'samples':10},ensure_ascii=False))
