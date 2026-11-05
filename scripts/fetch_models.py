"""Download official pretrained models only; never fetch user camera images."""
from pathlib import Path
import hashlib, urllib.request

root=Path(__file__).resolve().parents[1]/'work'/'ai'
root.mkdir(parents=True,exist_ok=True)
models={
    'yolox_tiny.onnx':'427cc366d34e27ff7a03e2899b5e3671425c262ea2291f88bb942bc1cc70b0f7',
    'yolox_s.onnx':'c5c2d13e59ae883e6af3b45daea64af4833a4951c92d116ec270d9ddbe998063',
    'yolox_m.onnx':'21ff6cfdeb53b013bac2249599e55f00bff3cfdfdab37ed7a4620818c1d15b3f',
}
def digest(path):
    with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()
for name,expected in models.items():
    path=root/name
    if not path.exists():
        temp=path.with_suffix('.download')
        urllib.request.urlretrieve('https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/'+name,temp)
        if digest(temp)!=expected:raise RuntimeError('Checksum no coincide: '+name)
        temp.replace(path)
    if digest(path)!=expected:raise RuntimeError('Checksum no coincide: '+name)
    print(name+': SHA256 OK')
licenses={
    'YOLOX-LICENSE.txt':'https://raw.githubusercontent.com/Megvii-BaseDetection/YOLOX/6ddff4824372906469a7fae2dc3206c7aa4bbaee/LICENSE',
    'ONNXRUNTIME-LICENSE.txt':'https://raw.githubusercontent.com/microsoft/onnxruntime/v1.23.2/LICENSE',
}
for name,url in licenses.items():
    if not (root/name).exists():urllib.request.urlretrieve(url,root/name)
