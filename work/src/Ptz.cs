using System.Globalization;
using System.Security;
using System.Xml;
using System.Xml.Linq;

namespace VisorIP;

public record PtzProfile(string Name,string Token,string Configuration)
{
    public override string ToString()=>Name;
}
public sealed record PtzAxis(string Space,double Min,double Max)
{
    public double Speed(int sign)=>sign==0?0:sign>0?Math.Max(0,Max)*0.28:Math.Min(0,Min)*0.28;
}
public record PtzOptions(PtzAxis? Pan,PtzAxis? Tilt,PtzAxis? Zoom,int TimeoutMs);
public record PtzDevice(string Endpoint,List<PtzProfile> Profiles);

public sealed partial class Onvif
{
    const string PtzNs="http://www.onvif.org/ver20/ptz/wsdl";
    static string Escape(string s)=>SecurityElement.Escape(s)??"";
    public async Task<PtzDevice> FindPtz(Camera cam,string preferredEndpoint,CancellationToken ct)
    {
        var endpoints=Net.DeviceEndpoints(cam,preferredEndpoint);
        foreach(var endpoint in endpoints)
        {
            try
            {
                await Clock(endpoint,ct);
                string ptz="",media="";bool media2=false;
                try
                {
                    var cap=await Call(endpoint,DeviceNs,"GetCapabilities","<m:Category>All</m:Category>",ct);
                    var p=cap.Descendants().FirstOrDefault(n=>n.Name.LocalName=="PTZ");if(p!=null)ptz=Net.Text(p,"XAddr");
                    var m=cap.Descendants().FirstOrDefault(n=>n.Name.LocalName=="Media");if(m!=null)media=Net.Text(m,"XAddr");
                }
                catch(InvalidOperationException){}
                if(!Net.SameHostHttp(ptz,host)||!Net.SameHostHttp(media,host))
                {
                    var services=await Call(endpoint,DeviceNs,"GetServices","<m:IncludeCapability>false</m:IncludeCapability>",ct);
                    foreach(var service in services.Descendants().Where(n=>n.Name.LocalName=="Service"))
                    {
                        var ns=Net.Text(service,"Namespace");var url=Net.Text(service,"XAddr");
                        if(!Net.SameHostHttp(url,host))continue;
                        if(ns==PtzNs)ptz=url;
                        if(ns==MediaNs||ns==Media2Ns){media=url;media2=ns==Media2Ns;}
                    }
                }
                if(!Net.SameHostHttp(ptz,host)||!Net.SameHostHttp(media,host))continue;
                var doc=await Call(media,media2?Media2Ns:MediaNs,"GetProfiles","",ct);
                var profiles=new List<PtzProfile>();
                foreach(var p in doc.Descendants().Where(n=>n.Name.LocalName=="Profiles"))
                {
                    var config=p.Descendants().FirstOrDefault(n=>n.Name.LocalName is "PTZConfiguration" or "PTZ");
                    var token=p.Attribute("token")?.Value;var configToken=config?.Attribute("token")?.Value;
                    if(string.IsNullOrEmpty(token)||string.IsNullOrEmpty(configToken))continue;
                    var name=Net.Text(p,"Name");profiles.Add(new(string.IsNullOrEmpty(name)?token:name,token,configToken));
                }
                if(profiles.Count>0)return new(ptz,profiles);
            }
            catch(UnauthorizedAccessException){throw;}
            catch(Exception e)when(e is HttpRequestException or TaskCanceledException or InvalidOperationException){ct.ThrowIfCancellationRequested();}
        }
        throw new InvalidOperationException("No se encontró PTZ por ONVIF. Verificá que el domo admita ONVIF PTZ y que el usuario tenga permiso de movimiento.");
    }
    public async Task<PtzOptions> GetPtzOptions(string endpoint,PtzProfile profile,CancellationToken ct)
    {
        var doc=await Call(endpoint,PtzNs,"GetConfigurationOptions",$"<m:ConfigurationToken>{Escape(profile.Configuration)}</m:ConfigurationToken>",ct);
        return ParsePtzOptions(doc);
    }
    public static PtzOptions ParsePtzOptions(XDocument doc)
    {
        PtzAxis? Axis(string element,string range)
        {
            foreach(var space in doc.Descendants().Where(n=>n.Name.LocalName==element))
            {
                var uri=Net.Text(space,"URI");var r=space.Elements().FirstOrDefault(n=>n.Name.LocalName==range);
                if(uri!=""&&r!=null&&double.TryParse(Net.Text(r,"Min"),NumberStyles.Float,CultureInfo.InvariantCulture,out var min)&&double.TryParse(Net.Text(r,"Max"),NumberStyles.Float,CultureInfo.InvariantCulture,out var max)&&double.IsFinite(min)&&double.IsFinite(max)&&min<=0&&max>=0&&min<max)return new(uri,min,max);
            }
            return null;
        }
        var timeout=doc.Descendants().FirstOrDefault(n=>n.Name.LocalName=="PTZTimeout");int duration=1000;
        if(timeout!=null)
        {
            try
            {
                var min=XmlConvert.ToTimeSpan(Net.Text(timeout,"Min")).TotalMilliseconds;
                var max=XmlConvert.ToTimeSpan(Net.Text(timeout,"Max")).TotalMilliseconds;
                if(min>2000||max<100||max<min)throw new InvalidOperationException("La cámara no admite movimientos con un tiempo de parada breve.");
                duration=(int)Math.Clamp(1000,min,Math.Min(max,2000));
            }
            catch(FormatException){throw new InvalidOperationException("La cámara informó un tiempo PTZ inválido.");}
        }
        return new(Axis("ContinuousPanTiltVelocitySpace","XRange"),Axis("ContinuousPanTiltVelocitySpace","YRange"),Axis("ContinuousZoomVelocitySpace","XRange"),duration);
    }
    public async Task PulsePtz(string endpoint,PtzProfile profile,PtzOptions options,int pan,int tilt,int zoom,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(pan!=0&&options.Pan==null||tilt!=0&&options.Tilt==null||zoom!=0&&options.Zoom==null)throw new InvalidOperationException("Este perfil no admite ese movimiento.");
        string N(double x)=>x.ToString("0.######",CultureInfo.InvariantCulture);
        string velocity="";
        if(pan!=0||tilt!=0)velocity=$"<tt:PanTilt x='{N(options.Pan?.Speed(pan)??0)}' y='{N(options.Tilt?.Speed(tilt)??0)}' space='{Escape((options.Pan??options.Tilt)!.Space)}'/>";
        if(zoom!=0)velocity+=$"<tt:Zoom x='{N(options.Zoom!.Speed(zoom))}' space='{Escape(options.Zoom.Space)}'/>";
        if(velocity=="")return;
        try
        {
            await Call(endpoint,PtzNs,"ContinuousMove",$"<m:ProfileToken>{Escape(profile.Token)}</m:ProfileToken><m:Velocity>{velocity}</m:Velocity><m:Timeout>{XmlConvert.ToString(TimeSpan.FromMilliseconds(options.TimeoutMs))}</m:Timeout>",ct);
            await Task.Delay(Math.Min(280,options.TimeoutMs),ct);
        }
        finally
        {
            // Independent token: cancellation of the move must not cancel its stop command.
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await StopPtz(endpoint,profile,stop.Token);
        }
    }
    public async Task StopPtz(string endpoint,PtzProfile profile,CancellationToken ct)=>_ = await Call(endpoint,PtzNs,"Stop",$"<m:ProfileToken>{Escape(profile.Token)}</m:ProfileToken><m:PanTilt>true</m:PanTilt><m:Zoom>true</m:Zoom>",ct);
}

