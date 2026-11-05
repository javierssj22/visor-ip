package com.visorip.tv;
import java.util.*;
import java.util.concurrent.atomic.AtomicBoolean;
import com.sun.net.httpserver.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
public class EventTests {
    static int checks;
    static void check(boolean ok,String label){if(!ok)throw new AssertionError(label);System.out.println("PASS: "+label);checks++;}
    static String fixture(String topic,String name,String value,String operation){return "<NotificationMessage><Topic>tns1:RuleEngine/"+topic+"</Topic><Message><Message PropertyOperation='"+operation+"'><Data><SimpleItem Name='"+name+"' Value='"+value+"'/></Data></Message></Message></NotificationMessage>";}
    public static void main(String[] args)throws Exception {
        NetSupport.Onvif client=new NetSupport.Onvif("127.0.0.1",new NetSupport.Login("admin","test"));NetSupport.Camera camera=new NetSupport.Camera("127.0.0.1");
        check(client.endpoints(camera).stream().anyMatch(e->e.contains(":2020/")),"Puerto 2020 incluido para cámaras ya guardadas");
        CameraEvents.Detection person=CameraEvents.parse(NetSupport.xml(fixture("PeopleDetector/People","IsPeople","true","Changed"))).get(0);
        check(person.kind.equals("Persona")&&person.active,"Evento IsPeople de Tapo se reconoce");
        check(CameraEvents.parse(NetSupport.xml(fixture("CellMotionDetector/Motion","IsMotion","true","Changed"))).isEmpty(),"No confunde movimiento genérico con personas");
        check(CameraEvents.parse(NetSupport.xml(fixture("PeopleDetector/People","ObjectId","1","Changed"))).isEmpty(),"Un identificador no dispara detección");
        check(CameraEvents.parse(NetSupport.xml(fixture("VehicleDetector/Vehicle","IsVehicle","1","Changed"))).get(0).kind.equals("Vehículo"),"Reconoce eventos de vehículos");
        CameraEvents.Filter filter=new CameraEvents.Filter();
        check(!filter.accept(new CameraEvents.Detection(person.kind,person.key,true,true),0)&&!filter.accept(person,1000),"Estado inicial y duplicados no avisan");
        filter.accept(new CameraEvents.Detection(person.kind,person.key,false,false),2000);check(filter.accept(person,3000),"Activación genera aviso");
        filter.accept(new CameraEvents.Detection(person.kind,person.key,false,false),4000);check(!filter.accept(person,5000),"Enfriamiento de 30 segundos");
        filter.accept(new CameraEvents.Detection(person.kind,person.key,false,false),34000);check(filter.accept(person,35000),"Detección posterior vuelve a avisar");
        HttpServer server=HttpServer.create(new InetSocketAddress(InetAddress.getLoopbackAddress(),0),0);int[] unsub={0};boolean[] reference={false};String base="http://127.0.0.1:"+server.getAddress().getPort();
        server.createContext("/",exchange->{try{
            String request=NetSupport.read(exchange.getRequestBody(),2000000),body="";
            if(request.contains("<m:GetCapabilities"))body="<Capabilities><Events><XAddr>"+base+"/events</XAddr></Events></Capabilities>";
            else if(request.contains("<m:GetEventProperties"))body="<TopicSet><RuleEngine><PeopleDetector><People/></PeopleDetector></RuleEngine></TopicSet>";
            else if(request.contains("<m:CreatePullPointSubscription"))body="<SubscriptionReference><Address>"+base+"/pull</Address><ReferenceParameters><e:SubscriptionId xmlns:e='urn:test'>fixture</e:SubscriptionId></ReferenceParameters></SubscriptionReference>";
            else if(request.contains("<m:PullMessages")){reference[0]=request.contains("IsReferenceParameter")&&request.contains("fixture")&&request.contains("PullPointSubscription/PullMessagesRequest");body=fixture("PeopleDetector/People","IsPeople","true","Changed");}
            else if(request.contains("<m:Unsubscribe"))unsub[0]++;
            byte[] response=("<Envelope><Body>"+body+"</Body></Envelope>").getBytes(StandardCharsets.UTF_8);exchange.sendResponseHeaders(200,response.length);exchange.getResponseBody().write(response);
        }catch(Exception e){exchange.sendResponseHeaders(500,-1);}finally{exchange.close();}});server.start();
        try{camera.endpoints.add(base+"/device");AtomicBoolean cancel=new AtomicBoolean();CameraEvents.Support support=CameraEvents.find(client,camera,cancel);check(support.people&&!support.vehicles,"Consulta capacidades por cámara");int[] received={0};try{CameraEvents.watch(client,support,cancel,d->{received[0]++;cancel.set(true);});}catch(java.io.InterruptedIOException ignored){}check(received[0]==1&&unsub[0]==1&&reference[0],"Suscripción con referencia, evento y cierre al cancelar");}finally{server.stop(0);}
        System.out.println("RESULTADO: "+checks+" PRUEBAS CORRECTAS");
    }
}
