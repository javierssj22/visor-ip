using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace VisorIP;

public sealed class Camera
{
    public string Ip { get; set; } = "";
    public string Name { get; set; } = "Cámara";
    public string Kind { get; set; } = "";
    public List<int> Ports { get; set; } = new();
    public List<string> Endpoints { get; set; } = new();
    public string SavedUrl { get; set; } = "";
    public override string ToString() => $"{Ip} · {Kind}";
}
public record Login(string User, string Password);
public record StreamChoice(string Name, string Url, string ProfileToken = "", string DeviceEndpoint = "")
{
    public override string ToString() => Name;
}
public static class Net
{
    public static IEnumerable<string> DeviceEndpoints(Camera camera,string preferred="")=>new[]{preferred}.Concat(camera.Endpoints).Concat(camera.Ports.Where(p=>p!=554&&p!=8554).Select(p=>$"http://{camera.Ip}:{p}/onvif/device_service")).Append($"http://{camera.Ip}:2020/onvif/device_service").Append($"http://{camera.Ip}/onvif/device_service").Where(s=>SameHostHttp(s,camera.Ip)).Distinct().Take(8);
    public static string Prefix(string input)
    {
        var s = input.Trim().ToLowerInvariant();
        if (s.EndsWith("/24")) s = s[..^3];
        var pieces = s.TrimEnd('.').Split('.');
        if (pieces.Length == 4 && (pieces[3] == "xxx" || pieces[3] == "*" || byte.TryParse(pieces[3], out _))) pieces = pieces[..3];
        if (pieces.Length != 3 || pieces.Any(x => !byte.TryParse(x, out _))) throw new ArgumentException("Ingresá una red como 172.16.0 o 192.168.1 (rango de 254 direcciones).");
        var p = string.Join('.', pieces.Select(byte.Parse));
        if (!IsPrivate(IPAddress.Parse(p + ".1"))) throw new ArgumentException("La búsqueda está limitada a redes locales privadas: 10.x.x, 172.16–31.x o 192.168.x.");
        return p;
    }
    public static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168);
    }
    public static IEnumerable<IPAddress> LocalAddresses()
    {
        try { return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up).SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).Where(IsPrivate).Distinct().ToArray(); }
        catch { return Array.Empty<IPAddress>(); }
    }
    public static string WithoutCredentials(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
        var b = new UriBuilder(u) { UserName = "", Password = "" };
        return b.Uri.AbsoluteUri;
    }
    public static string WithCredentials(string url, Login login)
    {
        if (string.IsNullOrWhiteSpace(login.User)) return url;
        var u = new Uri(url);
        var authority = u.IsDefaultPort ? u.Host : $"{u.Host}:{u.Port}";
        return $"{u.Scheme}://{Uri.EscapeDataString(login.User)}:{Uri.EscapeDataString(login.Password)}@{authority}{u.PathAndQuery}";
    }
    public static XDocument Xml(string s)
    {
        using var sr = new StringReader(s);
        using var xr = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
        return XDocument.Load(xr);
    }
    public static string Text(XContainer x, string name) => x.Descendants().FirstOrDefault(n => n.Name.LocalName == name)?.Value ?? "";
    public static bool SameHostHttp(string url, string ip) => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") && u.Host == ip && string.IsNullOrEmpty(u.UserInfo);
    public static async Task<bool> PortOpen(string ip, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(650);
        try { await client.ConnectAsync(ip, port, timeout.Token); return true; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }
    }
}

