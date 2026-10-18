from pathlib import Path
import argparse,os
import json,hashlib,math
from PIL import Image,ImageDraw,ImageFont,ImageFilter
parser=argparse.ArgumentParser();parser.add_argument('--output',type=Path,required=True);args=parser.parse_args();target=args.output.resolve();target.mkdir(parents=True,exist_ok=False)
font=ImageFont.truetype(str(Path(os.environ['WINDIR'])/'Fonts/YuGothM.ttc'),38)
text=['OCR公開テスト 2026年10月18日','試験番号 ABC-123 / 合計 12,345円','氏名：試験 太郎　住所：架空市テスト町1-2','電話：000-0000-0000　記号 + - / ( ) %']
image=Image.new('RGB',(1250,360),'white');d=ImageDraw.Draw(image)
for i,line in enumerate(text):d.text((35,25+i*76),line,font=font,fill='black')
image.save(target/'printed-ja.png')
image.save(target/'printed-ja.webp',lossless=True)
image.rotate(4,expand=True,fillcolor='white').filter(ImageFilter.GaussianBlur(.3)).save(target/'photo-skew.jpg',quality=87)
image.rotate(90,expand=True,fillcolor='white').save(target/'rotated-90.png')
vertical=Image.new('RGB',(380,650),'white');d=ImageDraw.Draw(vertical)
for column,line in enumerate(['日本語縦書き試験','令和八年十月十八日']):
 for row,char in enumerate(line):d.text((260-column*115,25+row*50),char,font=font,fill='black')
vertical.save(target/'vertical-ja.png')
table=Image.new('RGB',(1100,600),'white');d=ImageDraw.Draw(table)
xs=[30,360,700,1070];ys=[30,155,280,405,570]
for x in xs:d.line((x,30,x,570),fill='black',width=3)
for y in ys:d.line((30,y,1070,y),fill='black',width=3)
matrix=[['項目','数量','金額'],['テストA','2','1200'],['テストB','3','4500'],['合計','5','5700']]
for r,row in enumerate(matrix):
 for c,value in enumerate(row):d.text((xs[c]+15,ys[r]+30),value,font=font,fill='black')
table.save(target/'table-ruled.png')
unruled=Image.new('RGB',table.size,'white');d=ImageDraw.Draw(unruled)
for r,row in enumerate(matrix):
 for c,value in enumerate(row):d.text((xs[c]+15,ys[r]+30),value,font=font,fill='black')
unruled.save(target/'table-unruled.png')
merged=table.copy();d=ImageDraw.Draw(merged);d.line((360,32,360,153),fill='white',width=9);d.rectangle((40,40,690,145),fill='white');d.text((50,65),'結合セル試験',font=font,fill='black');merged.save(target/'table-merged.png')
image.save(target/'scan-two-pages.pdf',save_all=True,append_images=[table],resolution=144.0)
(target/'corrupt.pdf').write_bytes(b'%PDF-1.7\ninvalid public fixture')
(target/'corrupt.png').write_bytes(b'not an image')
blank=Image.new('RGB',(300,200),'white');blank.save(target/'blank.png')
manifest={'license':'CC0-1.0 generated non-confidential dummy fixtures; system font rasterization only, font files not redistributed','ground_truth':{'printed-ja.png':'\n'.join(text),'printed-ja.webp':'\n'.join(text),'photo-skew.jpg':'\n'.join(text),'rotated-90.png':'\n'.join(text),'vertical-ja.png':'日本語縦書き試験令和八年十月十八日'},'table_ground_truth':{'table-ruled.png':matrix,'table-unruled.png':matrix,'table-merged.png':{'header_merged_columns':2,'rows':4,'columns':3}},'limits':'Generated printed fixtures are not real photos or real handwriting. Handwriting qualification needs separate real labelled public data.','files':{p.name:{'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'bytes':p.stat().st_size} for p in target.iterdir() if p.is_file()}}
(target/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')

print('PUBLIC_PRINT_FIXTURES_CREATED_NO_PRIVATE_DATA')
