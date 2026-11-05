using System.Xml.Linq;
using System.Text.RegularExpressions;
namespace VisorIP;

public sealed class AlertSettings
{
    public bool Enabled { get; set; }
    public bool People { get; set; }=true;
    public bool Vehicles { get; set; }
    public bool Sound { get; set; }=true;
}
public record EventSupport(string Endpoint,bool People,bool Vehicles);
public record Detection(string Kind,string Key,bool Active,bool Initial);
public sealed class DetectionFilter
{
    readonly Dictionary<string,bool> states=new();
    readonly Dictionary<string,long> last=new();
    public bool Accept(Detection d,long now)
    {
        bool was=states.GetValueOrDefault(d.Key);states[d.Key]=d.Active;
        if(d.Initial||!d.Active||was)return false;
        if(last.TryGetValue(d.Kind,out var previous)&&now-previous<30000)return false;
        last[d.Kind]=now;return true;
    }
}
public sealed partial class Onvif
{
    const string EventNs="http://www.onvif.org/ver10/events/wsdl",Wsa="http://www.w3.org/2005/08/addressing";
    public static string DetectionKind(string text)
    {
        var name=text.ToLowerInvariant();
        if(Regex.IsMatch(name,@"people|person|human|pedestrian"))return "Persona";
        if(Regex.IsMatch(name,@"vehicle|(?:^|[/_:])cars?(?:$|[/_:])|cardetector"))return "Vehículo";
        return "";
    }
    public static IEnumerable<Detection> ParseDetections(XDocument doc)
    {
        foreach(var notification in doc.Descendants().Where(n=>n.Name.LocalName=="NotificationMessage"))
        {
            string topic=Net.Text(notification,"Topic");
            var message=notification.Descendants().FirstOrDefault(n=>n.Name.LocalName=="Message"&&n.Attribute("PropertyOperation")!=null)
                ??notification.Descendants().FirstOrDefault(n=>n.Name.LocalName=="Message"&&n.Elements().Any(c=>c.Name.LocalName=="Data"));
            if(message==null)continue;
            var data=message.Elements().FirstOrDefault(n=>n.Name.LocalName=="Data");if(data==null)continue;
            string source=string.Join("|",message.Elements().Where(n=>n.Name.LocalName=="Source").Descendants().Where(n=>n.Name.LocalName=="SimpleItem").Select(n=>n.Attribute("Name")?.Value+"="+n.Attribute("Value")?.Value));
            foreach(var item in data.Descendants().Where(n=>n.Name.LocalName=="SimpleItem"))
            {
                var name=item.Attribute("Name")?.Value??"";var value=item.Attribute("Value")?.Value??"";
                if(!Regex.IsMatch(name,@"^(ispeople|ishuman|isperson|isvehicle|iscar|state|active|alarm|ispresent|detected|status|ismotion)$",RegexOptions.IgnoreCase))continue;
                var kind=DetectionKind(topic);if(kind=="")kind=DetectionKind(name);if(kind=="")continue;
                bool active=value.Equals("true",StringComparison.OrdinalIgnoreCase)||value=="1";
                bool inactive=value.Equals("false",StringComparison.OrdinalIgnoreCase)||value=="0";
                if(!active&&!inactive)continue;
                yield return new(kind,topic+"|"+source+"|"+name,active&&(string?)message.Attribute("PropertyOperation")!="Deleted",(string?)message.Attribute("PropertyOperation")=="Initialized");
            }
        }
    }
    public async Task<EventSupport> FindEvents(Camera camera,CancellationToken ct)
    {
        foreach(var endpoint in Net.DeviceEndpoints(camera))
        {
            try{
                await Clock(endpoint,ct);
                var caps=await Call(endpoint,DeviceNs,"GetCapabilities","<m:Category>Events</m:Category>",ct);
                var events=caps.Descendants().FirstOrDefault(n=>n.Name.LocalName=="Events");var address=events==null?"":Net.Text(events,"XAddr");
                if(!Net.SameHostHttp(address,host))continue;
                var props=await EventCall(address,"GetEventProperties","",ct);
                var topicSet=props.Descendants().FirstOrDefault(n=>n.Name.LocalName=="TopicSet");
                var names=topicSet?.Descendants().Select(n=>n.Name.LocalName).ToArray()??Array.Empty<string>();
                return new(address,names.Any(n=>DetectionKind(n)=="Persona"),names.Any(n=>DetectionKind(n)=="Vehículo"));
            }catch(UnauthorizedAccessException){throw;}catch(Exception e)when(e is HttpRequestException or TaskCanceledException or InvalidOperationException){ct.ThrowIfCancellationRequested();}
        }
        throw new InvalidOperationException("La cámara no ofrece un servicio de alertas ONVIF accesible.");
    }
    Task<XDocument> EventCall(string endpoint,string op,string body,CancellationToken ct,string references="",bool subscription=false)
    {
        string action=EventNs+(op=="PullMessages"?"/PullPointSubscription/":"/EventPortType/")+op+"Request",ns=EventNs;
        if(subscription){action="http://docs.oasis-open.org/wsn/bw-2/SubscriptionManager/UnsubscribeRequest";ns="http://docs.oasis-open.org/wsn/b-2";}
        string header=$"<a:Action xmlns:a='{Wsa}'>{Escape(action)}</a:Action><a:To xmlns:a='{Wsa}'>{Escape(endpoint)}</a:To><a:MessageID xmlns:a='{Wsa}'>urn:uuid:{Guid.NewGuid()}</a:MessageID><a:ReplyTo xmlns:a='{Wsa}'><a:Address>{Wsa}/anonymous</a:Address></a:ReplyTo>"+references;
        return Call(endpoint,ns,op,body,ct,action:action,extraHeader:header);
    }
    public async Task WatchEvents(EventSupport support,Action<Detection> onDetection,CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            string address="",references="";
            try{
                var created=await EventCall(support.Endpoint,"CreatePullPointSubscription","<m:InitialTerminationTime>PT60S</m:InitialTerminationTime>",ct);
                var reference=created.Descendants().FirstOrDefault(n=>n.Name.LocalName=="SubscriptionReference")??throw new InvalidOperationException("Suscripción de alertas inválida.");
                address=Net.Text(reference,"Address");if(!Net.SameHostHttp(address,host))throw new InvalidOperationException("La suscripción anunció otra dirección IP.");
                var parameters=reference.Elements().FirstOrDefault(n=>n.Name.LocalName=="ReferenceParameters");
                if(parameters!=null)references=string.Concat(parameters.Elements().Select(n=>{var copy=new XElement(n);copy.SetAttributeValue(XName.Get("IsReferenceParameter",Wsa),"true");return copy.ToString(SaveOptions.DisableFormatting);}));
                long until=Environment.TickCount64+45000;
                while(Environment.TickCount64<until){ct.ThrowIfCancellationRequested();var pulled=await EventCall(address,"PullMessages","<m:Timeout>PT1S</m:Timeout><m:MessageLimit>64</m:MessageLimit>",ct,references);foreach(var d in ParseDetections(pulled))onDetection(d);await Task.Delay(150,ct);}
            }
            finally{
                if(Net.SameHostHttp(address,host))try{using var stop=new CancellationTokenSource(2500);await EventCall(address,"Unsubscribe","",stop.Token,references,true);}catch{}
            }
        }
    }
}
