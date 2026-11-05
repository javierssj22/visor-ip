from pathlib import Path
import urllib.request, zipfile, json, ast, hashlib

root=Path(__file__).resolve().parent/'training'
root.mkdir(exist_ok=True)
def fetch(url,path):
    if not path.exists():
        path.parent.mkdir(parents=True,exist_ok=True)
        temp=path.with_suffix(path.suffix+'.download')
        urllib.request.urlretrieve(url,temp)
        temp.replace(path)
    print(path.name, path.stat().st_size, flush=True)

fetch('https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip',root.parent/'downloads'/'python-3.12.10-embed-amd64.zip')
runtime=root/'runtime'
runtime.mkdir(exist_ok=True)
with zipfile.ZipFile(root.parent/'downloads'/'python-3.12.10-embed-amd64.zip') as z:z.extractall(runtime)
(runtime/'python312._pth').write_text('python312.zip\n.\nLib/site-packages\n..\nimport site\n',encoding='utf-8')
commit='6ddff4824372906469a7fae2dc3206c7aa4bbaee'
base=f'https://raw.githubusercontent.com/Megvii-BaseDetection/YOLOX/{commit}/'
for name in ['darknet','network_blocks','losses','yolo_head','yolo_pafpn','yolox']:
    fetch(base+f'yolox/models/{name}.py',root/'yolox'/'models'/f'{name}.py')
(root/'yolox'/'__init__.py').write_text('',encoding='utf-8')
(root/'yolox'/'models'/'__init__.py').write_text('from .yolox import YOLOX\nfrom .yolo_head import YOLOXHead\nfrom .yolo_pafpn import YOLOPAFPN\n',encoding='utf-8')
source=urllib.request.urlopen(base+'yolox/utils/boxes.py').read().decode()
tree=ast.parse(source)
parts=[ast.get_source_segment(source,n) for n in tree.body if isinstance(n,ast.FunctionDef) and n.name in ('bboxes_iou','cxcywh2xyxy')]
(root/'yolox'/'utils').mkdir(exist_ok=True)
(root/'yolox'/'utils'/'__init__.py').write_text('# Copyright (c) Megvii Inc. Apache-2.0. Minimal CPU subset.\nimport torch\n\n'+'\n\n'.join(parts)+'\n\ndef meshgrid(*args):\n    return torch.meshgrid(*args,indexing="ij")\n\ndef visualize_assign(*args,**kwargs):\n    raise NotImplementedError("Training visualization is not bundled")\n',encoding='utf-8')
fetch(base+'LICENSE',root/'YOLOX-LICENSE.txt')
fetch('https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_s.pth',root/'yolox_s.pth')
(root/'SOURCE.txt').write_text('YOLOX Apache-2.0, upstream commit '+commit+'\nMinimal model/loss subset; utils includes only required box functions. No CUDA trainer.\n'+base+'\nWeights: official 0.1.1rc0/yolox_s.pth\n',encoding='utf-8')
manifest={str(p.relative_to(root)):hashlib.sha256(p.read_bytes()).hexdigest() for p in root.rglob('*') if p.is_file()}
(root/'UPSTREAM-SHA256.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
