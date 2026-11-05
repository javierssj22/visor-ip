package com.visorip.tv;

import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.text.SimpleDateFormat;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;
import java.util.regex.*;
import javax.xml.parsers.*;
import org.w3c.dom.*;
import org.xml.sax.InputSource;

/** Network protocol code also exercised on the desktop JVM with simulated cameras. */
public final class NetSupport {
    public static class Camera {
        public String ip,name="Cámara",kind="Servicio web · sin confirmar";
        public List<Integer> ports=new ArrayList<>();
        public List<String> endpoints=new ArrayList<>();
        public Camera(String ip){this.ip=ip;}
        public String toString(){return ip+" · "+kind;}
    }
    public static class Login {public final String user,password; public Login(String u,String p){user=u;password=p;}}
    public static class Profile {
        public String name,url,token,device="",config="";
        public Profile(String name,String url,String token){this.name=name;this.url=url;this.token=token;}
        public String toString(){return name;}
    }
    public static class AuthException extends IOException {public AuthException(){super("La cámara rechazó las credenciales. Revisá el usuario, la contraseña y los permisos ONVIF.");}}
    public interface ScanListener {void found(Camera c); void progress(int n);}
    public static String prefix(String text) {
        String s=text.trim().toLowerCase(Locale.ROOT).replaceAll("/24$","").replaceAll("\\.$","");
        String[] p=s.split("\\.");
        if(p.length==4&&(p[3].equals("xxx")||p[3].equals("*")||p[3].matches("\\d{1,3}"))) {if(p[3].matches("\\d+")&&Integer.parseInt(p[3])>255)throw new IllegalArgumentException("Rango inválido");p=Arrays.copyOf(p,3);}
        if(p.length!=3)throw new IllegalArgumentException("Ingresá una red como 172.16.0");
        for(int i=0;i<3;i++){if(!p[i].matches("\\d{1,3}")||Integer.parseInt(p[i])>255)throw new IllegalArgumentException("Rango inválido");p[i]=""+Integer.parseInt(p[i]);}
        String result=String.join(".",p);if(!isPrivate(result+".1"))throw new IllegalArgumentException("Usá una red local privada: 10.x.x, 172.16–31.x o 192.168.x");return result;
    }
    static boolean isPrivate(String ip){String[] p=ip.split("\\.");if(p.length!=4)return false;try{int a=Integer.parseInt(p[0]),b=Integer.parseInt(p[1]);return a==10||(a==172&&b>=16&&b<=31)||(a==192&&b==168);}catch(Exception e){return false;}}
    static void check(AtomicBoolean cancel)throws InterruptedIOException{if(cancel.get()||Thread.currentThread().isInterrupted())throw new InterruptedIOException("Cancelado");}
    public static boolean sameHttp(String url,String ip){try{URI u=new URI(url);return (u.getScheme().equals("http")||u.getScheme().equals("https"))&&ip.equals(u.getHost())&&u.getUserInfo()==null;}catch(Exception e){return false;}}
    public static String clean(String url)throws Exception{URI u=new URI(url);return new URI(u.getScheme(),null,u.getHost(),u.getPort(),u.getPath(),u.getQuery(),null).toASCIIString();}
    static String enc(String s){try{return URLEncoder.encode(s,"UTF-8").replace("+","%20");}catch(Exception e){throw new IllegalArgumentException(e);}}
    public static String loginUrl(String url,Login login)throws Exception {
        if(login.user.isEmpty())return url;URI u=new URI(url);
        return u.getScheme()+"://"+enc(login.user)+":"+enc(login.password)+"@"+u.getHost()+(u.getPort()<0?"":":"+u.getPort())+(u.getRawPath()==null?"":u.getRawPath())+(u.getRawQuery()==null?"":"?"+u.getRawQuery());
    }
    public static String esc(String s){return s.replace("&","&amp;").replace("<","&lt;").replace(">","&gt;").replace("\"","&quot;").replace("'","&apos;");}
    static Document xml(String s)throws Exception {
        if(s.length()>2000000||s.toUpperCase(Locale.ROOT).contains("<!DOCTYPE")||s.toUpperCase(Locale.ROOT).contains("<!ENTITY"))throw new IOException("XML inválido");
        DocumentBuilderFactory f=DocumentBuilderFactory.newInstance();f.setNamespaceAware(true);f.setExpandEntityReferences(false);
        try{f.setFeature("http://apache.org/xml/features/disallow-doctype-decl",true);}catch(Exception ignored){}
        DocumentBuilder b=f.newDocumentBuilder();b.setEntityResolver((p,sys)->new InputSource(new StringReader("")));return b.parse(new InputSource(new StringReader(s)));
    }
    static List<Element> elements(Node parent,String name){NodeList all=parent instanceof Document?((Document)parent).getElementsByTagNameNS("*",name):((Element)parent).getElementsByTagNameNS("*",name);List<Element> result=new ArrayList<>();for(int i=0;i<all.getLength();i++)result.add((Element)all.item(i));return result;}
    static String text(Node node,String name){List<Element> all=elements(node,name);return all.isEmpty()?"":all.get(0).getTextContent();}
    static String read(InputStream in,int max)throws IOException{if(in==null)return "";try(InputStream stream=in;ByteArrayOutputStream out=new ByteArrayOutputStream()){byte[] bytes=new byte[4096];int n;while((n=stream.read(bytes))!=-1){if(out.size()+n>max)throw new IOException("Respuesta demasiado grande");out.write(bytes,0,n);}return new String(out.toByteArray(),StandardCharsets.UTF_8);}}
    static boolean open(String ip,int port){try(Socket s=new Socket()){s.connect(new InetSocketAddress(ip,port),500);return true;}catch(IOException e){return false;}}
    public static void scan(String prefix,AtomicBoolean cancel,ScanListener listener)throws Exception {
        ExecutorService pool=Executors.newFixedThreadPool(20);List<Future<?>> jobs=new ArrayList<>();AtomicInteger count=new AtomicInteger();
        jobs.add(pool.submit(()->{try{discover(prefix,cancel,listener);}catch(Exception ignored){}}));
        try {
            for(int i=1;i<=254;i++){final String ip=prefix+"."+i;jobs.add(pool.submit(()->{
                if(cancel.get())return;Camera c=new Camera(ip);
                for(int port:new int[]{554,8554,2020,80,8080,8000,8899}){if(cancel.get())return;if(open(ip,port))c.ports.add(port);}
                if(!c.ports.isEmpty()){
                    for(int p:c.ports)if(p==554||p==8554){try{if(rtsp("rtsp://"+ip+":"+p+"/","OPTIONS",null).status>0)c.kind="RTSP · cámara posible";}catch(Exception ignored){}}
                    if(!cancel.get())listener.found(c);
                }
                listener.progress(count.incrementAndGet());
            }));}
            for(Future<?> job:jobs){if(cancel.get())break;job.get();}
        } finally {pool.shutdownNow();pool.awaitTermination(4,TimeUnit.SECONDS);}
        check(cancel);
    }
    static void discover(String prefix,AtomicBoolean cancel,ScanListener listener)throws Exception {
        String request="<s:Envelope xmlns:s='http://www.w3.org/2003/05/soap-envelope' xmlns:a='http://schemas.xmlsoap.org/ws/2004/08/addressing' xmlns:d='http://schemas.xmlsoap.org/ws/2005/04/discovery' xmlns:dn='http://www.onvif.org/ver10/network/wsdl'><s:Header><a:MessageID>uuid:"+UUID.randomUUID()+"</a:MessageID><a:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</a:To><a:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</a:Action></s:Header><s:Body><d:Probe><d:Types>dn:NetworkVideoTransmitter</d:Types></d:Probe></s:Body></s:Envelope>";
        try(MulticastSocket socket=new MulticastSocket(0)){
            socket.setTimeToLive(1);socket.setSoTimeout(500);byte[] bytes=request.getBytes(StandardCharsets.UTF_8);socket.send(new DatagramPacket(bytes,bytes.length,InetAddress.getByName("239.255.255.250"),3702));
            long until=System.currentTimeMillis()+3500;
            while(!cancel.get()&&System.currentTimeMillis()<until){try{
                DatagramPacket packet=new DatagramPacket(new byte[65535],65535);socket.receive(packet);String ip=packet.getAddress().getHostAddress();if(!ip.startsWith(prefix+"."))continue;
                Document doc=xml(new String(packet.getData(),0,packet.getLength(),StandardCharsets.UTF_8));
                for(Element m:elements(doc,"ProbeMatch")){Camera c=new Camera(ip);for(String e:text(m,"XAddrs").split("\\s+"))if(sameHttp(e,ip))c.endpoints.add(e);if(c.endpoints.isEmpty())continue;c.kind="ONVIF";listener.found(c);}
            }catch(SocketTimeoutException ignored){}catch(Exception e){if(cancel.get())break;}}
        }
    }
    static String hash(String algorithm,String value)throws Exception{return hex(MessageDigest.getInstance(algorithm).digest(value.getBytes(StandardCharsets.UTF_8)));}
    static String hex(byte[] b){StringBuilder s=new StringBuilder();for(byte x:b)s.append(String.format(Locale.ROOT,"%02x",x&255));return s.toString();}
    static String auth(String challenge,String method,String uri,Login login)throws Exception {
        if(challenge.regionMatches(true,0,"Basic",0,5))return "Basic "+Base64.getEncoder().encodeToString((login.user+":"+login.password).getBytes(StandardCharsets.UTF_8));
        if(!challenge.regionMatches(true,0,"Digest",0,6))throw new IOException("Autenticación no compatible");
        Map<String,String> values=new HashMap<>();Matcher m=Pattern.compile("(\\w+)\\s*=\\s*(?:\"([^\"]*)\"|([^,\\s]+))").matcher(challenge);
        while(m.find())values.put(m.group(1).toLowerCase(Locale.ROOT),m.group(2)!=null?m.group(2):m.group(3));
        String realm=values.getOrDefault("realm",""),nonce=values.getOrDefault("nonce",""),alg=values.getOrDefault("algorithm","MD5"),qop=values.getOrDefault("qop","");
        if(!alg.equalsIgnoreCase("MD5")&&!alg.equalsIgnoreCase("MD5-sess")&&!alg.equalsIgnoreCase("SHA-256"))throw new IOException("Autenticación no compatible");
        String digest=alg.equalsIgnoreCase("SHA-256")?"SHA-256":"MD5",cnonce=UUID.randomUUID().toString().replace("-","");boolean useQop=Arrays.asList(qop.replace(" ","").split(",")).contains("auth");
        if(!qop.isEmpty()&&!useQop)throw new IOException("Digest qop no compatible");
        String a1=hash(digest,login.user+":"+realm+":"+login.password);if(alg.equalsIgnoreCase("MD5-sess"))a1=hash(digest,a1+":"+nonce+":"+cnonce);
        String response=hash(digest,a1+":"+nonce+":"+(useQop?"00000001:"+cnonce+":auth:":"")+hash(digest,method+":"+uri));
        return "Digest username=\""+quote(login.user)+"\", realm=\""+quote(realm)+"\", nonce=\""+quote(nonce)+"\", uri=\""+quote(uri)+"\", response=\""+response+"\", algorithm="+alg+(useQop?", qop=auth, nc=00000001, cnonce=\""+cnonce+"\"":alg.equalsIgnoreCase("MD5-sess")?", cnonce=\""+cnonce+"\"":"")+(values.containsKey("opaque")?", opaque=\""+quote(values.get("opaque"))+"\"":"");
    }
    static String quote(String s){return s.replace("\\","\\\\").replace("\"","\\\"").replace("\r","").replace("\n","");}
    static class Reply {int status;String header,body;Reply(int s,String h,String b){status=s;header=h;body=b;}}
    static Reply rtsp(String url,String method,String authorization)throws Exception {
        URI uri=new URI(url);try(Socket socket=new Socket()){
            socket.connect(new InetSocketAddress(uri.getHost(),uri.getPort()<0?554:uri.getPort()),1400);socket.setSoTimeout(1800);
            String req=method+" "+url+" RTSP/1.0\r\nCSeq: 1\r\nUser-Agent: VisorIP-TV/2.0\r\n"+(method.equals("DESCRIBE")?"Accept: application/sdp\r\n":"")+(authorization==null?"":"Authorization: "+authorization+"\r\n")+"\r\n";
            socket.getOutputStream().write(req.getBytes(StandardCharsets.UTF_8));InputStream in=socket.getInputStream();ByteArrayOutputStream data=new ByteArrayOutputStream();int c,matched=0;
            byte[] terminator={13,10,13,10};while(data.size()<65536&&(c=in.read())!=-1){data.write(c);matched=c==terminator[matched]?matched+1:(c==13?1:0);if(matched==4)break;}
            String header=new String(data.toByteArray(),StandardCharsets.UTF_8);Matcher status=Pattern.compile("^RTSP/1\\.\\d (\\d{3})").matcher(header);int code=status.find()?Integer.parseInt(status.group(1)):0;
            Matcher lm=Pattern.compile("(?im)^Content-Length:\\s*(\\d+)").matcher(header);int length=lm.find()?Math.min(131072,Integer.parseInt(lm.group(1))):0;ByteArrayOutputStream body=new ByteArrayOutputStream();for(int i=0;i<length&&(c=in.read())!=-1;i++)body.write(c);
            return new Reply(code,header,new String(body.toByteArray(),StandardCharsets.UTF_8));
        }
    }
    public static List<Profile> rtspProfiles(Camera c,Login login,AtomicBoolean cancel)throws Exception {
        List<Integer> ports=new ArrayList<>();for(int p:c.ports)if(p==554||p==8554)ports.add(p);if(ports.isEmpty())ports.add(554);
        String[] paths={"/Streaming/Channels/102","/cam/realmonitor?channel=1&subtype=1","/stream2","/Streaming/Channels/101","/cam/realmonitor?channel=1&subtype=0","/stream1","/onvif1","/h264Preview_01_main","/live","/"};
        for(int port:ports)for(String path:paths){check(cancel);String url="rtsp://"+c.ip+":"+port+path;try{
            Reply r=rtsp(url,"DESCRIBE",null);if(r.status==401){if(login.user.isEmpty())throw new AuthException();Matcher challenge=Pattern.compile("(?im)^WWW-Authenticate:\\s*(.+)$").matcher(r.header);if(!challenge.find())throw new AuthException();r=rtsp(url,"DESCRIBE",auth(challenge.group(1).trim(),"DESCRIBE",url,login));}
            if(r.status==401||r.status==403)throw new AuthException();
            if(r.status==200&&Pattern.compile("(?m)^m=video\\s").matcher(r.body).find())return new ArrayList<>(Collections.singletonList(new Profile("Video RTSP",url,"")));
        }catch(AuthException e){throw e;}catch(IOException e){check(cancel);}}
        return new ArrayList<>();
    }
    public static class Onvif {
        static final String DEVICE="http://www.onvif.org/ver10/device/wsdl",MEDIA="http://www.onvif.org/ver10/media/wsdl",MEDIA2="http://www.onvif.org/ver20/media/wsdl",PTZ="http://www.onvif.org/ver20/ptz/wsdl";
        final String host;final Login login;long clockOffset;
        public Onvif(String h,Login l){host=h;login=l;}
        static String security(Login login,long utc)throws Exception {
            if(login.user.isEmpty())return "";byte[] nonce=new byte[20];new SecureRandom().nextBytes(nonce);SimpleDateFormat df=new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'",Locale.ROOT);df.setTimeZone(TimeZone.getTimeZone("UTC"));String created=df.format(new Date(utc));
            ByteArrayOutputStream bytes=new ByteArrayOutputStream();bytes.write(nonce);bytes.write((created+login.password).getBytes(StandardCharsets.UTF_8));String digest=Base64.getEncoder().encodeToString(MessageDigest.getInstance("SHA-1").digest(bytes.toByteArray()));
            return "<wsse:Security xmlns:wsse='http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd' xmlns:wsu='http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd'><wsse:UsernameToken><wsse:Username>"+esc(login.user)+"</wsse:Username><wsse:Password Type='http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest'>"+digest+"</wsse:Password><wsse:Nonce EncodingType='http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary'>"+Base64.getEncoder().encodeToString(nonce)+"</wsse:Nonce><wsu:Created>"+created+"</wsu:Created></wsse:UsernameToken></wsse:Security>";
        }
        Document call(String endpoint,String ns,String op,String body,boolean anonymous)throws Exception {return call(endpoint,ns,op,body,anonymous,ns+"/"+op,"");}
        Document call(String endpoint,String ns,String op,String body,boolean anonymous,String action,String header)throws Exception {
            if(!sameHttp(endpoint,host))throw new IOException("El servicio ONVIF está en otra dirección IP");
            String soap="<s:Envelope xmlns:s='http://www.w3.org/2003/05/soap-envelope' xmlns:tt='http://www.onvif.org/ver10/schema'><s:Header>"+(anonymous?"":security(login,System.currentTimeMillis()+clockOffset))+header+"</s:Header><s:Body><m:"+op+" xmlns:m='"+ns+"'>"+body+"</m:"+op+"></s:Body></s:Envelope>";
            byte[] bytes=soap.getBytes(StandardCharsets.UTF_8);String authorization=null;
            for(int attempt=0;attempt<2;attempt++){
                HttpURLConnection conn=(HttpURLConnection)new URL(endpoint).openConnection(Proxy.NO_PROXY);
                try {
                    conn.setConnectTimeout(2000);conn.setReadTimeout(2500);conn.setInstanceFollowRedirects(false);conn.setRequestMethod("POST");conn.setDoOutput(true);conn.setRequestProperty("Content-Type","application/soap+xml; charset=utf-8; action=\""+action+"\"");conn.setRequestProperty("SOAPAction","\""+action+"\"");if(authorization!=null)conn.setRequestProperty("Authorization",authorization);conn.setFixedLengthStreamingMode(bytes.length);
                    try(OutputStream out=conn.getOutputStream()){out.write(bytes);}int code=conn.getResponseCode();
                    if(code==401&&attempt==0&&!login.user.isEmpty()){
                        String challenge=conn.getHeaderField("WWW-Authenticate");if(challenge==null)throw new AuthException();URI u=new URI(endpoint);String requestUri=u.getRawPath()+(u.getRawQuery()==null?"":"?"+u.getRawQuery());authorization=auth(challenge,"POST",requestUri,login);continue;
                    }
                    if(code==401||code==403)throw new AuthException();String response=read(code>=400?conn.getErrorStream():conn.getInputStream(),2000000);Document doc=xml(response);
                    if(!elements(doc,"Fault").isEmpty()){String fault=text(doc,"Fault");if(fault.contains("NotAuthorized")||fault.contains("FailedAuthentication"))throw new AuthException();throw new IOException("La cámara no admite esta operación ONVIF");}
                    if(code<200||code>=300)throw new IOException("Respuesta ONVIF inválida");return doc;
                }finally{conn.disconnect();}
            }
            throw new AuthException();
        }
        void clock(String endpoint){try{Document d=call(endpoint,DEVICE,"GetSystemDateAndTime","",true);List<Element> utc=elements(d,"UTCDateTime");if(!utc.isEmpty()){Element n=utc.get(0);Calendar cal=Calendar.getInstance(TimeZone.getTimeZone("UTC"));cal.clear();cal.set(Integer.parseInt(text(n,"Year")),Integer.parseInt(text(n,"Month"))-1,Integer.parseInt(text(n,"Day")),Integer.parseInt(text(n,"Hour")),Integer.parseInt(text(n,"Minute")),Integer.parseInt(text(n,"Second")));clockOffset=cal.getTimeInMillis()-System.currentTimeMillis();}}catch(Exception ignored){}}
        List<String> endpoints(Camera c){LinkedHashSet<String> e=new LinkedHashSet<>(c.endpoints);for(int p:c.ports)if(p!=554&&p!=8554)e.add("http://"+c.ip+":"+p+"/onvif/device_service");e.add("http://"+c.ip+":2020/onvif/device_service");e.add("http://"+c.ip+"/onvif/device_service");return new ArrayList<>(e);}
        public List<Profile> profiles(Camera c,AtomicBoolean cancel)throws Exception {
            for(String endpoint:endpoints(c)){check(cancel);try{
                clock(endpoint);Document caps=call(endpoint,DEVICE,"GetCapabilities","<m:Category>Media</m:Category>",false);List<Element> media=elements(caps,"Media");
                if(!media.isEmpty()){String url=text(media.get(0),"XAddr");if(sameHttp(url,host)){List<Profile> p=profilesAt(url,MEDIA,endpoint,cancel);if(!p.isEmpty())return p;}}
            }catch(AuthException e){throw e;}catch(Exception ignored){check(cancel);}
            try {
                Document services=call(endpoint,DEVICE,"GetServices","<m:IncludeCapability>false</m:IncludeCapability>",false);
                for(Element s:elements(services,"Service")){String ns=text(s,"Namespace"),url=text(s,"XAddr");if((ns.equals(MEDIA)||ns.equals(MEDIA2))&&sameHttp(url,host)){List<Profile> p=profilesAt(url,ns,endpoint,cancel);if(!p.isEmpty())return p;}}
            }catch(AuthException e){throw e;}catch(Exception ignored){check(cancel);}}
            return new ArrayList<>();
        }
        List<Profile> profilesAt(String endpoint,String ns,String device,AtomicBoolean cancel)throws Exception {
            Document doc=call(endpoint,ns,"GetProfiles","",false);List<Profile> result=new ArrayList<>();int total=0;
            for(Element p:elements(doc,"Profiles")){if(++total>12)break;check(cancel);String token=p.getAttribute("token");if(token.isEmpty())continue;
                String setup=ns.equals(MEDIA2)?"<m:Protocol>RTSP</m:Protocol>":"<m:StreamSetup><tt:Stream>RTP-Unicast</tt:Stream><tt:Transport><tt:Protocol>RTSP</tt:Protocol></tt:Transport></m:StreamSetup>";
                try{Document response=call(endpoint,ns,"GetStreamUri",setup+"<m:ProfileToken>"+esc(token)+"</m:ProfileToken>",false);URI u=new URI(text(response,"Uri"));if(!Arrays.asList("rtsp","http","https").contains(u.getScheme()))continue;String url=new URI(u.getScheme(),null,host,u.getPort(),u.getPath(),u.getQuery(),null).toASCIIString();Profile profile=new Profile(text(p,"Name"),url,token);profile.device=device;result.add(profile);}catch(AuthException e){throw e;}catch(Exception ignored){}
            }
            // Prefer smaller streams on TV, while preserving the user's ability to choose any profile.
            result.sort(Comparator.comparingInt(p->p.name.toLowerCase(Locale.ROOT).matches(".*(sub|secund|minor|low).*" )?0:1));return result;
        }
        public PtzDevice ptz(Camera c,Profile preferred,AtomicBoolean cancel)throws Exception {
            List<String> endpoints=endpoints(c);if(preferred!=null&&!preferred.device.isEmpty()){endpoints.remove(preferred.device);endpoints.add(0,preferred.device);}
            for(String endpoint:endpoints){check(cancel);try{
                clock(endpoint);String ptz="",media="",ns=MEDIA;
                try{Document caps=call(endpoint,DEVICE,"GetCapabilities","<m:Category>All</m:Category>",false);List<Element> p=elements(caps,"PTZ"),m=elements(caps,"Media");if(!p.isEmpty())ptz=text(p.get(0),"XAddr");if(!m.isEmpty())media=text(m.get(0),"XAddr");}catch(AuthException e){throw e;}catch(Exception ignored){}
                if(!sameHttp(ptz,host)||!sameHttp(media,host)){
                    Document services=call(endpoint,DEVICE,"GetServices","<m:IncludeCapability>false</m:IncludeCapability>",false);
                    for(Element s:elements(services,"Service")){String n=text(s,"Namespace"),u=text(s,"XAddr");if(!sameHttp(u,host))continue;if(n.equals(PTZ))ptz=u;if(n.equals(MEDIA)||n.equals(MEDIA2)){media=u;ns=n;}}
                }
                if(!sameHttp(ptz,host)||!sameHttp(media,host))continue;
                Document profiles=call(media,ns,"GetProfiles","",false);List<Profile> available=new ArrayList<>();
                for(Element p:elements(profiles,"Profiles")){List<Element> config=elements(p,"PTZConfiguration");if(config.isEmpty())config=elements(p,"PTZ");if(config.isEmpty())continue;String conf=config.get(0).getAttribute("token"),token=p.getAttribute("token");if(conf.isEmpty()||token.isEmpty())continue;Profile prof=new Profile(text(p,"Name"),"",token);prof.config=conf;available.add(prof);}
                if(!available.isEmpty())return new PtzDevice(ptz,available);
            }catch(AuthException e){throw e;}catch(Exception ignored){check(cancel);}}
            throw new IOException("No se encontró ONVIF PTZ. Verificá soporte y permisos del usuario.");
        }
        public Options options(String endpoint,Profile p)throws Exception {return parseOptions(call(endpoint,PTZ,"GetConfigurationOptions","<m:ConfigurationToken>"+esc(p.config)+"</m:ConfigurationToken>",false));}
        public void pulse(String endpoint,Profile p,Options o,int x,int y,int z,AtomicBoolean cancel)throws Exception {
            check(cancel);if(x!=0&&o.pan==null||y!=0&&o.tilt==null||z!=0&&o.zoom==null)throw new IOException("Movimiento no compatible");String velocity="";
            if(x!=0||y!=0)velocity="<tt:PanTilt x='"+number(o.pan==null?0:o.pan.speed(x))+"' y='"+number(o.tilt==null?0:o.tilt.speed(y))+"' space='"+esc(o.pan!=null?o.pan.space:o.tilt.space)+"'/>";
            if(z!=0)velocity+="<tt:Zoom x='"+number(o.zoom.speed(z))+"' space='"+esc(o.zoom.space)+"'/>";if(velocity.isEmpty())return;
            try{
                call(endpoint,PTZ,"ContinuousMove","<m:ProfileToken>"+esc(p.token)+"</m:ProfileToken><m:Velocity>"+velocity+"</m:Velocity><m:Timeout>PT"+number(o.timeout/1000.0)+"S</m:Timeout>",false);
                long until=System.currentTimeMillis()+Math.min(280,o.timeout);while(System.currentTimeMillis()<until){check(cancel);Thread.sleep(20);}
            }finally{stop(endpoint,p);}
        }
        public void stop(String endpoint,Profile p)throws Exception {call(endpoint,PTZ,"Stop","<m:ProfileToken>"+esc(p.token)+"</m:ProfileToken><m:PanTilt>true</m:PanTilt><m:Zoom>true</m:Zoom>",false);}
    }
    static String number(double x){return String.format(Locale.ROOT,"%.6f",x);}
    public static class PtzDevice {public String endpoint;public List<Profile> profiles;PtzDevice(String e,List<Profile> p){endpoint=e;profiles=p;}}
    public static class Axis {public String space;public double min,max;Axis(String s,double a,double b){space=s;min=a;max=b;}double speed(int sign){return sign==0?0:sign>0?Math.max(0,max)*0.28:Math.min(0,min)*0.28;}}
    public static class Options {public Axis pan,tilt,zoom;public int timeout=1000;}
    static Axis axis(Document doc,String element,String range){for(Element n:elements(doc,element)){List<Element> r=elements(n,range);if(r.isEmpty())continue;try{double min=Double.parseDouble(text(r.get(0),"Min")),max=Double.parseDouble(text(r.get(0),"Max"));String space=text(n,"URI");if(!space.isEmpty()&&Double.isFinite(min)&&Double.isFinite(max)&&min<=0&&max>=0&&min<max)return new Axis(space,min,max);}catch(Exception ignored){}}return null;}
    static int duration(String value)throws IOException {Matcher m=Pattern.compile("^PT(?:(\\d+(?:\\.\\d+)?)H)?(?:(\\d+(?:\\.\\d+)?)M)?(?:(\\d+(?:\\.\\d+)?)S)?$").matcher(value);if(!m.matches())throw new IOException("Timeout PTZ inválido");double ms=0;for(int i=1;i<=3;i++)if(m.group(i)!=null)ms+=Double.parseDouble(m.group(i))*new int[]{3600000,60000,1000}[i-1];return (int)Math.min(Integer.MAX_VALUE,ms);}
    static Options parseOptions(Document doc)throws Exception {
        Options o=new Options();o.pan=axis(doc,"ContinuousPanTiltVelocitySpace","XRange");o.tilt=axis(doc,"ContinuousPanTiltVelocitySpace","YRange");o.zoom=axis(doc,"ContinuousZoomVelocitySpace","XRange");List<Element> times=elements(doc,"PTZTimeout");
        if(!times.isEmpty()){int min=duration(text(times.get(0),"Min")),max=duration(text(times.get(0),"Max"));if(min>2000||max<100||max<min)throw new IOException("La cámara no admite un tiempo de parada breve");o.timeout=Math.max(min,Math.min(1000,Math.min(max,2000)));}return o;
    }
}