public sealed class PtzForm:Form
{
    readonly Onvif client;
    readonly Camera camera;
    readonly StreamChoice? stream;
    readonly CancellationTokenSource lifetime=new();
    readonly ComboBox profiles=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    readonly Label info=new(){Dock=DockStyle.Fill,Text="Consultando funciones ONVIF…",TextAlign=ContentAlignment.MiddleLeft};
    readonly List<(Button Button,int Pan,int Tilt,int Zoom)> directions=new();
    PtzDevice? device;PtzOptions? options;CancellationTokenSource? move;Task? pending;bool closeAllowed,closing;
    public PtzForm(Camera camera,Login login,StreamChoice? stream,bool testing=false)
    {
        this.camera=camera;this.stream=stream;client=new(camera.Ip,login);
        Text=$"PTZ · {camera.Ip}";Size=new(405,410);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;
        BackColor=Color.FromArgb(23,35,52);ForeColor=Color.White;Font=new("Segoe UI",10);
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(16),ColumnCount=1,RowCount=5};
        foreach(var h in new[]{34,36,160,38,65})root.RowStyles.Add(new(SizeType.Absolute,h));
        root.Controls.Add(new Label{Text=$"CÁMARA  {camera.Ip}  ·  PTZ",Dock=DockStyle.Fill,ForeColor=Color.Turquoise},0,0);root.Controls.Add(profiles,0,1);
        var pad=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=3};for(int i=0;i<3;i++){pad.ColumnStyles.Add(new(SizeType.Percent,33.33f));pad.RowStyles.Add(new(SizeType.Percent,33.33f));}
        void Add(string text,int x,int y,int z,int col,int row){var b=new Button{Text=text,Dock=DockStyle.Fill,Enabled=false,FlatStyle=FlatStyle.Flat,Font=new("Segoe UI",14)};directions.Add((b,x,y,z));pad.Controls.Add(b,col,row);b.Click+=async(_,_)=>await Pulse(x,y,z);}
        Add("↑",0,1,0,1,0);Add("←",-1,0,0,0,1);Add("→",1,0,0,2,1);Add("↓",0,-1,0,1,2);Add("Zoom +",0,0,1,2,0);Add("Zoom −",0,0,-1,0,0);
        var stop=new Button{Text="DETENER",Dock=DockStyle.Fill,FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(151,57,57)};pad.Controls.Add(stop,1,1);stop.Click+=async(_,_)=>await Stop();root.Controls.Add(pad,0,2);
        root.Controls.Add(new Label{Text="Cada clic da un paso corto y se detiene.",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,3);root.Controls.Add(info,0,4);Controls.Add(root);
        profiles.SelectedIndexChanged+=async(_,_)=>await LoadOptions();
        Shown+=async(_,_)=>{if(testing){profiles.Items.Add(new PtzProfile("Perfil secundario","sub","test"));profiles.SelectedIndex=0;options=new(new("test",-1,1),new("test",-1,1),new("test",-1,1),1000);info.Text="Vista de prueba · datos simulados";UpdateButtons();}else{pending=LoadDevice();await pending;}};
        Deactivate+=(_,_)=>move?.Cancel();
        FormClosing+=async(_,e)=>
        {
            if(closeAllowed)return;e.Cancel=true;if(closing)return;closing=true;move?.Cancel();lifetime.Cancel();Enabled=false;
            if(pending!=null)try{await pending;}catch{}
            client.Dispose();lifetime.Dispose();closeAllowed=true;Close();
        };
    }
    async Task LoadDevice()
    {
        try
        {
            device=await client.FindPtz(camera,stream?.DeviceEndpoint??"",lifetime.Token);
            profiles.Items.AddRange(device.Profiles.ToArray());var preferred=device.Profiles.FindIndex(p=>p.Token==stream?.ProfileToken);profiles.SelectedIndex=preferred>=0?preferred:0;
        }
        catch(OperationCanceledException){}
        catch(Exception e){info.Text=Program.SafeError(e);}
    }
    async Task LoadOptions()
    {
        if(device==null||profiles.SelectedItem is not PtzProfile profile)return;
        options=null;UpdateButtons();profiles.Enabled=false;
        async Task Core(){try{options=await client.GetPtzOptions(device.Endpoint,profile,lifetime.Token);info.Text=options.Pan==null&&options.Tilt==null&&options.Zoom==null?"El perfil no anuncia movimientos PTZ compatibles.":$"Listo · {profile.Name}";}catch(OperationCanceledException){}catch(Exception e){info.Text=Program.SafeError(e);}finally{profiles.Enabled=true;UpdateButtons();}}
        pending=Core();await pending;
    }
    void UpdateButtons(){foreach(var d in directions)d.Button.Enabled=options!=null&&move==null&&(d.Pan==0||options.Pan!=null)&&(d.Tilt==0||options.Tilt!=null)&&(d.Zoom==0||options.Zoom!=null);}
    async Task Pulse(int x,int y,int z)
    {
        if(move!=null||device==null||options==null||profiles.SelectedItem is not PtzProfile profile)return;
        move=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);UpdateButtons();profiles.Enabled=false;
        async Task Core(){try{await client.PulsePtz(device.Endpoint,profile,options,x,y,z,move.Token);info.Text="Movimiento terminado.";}catch(OperationCanceledException){info.Text="Detenido.";}catch(Exception e){info.Text=Program.SafeError(e);}finally{move.Dispose();move=null;profiles.Enabled=true;UpdateButtons();}}
        pending=Core();await pending;
    }
    async Task Stop()
    {
        if(move!=null){move.Cancel();if(pending!=null)await pending;return;}
        if(device==null||profiles.SelectedItem is not PtzProfile profile)return;
        try{using var timeout=new CancellationTokenSource(4000);await client.StopPtz(device.Endpoint,profile,timeout.Token);info.Text="Orden de parada enviada.";}catch(Exception e){info.Text=Program.SafeError(e);}
    }
}
