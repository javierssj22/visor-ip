import json, urllib.request, pathlib, xml.etree.ElementTree as E, zipfile, re, time
root=pathlib.Path(__file__).resolve().parent/'deps'
root.mkdir(exist_ok=True)
ns={'m':'http://maven.apache.org/POM/4.0.0'}
resolved={}
def fetch(url,path):
    if not path.exists():
        for i in range(3):
            try:
                with urllib.request.urlopen(url,timeout=90) as response: path.write_bytes(response.read())
                break
            except Exception:
                if i==2: raise
                time.sleep(1)
def key(v): return tuple(int(n) if n.isdigit() else n for n in re.split('[.-]',v))
def resolve(group,name,version):
    identity=group+':'+name
    if identity in resolved and key(resolved[identity]['version'])>=key(version):return
    base=('https://dl.google.com/dl/android/maven2/' if group.startswith('androidx.') else 'https://repo.maven.apache.org/maven2/')+group.replace('.','/')+'/'+name+'/'+version+'/'+name+'-'+version
    folder=root/(name+'-'+version);folder.mkdir(exist_ok=True)
    pom=folder/'pom.xml';fetch(base+'.pom',pom)
    xml=E.parse(pom).getroot()
    val=lambda n,tag,default='':n.findtext('m:'+tag,default,namespaces=ns)
    typ=val(xml,'packaging','jar')
    if typ not in ('aar','jar'): typ='jar'
    artifact=folder/(name+'.'+typ);fetch(base+'.'+typ,artifact)
    item={'group':group,'name':name,'version':version,'type':typ,'file':str(artifact)}
    if typ=='aar':
        with zipfile.ZipFile(artifact) as z:z.extractall(folder/'unpacked')
        item['unpacked']=str(folder/'unpacked')
    resolved[identity]=item
    print(identity+':'+version,flush=True)
    props={'project.version':version}
    pe=xml.find('m:properties',ns)
    if pe is not None:props.update({x.tag.split('}')[-1]:x.text for x in pe})
    for dep in xml.findall('m:dependencies/m:dependency',ns):
        if val(dep,'scope','compile') not in ('compile','runtime') or val(dep,'optional')=='true':continue
        v=val(dep,'version')
        if v.startswith('${'):v=props.get(v[2:-1],v)
        if not v or '${' in v:raise ValueError('Unresolved version '+E.tostring(dep).decode())
        resolve(val(dep,'groupId'),val(dep,'artifactId'),v)
resolve('org.videolan.android','libvlc-all','3.7.6')
(root/'resolved.json').write_text(json.dumps(list(resolved.values()),indent=2),encoding='utf8')
print('DEPENDENCIAS LISTAS:',len(resolved),flush=True)
