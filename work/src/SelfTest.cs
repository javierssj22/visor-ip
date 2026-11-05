using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VisorIP;

internal static class SelfTest
{
    static readonly List<string> results=new();
    static string Out=>Environment.GetEnvironmentVariable("VISOR_TEST_OUTPUT")??AppContext.BaseDirectory;
    static void Check(bool condition,string name){if(!condition)throw new Exception("FAIL: "+name);results.Add("PASS: "+name);File.WriteAllLines(Path.Combine(Out,"avance-pruebas.txt"),results);}
    public static string EventFixture(string topic="PeopleDetector/People",string name="IsPeople",string value="true",string operation="Changed")=>$"<NotificationMessage><Topic>tns1:RuleEngine/{topic}</Topic><Message><Message PropertyOperation='{operation}'><Data><SimpleItem Name='{name}' Value='{value}'/></Data></Message></Message></NotificationMessage>";
    public static async Task Run()
    {
        Directory.CreateDirectory(Out);
        try
        {
            Check(Net.Prefix("172.16.0.xxx")=="172.16.0" && Net.Prefix("192.168.1.0/24")=="192.168.1" && Net.Prefix("10.0.4")=="10.0.4","Normalización de redes locales /24");
            foreach(var invalid in new[]{"8.8.8","172.32.0","172.16.999","192.168.0.0/16","1;cmd"})
            {
                bool failed=false;try{Net.Prefix(invalid);}catch(ArgumentException){failed=true;}Check(failed,"Rechaza rango inválido: "+invalid);
            }
            var credentials=new Login("admin@local","p:a/ss?# ñ");
            var encoded=Net.WithCredentials("rtsp://172.16.0.12:8554/stream?channel=1",credentials);
            var uri=new Uri(encoded);
            Check(Uri.UnescapeDataString(uri.UserInfo)=="admin@local:p:a/ss?# ñ","Credenciales con caracteres especiales");
            Check(Net.WithoutCredentials(encoded)=="rtsp://172.16.0.12:8554/stream?channel=1","Favoritas sin credenciales en la URL");
            Check(!Net.SameHostHttp("http://172.16.0.99/onvif","172.16.0.12"),"No envía credenciales ONVIF a otro host");
            bool dtd=false;try{Net.Xml("<!DOCTYPE foo [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><foo>&x;</foo>");}catch(System.Xml.XmlException){dtd=true;}Check(dtd,"XML externo deshabilitado");
            var nonce=Enumerable.Range(0,20).Select(x=>(byte)x).ToArray();
            var when=new DateTime(2026,9,18,12,0,0,DateTimeKind.Utc);
            var security=Net.Xml(Onvif.Security(new("a<&","páss"),when,nonce));
            var expected=Convert.ToBase64String(SHA1.HashData(nonce.Concat(Encoding.UTF8.GetBytes("2026-09-18T12:00:00.000Zpáss")).ToArray()));
            Check(Net.Text(security,"Username")=="a<&" && Net.Text(security,"Password")==expected,"WS-Security: escape XML y PasswordDigest");
            var auth=Rtsp.Authorization("WWW-Authenticate: Digest realm=\"cam\", nonce=\"1234\", qop=\"auth,auth-int\"","DESCRIBE","rtsp://172.16.0.12/",new("admin","123"));
            Check(auth.Contains("qop=auth") && auth.Contains("cnonce=") && auth.Contains("response="),"RTSP Digest con qop");
            var old=Rtsp.Authorization("WWW-Authenticate: Digest realm=\"testrealm@host.com\", nonce=\"dcd98b7102dd2f0e8b11d0f600bfb0c093\"","GET","/dir/index.html",new("Mufasa","Circle Of Life"));
            Check(old.Contains("670fd8c2df070c60b045671b8b24ff02"),"RTSP Digest: vector conocido sin qop");
            Check(Rtsp.Authorization("WWW-Authenticate: Basic realm=\"cam\"","DESCRIBE","rtsp://x/",new("u","p"))=="Basic dTpw","RTSP Basic");
            using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using(var server=new FakeServer())
            {
                var cam=new Camera{Ip="127.0.0.1",Endpoints=new(){$"http://127.0.0.1:{server.Port}/onvif/device_service"}};
                using var onvif=new Onvif(cam.Ip,new("admin","test"));
                var profiles=await onvif.Resolve(cam,cts.Token);
                Check(profiles.Count==2 && profiles[0].Name=="Principal" && profiles[1].Name=="Secundario","ONVIF simulado: capacidades y dos perfiles");
                Check(profiles.All(p=>new Uri(p.Url).Host==cam.Ip && new Uri(p.Url).UserInfo==""),"Corrige IP anunciada y elimina credenciales del perfil");
                Check(server.SawDigest,"La cámara simulada recibió WS-Security");
                var options=await Rtsp.Request($"rtsp://127.0.0.1:{server.Port}/","OPTIONS",null,cts.Token);
                Check(options.Status==200,"Detección RTSP OPTIONS en servidor local");
                var challenge=await Rtsp.Request($"rtsp://127.0.0.1:{server.Port}/stream1","DESCRIBE",null,cts.Token);
                Check(challenge.Status==401,"RTSP solicita credenciales");
                var rtspUrl=$"rtsp://127.0.0.1:{server.Port}/stream1";
                var reply=await Rtsp.Request(rtspUrl,"DESCRIBE",Rtsp.Authorization(challenge.Header,"DESCRIBE",rtspUrl,new("admin","test")),cts.Token);
                Check(reply.Status==200&&reply.Body.Contains("m=video"),"RTSP autenticado entrega SDP de video");
                Check(await Net.PortOpen("127.0.0.1",server.Port,cts.Token),"Detección de puerto TCP abierto");
                server.Reject=true;
                bool rejected=false;try{await onvif.Resolve(cam,cts.Token);}catch(UnauthorizedAccessException){rejected=true;}Check(rejected,"Credenciales rechazadas detienen detección ONVIF");
            }
            await using(var server=new FakeServer{Media2=true})
            {
                var cam=new Camera{Ip="127.0.0.1",Endpoints=new(){$"http://127.0.0.1:{server.Port}/onvif/device_service"}};
                using var onvif=new Onvif(cam.Ip,new("admin","test"));
                var profiles=await onvif.Resolve(cam,cts.Token);
                Check(profiles.Count==2&&server.SawMedia2,"ONVIF Media2 mediante GetServices");
            }
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();bool stopped=false;
            await using(var server=new FakeServer())
            {
                var cam=new Camera{Ip="127.0.0.1",Endpoints=new(){$"http://127.0.0.1:{server.Port}/onvif/device_service"}};
                using var onvif=new Onvif(cam.Ip,new("admin","test"));
                var device=await onvif.FindPtz(cam,"",cts.Token);Check(device.Profiles.Count==2,"PTZ: descubre servicio y perfiles de movimiento");
                var profile=device.Profiles[0];var options=await onvif.GetPtzOptions(device.Endpoint,profile,cts.Token);
                Check(options.Pan!=null&&options.Tilt!=null&&options.Zoom!=null&&options.TimeoutMs==1000,"PTZ: rangos y tiempo de parada anunciados por cámara");
                await onvif.PulsePtz(device.Endpoint,profile,options,1,0,0,cts.Token);
                Check(server.Moves==1&&server.Stops==1&&server.LastMove.Contains("PT1S")&&server.LastMove.Contains("0.28"),"PTZ: movimiento corto con timeout y Stop");
                using var cancelMove=new CancellationTokenSource(70);try{await onvif.PulsePtz(device.Endpoint,profile,options,0,-1,0,cancelMove.Token);}catch(OperationCanceledException){}
                Check(server.Stops==2,"PTZ: cancelación también envía Stop");
                server.RejectMove=true;try{await onvif.PulsePtz(device.Endpoint,profile,options,0,0,1,cts.Token);}catch(InvalidOperationException){}
                Check(server.Stops==3,"PTZ: movimiento rechazado también envía Stop");
                bool unsupported=false;try{await onvif.PulsePtz(device.Endpoint,profile,options with{Zoom=null},0,0,1,cts.Token);}catch(InvalidOperationException){unsupported=true;}Check(unsupported&&server.Moves==3,"PTZ: no envía movimiento sin soporte");
            }
            try{await Discovery.Scan("172.16.0",_=>{},_=>{},cancelled.Token);}catch(OperationCanceledException){stopped=true;}Check(stopped,"Cancelación de búsqueda");
            Check(Net.DeviceEndpoints(new Camera{Ip="172.16.0.22",Ports=new(){554}}).Any(u=>u.Contains(":2020/")),"PTZ y alertas encuentran puerto 2020 en cámaras guardadas antiguas");
            var person=Onvif.ParseDetections(Net.Xml(EventFixture())).Single();Check(person.Kind=="Persona"&&person.Active,"Evento Tapo IsPeople se clasifica como persona");
            Check(!Onvif.ParseDetections(Net.Xml(EventFixture("CellMotionDetector/Motion","IsMotion"))).Any(),"Movimiento genérico no se presenta como persona o vehículo");
            Check(!Onvif.ParseDetections(Net.Xml(EventFixture(name:"ObjectId",value:"1"))).Any(),"Identificadores numéricos no disparan falsas alarmas");
            Check(Onvif.ParseDetections(Net.Xml(EventFixture("VehicleDetector/Vehicle","IsVehicle"))).Single().Kind=="Vehículo","Evento explícito de vehículo se reconoce");
            var filter=new DetectionFilter();
            Check(!filter.Accept(person with{Initial=true},0)&&!filter.Accept(person,1000),"Estado inicial y repetición activa no disparan alarma");
            filter.Accept(person with{Active=false},2000);Check(filter.Accept(person,3000),"Cambio inactivo a activo dispara alarma");
            filter.Accept(person with{Active=false},4000);Check(!filter.Accept(person,5000),"Enfriamiento evita sonidos repetidos durante 30 segundos");
            filter.Accept(person with{Active=false},34000);Check(filter.Accept(person,35000),"Una detección posterior vuelve a avisar");
            await using(var server=new FakeServer())
            {
                using var client=new Onvif("127.0.0.1",new("admin","test"));var camera=new Camera{Ip="127.0.0.1",Endpoints=new(){$"http://127.0.0.1:{server.Port}/device"}};
                var support=await client.FindEvents(camera,CancellationToken.None);Check(support.People&&!support.Vehicles,"Detecciones disponibles se consultan por cámara");
                using var stop=new CancellationTokenSource(4000);int received=0;
                try{await client.WatchEvents(support,d=>{received++;stop.Cancel();},stop.Token);}catch(OperationCanceledException){}
                Check(received==1&&server.Pulls==1&&server.Unsubscribes==1&&server.SawReference,"PullPoint conserva referencia, recibe evento y cancela con Unsubscribe");
            }
            results.Add("RESULTADO: TODAS LAS PRUEBAS PASARON");
            File.WriteAllLines(Path.Combine(Out,"pruebas-red.txt"),results);
        }
        catch(Exception e){results.Add(e.ToString());File.WriteAllLines(Path.Combine(Out,"pruebas-red.txt"),results);Environment.ExitCode=1;}
    }
    public static void Smoke()
    {
        Directory.CreateDirectory(Out);
        try
        {
            using var form=new MainForm(true);form.ShowInTaskbar=false;form.Show();form.PopulatePreview();Application.DoEvents();
            var sample=new Login("usuario de prueba","clave ficticia ñ : @");
            var encrypted=AccessVault.Seal(sample);
            Check(AccessVault.Open(encrypted)==sample&&!encrypted.Contains(sample.Password),"DPAPI recupera el acceso sin guardarlo en texto plano");
            var schedule=new ReconnectSchedule();schedule.Begin(1000);schedule.Failed=true;
            Check(!schedule.Due(15999,true,false)&&schedule.Due(16000,true,false),"Reintento a los 15 segundos, sin bucle inmediato");
            schedule.Begin(20000);schedule.Advanced(319999);
            Check(!schedule.Due(319999,true,false)&&schedule.Due(320000,true,false),"Refresco a los 5 minutos aunque siga el video");
            schedule.Begin(0);Check(schedule.Due(30000,true,false)&&!schedule.Due(30000,false,false)&&!schedule.Due(30000,true,true),"Imagen detenida reintenta; pausa manual y operación en curso no");
            foreach(var count in new[]{1,4,6,9,16})
            {
                Check(form.TestLayout(count),$"Cuadrícula de {count}: visores visibles y dentro del área");Application.DoEvents();
                if(count==6)Check(form.TestSixRatio(),"Seis superficies de video 4:3 en ventana");
                using var screenshot=new Bitmap(form.Width,form.Height);form.DrawToBitmap(screenshot,new Rectangle(0,0,form.Width,form.Height));screenshot.Save(Path.Combine(Out,$"interfaz-{count}.png"));
            }
            using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));bitmap.Save(Path.Combine(Out,"interfaz.png"));}
            var normalBounds=form.Bounds;
            form.SetFullscreen(true);Application.DoEvents();
            Check(form.TestFullscreenArea(),"Pantalla completa ocupa el monitor y oculta menús");
            foreach(var count in new[]{1,4,6,9,16})
            {
                Check(form.TestLayout(count),$"Pantalla completa: {count} visores dentro del área");Application.DoEvents();
                Check(form.TestExitButton(),$"Botón Volver accesible en pantalla completa con {count} visores");
                if(count==6)Check(form.TestSixRatio(),"Seis superficies de video 4:3 en pantalla completa");
                using var shot=new Bitmap(form.Width,form.Height);form.DrawToBitmap(shot,new Rectangle(Point.Empty,form.Size));shot.Save(Path.Combine(Out,$"pantalla-completa-{count}.png"));
            }
            form.SetFullscreen(false);Application.DoEvents();
            Check(form.Bounds==normalBounds,"Salir restaura posición y tamaño de ventana");
            var panel=new Panel{Size=new Size(640,360)};form.Controls.Add(panel);panel.CreateControl();
            var player=new Player(panel.Handle);
            var file=Path.Combine(Out,"prueba.avi");MakeAvi(file);
            player.Play(new Uri(file).AbsoluteUri);
            var until=DateTime.UtcNow.AddSeconds(10);bool playing=false;
            while(DateTime.UtcNow<until){Application.DoEvents();if(player.State==3&&player.HasVideo){playing=true;break;}if(player.State==7)break;Thread.Sleep(50);}
            Check(playing,"Motor VLC reproduce archivo AVI de prueba");Pump(Task.Run(player.Stop));Check(player.State is 0 or 5,"Motor VLC detiene reproducción");
            Pump(Task.Run(player.Dispose));
            form.Controls.Remove(panel);panel.Dispose();
            var group=form.TestSurfaces().Select(p=>new Player(p.Handle)).ToArray();
            Pump(Task.WhenAll(group.Select(p=>Task.Run(()=>p.Play(new Uri(file).AbsoluteUri)))));
            until=DateTime.UtcNow.AddSeconds(12);
            while(DateTime.UtcNow<until&&!group.All(p=>p.State==3&&p.HasVideo)){Application.DoEvents();Thread.Sleep(30);}
            File.WriteAllText(Path.Combine(Out,"estados-16.txt"),string.Join(", ",group.Select((p,i)=>$"{i+1}:estado={p.State},video={p.HasVideo}")));
            Check(group.All(p=>p.State==3&&p.HasVideo),"16 reproductores simultáneos con salida de video");
            Check(form.TestLayout(1)&&form.TestLayout(16),"Cambio 16 → 1 → 16 durante reproducción");
            var handles=form.TestSurfaces().Select(p=>p.Handle).ToArray();
            form.SetFullscreen(true);Application.DoEvents();
            Check(form.TestFullscreenArea()&&group.All(p=>p.State==3&&p.HasVideo),"16 videos continúan al entrar en pantalla completa");
            form.SetFullscreen(false);Application.DoEvents();
            Check(handles.SequenceEqual(form.TestSurfaces().Select(p=>p.Handle))&&group.All(p=>p.State==3&&p.HasVideo),"Volver conserva superficies y los 16 videos activos");
            Pump(Task.WhenAll(group.Select(p=>Task.Run(p.Dispose))));
            Pump(form.TestRecovery(file));Check(true,"Video recuperado tras corte, refresco de 5 minutos y Detener respetado");
            Check(form.TestAlertIsolation(Path.Combine(Out,"alerta-persona.png")),"Activación, tipo de detección y desactivación se respetan por cámara");
            using(var ptz=new PtzForm(new Camera{Ip="172.16.0.20"},new Login("",""),null,true))
            {
                ptz.ShowInTaskbar=false;ptz.Opacity=0;ptz.Show();Application.DoEvents();
                using var shot=new Bitmap(ptz.Width,ptz.Height);ptz.DrawToBitmap(shot,new Rectangle(0,0,ptz.Width,ptz.Height));shot.Save(Path.Combine(Out,"ptz.png"));ptz.Close();Application.DoEvents();
            }
            File.WriteAllLines(Path.Combine(Out,"pruebas-interfaz.txt"),results.Append("RESULTADO: INTERFAZ Y MOTOR VLC OK"));
            form.Close();Application.DoEvents();
        }
        catch(Exception e){File.WriteAllText(Path.Combine(Out,"pruebas-interfaz.txt"),e.ToString());Environment.ExitCode=1;}
    }
    public static void AlertUi()
    {
        Directory.CreateDirectory(Out);
        try
        {
            using var form=new MainForm(true);form.ShowInTaskbar=false;form.Show();form.PopulatePreview();Application.DoEvents();
            Check(form.TestAlertIsolation(Path.Combine(Out,"alerta-persona.png")),"Alertas respetan cámara, tipo y desactivación; aviso visual correcto");
            form.Close();Application.DoEvents();File.WriteAllLines(Path.Combine(Out,"pruebas-alertas-ui.txt"),results);
        }
        catch(Exception e){File.WriteAllText(Path.Combine(Out,"pruebas-alertas-ui.txt"),e.ToString());Environment.ExitCode=1;}
    }
    static void Pump(Task task)
    {
        var deadline=DateTime.UtcNow.AddSeconds(15);
        while(!task.IsCompleted){Application.DoEvents();if(DateTime.UtcNow>deadline)throw new TimeoutException("El motor no respondió al cierre dentro del plazo de prueba.");Thread.Sleep(15);}
        task.GetAwaiter().GetResult();
    }
    public static async Task Scan()
    {
        Directory.CreateDirectory(Out);
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var found=new System.Collections.Concurrent.ConcurrentDictionary<string,string>();int progress=0;
        try
        {
            await Discovery.Scan("172.16.0",c=>found[c.Ip]=c.Kind,n=>Interlocked.Exchange(ref progress,n),ct.Token);
            File.WriteAllLines(Path.Combine(Out,"prueba-red-real.txt"),new[]{"Rango: 172.16.0.1–254",$"Direcciones revisadas: {progress}",$"Dispositivos encontrados: {found.Count}"}.Concat(found.Select(x=>x.Key+" · "+x.Value)));
        }
        catch(Exception e){File.WriteAllText(Path.Combine(Out,"prueba-red-real.txt"),$"Direcciones revisadas: {progress}\n"+e.GetType().Name);Environment.ExitCode=1;}
    }
    static void MakeAvi(string path)
    {
        // A small uncompressed AVI avoids external test assets and tests the actual bundled decoder.
        using var f=File.Create(path);using var w=new BinaryWriter(f);
        void Four(string s)=>w.Write(Encoding.ASCII.GetBytes(s));
        long Begin(string name){Four(name);w.Write(0);return f.Position;}
        void End(long p){long end=f.Position;f.Position=p-4;w.Write((int)(end-p));f.Position=end;if(end%2!=0)w.Write((byte)0);}
        var riff=Begin("RIFF");Four("AVI ");var hdrl=Begin("LIST");Four("hdrl");var avih=Begin("avih");
        w.Write(100000);w.Write(64*48*3*10);w.Write(0);w.Write(0);w.Write(600);w.Write(0);w.Write(1);w.Write(64*48*3);w.Write(64);w.Write(48);for(int i=0;i<4;i++)w.Write(0);End(avih);
        var strl=Begin("LIST");Four("strl");var strh=Begin("strh");Four("vids");Four("DIB ");w.Write(0);w.Write((short)0);w.Write((short)0);w.Write(0);w.Write(1);w.Write(10);w.Write(0);w.Write(600);w.Write(64*48*3);w.Write(-1);w.Write(0);w.Write((short)0);w.Write((short)0);w.Write((short)64);w.Write((short)48);End(strh);
        var strf=Begin("strf");w.Write(40);w.Write(64);w.Write(48);w.Write((short)1);w.Write((short)24);w.Write(0);w.Write(64*48*3);for(int i=0;i<4;i++)w.Write(0);End(strf);End(strl);End(hdrl);
        var movi=Begin("LIST");Four("movi");for(int frame=0;frame<600;frame++){var chunk=Begin("00db");for(int y=0;y<48;y++)for(int x=0;x<64;x++){w.Write((byte)(x*4));w.Write((byte)(y*5));w.Write((byte)(frame*4));}End(chunk);}End(movi);End(riff);
    }
    sealed class FakeServer : IAsyncDisposable
    {
        readonly TcpListener listener=new(IPAddress.Loopback,0);
        readonly CancellationTokenSource cts=new();readonly Task loop;
        public int Port=>((IPEndPoint)listener.LocalEndpoint).Port;
        public bool SawDigest,Reject,Media2,SawMedia2,RejectMove;
        public int Moves,Stops,Pulls,Unsubscribes;public bool SawReference;public string LastMove="";
        public FakeServer(){listener.Start();loop=Loop();}
        async Task Loop()
        {
            try{while(!cts.IsCancellationRequested){using var client=await listener.AcceptTcpClientAsync(cts.Token);await Handle(client);}}
            catch(Exception e)when(e is OperationCanceledException or SocketException or IOException){}
        }
        async Task Handle(TcpClient client)
        {
            using var stream=client.GetStream();using var data=new MemoryStream();var buffer=new byte[8192];string all="";int end=-1,len=0;
            while(true){var n=await stream.ReadAsync(buffer,cts.Token);if(n==0)return;data.Write(buffer,0,n);all=Encoding.UTF8.GetString(data.ToArray());end=all.IndexOf("\r\n\r\n");if(end<0)continue;var m=Regex.Match(all[..end],@"(?im)^Content-Length:\s*(\d+)");len=m.Success?int.Parse(m.Groups[1].Value):0;if(data.Length>=Encoding.UTF8.GetByteCount(all[..(end+4)])+len)break;}
            string body="",status="200 OK",headers="";bool rtsp=all[..end].Contains("RTSP/1.0");
            if(rtsp)
            {
                if(all.StartsWith("DESCRIBE")&&!all.Contains("Authorization: Digest")){status="401 Unauthorized";headers="WWW-Authenticate: Digest realm=\"camera\", nonce=\"test-nonce\"\r\n";}
                else if(all.StartsWith("DESCRIBE")){body="v=0\r\nm=video 0 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\n";headers="Content-Type: application/sdp\r\n";}
            }
            else if(Reject){status="401 Unauthorized";}
            else
            {
                var xml=Net.Xml(all[(end+4)..]);SawDigest|=Net.Text(xml,"Password")!="";
                var op=xml.Descendants().First(n=>n.Parent?.Name.LocalName=="Body");
                var ns=op.Name.NamespaceName;string content="";
                switch(op.Name.LocalName)
                {
                    case "GetSystemDateAndTime":content="<SystemDateAndTime><UTCDateTime><Time><Hour>12</Hour><Minute>0</Minute><Second>0</Second></Time><Date><Year>2026</Year><Month>9</Month><Day>18</Day></Date></UTCDateTime></SystemDateAndTime>";break;
                    case "GetCapabilities":content=Media2?"<Capabilities/>":$"<Capabilities><Media><XAddr>http://127.0.0.1:{Port}/media</XAddr></Media><PTZ><XAddr>http://127.0.0.1:{Port}/ptz</XAddr></PTZ><Events><XAddr>http://127.0.0.1:{Port}/events</XAddr></Events></Capabilities>";break;
                    case "GetServices":content=$"<Service><Namespace>http://www.onvif.org/ver20/media/wsdl</Namespace><XAddr>http://127.0.0.1:{Port}/media2</XAddr></Service><Service><Namespace>http://www.onvif.org/ver20/ptz/wsdl</Namespace><XAddr>http://127.0.0.1:{Port}/ptz</XAddr></Service>";break;
                    case "GetProfiles":content="<Profiles token='main'><Name>Principal</Name><PTZConfiguration token='ptz1'/></Profiles><Profiles token='sub'><Name>Secundario</Name><Configurations><PTZ token='ptz1'/></Configurations></Profiles>";SawMedia2|=ns.Contains("ver20");break;
                    case "GetStreamUri":content=$"<MediaUri><Uri>rtsp://embedded:secret@0.0.0.0:554/{Net.Text(xml,"ProfileToken")}</Uri></MediaUri>";break;
                    case "GetConfigurationOptions":content="<PTZConfigurationOptions><Spaces><ContinuousPanTiltVelocitySpace><URI>urn:ptz:pan-tilt</URI><XRange><Min>-1</Min><Max>1</Max></XRange><YRange><Min>-1</Min><Max>1</Max></YRange></ContinuousPanTiltVelocitySpace><ContinuousZoomVelocitySpace><URI>urn:ptz:zoom</URI><XRange><Min>-1</Min><Max>1</Max></XRange></ContinuousZoomVelocitySpace></Spaces><PTZTimeout><Min>PT0.1S</Min><Max>PT10S</Max></PTZTimeout></PTZConfigurationOptions>";break;
                    case "ContinuousMove":Moves++;LastMove=op.ToString();if(RejectMove)content="<Fault><Reason>Unsupported move</Reason></Fault>";break;
                    case "Stop":Stops++;break;
                    case "GetEventProperties":content="<TopicSet><RuleEngine><PeopleDetector><People/></PeopleDetector></RuleEngine></TopicSet>";break;
                    case "CreatePullPointSubscription":content=$"<SubscriptionReference><Address>http://127.0.0.1:{Port}/pull</Address><ReferenceParameters><e:SubscriptionId xmlns:e='urn:test'>fixture</e:SubscriptionId></ReferenceParameters></SubscriptionReference>";break;
                    case "PullMessages":Pulls++;SawReference=Net.Text(xml,"SubscriptionId")=="fixture"&&all.Contains("IsReferenceParameter")&&all.Contains("PullPointSubscription/PullMessagesRequest");content=EventFixture();break;
                    case "Unsubscribe":Unsubscribes++;break;
                }
                body=$"<s:Envelope xmlns:s='http://www.w3.org/2003/05/soap-envelope'><s:Body><Response>{content}</Response></s:Body></s:Envelope>";
                headers="Content-Type: application/soap+xml\r\n";
            }
            var response=$"{(rtsp?"RTSP/1.0":"HTTP/1.1")} {status}\r\n{headers}Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(response),cts.Token);
        }
        public async ValueTask DisposeAsync(){cts.Cancel();listener.Stop();await loop;cts.Dispose();}
    }
}
