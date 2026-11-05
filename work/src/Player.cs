using System.Runtime.InteropServices;

namespace VisorIP;

public sealed class Player : IDisposable
{
    readonly object videoLock=new();
    IntPtr instance, player;
    long overlayVersion;
    static readonly object engineLock=new();
    static IntPtr sharedEngine;
    static int engineUsers;
    public Player(IntPtr hwnd)
    {
        lock(engineLock){if(sharedEngine==IntPtr.Zero)sharedEngine=CreateEngine();instance=sharedEngine;engineUsers++;}
        player=Native.libvlc_media_player_new(instance);
        if(player==IntPtr.Zero) { ReleaseEngine();throw new InvalidOperationException("No se pudo crear el reproductor."); }
        Native.libvlc_media_player_set_hwnd(player,hwnd);
        Native.libvlc_video_set_mouse_input(player,0);
        Native.libvlc_video_set_key_input(player,0);
        Native.libvlc_audio_set_mute(player,1);
    }
    static IntPtr CreateEngine()
    {
        IntPtr engine;
        var folder=Path.Combine(AppContext.BaseDirectory,"motor");
        if(!File.Exists(Path.Combine(folder,"libvlc.dll"))) throw new InvalidOperationException("Falta la carpeta motor. Conservá todos los archivos del programa juntos.");
        Native.SetDllDirectory(folder);
        Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH",Path.Combine(folder,"plugins"));
        var args=new[]{"--ignore-config","--no-video-title-show","--no-osd","--quiet","--no-plugins-cache","--no-snapshot-preview"};
        var pointers=args.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        var argv=Marshal.AllocHGlobal(IntPtr.Size*pointers.Length);
        try { Marshal.Copy(pointers,0,argv,pointers.Length); engine=Native.libvlc_new(args.Length,argv); }
        finally { foreach(var p in pointers) Marshal.FreeCoTaskMem(p); Marshal.FreeHGlobal(argv); }
        if(engine==IntPtr.Zero) throw new InvalidOperationException("No se pudo iniciar el motor de VLC.");
        return engine;
    }
    void ReleaseEngine()
    {
        lock(engineLock){if(instance==IntPtr.Zero)return;instance=IntPtr.Zero;if(--engineUsers==0){Native.libvlc_release(sharedEngine);sharedEngine=IntPtr.Zero;}}
    }
    public int State => player==IntPtr.Zero?0:Native.libvlc_media_player_get_state(player);
    public long Time=>player==IntPtr.Zero?-1:Native.libvlc_media_player_get_time(player);
    public bool HasVideo => player!=IntPtr.Zero&&Native.libvlc_media_player_has_vout(player)>0;
    public void Mute(bool mute) { if(player!=IntPtr.Zero) Native.libvlc_audio_set_mute(player,mute?1:0); }
    public void Play(string url)
    {
        lock(videoLock)
        {
        Stop();
        var media=Native.libvlc_media_new_location(instance,url);
        if(media==IntPtr.Zero) throw new InvalidOperationException("No se pudo abrir la dirección de video.");
        try
        {
            Native.libvlc_media_add_option(media,":rtsp-tcp");
            Native.libvlc_media_add_option(media,":network-caching=650");
            Native.libvlc_media_add_option(media,":no-video-title-show");
            Native.libvlc_media_player_set_media(player,media);
            if(Native.libvlc_media_player_play(player)!=0) throw new InvalidOperationException("No se pudo iniciar el video. Revisá la dirección y las credenciales.");
        }
        finally { Native.libvlc_media_release(media); }
        }
    }
    public (Bitmap Image,Size VideoSize)? Snapshot(string path,uint widthLimit=1280)
    {
        lock(videoLock)
        {
            if(player==IntPtr.Zero||!HasVideo)return null;
            if(Native.libvlc_video_get_size(player,0,out var width,out var height)!=0||width==0||height==0||width>8192||height>8192)return null;
            if(Native.libvlc_video_take_snapshot(player,0,path,widthLimit,0)!=0)return null;
            using var image=new Bitmap(path);return (new Bitmap(image),new Size((int)width,(int)height));
        }
    }
    public void SetAiOverlay(string? path,int lifetimeMs=0)
    {
        lock(videoLock)
        {
            if(player==IntPtr.Zero)return;
            long version=++overlayVersion;
            if(path==null){Native.libvlc_video_set_logo_int(player,0,0);return;}
            Native.libvlc_video_set_logo_string(player,1,path);
            Native.libvlc_video_set_logo_int(player,2,0);Native.libvlc_video_set_logo_int(player,3,0);
            Native.libvlc_video_set_logo_int(player,5,-1);Native.libvlc_video_set_logo_int(player,6,255);Native.libvlc_video_set_logo_int(player,7,5);
            Native.libvlc_video_set_logo_int(player,0,1);
            if(lifetimeMs>0)_=ExpireOverlay(version,lifetimeMs);
        }
    }
    async Task ExpireOverlay(long version,int delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        lock(videoLock){if(player!=IntPtr.Zero&&version==overlayVersion)Native.libvlc_video_set_logo_int(player,0,0);}
    }
    public bool AiOverlayEnabled=>player!=IntPtr.Zero&&Native.libvlc_video_get_logo_int(player,0)!=0;
    public void Stop() { lock(videoLock){if(player!=IntPtr.Zero){overlayVersion++;Native.libvlc_video_set_logo_int(player,0,0);Native.libvlc_media_player_stop(player);}} }
    public void Dispose()
    {
        lock(videoLock)
        {
        if(player!=IntPtr.Zero) { Stop(); Native.libvlc_media_player_release(player); player=IntPtr.Zero; }
        ReleaseEngine();
        }
    }
}
internal static class Native
{
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern int libvlc_video_take_snapshot(IntPtr player,uint num,[MarshalAs(UnmanagedType.LPUTF8Str)] string path,uint width,uint height);
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern int libvlc_video_get_size(IntPtr player,uint num,out uint width,out uint height);
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern void libvlc_video_set_logo_string(IntPtr player,uint option,[MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern void libvlc_video_set_logo_int(IntPtr player,uint option,int value);
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern int libvlc_video_get_logo_int(IntPtr player,uint option);
    [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern bool SetDllDirectory(string path);
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr libvlc_new(int argc,IntPtr argv);
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern void libvlc_release(IntPtr instance);
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr libvlc_media_player_new(IntPtr instance);
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern void libvlc_media_player_release(IntPtr player);
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr libvlc_media_new_location(IntPtr instance,[MarshalAs(UnmanagedType.LPUTF8Str)] string url);
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern void libvlc_media_add_option(IntPtr media,[MarshalAs(UnmanagedType.LPUTF8Str)] string option);
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern void libvlc_media_release(IntPtr media);
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern void libvlc_media_player_set_media(IntPtr player,IntPtr media);
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern void libvlc_media_player_set_hwnd(IntPtr player,IntPtr hwnd);
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern int libvlc_media_player_play(IntPtr player);
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern long libvlc_media_player_get_time(IntPtr player);
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern int libvlc_media_player_get_state(IntPtr player);
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern uint libvlc_media_player_has_vout(IntPtr player);
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern void libvlc_media_player_stop(IntPtr player);
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern void libvlc_audio_set_mute(IntPtr player,int mute);
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern void libvlc_video_set_mouse_input(IntPtr player,uint on);
    [DllImport("libvlc",CallingConvention=CallingConvention.Cdecl)] internal static extern void libvlc_video_set_key_input(IntPtr player,uint on);
}
