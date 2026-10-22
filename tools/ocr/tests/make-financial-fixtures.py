"""Synthetic Japanese tables only. No production document or network input."""
from pathlib import Path
from PIL import Image,ImageDraw,ImageFont
import json,sys
out=Path(sys.argv[1]);out.mkdir(parents=True,exist_ok=True)
font=ImageFont.truetype(r'C:\Windows\Fonts\meiryo.ttc',34)
truth=[['科目','金額（円）','構成比'],['売上高','1,234,567','60.5%'],['営業利益','345,678','16.9%'],['当期利益','98,765','4.8%']]
def table(draw,x,y,scale=1,merged=False):
 xs=[x,x+400*scale,x+780*scale,x+1080*scale];ys=[y+i*95*scale for i in range(5)]
 for yy in ys:draw.line((xs[0],yy,xs[-1],yy),fill='black',width=2*scale)
 for i,xx in enumerate(xs):draw.line((xx,ys[1] if merged and i==1 else ys[0],xx,ys[-1]),fill='black',width=2*scale)
 for r,row in enumerate(truth):
  for c,text in enumerate(row):
   if merged and r==0 and c==1:continue
   draw.text((xs[c]+15*scale,ys[r]+17*scale),text,font=font,fill='black')
 return truth
image=Image.new('RGB',(1320,1100),'white');d=ImageDraw.Draw(image);d.text((70,20),'公開合成：財務表',font=font,fill='black');table(d,80,110);table(d,80,620,merged=True)
d.text((80,1010),'注記：金額と比率は公開試験用の架空値です。',font=font,fill='black')
image.save(out/'financial.png');image.save(out/'financial.webp',lossless=True)
small=image.resize((990,825));small.save(out/'financial-small.jpg',quality=95)
large=Image.new('RGB',(3060,4080),'white');large.paste(image,(100,100));large.paste(image,(1600,2200));large.save(out/'financial-large.jpg',quality=95)
exif=image.getexif();exif[274]=6;image.save(out/'financial-exif.jpg',quality=95,exif=exif)
image.save(out/'financial.pdf',resolution=100)
(out/'truth.json').write_text(json.dumps({'private_data':False,'tables_per_image':2,'rows':4,'columns':3,'simple_table':truth,'merged_cell':{'row':0,'column':0,'colspan':2},'large_tables':4},ensure_ascii=False,indent=2),encoding='utf8')
print(json.dumps({'public_fixtures':6,'private_data':False}))
