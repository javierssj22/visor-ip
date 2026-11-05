package com.visorip.tv;
import com.sun.net.httpserver.*;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;
import org.w3c.dom.*;

public class NetworkTests {
 static int checks;
 static void check(boolean value,String name){if(!value)throw new AssertionError(name);System.out.println("PASS: "+name);checks++;}
 static final String OPTIONS="<PTZConfigurationOptions><Spaces><ContinuousPanTiltVelocitySpace><URI>urn:pan</URI><XRange><Min>-1</Min><Max>1</Max></XRange><YRange><Min>-0.5</Min><Max>0.5</Max></YRange></ContinuousPanTiltVelocitySpace><ContinuousZoomVelocitySpace><URI>urn:zoom</URI><XRange><Min>-1</Min><Max>1</Max></XRange></ContinuousZoomVelocitySpace></Spaces><PTZTimeout><Min>PT0.1S</Min><Max>PT10S</Max></PTZTimeout></PTZConfigurationOptions>";
 public static void main(String[] args)throws Exception {
   check(NetSupport.prefix("172.16.0.xxx").equals("172.16.0"),"Rango con xxx");
   check(NetSupport.prefix("192.168.4.0/24").equals("192.168.4"),"Rango CIDR /24");
   for(String bad:new String[]{"8.8.8","172.32.0","172.16.999","172.16.0.999"}){boolean rejected=false;try{NetSupport.prefix(bad);}catch(IllegalArgumentException e){rejected=true;}check(rejected,"Rechaza "+bad);}
   NetSupport.Login login=new NetSupport.Login("a<&@","p:a/? ñ");String url=NetSupport.loginUrl("rtsp://172.16.0.2:8554/stream?channel=1",login);
   check(new URI(url).getUserInfo().equals("a<&@:p:a/? ñ"),"Escape de credenciales");check(NetSupport.clean(url).equals("rtsp://172.16.0.2:8554/stream?channel=1"),"Direcciones guardadas sin credenciales");
   check(!NetSupport.sameHttp("http://172.16.0.99/device","172.16.0.2"),"No envía credenciales a otro host");
   boolean dtd=false;try{NetSupport.xml("<!DOCTYPE foo><foo/>");}catch(IOException e){dtd=true;}check(dtd,"XML DTD bloqueado");
   String authorization=NetSupport.auth("Digest realm=\"testrealm@host.com\", nonce=\"dcd98b7102dd2f0e8b11d0f600bfb0c093\"","GET","/dir/index.html",new NetSupport.Login("Mufasa","Circle Of Life"));
   check(authorization.contains("670fd8c2df070c60b045671b8b24ff02"),"Digest: vector conocido");
   check(NetSupport.auth("Basic realm=\"cam\"","POST","/",new NetSupport.Login("u","p")).equals("Basic dTpw"),"Autenticación Basic");
   Document security=NetSupport.xml(NetSupport.Onvif.security(login,1700000000000L));ByteArrayOutputStream digest=new ByteArrayOutputStream();digest.write(Base64.getDecoder().decode(NetSupport.text(security,"Nonce")));digest.write((NetSupport.text(security,"Created")+login.password).getBytes(StandardCharsets.UTF_8));check(NetSupport.text(security,"Password").equals(Base64.getEncoder().encodeToString(MessageDigest.getInstance("SHA-1").digest(digest.toByteArray()))),"WS-Security PasswordDigest");
   NetSupport.Options options=NetSupport.parseOptions(NetSupport.xml(OPTIONS));check(options.pan!=null&&options.zoom!=null&&options.timeout==1000&&options.tilt.speed(-1)==-0.14,"PTZ: espacios, velocidad y timeout");
   try(FakeCamera fake=new FakeCamera()){
     NetSupport.Camera cam=new NetSupport.Camera("127.0.0.1");cam.endpoints.add(fake.endpoint());NetSupport.Onvif onvif=new NetSupport.Onvif(cam.ip,new NetSupport.Login("admin","test"));AtomicBoolean cancel=new AtomicBoolean();
     List<NetSupport.Profile> profiles=onvif.profiles(cam,cancel);check(profiles.size()==2&&profiles.get(0).name.equals("Secundario"),"ONVIF obtiene perfiles y prioriza secundario");
     check(new URI(profiles.get(0).url).getHost().equals("127.0.0.1")&&new URI(profiles.get(0).url).getUserInfo()==null,"Perfil corrige IP y elimina credenciales anunciadas");
     NetSupport.PtzDevice device=onvif.ptz(cam,profiles.get(0),cancel);check(device.profiles.size()==2,"Servicio PTZ y perfiles");NetSupport.Profile p=device.profiles.get(0);NetSupport.Options o=onvif.options(device.endpoint,p);
     onvif.pulse(device.endpoint,p,o,1,0,0,cancel);check(fake.moves.get()==1&&fake.stops.get()==1&&fake.lastMove.contains("PT1.000000S")&&fake.lastMove.contains("0.280000"),"PTZ ContinuousMove seguido de Stop");
     ScheduledExecutorService timer=Executors.newSingleThreadScheduledExecutor();AtomicBoolean mid=new AtomicBoolean();timer.schedule(()->mid.set(true),75,TimeUnit.MILLISECONDS);try{onvif.pulse(device.endpoint,p,o,0,1,0,mid);}catch(InterruptedIOException ignored){}finally{timer.shutdownNow();}check(fake.stops.get()==2,"Cancelación de PTZ envía Stop");
     fake.failMove=true;try{onvif.pulse(device.endpoint,p,o,0,0,1,cancel);}catch(IOException ignored){}check(fake.stops.get()==3,"Fallo del movimiento envía Stop");
     fake.media2=true;check(onvif.profiles(cam,cancel).size()==2,"ONVIF Media2");
     fake.reject=true;boolean rejected=false;try{onvif.profiles(cam,cancel);}catch(NetSupport.AuthException e){rejected=true;}check(rejected,"Credenciales rechazadas no se reintentan en otros perfiles");
   }
   try(ServerSocket server=new ServerSocket(0,5,InetAddress.getLoopbackAddress())){
     ExecutorService pool=Executors.newSingleThreadExecutor();Future<?> job=pool.submit(()->{try(Socket socket=server.accept()){BufferedReader input=new BufferedReader(new InputStreamReader(socket.getInputStream()));String line;while((line=input.readLine())!=null&&!line.isEmpty()){}String body="v=0\r\nm=video 0 RTP/AVP 96\r\n";socket.getOutputStream().write(("RTSP/1.0 200 OK\r\nContent-Length: "+body.length()+"\r\n\r\n"+body).getBytes(StandardCharsets.UTF_8));}catch(Exception e){throw new RuntimeException(e);}});
     NetSupport.Reply reply=NetSupport.rtsp("rtsp://127.0.0.1:"+server.getLocalPort()+"/stream","DESCRIBE",null);check(reply.status==200&&reply.body.contains("m=video"),"RTSP de cámara simulada");job.get();pool.shutdown();
   }
   System.out.println("RESULTADO: "+checks+" PRUEBAS CORRECTAS");
 }
 static class FakeCamera implements AutoCloseable {
   final HttpServer server;final AtomicInteger moves=new AtomicInteger(),stops=new AtomicInteger();volatile boolean reject,failMove,media2;volatile String lastMove="";
   FakeCamera()throws Exception{server=HttpServer.create(new InetSocketAddress(InetAddress.getLoopbackAddress(),0),0);server.createContext("/",this::handle);server.start();}
   String base(){return "http://127.0.0.1:"+server.getAddress().getPort();}String endpoint(){return base()+"/device";}
   void handle(HttpExchange exchange)throws IOException {
     try{
       String request=NetSupport.read(exchange.getRequestBody(),2000000);if(reject){exchange.sendResponseHeaders(401,-1);return;}Document doc=NetSupport.xml(request);String name="";for(Element n:NetSupport.elements(doc,"Body")){for(Node p=n.getFirstChild();p!=null;p=p.getNextSibling())if(p instanceof Element){name=p.getLocalName();break;}}
       String content="";
       switch(name){
         case "GetSystemDateAndTime":content="<UTCDateTime><Time><Hour>12</Hour><Minute>0</Minute><Second>0</Second></Time><Date><Year>2026</Year><Month>9</Month><Day>18</Day></Date></UTCDateTime>";break;
         case "GetCapabilities":content=media2?"<Capabilities/>":"<Capabilities><Media><XAddr>"+base()+"/media</XAddr></Media><PTZ><XAddr>"+base()+"/ptz</XAddr></PTZ></Capabilities>";break;
         case "GetServices":content="<Service><Namespace>"+NetSupport.Onvif.MEDIA2+"</Namespace><XAddr>"+base()+"/media2</XAddr></Service><Service><Namespace>"+NetSupport.Onvif.PTZ+"</Namespace><XAddr>"+base()+"/ptz</XAddr></Service>";break;
         case "GetProfiles":content="<Profiles token='main'><Name>Principal</Name><PTZConfiguration token='p1'/></Profiles><Profiles token='sub'><Name>Secundario</Name><Configurations><PTZ token='p1'/></Configurations></Profiles>";break;
         case "GetStreamUri":content="<MediaUri><Uri>rtsp://hidden:secret@0.0.0.0:554/"+NetSupport.text(doc,"ProfileToken")+"</Uri></MediaUri>";break;
         case "GetConfigurationOptions":content=OPTIONS;break;
         case "ContinuousMove":moves.incrementAndGet();lastMove=request;if(failMove)content="<Fault><Reason>No move</Reason></Fault>";break;
         case "Stop":stops.incrementAndGet();break;
       }
       byte[] response=("<s:Envelope xmlns:s='http://www.w3.org/2003/05/soap-envelope'><s:Body><Response>"+content+"</Response></s:Body></s:Envelope>").getBytes(StandardCharsets.UTF_8);exchange.getResponseHeaders().add("Content-Type","application/soap+xml");exchange.sendResponseHeaders(200,response.length);exchange.getResponseBody().write(response);
     }catch(Exception e){exchange.sendResponseHeaders(500,-1);}finally{exchange.close();}
   }
   public void close(){server.stop(0);}
 }
}
