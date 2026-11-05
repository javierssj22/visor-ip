using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace VisorIP;

public sealed class SavedTile
{
    public int Index { get; set; }
    public Camera Camera { get; set; }=new();
    public StreamChoice? Stream { get; set; }
    public string Url { get; set; }="";
    public string Access { get; set; }="";
    public bool Enabled { get; set; }
}

// DPAPI binds saved access to the current Windows user, without a bundled key.
internal static class AccessVault
{
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern bool CryptProtectData(ref Blob input,string description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true)]
    static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    public static string Seal(Login login)=>Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(login)),true));
    public static Login Open(string value)
    {
        if(string.IsNullOrEmpty(value))return new("","");
        var clear=Transform(Convert.FromBase64String(value),false);
        try{return JsonSerializer.Deserialize<Login>(clear)??new("","");}finally{Array.Clear(clear);}
    }
    static byte[] Transform(byte[] bytes,bool encrypt)
    {
        var input=new Blob{Size=bytes.Length,Data=Marshal.AllocHGlobal(bytes.Length)};Blob output=default;
        try
        {
            Marshal.Copy(bytes,0,input.Data,bytes.Length);
            bool ok=encrypt?CryptProtectData(ref input,"Visor IP",IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output):CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output);
            if(!ok)throw new InvalidOperationException("No se pudo guardar o recuperar el acceso cifrado de Windows.");
            var result=new byte[output.Size];Marshal.Copy(output.Data,result,0,result.Length);return result;
        }
        finally
        {
            for(int i=0;i<input.Size;i++)Marshal.WriteByte(input.Data,i,0);
            Marshal.FreeHGlobal(input.Data);
            if(output.Data!=IntPtr.Zero){for(int i=0;i<output.Size;i++)Marshal.WriteByte(output.Data,i,0);LocalFree(output.Data);}
            Array.Clear(bytes);
        }
    }
}

public sealed class ReconnectSchedule
{
    public const long RefreshMs=300000,RetryMs=15000,StallMs=30000;
    public long Attempt,Progress;
    public bool Started,Failed;
    public void Begin(long now){Started=true;Failed=false;Attempt=Progress=now;}
    public void Advanced(long now){Progress=now;Failed=false;}
    public bool Due(long now,bool enabled,bool busy)=>enabled&&!busy&&(!Started||now-Attempt>=RefreshMs||(Failed?now-Attempt>=RetryMs:now-Progress>=StallMs));
}
