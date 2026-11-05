"""Prepare a portable Python 3.12 x64 CPU trainer without a global pip install."""
from pathlib import Path
import sys,struct,subprocess,shutil

if sys.version_info[:2]!=(3,12) or struct.calcsize('P')!=8:
    raise SystemExit('Ejecutá este script con Python 3.12 de 64 bits (por ejemplo py -3.12).')
root=Path(__file__).resolve().parents[1]
subprocess.run([sys.executable,str(root/'work/prepare_training.py')],check=True,cwd=root)
trainer=root/'work/training';target=trainer/'runtime/Lib/site-packages'
pip=[sys.executable,'-m','pip','install','--target',str(target),'--no-warn-script-location','--no-compile']
subprocess.run(pip+['torch==2.7.1','--index-url','https://download.pytorch.org/whl/cpu'],check=True)
subprocess.run(pip+['-r',str(trainer/'requirements.txt')],check=True)
destination=root/'work/build/aprendizaje'
if destination.is_symlink() or getattr(destination,'is_junction',lambda:False)():
    raise SystemExit('El destino es un enlace existente. Conservado; copiá a una carpeta nueva para distribuir.')
shutil.copytree(trainer,destination,dirs_exist_ok=True,ignore=shutil.ignore_patterns('__pycache__','*.pyc'))
print('Entrenador portátil listo en work/build/aprendizaje')
