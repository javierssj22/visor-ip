using System.Text.Json;
using System.Xml.Linq;
namespace VisorIP;

public sealed partial class Onvif
{
    public async Task<object> InspectCamera(Camera camera,CancellationToken ct)
    {
        var endpoint=$"http://{camera.Ip}:2020/onvif/device_service";
        await Clock(endpoint,ct);
        var report=new Dictionary<string,object>{{"Ip",camera.Ip}};
        try{
            var device=await Call(endpoint,DeviceNs,"GetDeviceInformation","",ct);
            report["Manufacturer"]=Net.Text(device.Root!,"Manufacturer");report["Model"]=Net.Text(device.Root!,"Model");report["Firmware"]=Net.Text(device.Root!,"FirmwareVersion");
            var caps=await Call(endpoint,DeviceNs,"GetCapabilities","<m:Category>All</m:Category>",ct);
            report["Services"]=caps.Descendants().Where(n=>n.Elements().Any(e=>e.Name.LocalName=="XAddr")).Select(n=>new{Kind=n.Name.LocalName,Address=Net.Text(n,"XAddr")}).ToArray();
            camera.Ports=camera.Ports.Union(new[]{2020}).ToList();camera.Endpoints.Add(endpoint);
            try{var ptz=await FindPtz(camera,endpoint,ct);report["PtzProfiles"]=ptz.Profiles.Select(p=>new{p.Name,p.Token,p.Configuration}).ToArray();var opts=await GetPtzOptions(ptz.Endpoint,ptz.Profiles[0],ct);report["PtzOptions"]=opts;}catch(Exception e){report["PtzError"]=Program.SafeError(e);}
            var events=caps.Descendants().FirstOrDefault(n=>n.Name.LocalName=="Events");
            if(events!=null){var address=Net.Text(events,"XAddr");try{
                var props=await Call(address,"http://www.onvif.org/ver10/events/wsdl","GetEventProperties","",ct,action:"http://www.onvif.org/ver10/events/wsdl/EventPortType/GetEventPropertiesRequest");
                report["EventTopics"]=props.Descendants().FirstOrDefault(n=>n.Name.LocalName=="TopicSet")?.ToString(SaveOptions.DisableFormatting)??"Sin temas anunciados";
            }catch(Exception e){report["EventsError"]=Program.SafeError(e);}}
        }catch(Exception e){report["Error"]=Program.SafeError(e);}
        return report;
    }
}
internal static class CameraDiagnostics
{
    public static async Task Events()
    {
        var prefs=Preferences.Load();var results=new List<object>();
        foreach(var saved in prefs.Tiles.GroupBy(t=>t.Camera.Ip).Select(g=>g.First()))
        {
            using var client=new Onvif(saved.Camera.Ip,AccessVault.Open(saved.Access));using var stop=new CancellationTokenSource(9000);
            var received=new List<Detection>();string result="";EventSupport? support=null;
            try{support=await client.FindEvents(saved.Camera,stop.Token);await client.WatchEvents(support,d=>received.Add(d),stop.Token);}
            catch(OperationCanceledException){result="Suscripción y consulta completadas";}catch(Exception e){result=Program.SafeError(e);}
            results.Add(new{Ip=saved.Camera.Ip,Result=result,Support=support,Messages=received.Select(d=>new{d.Kind,d.Active,d.Initial}).ToArray()});
        }
        var path=Path.Combine(Environment.GetEnvironmentVariable("VISOR_TEST_OUTPUT")??AppContext.BaseDirectory,"event-diagnostics.json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
    }
    public static async Task Run()
    {
        var prefs=Preferences.Load();var results=new List<object>();
        foreach(var saved in prefs.Tiles.GroupBy(t=>t.Camera.Ip).Select(g=>g.First()))
        {
            using var client=new Onvif(saved.Camera.Ip,AccessVault.Open(saved.Access));using var timeout=new CancellationTokenSource(30000);
            results.Add(await client.InspectCamera(saved.Camera,timeout.Token));
        }
        var path=Path.Combine(Environment.GetEnvironmentVariable("VISOR_TEST_OUTPUT")??AppContext.BaseDirectory,"camera-diagnostics.json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
    }
}