public static class Discovery
{
    static readonly int[] Ports = { 554, 8554, 2020, 80, 8080, 8000, 8899 };
    public static async Task Scan(string prefix, Action<Camera> found, Action<int> progress, CancellationToken ct)
    {
        int done = 0;
        var ws = WsDiscovery(prefix, found, ct);
        await Parallel.ForEachAsync(Enumerable.Range(1,254), new ParallelOptions { MaxDegreeOfParallelism = 20, CancellationToken = ct }, async (i, token) =>
        {
            var ip = $"{prefix}.{i}";
            var opened = await Task.WhenAll(Ports.Select(async p => (Port:p, Open:await Net.PortOpen(ip,p,token))));
            var ports = opened.Where(p=>p.Open).Select(p=>p.Port).ToList();
            if (ports.Count > 0)
            {
                var cam = new Camera { Ip = ip, Ports = ports, Kind = "Servicio web · sin confirmar" };
                bool rtsp = false;
                foreach (var p in ports.Where(p=>p==554 || p==8554))
                {
                    try { var r = await Rtsp.Request($"rtsp://{ip}:{p}/", "OPTIONS", null, token); if (r.Status > 0) rtsp = true; } catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { token.ThrowIfCancellationRequested(); }
                }
                if (rtsp) cam.Kind = "RTSP · cámara posible";
                // An open web port is only a candidate, never a confirmed camera.
                found(cam);
            }
            progress(Interlocked.Increment(ref done));
        });
        await ws;
    }
    public static async Task WsDiscovery(string prefix, Action<Camera> found, CancellationToken ct)
    {
        string probe = $"<s:Envelope xmlns:s='http://www.w3.org/2003/05/soap-envelope' xmlns:a='http://schemas.xmlsoap.org/ws/2004/08/addressing' xmlns:d='http://schemas.xmlsoap.org/ws/2005/04/discovery' xmlns:dn='http://www.onvif.org/ver10/network/wsdl'><s:Header><a:MessageID>uuid:{Guid.NewGuid()}</a:MessageID><a:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</a:To><a:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</a:Action></s:Header><s:Body><d:Probe><d:Types>dn:NetworkVideoTransmitter</d:Types></d:Probe></s:Body></s:Envelope>";
        await Task.WhenAll(Net.LocalAddresses().Select(async ip =>
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(ip, 0));
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                var data = Encoding.UTF8.GetBytes(probe);
                await udp.SendAsync(data, new IPEndPoint(IPAddress.Parse("239.255.255.250"),3702),ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(4000);
                while (!timeout.IsCancellationRequested)
                {
                    var response = await udp.ReceiveAsync(timeout.Token);
                    var remote = response.RemoteEndPoint.Address.ToString();
                    if (!remote.StartsWith(prefix + ".", StringComparison.Ordinal)) continue;
                    try
                    {
                        var doc = Net.Xml(Encoding.UTF8.GetString(response.Buffer));
                        foreach (var match in doc.Descendants().Where(n=>n.Name.LocalName=="ProbeMatch"))
                        {
                            var endpoints = Net.Text(match,"XAddrs").Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Where(u=>Net.SameHostHttp(u,remote)).ToList();
                            if (endpoints.Count == 0) continue;
                            var name = Net.Text(match,"Scopes").Split(' ').FirstOrDefault(s=>s.Contains("/name/"));
                            found(new Camera { Ip=remote, Kind="ONVIF", Name=name==null ? "Cámara ONVIF" : Uri.UnescapeDataString(name[(name.IndexOf("/name/")+6)..]), Endpoints=endpoints });
                        }
                    }
                    catch (Exception e) when (e is XmlException or UriFormatException) { }
                }
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException) { }
        }));
    }
}

