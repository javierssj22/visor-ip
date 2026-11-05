"""Local YOLOX head fine-tuning. No network access and no unreviewed labels.

Backbone is frozen for CPU use; classification, objectness and box regression
heads learn through the official YOLOX losses. Candidates never install themselves.
"""
from pathlib import Path
import os, sys, json, hashlib, math, random, time, argparse, traceback
from datetime import datetime

os.environ['OMP_NUM_THREADS']='1'
os.environ['MKL_NUM_THREADS']='1'
ROOT=Path(__file__).resolve().parent

def atomic_json(path, value):
    path=Path(path); path.parent.mkdir(parents=True,exist_ok=True)
    temp=path.with_suffix('.tmp')
    temp.write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8')
    os.replace(temp,path)

def load_samples(folder,files=None):
    """Validate every label; exact duplicates cannot leak into the holdout."""
    from PIL import Image
    samples=[]; seen={}
    paths=Path(folder).glob('*.json') if files is None else [Path(folder)/name for name in files if Path(name).name==name and '/' not in name and '\\' not in name]
    for file in sorted(paths):
        s=json.loads(file.read_text(encoding='utf-8-sig'))
        name=s['Image']
        if Path(name).name!=name or '/' in name or '\\' in name: raise ValueError('Nombre de captura inválido')
        image=Path(folder)/name
        with Image.open(image) as im:
            if im.size!=(s['Width'],s['Height']): raise ValueError('Dimensiones de captura inválidas')
            # Hash decoded pixels: JPEG metadata changes cannot create new examples.
            digest=hashlib.sha256(im.convert('RGB').tobytes()).hexdigest()
        boxes=[]
        for b in s['Boxes']:
            x,y,w,h=[float(b[k]) for k in ('X','Y','Width','Height')]
            if not all(math.isfinite(v) for v in (x,y,w,h)) or min(x,y)<0 or min(w,h)<=0 or x+w>1.00001 or y+h>1.00001: raise ValueError('Recuadro inválido')
            boxes.append([x,y,x+w,y+h])
        if digest in seen:
            if seen[digest]!=boxes: raise ValueError('La misma captura tiene etiquetas contradictorias. Revisá los ejemplos.')
            continue
        seen[digest]=boxes
        timestamp=datetime.fromisoformat(s['CapturedUtc'].replace('Z','+00:00')).timestamp()
        samples.append(dict(image=image,boxes=boxes,camera=s['Camera'],time=timestamp,hash=digest))
    return samples

def split_samples(samples):
    """Whole camera sequences, separated by a gap of 10 min, stay together."""
    groups={}; previous={}; current={}
    for s in sorted(samples,key=lambda s:(s['camera'],s['time'])):
        camera=s['camera']
        if camera not in previous or s['time']-previous[camera]>=600:
            current[camera]=camera+'|'+str(int(s['time']))
        previous[camera]=s['time']
        groups.setdefault(current[camera],[]).append(s)
    positives=sum(bool(s['boxes']) for s in samples); negatives=len(samples)-positives
    if positives<20 or negatives<20 or len(groups)<4:return list(samples),[]
    # Stable group hash split; no selecting a split based on model scores.
    validation=[s for key,group in groups.items() if int(hashlib.sha256(key.encode()).hexdigest()[:8],16)%4==0 for s in group]
    ids={s['hash'] for s in validation}; train=[s for s in samples if s['hash'] not in ids]
    def counts(part):
        p=sum(bool(s['boxes']) for s in part); return p,len(part)-p
    if min(counts(validation))<5 or min(counts(train))<10:return list(samples),[]
    if set(s['camera'] for s in validation)!=set(s['camera'] for s in samples):return list(samples),[]
    if set(s['camera'] for s in train)!=set(s['camera'] for s in samples):return list(samples),[]
    return train,validation

def prepare(s,side=640,flip=False):
    import numpy as np
    from PIL import Image, ImageEnhance
    with Image.open(s['image']) as source: im=source.convert('RGB')
    boxes=np.array(s['boxes'],dtype=np.float32).reshape(-1,4).copy()
    if flip:
        im=im.transpose(Image.Transpose.FLIP_LEFT_RIGHT)
        if len(boxes): boxes[:,[0,2]]=1-boxes[:,[2,0]]
    width,height=im.size; scale=min(side/width,side/height)
    im=im.resize((max(1,int(width*scale)),max(1,int(height*scale))),Image.Resampling.BILINEAR)
    arr=np.full((side,side,3),114,dtype=np.float32)
    arr[:im.height,:im.width]=np.asarray(im)[:,:,::-1]
    target=np.zeros((max(1,len(boxes)),5),dtype=np.float32)
    if len(boxes):
        boxes*=np.array([width,height,width,height])*scale
        target[:len(boxes),1:3]=(boxes[:,:2]+boxes[:,2:])/2
        target[:len(boxes),3:5]=boxes[:,2:]-boxes[:,:2]
    return np.ascontiguousarray(arr.transpose(2,0,1)[None]),target[None],(width,height),scale

