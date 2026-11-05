"""Check the exact Git index before publishing. Findings never print secret values.

This is an accidental-disclosure guard, not a comprehensive security audit.
"""
from pathlib import PurePosixPath
import subprocess,re,sys

def git(*args):return subprocess.check_output(['git',*args])
names=git('diff','--cached','--name-only','--diff-filter=ACM','-z').decode().split('\0')
errors=[];count=0;total=0
secret_patterns=[
    re.compile(rb'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'),
    re.compile(rb'(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})'),
    re.compile(rb'AKIA[0-9A-Z]{16}'),
    re.compile(rb'AIza[0-9A-Za-z_-]{35}'),
]
allowed_roots={'.gitignore','.gitattributes','README.md','SECURITY.md','THIRD_PARTY.md','docs','scripts','work'}
for name in filter(None,names):
    path=PurePosixPath(name);parts=path.parts;count+=1
    if parts[0] not in allowed_roots:errors.append((name,'raíz no autorizada'))
    if any(part.lower() in {'signing','runtime','bin','obj','outputs','modelos','entrenamiento','rondas-diarias','__pycache__'} or part.startswith('test-results') for part in parts):errors.append((name,'carpeta de datos o generados'))
    if path.suffix.lower() in {'.exe','.dll','.jks','.pem','.key','.pfx','.p12','.onnx','.pth','.pt','.apk','.jpg','.jpeg','.mp4','.avi','.zip'}:errors.append((name,'binario, modelo, clave o captura'))
    if path.name.lower() in {'password.txt','preferencias.json','.env'}:errors.append((name,'archivo privado'))
    data=git('show',':'+name);total+=len(data)
    if len(data)>2_000_000:errors.append((name,'archivo mayor de 2 MB'))
    if any(pattern.search(data) for pattern in secret_patterns):errors.append((name,'patrón de credencial'))
    if path.suffix.lower()!='.png':
        if re.search(rb'C:[\\/]+Users[\\/]+',data,re.I):errors.append((name,'ruta personal de Windows'))
        # Only deliberately fake credentials in protocol test fixtures are allowed.
        if re.search(rb'(?:rtsp|https?)://[^/\s"<>]+:[^/\s"<>]+@',data) and path.name not in {'SelfTest.cs','NetworkTests.java'}:
            errors.append((name,'URL con credenciales incrustadas'))
for name,reason in errors:print(name+': '+reason)
print(f'{count} archivos revisados; {total} bytes; {len(errors)} hallazgos bloqueantes.')
if not count:raise SystemExit('No hay archivos preparados para revisar.')
sys.exit(1 if errors else 0)