public sealed partial class Onvif : IDisposable
{
    readonly HttpClient client;
    readonly Login login;
    readonly string host;
    TimeSpan offset;
    const string DeviceNs = "http://www.onvif.org/ver10/device/wsdl";
    const string MediaNs = "http://www.onvif.org/ver10/media/wsdl";
    const string Media2Ns = "http://www.onvif.org/ver20/media/wsdl";
    public Onvif(string host, Login login)
    {
        this.host = host; this.login = login;
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, Credentials = string.IsNullOrEmpty(login.User) ? null : new NetworkCredential(login.User,login.Password) };
        client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize=2_000_000 };
    }
    public static string Security(Login login, DateTime utc, byte[] nonce)
    {
        if (string.IsNullOrEmpty(login.User)) return "";
        var created = utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ",System.Globalization.CultureInfo.InvariantCulture);
        var digest = Convert.ToBase64String(SHA1.HashData(nonce.Concat(Encoding.UTF8.GetBytes(created+login.Password)).ToArray()));
        var w = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
        var u = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
        XNamespace ws = w, util = u;
        return new XElement(ws+"Security",new XAttribute(XNamespace.Xmlns+"wsse",w),new XAttribute(XNamespace.Xmlns+"wsu",u),
            new XElement(ws+"UsernameToken",new XElement(ws+"Username",login.User),
                new XElement(ws+"Password",new XAttribute("Type","http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest"),digest),
                new XElement(ws+"Nonce",new XAttribute("EncodingType","http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary"),Convert.ToBase64String(nonce)),new XElement(util+"Created",created))).ToString(SaveOptions.DisableFormatting);
    }
    async Task<XDocument> Call(string endpoint, string ns, string op, string body, CancellationToken ct, bool anonymous=false,string? action=null,string extraHeader="")
    {
        if (!Net.SameHostHttp(endpoint, host)) throw new InvalidOperationException("La cámara anunció un servicio en otra dirección IP. Usá la URL manual.");
        var security = anonymous ? "" : Security(login,DateTime.UtcNow+offset,RandomNumberGenerator.GetBytes(20));
        action??=$"{ns}/{op}";
        var xml = $"<s:Envelope xmlns:s='http://www.w3.org/2003/05/soap-envelope' xmlns:tt='http://www.onvif.org/ver10/schema'><s:Header>{security}{extraHeader}</s:Header><s:Body><m:{op} xmlns:m='{ns}'>{body}</m:{op}></s:Body></s:Envelope>";
        using var req = new HttpRequestMessage(HttpMethod.Post,endpoint);
        req.Content = new StringContent(xml,Encoding.UTF8,"application/soap+xml");
        req.Content.Headers.ContentType!.Parameters.Add(new System.Net.Http.Headers.NameValueHeaderValue("action",$"\"{action}\""));
        req.Headers.TryAddWithoutValidation("SOAPAction",$"\"{action}\"");
        using var res = await client.SendAsync(req,ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UnauthorizedAccessException("La cámara rechazó el usuario o la contraseña. Verificá también que ONVIF esté habilitado.");
        var content = await res.Content.ReadAsStringAsync(ct);
        XDocument doc;
        try { doc = Net.Xml(content); } catch (XmlException) { throw new InvalidOperationException("El servicio no respondió como ONVIF."); }
        var fault = doc.Descendants().FirstOrDefault(n=>n.Name.LocalName=="Fault");
        if (fault != null)
        {
            if (fault.Value.Contains("NotAuthorized",StringComparison.OrdinalIgnoreCase) || fault.Value.Contains("FailedAuthentication",StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("ONVIF rechazó las credenciales o la hora de la cámara. Revisá usuario, contraseña y configuración ONVIF.");
            throw new InvalidOperationException("La cámara no admite esta operación ONVIF.");
        }
        res.EnsureSuccessStatusCode();
        return doc;
    }
    async Task Clock(string endpoint,CancellationToken ct)
    {
        try
        {
            var doc=await Call(endpoint,DeviceNs,"GetSystemDateAndTime","",ct,true);
            var utc=doc.Descendants().FirstOrDefault(n=>n.Name.LocalName=="UTCDateTime");
            if(utc!=null)
            {
                int I(string n)=>int.Parse(Net.Text(utc,n));
                offset=new DateTime(I("Year"),I("Month"),I("Day"),I("Hour"),I("Minute"),I("Second"),DateTimeKind.Utc)-DateTime.UtcNow;
            }
        }
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException or InvalidOperationException or UnauthorizedAccessException or FormatException or ArgumentOutOfRangeException) { ct.ThrowIfCancellationRequested(); }
    }
    public async Task<List<StreamChoice>> Resolve(Camera cam,CancellationToken ct)
    {
        var endpoints = Net.DeviceEndpoints(cam);
        foreach(var endpoint in endpoints)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await Clock(endpoint,ct);
                var cap=await Call(endpoint,DeviceNs,"GetCapabilities","<m:Category>Media</m:Category>",ct);
                var media=cap.Descendants().FirstOrDefault(n=>n.Name.LocalName=="Media");
                var mediaUrl=media==null ? "" : Net.Text(media,"XAddr");
                if(Net.SameHostHttp(mediaUrl,host))
                {
                    var result=await Profiles(mediaUrl,false,ct);
                    if(result.Count>0) return result.Select(p=>p with {DeviceEndpoint=endpoint}).ToList();
                }
            }
            catch(UnauthorizedAccessException) { throw; }
            catch(Exception e) when(e is HttpRequestException or TaskCanceledException or InvalidOperationException) { ct.ThrowIfCancellationRequested(); }
            try
            {
                var services=await Call(endpoint,DeviceNs,"GetServices","<m:IncludeCapability>false</m:IncludeCapability>",ct);
                foreach(var service in services.Descendants().Where(n=>n.Name.LocalName=="Service"))
                {
                    var ns=Net.Text(service,"Namespace"); var url=Net.Text(service,"XAddr");
                    if((ns==MediaNs || ns==Media2Ns) && Net.SameHostHttp(url,host))
                    {
                        var result=await Profiles(url,ns==Media2Ns,ct);
                        if(result.Count>0) return result.Select(p=>p with {DeviceEndpoint=endpoint}).ToList();
                    }
                }
            }
            catch(UnauthorizedAccessException) { throw; }
            catch(Exception e) when(e is HttpRequestException or TaskCanceledException or InvalidOperationException) { ct.ThrowIfCancellationRequested(); }
        }
        return new();
    }
    async Task<List<StreamChoice>> Profiles(string endpoint,bool media2,CancellationToken ct)
    {
        var ns=media2?Media2Ns:MediaNs;
        var doc=await Call(endpoint,ns,"GetProfiles","",ct);
        var profiles=doc.Descendants().Where(n=>n.Name.LocalName=="Profiles").Take(12).ToArray();
        var result=new List<StreamChoice>();
        foreach(var profile in profiles)
        {
            var token=profile.Attribute("token")?.Value;
            if(string.IsNullOrEmpty(token)) continue;
            var safe=System.Security.SecurityElement.Escape(token);
            var body=media2?$"<m:Protocol>RTSP</m:Protocol><m:ProfileToken>{safe}</m:ProfileToken>":$"<m:StreamSetup><tt:Stream>RTP-Unicast</tt:Stream><tt:Transport><tt:Protocol>RTSP</tt:Protocol></tt:Transport></m:StreamSetup><m:ProfileToken>{safe}</m:ProfileToken>";
            try
            {
                var stream=await Call(endpoint,ns,"GetStreamUri",body,ct);
                var url=Net.Text(stream,"Uri");
                if(!Uri.TryCreate(url,UriKind.Absolute,out var u) || !(u.Scheme is "rtsp" or "http" or "https")) continue;
                // Some devices return 0.0.0.0 or a stale hostname. Keep requests on the selected camera.
                var fixedUrl=new UriBuilder(u) { Host=host,UserName="",Password="" }.Uri.AbsoluteUri;
                var name=Net.Text(profile,"Name");
                result.Add(new StreamChoice(string.IsNullOrEmpty(name)?$"Perfil {result.Count+1}":name,fixedUrl,token));
            }
            catch(UnauthorizedAccessException) { throw; }
            catch(Exception e) when(e is HttpRequestException or TaskCanceledException or InvalidOperationException) { ct.ThrowIfCancellationRequested(); }
        }
        return result;
    }
    public void Dispose()=>client.Dispose();
}