def make_model():
    import torch
    from yolox.models import YOLOX,YOLOPAFPN,YOLOXHead
    model=YOLOX(YOLOPAFPN(.33,.50),YOLOXHead(1,.50))
    # Only trusted, bundled upstream weights; never unpickle user models.
    state=torch.load(ROOT/'yolox_s.pth',map_location='cpu',weights_only=True)['model']
    state={k:(v[:1].clone() if 'head.cls_preds.' in k else v) for k,v in state.items()}
    model.load_state_dict(state,strict=True)
    for module in model.modules():
        if isinstance(module,torch.nn.BatchNorm2d): module.eps=1e-3;module.momentum=.03
    return model

def iou(a,b):
    x=max(a[0],b[0]);y=max(a[1],b[1]);r=min(a[2],b[2]);d=min(a[3],b[3])
    area=max(0,r-x)*max(0,d-y)
    return area/max(1e-9,(a[2]-a[0])*(a[3]-a[1])+(b[2]-b[0])*(b[3]-b[1])-area)

def decode(raw,shape,scale,threshold):
    import numpy as np
    data=raw[0];side=640 if len(data)==8400 else 416
    grid=[];strides=[]
    for stride in (8,16,32):
        for y in range(side//stride):
            for x in range(side//stride):grid.append((x,y));strides.append(stride)
    stride=np.asarray(strides)[:,None]; grid=np.asarray(grid)
    scores=data[:,4]*data[:,5]
    keep=(scores>=threshold)&(np.argmax(data[:,5:],axis=1)==0)&np.isfinite(data).all(axis=1)
    center=(data[keep,:2]+grid[keep])*stride[keep]/scale
    wh=np.exp(np.clip(data[keep,2:4],-20,20))*stride[keep]/scale
    boxes=np.concatenate((center-wh/2,center+wh/2),axis=1)
    boxes/=np.array([shape[0],shape[1],shape[0],shape[1]])
    boxes=np.clip(boxes,0,1);scores=scores[keep]
    selected=[]
    for idx in np.argsort(-scores)[:200]:
        box=boxes[idx].tolist()
        if (box[2]-box[0])*shape[0]<1 or (box[3]-box[1])*shape[1]<1:continue
        if all(iou(box,other)<.45 for other in selected):selected.append(box)
        if len(selected)==30:break
    return selected

def metrics(counts):
    tp,fp,fn,negative_fp=counts
    return dict(tp=tp,fp=fp,fn=fn,negative_fp=negative_fp,precision=tp/max(1,tp+fp),recall=tp/max(1,tp+fn),f1=2*tp/max(1,2*tp+fp+fn))

def evaluate(samples,paths,thresholds,progress):
    import onnxruntime as ort
    options=ort.SessionOptions();options.intra_op_num_threads=1;options.inter_op_num_threads=1
    sessions={}; per_camera={}
    for index,s in enumerate(samples):
        progress('Validando imágenes reservadas',index,len(samples))
        path=str(paths.get(s['camera'],paths['default']))
        if path not in sessions:sessions[path]=ort.InferenceSession(path,options,providers=['CPUExecutionProvider'])
        session=sessions[path]; inp=session.get_inputs()[0]
        pixels,_,shape,scale=prepare(s,inp.shape[2])
        predicted=decode(session.run(None,{inp.name:pixels})[0],shape,scale,thresholds.get(s['camera'],.45))
        unmatched=set(range(len(s['boxes'])));tp=0
        for box in predicted:
            match=max(unmatched,key=lambda i:iou(box,s['boxes'][i]),default=None)
            if match is not None and iou(box,s['boxes'][match])>=.5:unmatched.remove(match);tp+=1
        fp=len(predicted)-tp;fn=len(unmatched)
        c=per_camera.setdefault(s['camera'],[0,0,0,0])
        for k,v in enumerate([tp,fp,fn,fp if not s['boxes'] else 0]):c[k]+=v
    total=[sum(c[i] for c in per_camera.values()) for i in range(4)]
    return dict(overall=metrics(total),cameras={k:metrics(v) for k,v in per_camera.items()})

def improves(base,candidate):
    a=base['overall'];b=candidate['overall']
    return (b['f1']>=a['f1']+.02 and b['precision']>=max(.75,a['precision']-.02)
        and b['recall']>=max(.5,a['recall']-.02) and b['negative_fp']<=a['negative_fp']
        and all(candidate['cameras'][k]['f1']>=v['f1']-.03 for k,v in base['cameras'].items()))

def run(request,job,smoke=False):
    started=time.monotonic()
    def progress(message,step=0,total=0):
        if (job/'cancel').exists():raise InterruptedError('Entrenamiento pausado')
        if time.monotonic()-started>7200:raise TimeoutError('Se alcanzó el límite de dos horas; el detector anterior sigue activo')
        atomic_json(job/'status.json',dict(message=message,step=step,total=total))
    progress('Leyendo correcciones')
    samples=load_samples(request['Samples'],request.get('Files'))
    positive=sum(bool(s['boxes']) for s in samples);negative=len(samples)-positive
    report=dict(approved=False,positive=positive,negative=negative,cameras=sorted(set(s['camera'] for s in samples)))
    if not smoke and (positive<8 or negative<4):
        report['message']=f'Guardados {positive} con personas y {negative} sin personas. El entrenamiento comienza con 8 y 4 distintos, respectivamente.'
        atomic_json(job/'report.json',report);return report
    train,validation=split_samples(samples)
    atomic_json(job/'split.json',dict(train=[s['hash'] for s in train],validation=[s['hash'] for s in validation]))
    report.update(train=len(train),validation=len(validation))
    progress('Cargando red neuronal para aprender')
    import numpy as np
    import torch
    import onnxruntime as ort
    torch.set_num_threads(1);torch.set_num_interop_threads(1)
    torch.manual_seed(2026);random.seed(2026);np.random.seed(2026)
    model=make_model()
    for p in model.backbone.parameters():p.requires_grad_(False)
    model.train();model.backbone.eval()
    # Keep pretrained normalization stable with a CPU batch of one.
    for module in model.modules():
        if isinstance(module,torch.nn.BatchNorm2d):module.eval()
    optimizer=torch.optim.SGD(model.head.parameters(),lr=.0001,momentum=.9,weight_decay=.0005)
    initial=model.head.obj_preds[0].weight.detach().clone()
    # Bounded replay of accumulated examples, balanced by class of image.
    pos=[s for s in train if s['boxes']];neg=[s for s in train if not s['boxes']]
    chosen=random.sample(pos,min(128,len(pos)))+random.sample(neg,min(128,len(neg)))
    epochs=1 if smoke else 4;losses=[];step=0
    for epoch in range(epochs):
        random.shuffle(chosen)
        for s in chosen:
            progress(f'Aprendiendo: vuelta {epoch+1}/{epochs}',step,epochs*len(chosen))
            pixels,labels,_,_=prepare(s,640,flip=(not smoke and random.random()<.5))
            optimizer.zero_grad(set_to_none=True)
            loss=model(torch.from_numpy(pixels),torch.from_numpy(labels))['total_loss']
            if not torch.isfinite(loss):raise RuntimeError('Pérdida no finita; se conserva el detector anterior')
            loss.backward();torch.nn.utils.clip_grad_norm_(model.head.parameters(),10.0);optimizer.step()
            losses.append(float(loss.detach()));step+=1
            time.sleep(.1)
    delta=float((initial-model.head.obj_preds[0].weight.detach()).abs().max())
    if delta<=0:raise RuntimeError('El entrenamiento no modificó los pesos')
    report.update(weight_change=delta,steps=step,first_loss=losses[0],last_loss=losses[-1])
    progress('Exportando el modelo aprendido')
    model.eval();model.head.decode_in_inference=False
    pixels,_,_,_=prepare(chosen[0]);dummy=torch.from_numpy(pixels)
    with torch.no_grad():expected=model(dummy).numpy()
    candidate=job/'candidate.onnx'
    torch.onnx.export(model,dummy,str(candidate),input_names=['images'],output_names=['output'],opset_version=17,dynamo=False)
    options=ort.SessionOptions();options.intra_op_num_threads=1;options.inter_op_num_threads=1
    session=ort.InferenceSession(str(candidate),options,providers=['CPUExecutionProvider'])
    result=session.run(None,{'images':pixels})[0]
    if result.shape!=(1,8400,6) or not np.isfinite(result).all() or not np.allclose(result,expected,atol=.002,rtol=.002):
        raise RuntimeError('La exportación ONNX no coincide con la red entrenada')
    del session,model,optimizer
    report['onnx_verified']=True
    if validation:
        base=evaluate(validation,request['Baselines'],request['Thresholds'],progress)
        result=evaluate(validation,{'default':str(candidate)},request['Thresholds'],progress)
        report.update(baseline=base,candidate=result,approved=improves(base,result))
        report['message']='Modelo entrenado y validado: mejoró en las capturas reservadas.' if report['approved'] else 'Entrenamiento terminado. No mejoró lo suficiente en validación; se conserva el detector actual.'
    else:
        report['message']='La red aprendió de las correcciones. Aún no se activa: hacen falta al menos 20 capturas con personas y 20 sin personas, de varias secuencias separadas por 10 minutos, para validar sin reutilizar las imágenes de entrenamiento.'
    report['elapsed_seconds']=round(time.monotonic()-started,1)
    atomic_json(job/'report.json',report)
    progress(report['message'],step,step)
    return report

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--request',required=True);parser.add_argument('--smoke',action='store_true')
    args=parser.parse_args();request_path=Path(args.request).resolve();job=request_path.parent
    try:
        request=json.loads(request_path.read_text(encoding='utf-8-sig'))
        run(request,job,args.smoke)
    except Exception as error:
        atomic_json(job/'report.json',dict(approved=False,message=str(error),error=type(error).__name__))
        (job/'error.txt').write_text(traceback.format_exc(),encoding='utf-8')
        return 2
    return 0

if __name__=='__main__':sys.exit(main())
