import pathlib, json, subprocess, os, zipfile, xml.etree.ElementTree as E, secrets, hashlib
root=pathlib.Path(__file__).resolve().parent
sdk=pathlib.Path(os.environ.get('ANDROID_HOME',r'C:\Program Files (x86)\Android\android-sdk'))
jdk=pathlib.Path(os.environ.get('JAVA_HOME',r'C:\Program Files\Android\openjdk\jdk-21.0.8'))
tools=sdk/'build-tools'/'36.0.0'; android=sdk/'platforms'/'android-35'/'android.jar'
build=root/'build';build.mkdir(exist_ok=True)
output=pathlib.Path(os.environ.get('VISOR_APK_OUTPUT',str(root/'dist')));output.mkdir(parents=True,exist_ok=True)
deps=json.loads((root/'deps'/'resolved.json').read_text())
def run(args,**kwargs):
    print('RUN',pathlib.Path(str(args[0])).name,flush=True)
    subprocess.run([str(a) for a in args],check=True,**kwargs)
res=[];jars=[];packages=[];native=[];assets=[]
for item in deps:
    if item['type']=='jar':jars.append(pathlib.Path(item['file']));continue
    folder=pathlib.Path(item['unpacked']); classes=folder/'classes.jar'
    if classes.exists():jars.append(classes)
    for extra in (folder/'libs').glob('*.jar'):jars.append(extra)
    if (folder/'AndroidManifest.xml').exists():
        package=E.parse(folder/'AndroidManifest.xml').getroot().get('package')
        if package:packages.append(package)
    if (folder/'res').exists() and any((folder/'res').rglob('*.*')):
        archive=build/(item['name']+'-res.zip');run([tools/'aapt2.exe','compile','--dir',folder/'res','-o',archive]);res.append(archive)
    if (folder/'jni').exists():native.append(folder/'jni')
    if (folder/'assets').exists():assets.append(folder/'assets')
appres=build/'app-res.zip';run([tools/'aapt2.exe','compile','--dir',root/'res','-o',appres]);res.append(appres)
gen=build/'gen';gen.mkdir(exist_ok=True)
unsigned=build/'unsigned.apk'
link=[tools/'aapt2.exe','link','-o',unsigned,'--manifest',root/'AndroidManifest.xml','-I',android,'--java',gen,'--auto-add-overlay','--extra-packages',':'.join(sorted(set(packages))),'--min-sdk-version','26','--target-sdk-version','35']
for r in res:link+=['-R',r]
run(link)
classes=build/'classes';classes.mkdir(exist_ok=True)
sources=list((root/'src').rglob('*.java'))+list(gen.rglob('*.java'))
args=build/'javac.args'
def q(p):return '"'+str(p).replace('\\','/')+'"'
args.write_text('\n'.join(['--release','8','-encoding','UTF-8','-classpath',q(str(android)+';'+ ';'.join(str(j) for j in jars)),'-d',q(classes)]+[q(p) for p in sources]),encoding='utf8')
run([jdk/'bin'/'javac.exe','@'+str(args)])
appjar=build/'app.jar';run([jdk/'bin'/'jar.exe','--create','--file',appjar,'-C',classes,'.'])
dex=build/'dex';dex.mkdir(exist_ok=True)
run([jdk/'bin'/'java.exe','-Xmx2g','-cp',tools/'lib'/'d8.jar','com.android.tools.r8.D8','--release','--min-api','26','--lib',android,'--output',dex,appjar]+jars)
with zipfile.ZipFile(unsigned,'a',compression=zipfile.ZIP_DEFLATED) as z:
    for d in dex.glob('classes*.dex'):z.write(d,d.name)
    for folder in native:
        for f in folder.rglob('*.so'):z.write(f,'lib/'+f.relative_to(folder).as_posix())
    used=set()
    for folder in assets:
        for f in folder.rglob('*'):
            if not f.is_file():continue
            name='assets/'+f.relative_to(folder).as_posix()
            if name not in used:z.write(f,name);used.add(name)
aligned=build/'aligned.apk';run([tools/'zipalign.exe','-f','-P','16','4',unsigned,aligned])
signing=root/'signing';signing.mkdir(exist_ok=True)
password=signing/'password.txt'
if not password.exists():password.write_text(secrets.token_urlsafe(32),encoding='utf8')
env=os.environ.copy();env['VISOR_KEY_PASSWORD']=password.read_text().strip()
key=signing/'visor-ip-tv.jks'
if not key.exists():run([jdk/'bin'/'keytool.exe','-genkeypair','-keystore',key,'-alias','visorip','-keyalg','RSA','-keysize','3072','-validity','10000','-dname','CN=Visor IP TV, OU=Personal, O=Visor IP','-storepass:env','VISOR_KEY_PASSWORD','-keypass:env','VISOR_KEY_PASSWORD'],env=env)
apk=output/'Visor-IP-TV-2.3.apk'
run([jdk/'bin'/'java.exe','-jar',tools/'lib'/'apksigner.jar','sign','--ks',key,'--ks-key-alias','visorip','--ks-pass','env:VISOR_KEY_PASSWORD','--key-pass','env:VISOR_KEY_PASSWORD','--out',apk,aligned],env=env)
verify=subprocess.check_output([str(jdk/'bin'/'java.exe'),'-jar',str(tools/'lib'/'apksigner.jar'),'verify','--verbose','--print-certs',str(apk)],text=True,encoding='utf8')
(build/'signature.txt').write_text(verify,encoding='utf8');print(verify,flush=True)
badging=subprocess.check_output([str(tools/'aapt.exe'),'dump','badging',str(apk)],text=True,encoding='utf8');(build/'manifest-report.txt').write_text(badging,encoding='utf8')
sha=hashlib.sha256(apk.read_bytes()).hexdigest();(output/'Visor-IP-TV-2.3.apk.sha256').write_text(sha+'  '+apk.name+'\n',encoding='utf8')
print('APK LISTA:',apk,apk.stat().st_size,'bytes',flush=True)