public record RtspReply(int Status,string Header,string Body);
public static class Rtsp
{
    public static async Task<RtspReply> Request(string url,string method,string? auth,CancellationToken ct)
    {
        var uri=new Uri(url);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(1800);
        using var tcp=new TcpClient();
        await tcp.ConnectAsync(uri.Host,uri.Port>0?uri.Port:554,timeout.Token);
        using var stream=tcp.GetStream();
        var request=$"{method} {url} RTSP/1.0\r\nCSeq: 1\r\nUser-Agent: VisorIP/1.0\r\n"+(method=="DESCRIBE"?"Accept: application/sdp\r\n":"")+(auth==null?"":$"Authorization: {auth}\r\n")+"\r\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request),timeout.Token);
        using var data=new MemoryStream(); var buffer=new byte[4096];
        int end=-1,length=0;
        while(data.Length<131072)
        {
            int n=await stream.ReadAsync(buffer,timeout.Token); if(n==0) break;
            data.Write(buffer,0,n);
            var s=Encoding.UTF8.GetString(data.ToArray());
            end=s.IndexOf("\r\n\r\n",StringComparison.Ordinal);
            if(end>=0)
            {
                var m=Regex.Match(s[..end],@"(?im)^Content-Length:\s*(\d+)");
                length=m.Success?int.Parse(m.Groups[1].Value):0;
                if(Encoding.UTF8.GetByteCount(s[..(end+4)])+length<=data.Length) break;
            }
        }
        var all=Encoding.UTF8.GetString(data.ToArray());
        var status=Regex.Match(all,@"^RTSP/1\.\d (\d{3})");
        return new(status.Success?int.Parse(status.Groups[1].Value):0,end>=0?all[..end]:all,end>=0?all[(end+4)..]:"");
    }
    public static string Authorization(string header,string method,string url,Login login)
    {
        var line=header.Split(new[]{"\r\n"},StringSplitOptions.None).FirstOrDefault(l=>l.StartsWith("WWW-Authenticate: Digest",StringComparison.OrdinalIgnoreCase));
        if(line==null)
        {
            if(header.Contains("WWW-Authenticate: Basic",StringComparison.OrdinalIgnoreCase)) return "Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes(login.User+":"+login.Password));
            throw new InvalidOperationException("La cámara usa una autenticación RTSP no compatible. Probá la URL manual.");
        }
        var fields=Regex.Matches(line,@"(\w+)\s*=\s*(?:""([^""]*)""|([^,\s]+))").ToDictionary(m=>m.Groups[1].Value,m=>m.Groups[2].Success?m.Groups[2].Value:m.Groups[3].Value,StringComparer.OrdinalIgnoreCase);
        string F(string k)=>fields.GetValueOrDefault(k,"");
        string H(string s)=>Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
        var alg=F("algorithm");
        if(alg!=""&&!alg.Equals("MD5",StringComparison.OrdinalIgnoreCase)&&!alg.Equals("MD5-sess",StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Autenticación RTSP no compatible. Probá reproducir la URL manual con VLC.");
        var cnonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var ha1=H(login.User+":"+F("realm")+":"+login.Password);
        if(alg.Equals("MD5-sess",StringComparison.OrdinalIgnoreCase)) ha1=H(ha1+":"+F("nonce")+":"+cnonce);
        bool qop=F("qop").Split(',').Any(x=>x.Trim()=="auth");
        if(F("qop")!=""&&!qop) throw new InvalidOperationException("La cámara solicita un modo Digest no compatible.");
        var response=H(ha1+":"+F("nonce")+":"+(qop?$"00000001:{cnonce}:auth:":"")+H(method+":"+url));
        string Q(string s)=>s.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r","").Replace("\n","");
        return $"Digest username=\"{Q(login.User)}\", realm=\"{Q(F("realm"))}\", nonce=\"{Q(F("nonce"))}\", uri=\"{Q(url)}\", response=\"{response}\""+(alg!=""?$", algorithm={alg}":"")+(qop?$", qop=auth, nc=00000001, cnonce=\"{cnonce}\"":alg.Equals("MD5-sess",StringComparison.OrdinalIgnoreCase)?$", cnonce=\"{cnonce}\"":"")+(F("opaque")!=""?$", opaque=\"{Q(F("opaque"))}\"":"");
    }
    public static async Task<List<StreamChoice>> Resolve(Camera cam,Login login,CancellationToken ct)
    {
        var result=new List<StreamChoice>();
        var paths=new[]{("Principal Hikvision","/Streaming/Channels/101"),("Principal Dahua / Amcrest","/cam/realmonitor?channel=1&subtype=0"),("Principal genérico","/stream1"),("ONVIF / genérico","/onvif1"),("Principal H.264","/h264Preview_01_main"),("Video principal","/live/ch00_0"),("Video en vivo","/live"),("Raíz RTSP","/")};
        foreach(var port in cam.Ports.Where(p=>p==554||p==8554).DefaultIfEmpty(554))
        {
            foreach(var (name,path) in paths)
            {
                ct.ThrowIfCancellationRequested();
                var url=$"rtsp://{cam.Ip}:{port}{path}";
                try
                {
                    var reply=await Request(url,"DESCRIBE",null,ct);
                    if(reply.Status==401)
                    {
                        if(string.IsNullOrEmpty(login.User)) throw new UnauthorizedAccessException("Ingresá el usuario y la contraseña para acceder al video.");
                        reply=await Request(url,"DESCRIBE",Authorization(reply.Header,"DESCRIBE",url,login),ct);
                        if(reply.Status==401 || reply.Status==403) throw new UnauthorizedAccessException("La cámara rechazó las credenciales RTSP. Corregilas antes de volver a intentar.");
                    }
                    if(reply.Status==200 && Regex.IsMatch(reply.Body,@"(?m)^m=video\s")) { result.Add(new(name,url)); return result; }
                }
                catch(Exception e) when(e is IOException or SocketException or TaskCanceledException) { ct.ThrowIfCancellationRequested(); }
            }
        }
        return result;
    }
}
