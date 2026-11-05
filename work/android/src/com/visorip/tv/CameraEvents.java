package com.visorip.tv;
import java.util.*;
import java.util.concurrent.atomic.AtomicBoolean;
import javax.xml.transform.*;
import javax.xml.transform.dom.DOMSource;
import javax.xml.transform.stream.StreamResult;
import java.io.StringWriter;
import org.w3c.dom.*;

final class CameraEvents {
    static final String NS="http://www.onvif.org/ver10/events/wsdl",WSA="http://www.w3.org/2005/08/addressing";
    static class Support {final String endpoint;final boolean people,vehicles;Support(String e,boolean p,boolean v){endpoint=e;people=p;vehicles=v;}}
    static class Detection {final String kind,key;final boolean active,initial;Detection(String k,String id,boolean a,boolean i){kind=k;key=id;active=a;initial=i;}}
    interface Listener {void detected(Detection detection);}
    static class Filter {
        final Map<String,Boolean> states=new HashMap<>();final Map<String,Long> last=new HashMap<>();
        boolean accept(Detection d,long now){boolean was=Boolean.TRUE.equals(states.put(d.key,d.active));if(d.initial||!d.active||was)return false;Long previous=last.get(d.kind);if(previous!=null&&now-previous<30000)return false;last.put(d.kind,now);return true;}
    }
    static String kind(String text){String n=text.toLowerCase(Locale.ROOT);if(n.matches(".*(people|person|human|pedestrian).*"))return "Persona";if(n.contains("vehicle")||n.contains("cardetector")||n.matches(".*(^|[/_:])cars?($|[/_:]).*"))return "Vehículo";return "";}
    static List<Detection> parse(Document doc){List<Detection> result=new ArrayList<>();
        for(Element notification:NetSupport.elements(doc,"NotificationMessage")){
            String topic=NetSupport.text(notification,"Topic");Element message=null;
            for(Element candidate:NetSupport.elements(notification,"Message"))if(candidate.hasAttribute("PropertyOperation")||!NetSupport.elements(candidate,"Data").isEmpty())message=candidate;
            if(message==null)continue;List<Element> data=NetSupport.elements(message,"Data");if(data.isEmpty())continue;
            StringBuilder source=new StringBuilder();for(Element src:NetSupport.elements(message,"Source"))for(Element item:NetSupport.elements(src,"SimpleItem"))source.append(item.getAttribute("Name")).append('=').append(item.getAttribute("Value")).append('|');
            for(Element item:NetSupport.elements(data.get(0),"SimpleItem")){
                String name=item.getAttribute("Name"),value=item.getAttribute("Value"),k=kind(topic);if(k.isEmpty())k=kind(name);if(k.isEmpty())continue;
                if(!name.toLowerCase(Locale.ROOT).matches("ispeople|ishuman|isperson|isvehicle|iscar|state|active|alarm|ispresent|detected|status|ismotion"))continue;
                boolean active=value.equalsIgnoreCase("true")||value.equals("1"),inactive=value.equalsIgnoreCase("false")||value.equals("0");if(!active&&!inactive)continue;
                result.add(new Detection(k,topic+'|'+source+'|'+name,active&&!message.getAttribute("PropertyOperation").equals("Deleted"),message.getAttribute("PropertyOperation").equals("Initialized")));
            }
        }return result;
    }
    static Document call(NetSupport.Onvif client,String endpoint,String operation,String body,String references,boolean unsubscribe)throws Exception {
        String ns=NS,action=NS+(operation.equals("PullMessages")?"/PullPointSubscription/":"/EventPortType/")+operation+"Request";
        if(unsubscribe){ns="http://docs.oasis-open.org/wsn/b-2";action="http://docs.oasis-open.org/wsn/bw-2/SubscriptionManager/UnsubscribeRequest";}
        String header="<a:Action xmlns:a='"+WSA+"'>"+NetSupport.esc(action)+"</a:Action><a:To xmlns:a='"+WSA+"'>"+NetSupport.esc(endpoint)+"</a:To><a:MessageID xmlns:a='"+WSA+"'>urn:uuid:"+UUID.randomUUID()+"</a:MessageID><a:ReplyTo xmlns:a='"+WSA+"'><a:Address>"+WSA+"/anonymous</a:Address></a:ReplyTo>"+references;
        return client.call(endpoint,ns,operation,body,false,action,header);
    }
    static Support find(NetSupport.Onvif client,NetSupport.Camera camera,AtomicBoolean cancel)throws Exception {
        for(String endpoint:client.endpoints(camera)){NetSupport.check(cancel);try{
            client.clock(endpoint);Document caps=client.call(endpoint,NetSupport.Onvif.DEVICE,"GetCapabilities","<m:Category>Events</m:Category>",false);
            List<Element> events=NetSupport.elements(caps,"Events");if(events.isEmpty())continue;String address=NetSupport.text(events.get(0),"XAddr");if(!NetSupport.sameHttp(address,camera.ip))continue;
            Document props=call(client,address,"GetEventProperties","","",false);List<Element> sets=NetSupport.elements(props,"TopicSet");boolean people=false,vehicles=false;
            if(!sets.isEmpty()){NodeList elements=sets.get(0).getElementsByTagNameNS("*","*");for(int i=0;i<elements.getLength();i++){String k=kind(elements.item(i).getLocalName());people|=k.equals("Persona");vehicles|=k.equals("Vehículo");}}
            return new Support(address,people,vehicles);
        }catch(NetSupport.AuthException e){throw e;}catch(Exception ignored){NetSupport.check(cancel);}}
        throw new java.io.IOException("La cámara no ofrece alertas ONVIF accesibles.");
    }
    static void watch(NetSupport.Onvif client,Support support,AtomicBoolean cancel,Listener listener)throws Exception {
        while(!cancel.get()){
            String address="",references="";
            try{
                Document created=call(client,support.endpoint,"CreatePullPointSubscription","<m:InitialTerminationTime>PT60S</m:InitialTerminationTime>","",false);
                List<Element> ref=NetSupport.elements(created,"SubscriptionReference");if(ref.isEmpty())throw new java.io.IOException("Suscripción inválida");
                address=NetSupport.text(ref.get(0),"Address");if(!NetSupport.sameHttp(address,client.host))throw new java.io.IOException("Suscripción en otra IP");
                List<Element> parameters=NetSupport.elements(ref.get(0),"ReferenceParameters");
                if(!parameters.isEmpty())for(Node n=parameters.get(0).getFirstChild();n!=null;n=n.getNextSibling())if(n instanceof Element){Element copy=(Element)n.cloneNode(true);copy.setAttributeNS(WSA,"a:IsReferenceParameter","true");StringWriter out=new StringWriter();Transformer transformer=TransformerFactory.newInstance().newTransformer();transformer.setOutputProperty(OutputKeys.OMIT_XML_DECLARATION,"yes");transformer.transform(new DOMSource(copy),new StreamResult(out));references+=out;}
                long until=System.nanoTime()/1000000+45000;
                while(System.nanoTime()/1000000<until){NetSupport.check(cancel);Document pulled=call(client,address,"PullMessages","<m:Timeout>PT1S</m:Timeout><m:MessageLimit>64</m:MessageLimit>",references,false);for(Detection d:parse(pulled))listener.detected(d);Thread.sleep(150);}
            }finally{if(NetSupport.sameHttp(address,client.host))try{call(client,address,"Unsubscribe","",references,true);}catch(Exception ignored){}}
        }
    }
}
