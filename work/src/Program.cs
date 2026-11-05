using System.Text.Json;

namespace VisorIP;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if(args.Contains("--daily-test")) { DailyLearningTests.Run(); return; }
        if(args.Contains("--auto-capture-test")) { AutoCaptureTests.Run(); return; }
        if(args.Contains("--training-test")) { TrainingTests.Run(); return; }
        if(args.Contains("--ai-benchmark")) { AiBenchmark.Run(); return; }
        if(args.Contains("--ai-test")) { AiTests.Run(); return; }
        if(args.Contains("--ai-camera-check")) { AiTests.RealCameras(); return; }
        if(args.Contains("--self-test")) { SelfTest.Run().GetAwaiter().GetResult(); return; }
        if(args.Contains("--smoke-test")) { SelfTest.Smoke(); return; }
        if(args.Contains("--scan-test")) { SelfTest.Scan().GetAwaiter().GetResult(); return; }
        if(args.Contains("--camera-check")) { CameraDiagnostics.Run().GetAwaiter().GetResult(); return; }
        if(args.Contains("--events-check")) { CameraDiagnostics.Events().GetAwaiter().GetResult(); return; }
        if(args.Contains("--alerts-ui-test")) { SelfTest.AlertUi(); return; }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException+=(_,e)=>MessageBox.Show("No se pudo completar la operación. Cerrá y volvé a abrir el programa si el problema continúa.\n\n"+SafeError(e.Exception),"Visor IP",MessageBoxButtons.OK,MessageBoxIcon.Warning);
        Application.Run(new MainForm());
    }
    public static string SafeError(Exception e) => e is UnauthorizedAccessException or ArgumentException or InvalidOperationException ? e.Message : "Revisá la conexión, la dirección de video y el acceso a la cámara.";
}

public sealed class Preferences
{
    public string Network { get; set; }="172.16.0";
    public int AiVersion { get; set; }
    public Dictionary<string,AiSettings> Ai { get; set; }=new();
    public Dictionary<string,AlertSettings> Alerts { get; set; }=new();
    public int Layout { get; set; }=4;
    public bool Fullscreen { get; set; }
    public bool Shared { get; set; }=true;
    public string CommonAccess { get; set; }="";
    public Dictionary<string,string> CameraAccess { get; set; }=new();
    public List<SavedTile> Tiles { get; set; }=new();
    public List<Camera> Favorites { get; set; }=new();
    static string FilePath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Visor IP","preferencias.json");
    public static Preferences Load()
    {
        try { var p=JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath))??new(); p.Favorites??=new(); return p; }
        catch { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        foreach(var c in Favorites) c.SavedUrl=Net.WithoutCredentials(c.SavedUrl);
        var temp=FilePath+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true}));
        File.Move(temp,FilePath,true);
    }
}

public sealed class VideoTile : Panel
{
    public Panel Surface { get; }=new(){Dock=DockStyle.Fill,BackColor=Color.FromArgb(8,14,24)};
    public Label Caption { get; }=new(){Dock=DockStyle.Top,Height=31,TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(10,0,0,0),ForeColor=Color.White};
    public Label Empty { get; }=new(){Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,ForeColor=Color.FromArgb(142,163,184),Text="Seleccioná una cámara\ny presioná Ver cámara"};
    public Player? Player;
    public bool Busy;
    public bool Active;
    public bool HadVideo;
    public DateTime Started;
    public string Url="";
    public long LastTime=-1;
    public readonly ReconnectSchedule Reconnect=new();
    public string CameraName="";
    public Camera? Camera;
    public Login? Login;
    public StreamChoice? Stream;
    public int Index;
    public readonly Font RegularFont=new("Segoe UI",10),CompactFont=new("Segoe UI",8);
    public VideoTile(int index)
    {
        Index=index; Padding=new Padding(2); Margin=new Padding(5);
        Caption.Text=$"  {index+1:00}   /   SIN VIDEO"; BackColor=Color.FromArgb(38,53,72);
        Surface.Controls.Add(Empty); Controls.Add(Surface); Controls.Add(Caption);
    }
}

public sealed partial class MainForm : Form
{
    static readonly Color Bg=Color.FromArgb(14,23,37),Card=Color.FromArgb(23,35,52),Accent=Color.FromArgb(58,217,185),Muted=Color.FromArgb(155,175,195);
    readonly TextBox network=new(){Text="172.16.0",Width=130};
    readonly Button scan=Button("Buscar cámaras",true),cancel=Button("Cancelar"),detect=Button("Detectar video"),play=Button("Ver cámara",true);
    readonly ListView cameras=new(){View=View.Details,FullRowSelect=true,HideSelection=false,MultiSelect=false,Dock=DockStyle.Fill,BorderStyle=BorderStyle.None};
    readonly TextBox user=new(),password=new(){UseSystemPasswordChar=true},url=new();
    readonly CheckBox shared=new(){Text="Usar estas credenciales en todas",Checked=true,Dock=DockStyle.Fill,Margin=Padding.Empty,Font=new Font("Segoe UI",9)};
    readonly CheckBox showPassword=new(){Text="Mostrar",Dock=DockStyle.Fill,Margin=Padding.Empty,Font=new Font("Segoe UI",9)};
    readonly ComboBox streams=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    readonly Label status=new(){Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true};
    readonly Label netInfo=new(){Dock=DockStyle.Fill,ForeColor=Muted,AutoEllipsis=true};
    readonly Label count=new(){Text="DISPOSITIVOS  /  0",ForeColor=Muted,Dock=DockStyle.Fill};
    readonly Label selected=new(){Text="VISOR 1",ForeColor=Accent,AutoSize=true,Margin=new Padding(10,10,12,0)};
    readonly ProgressBar progress=new(){Dock=DockStyle.Fill,Maximum=254,Height=5,Style=ProgressBarStyle.Continuous};
    readonly TableLayoutPanel rootLayout,bodyLayout,viewerLayout;
    readonly Control[] chrome;
    readonly Button exitFullscreen=Button("Volver (Esc)");
    bool fullscreen;
    Rectangle windowBounds;
    FormWindowState windowState;
    FormBorderStyle windowBorder;
    Size windowMinimum;
    bool windowTopMost;
    Padding windowPadding,gridMargin;
    float[] rowHeights=Array.Empty<float>();
    float sidebarWidth,toolbarHeight;
    readonly Panel grid=new(){Dock=DockStyle.Fill,BackColor=Bg};
    readonly ComboBox layout=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=130};
    readonly VideoTile[] tiles=Enumerable.Range(0,16).Select(i=>new VideoTile(i)).ToArray();
    readonly Dictionary<string,Camera> devices=new();
    readonly Dictionary<string,Login> logins=new();
    readonly Dictionary<string,List<StreamChoice>> choices=new();
    readonly System.Windows.Forms.Timer timer=new(){Interval=700};
    readonly Preferences prefs;
    CancellationTokenSource? scanCts,resolveCts;
    Task? scanTask,resolveTask;
    Camera? current;
    int activeSlot,viewCount=4,previousView=4;
    bool loading,closing,canClose,audioMuted=true,ready;
    long credentialsChanged;bool credentialsDirty;
    readonly bool testing;
    public MainForm(bool testing=false)
    {
        this.testing=testing;
        prefs=testing?new():Preferences.Load();
        Text="Visor IP · Cámaras de red"; Size=new Size(1280,860); MinimumSize=new Size(1180,780);
        StartPosition=FormStartPosition.CenterScreen; BackColor=Bg; ForeColor=Color.White; Font=new Font("Segoe UI",10);
        AutoScaleMode=AutoScaleMode.Dpi; KeyPreview=true;
        network.Text=prefs.Network;
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=new Padding(20,14,20,12)};
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,53));root.RowStyles.Add(new RowStyle(SizeType.Absolute,82));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,36));
        var header=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2};header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,60));header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,40));
        header.Controls.Add(new Label{Text="VISOR IP",Font=new Font("Segoe UI",24,FontStyle.Bold),ForeColor=Color.White,AutoSize=true},0,0);
        var fullscreenButton=Button("Pantalla completa (F11)",true);fullscreenButton.Anchor=AnchorStyles.Right;fullscreenButton.Click+=(_,_)=>SetFullscreen(true);header.Controls.Add(fullscreenButton,1,0);
        var networkBox=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,BackColor=Card,Padding=new Padding(10,7,10,5),Margin=new Padding(0,0,0,12)};
        networkBox.RowStyles.Add(new RowStyle(SizeType.Absolute,35));networkBox.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var networkRow=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};
        networkRow.Controls.Add(new Label{Text="Red local",AutoSize=true,Margin=new Padding(0,5,12,0)});networkRow.Controls.Add(network);networkRow.Controls.Add(scan);networkRow.Controls.Add(cancel);
        networkRow.Controls.Add(new Label{Text="254 direcciones · ONVIF + RTSP",ForeColor=Muted,AutoSize=true,Margin=new Padding(18,6,0,0)});
        cancel.Enabled=false;networkBox.Controls.Add(networkRow,0,0);networkBox.Controls.Add(netInfo,0,1);
        var body=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,375));body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        var left=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,BackColor=Card,Padding=new Padding(12),Margin=new Padding(0,0,12,0)};
        left.RowStyles.Add(new RowStyle(SizeType.Absolute,29));left.RowStyles.Add(new RowStyle(SizeType.Percent,100));left.RowStyles.Add(new RowStyle(SizeType.Absolute,363));
        cameras.BackColor=Card;cameras.ForeColor=Color.White;cameras.Columns.Add("Dirección",119);cameras.Columns.Add("Detección",212);
        left.Controls.Add(count,0,0);left.Controls.Add(cameras,0,1);
        var connection=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=11,Margin=Padding.Empty};
        foreach(var h in new[]{25,31,31,30,35,31,25,31,41,39,28}) connection.RowStyles.Add(new RowStyle(SizeType.Absolute,h));
        connection.Controls.Add(new Label{Text="ACCESO A LA CÁMARA",ForeColor=Muted,AutoSize=true,Margin=new Padding(0,5,0,0)},0,0);
        var userrow=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Margin=Padding.Empty};userrow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,85));userrow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));userrow.Controls.Add(new Label{Text="Usuario",AutoSize=true},0,0);user.Dock=DockStyle.Fill;userrow.Controls.Add(user,1,0);connection.Controls.Add(userrow,0,1);
        var passrow=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,Margin=Padding.Empty};passrow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,90));passrow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));passrow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,82));passrow.Controls.Add(new Label{Text="Contraseña",Dock=DockStyle.Fill,Font=new Font("Segoe UI",9),Margin=Padding.Empty,TextAlign=ContentAlignment.MiddleLeft},0,0);password.Dock=DockStyle.Fill;passrow.Controls.Add(password,1,0);passrow.Controls.Add(showPassword,2,0);connection.Controls.Add(passrow,0,2);
        connection.Controls.Add(shared,0,3);
        detect.Dock=DockStyle.Fill;connection.Controls.Add(detect,0,4);connection.Controls.Add(streams,0,5);
        connection.Controls.Add(new Label{Text="Dirección de video (también manual)",ForeColor=Muted,AutoSize=true},0,6);url.Dock=DockStyle.Fill;url.PlaceholderText="rtsp://172.16.0.10:554/...";connection.Controls.Add(url,0,7);
        play.Dock=DockStyle.Fill;connection.Controls.Add(play,0,8);
        var favrow=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};var fav=Button("Guardar favorita");var remove=Button("Quitar favorita");favrow.Controls.AddRange(new Control[]{fav,remove});connection.Controls.Add(favrow,0,9);
        var saveAccess=Button("Guardar acceso",true);saveAccess.Dock=DockStyle.Fill;saveAccess.Click+=(_,_)=>{if(PersistSession())status.Text="Acceso guardado cifrado en esta PC.";};connection.Controls.Add(saveAccess,0,10);
        left.Controls.Add(connection,0,2);
        var right=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};right.RowStyles.Add(new RowStyle(SizeType.Absolute,44));right.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var toolbar=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,AutoScroll=true};toolbar.Controls.Add(selected);layout.Items.AddRange(new object[]{"1 cámara","4 cámaras","6 cámaras · 4:3","9 cámaras","16 cámaras"});layout.SelectedIndex=1;var stop=Button("Detener");var ptz=Button("PTZ");var mute=new CheckBox{Text="Silencio",Checked=true,AutoSize=true,Margin=new Padding(9,9,0,0)};toolbar.Controls.AddRange(new Control[]{layout,stop,ptz,mute});
        var alertsButton=Button("Alertas");alertsButton.Click+=(_,_)=>OpenAlerts();toolbar.Controls.Add(alertsButton);var aiButton=Button("IA local");aiLearningButton=aiButton;aiButton.Click+=(_,_)=>OpenAi();toolbar.Controls.Add(aiButton);var dailyHelp=Button("Ayudar con imágenes");dailyHelp.Click+=(_,_)=>OpenDailyReview();toolbar.Controls.Add(dailyHelp);var help=Button("Ayuda");toolbar.Controls.Add(help);
        for(int i=0;i<16;i++)
        {
            var tile=tiles[i]; grid.Controls.Add(tile);
            tile.Click+=(_,_)=>SelectSlot(tile.Index);tile.Caption.Click+=(_,_)=>SelectSlot(tile.Index);tile.Surface.Click+=(_,_)=>SelectSlot(tile.Index);tile.Empty.Click+=(_,_)=>SelectSlot(tile.Index);
            tile.Caption.DoubleClick+=(_,_)=>ToggleView();
        }
        grid.Resize+=(_,_)=>ArrangeTiles();layout.SelectedIndexChanged+=(_,_)=>SetView(new[]{1,4,6,9,16}[layout.SelectedIndex]);ptz.Click+=(_,_)=>OpenPtz();
        right.Controls.Add(toolbar,0,0);right.Controls.Add(grid,0,1);
        body.Controls.Add(left,0,0);body.Controls.Add(right,1,0);
        var footer=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};footer.RowStyles.Add(new RowStyle(SizeType.Absolute,5));footer.RowStyles.Add(new RowStyle(SizeType.Percent,100));footer.Controls.Add(progress,0,0);footer.Controls.Add(status,0,1);
        root.Controls.Add(header,0,0);root.Controls.Add(networkBox,0,1);root.Controls.Add(body,0,2);root.Controls.Add(footer,0,3);Controls.Add(root);
        rootLayout=root;bodyLayout=body;viewerLayout=right;chrome=new Control[]{header,networkBox,left,toolbar,footer};
        exitFullscreen.Visible=false;exitFullscreen.Click+=(_,_)=>SetFullscreen(false);tiles[0].Caption.Controls.Add(exitFullscreen);exitFullscreen.AutoSize=false;exitFullscreen.Size=new Size(104,24);exitFullscreen.Padding=Padding.Empty;
        foreach(var text in new[]{network,user,password,url}) { text.BackColor=Color.FromArgb(32,47,65);text.ForeColor=Color.White;text.BorderStyle=BorderStyle.FixedSingle; }
        streams.BackColor=Color.FromArgb(32,47,65);streams.ForeColor=Color.White;
        scan.Click+=async(_,_)=>await StartScan();cancel.Click+=(_,_)=>{scanCts?.Cancel();resolveCts?.Cancel();};
        cameras.SelectedIndexChanged+=(_,_)=>ChooseCamera();cameras.DoubleClick+=async(_,_)=>await StartPlay();
        detect.Click+=async(_,_)=>await StartResolve();play.Click+=async(_,_)=>await StartPlay();
        streams.SelectedIndexChanged+=(_,_)=>{if(streams.SelectedItem is StreamChoice s) url.Text=s.Url;};
        showPassword.CheckedChanged+=(_,_)=>password.UseSystemPasswordChar=!showPassword.Checked;
        shared.CheckedChanged+=(_,_)=>CredentialChanged();
        user.TextChanged+=(_,_)=>CredentialChanged();password.TextChanged+=(_,_)=>CredentialChanged();
        fav.Click+=(_,_)=>SaveFavorite();remove.Click+=(_,_)=>RemoveFavorite();
        stop.Click+=async(_,_)=>await StopTile(tiles[activeSlot]);mute.CheckedChanged+=(_,_)=>{audioMuted=mute.Checked;foreach(var t in tiles) if(!t.Busy)t.Player?.Mute(audioMuted);};
        help.Click+=(_,_)=>MessageBox.Show("Elegí una vista de 1, 4, 6, 9 o 16 cámaras. Para cargar un video, seleccioná el visor, elegí una cámara de la lista y presioná Ver cámara.\n\nPantalla completa (F11) oculta los menús y ocupa el monitor actual. Esc o Volver recupera los controles. Funciona con 1, 4, 6, 9 y 16 cámaras.\n\nDoble clic en el encabezado amplía un visor; otro doble clic vuelve a la cuadrícula. Cambiar la vista no detiene los videos ocultos: Detener actúa sobre el visor seleccionado.\n\nPara mover un domo, seleccioná su visor y presioná PTZ. Los controles actúan sobre esa cámara y requieren soporte ONVIF PTZ y permisos del usuario. Cada clic da un paso corto; DETENER envía una orden de parada.\n\nEn vistas de 9 o 16, elegí perfiles secundarios de menor resolución cuando estén disponibles.\n\nONVIF puede necesitar un usuario específico. También podés pegar una URL RTSP/HTTP. Guardar acceso conserva el usuario y la contraseña cifrados en esta PC. Las cámaras se reabren al iniciar. Se refrescan cada 5 minutos; si falla la conexión, reintentan automáticamente cada 15 segundos. La vista de 6 tiene áreas de video 4:3, sin recortar la imagen.","Cómo usar Visor IP",MessageBoxButtons.OK,MessageBoxIcon.Information);
        timer.Tick+=(_,_)=>RefreshPlayers();timer.Start();FormClosing+=HandleClosing;
        network.TextChanged+=(_,_)=>NetworkInfo();SelectSlot(0);NetworkInfo();
        foreach(var cam in prefs.Favorites) AddCamera(cam);
        status.Text="Listo para buscar cámaras en la red local.";
        RestoreSession();ready=true;InitializeAlertMenus();InitializeAi();
        Shown+=async(_,_)=>{if(!testing){if(prefs.Fullscreen)SetFullscreen(true);await StartScan();}};
    }
    static Button Button(string text,bool primary=false)=>new(){Text=text,AutoSize=true,Height=30,FlatStyle=FlatStyle.Flat,BackColor=primary?Accent:Color.FromArgb(39,55,75),ForeColor=primary?Bg:Color.White,Cursor=Cursors.Hand,Margin=new Padding(3,0,3,0),Padding=new Padding(5,2,5,2),Font=new Font("Segoe UI",9,primary?FontStyle.Bold:FontStyle.Regular)};
    public void SetFullscreen(bool enabled)
    {
        if(fullscreen==enabled)return;
        var screen=Screen.FromControl(this).Bounds;
        SuspendLayout();rootLayout.SuspendLayout();bodyLayout.SuspendLayout();viewerLayout.SuspendLayout();
        if(enabled)
        {
            windowState=WindowState;windowBounds=WindowState==FormWindowState.Normal?Bounds:RestoreBounds;
            windowBorder=FormBorderStyle;windowMinimum=MinimumSize;windowTopMost=TopMost;
            windowPadding=rootLayout.Padding;gridMargin=grid.Margin;
            rowHeights=rootLayout.RowStyles.Cast<RowStyle>().Select(r=>r.Height).ToArray();
            sidebarWidth=bodyLayout.ColumnStyles[0].Width;toolbarHeight=viewerLayout.RowStyles[0].Height;
        }
        fullscreen=enabled;
        foreach(var control in chrome)control.Visible=!enabled;
        foreach(var row in new[]{0,1,3})rootLayout.RowStyles[row].Height=enabled?0:rowHeights[row];
        bodyLayout.ColumnStyles[0].Width=enabled?0:sidebarWidth;
        viewerLayout.RowStyles[0].Height=enabled?0:toolbarHeight;
        rootLayout.Padding=enabled?Padding.Empty:windowPadding;grid.Margin=enabled?Padding.Empty:gridMargin;
        exitFullscreen.Visible=enabled;
        if(enabled)
        {
            MinimumSize=Size.Empty;WindowState=FormWindowState.Normal;FormBorderStyle=FormBorderStyle.None;Bounds=screen;TopMost=true;
        }
        else
        {
            TopMost=windowTopMost;FormBorderStyle=windowBorder;MinimumSize=windowMinimum;Bounds=windowBounds;WindowState=windowState;
        }
        viewerLayout.ResumeLayout(true);bodyLayout.ResumeLayout(true);rootLayout.ResumeLayout(true);ResumeLayout(true);
        ArrangeTiles();if(ready)PersistSession();
    }
    protected override bool ProcessCmdKey(ref Message msg,Keys keyData)
    {
        if(keyData==Keys.F11){SetFullscreen(!fullscreen);return true;}
        if(keyData==Keys.Escape&&fullscreen){SetFullscreen(false);return true;}
        return base.ProcessCmdKey(ref msg,keyData);
    }
    public bool TestFullscreenArea()=>fullscreen&&chrome.All(c=>!c.Visible)&&FormBorderStyle==FormBorderStyle.None&&Bounds==Screen.FromControl(this).Bounds&&grid.Size==ClientSize;
    void Ui(Action a) { if(!IsDisposed&&!closing) try { BeginInvoke(a); } catch(InvalidOperationException){} }
    void NetworkInfo()
    {
        var local=Net.LocalAddresses().Select(x=>x.ToString()).ToArray();
        netInfo.Text="PC: "+(local.Length==0?"sin conexión local detectada":string.Join(" · ",local));
        try { var p=Net.Prefix(network.Text);if(!local.Any(x=>x.StartsWith(p+".")))netInfo.Text+=$"   |   Buscando en otra subred: {p}.x"; }
        catch(ArgumentException){}
    }
    void AddCamera(Camera camera)
    {
        if(devices.TryGetValue(camera.Ip,out var old))
        {
            old.Ports=old.Ports.Union(camera.Ports).ToList();old.Endpoints=old.Endpoints.Union(camera.Endpoints).ToList();
            if(camera.Kind=="ONVIF" || old.Kind!="ONVIF" && camera.Kind.StartsWith("RTSP")) old.Kind=camera.Kind;
            if(camera.Name!="Cámara")old.Name=camera.Name;
            foreach(ListViewItem row in cameras.Items)if(row.Name==camera.Ip){row.SubItems[1].Text=old.Kind;break;}
        }
        else
        {
            devices.Add(camera.Ip,camera);
            var row=new ListViewItem(camera.Ip){Name=camera.Ip,Tag=camera};row.SubItems.Add(camera.Kind);cameras.Items.Add(row);
        }
        count.Text=$"DISPOSITIVOS  /  {devices.Count}";
    }
    async Task StartScan()
    {
        if(scanCts!=null)return;
        string prefix;
        try{prefix=Net.Prefix(network.Text);}catch(Exception e){ShowError(e);return;}
        scanCts=new();var source=scanCts;scan.Enabled=false;network.Enabled=false;cancel.Enabled=true;progress.Value=0;
        status.Text=$"Buscando en {prefix}.1–254…";NetworkInfo();
        // Preserve saved addresses and the current selection while refreshing other candidates.
        var preserved=prefs.Favorites.Select(c=>c.Ip).ToHashSet();if(current!=null)preserved.Add(current.Ip);
        foreach(var ip in devices.Keys.Where(ip=>!preserved.Contains(ip)).ToArray()){devices.Remove(ip);cameras.Items.RemoveByKey(ip);}
        count.Text=$"DISPOSITIVOS  /  {devices.Count}";
        prefs.Network=prefix;TrySave();
        try
        {
            scanTask=Task.Run(()=>Discovery.Scan(prefix,c=>Ui(()=>AddCamera(c)),n=>Ui(()=>{progress.Value=Math.Clamp(n,0,254);status.Text=$"Buscando… {n}/254 direcciones · {devices.Count} dispositivos";}),source.Token));
            await scanTask;
            status.Text=devices.Count==0?"No se encontraron dispositivos. Revisá la red, el rango y la conexión a las cámaras.":$"Búsqueda terminada · {devices.Count} dispositivos. Elegí una cámara para ver el video.";
        }
        catch(OperationCanceledException){status.Text="Búsqueda cancelada. Podés usar los dispositivos ya encontrados.";}
        catch(Exception e){ShowError(e);}
        finally{scanCts=null;source.Dispose();scan.Enabled=true;network.Enabled=true;cancel.Enabled=resolveCts!=null;}
    }
    void CredentialChanged()
    {
        if(loading||!ready)return;
        if(current!=null&&!shared.Checked)logins[current.Ip]=new(user.Text,password.Text);
        credentialsDirty=true;credentialsChanged=Environment.TickCount64;
    }
    void ChooseCamera()
    {
        if(cameras.SelectedItems.Count==0)return;
        current=(Camera)cameras.SelectedItems[0].Tag!;
        loading=true;
        if(!shared.Checked){var l=logins.GetValueOrDefault(current.Ip,new("",""));user.Text=l.User;password.Text=l.Password;}
        streams.Items.Clear();url.Text=current.SavedUrl;
        if(choices.TryGetValue(current.Ip,out var options)){streams.Items.AddRange(options.ToArray());if(options.Count>0)streams.SelectedIndex=0;}
        loading=false;status.Text=$"{current.Ip} · {current.Kind}. Ingresá las credenciales y presioná Ver cámara.";
    }
    async Task StartResolve()
    {
        if(resolveCts!=null)return;
        if(current==null){status.Text="Seleccioná primero un dispositivo de la lista.";return;}
        var cam=current;var login=new Login(user.Text.Trim(),password.Text);
        resolveCts=new();var source=resolveCts;detect.Enabled=false;play.Enabled=false;cameras.Enabled=false;cancel.Enabled=true;
        status.Text=$"Consultando video de {cam.Ip}…";
        resolveTask=ResolveCore(cam,login,source.Token);
        try{await resolveTask;}
        catch(OperationCanceledException){status.Text="Detección de video cancelada.";}
        catch(Exception e){ShowError(e);}
        finally{source.Dispose();resolveCts=null;detect.Enabled=true;play.Enabled=true;cameras.Enabled=true;cancel.Enabled=scanCts!=null;}
    }
    async Task ResolveCore(Camera cam,Login login,CancellationToken ct)
    {
        using var onvif=new Onvif(cam.Ip,login);
        var result=await onvif.Resolve(cam,ct);
        if(result.Count==0){status.Text=$"Buscando una ruta RTSP en {cam.Ip}…";result=await Rtsp.Resolve(cam,login,ct);}
        if(result.Count==0){status.Text="No se encontró la ruta de video. Verificá ONVIF o ingresá la URL RTSP manual.";return;}
        choices[cam.Ip]=result;streams.Items.Clear();streams.Items.AddRange(result.ToArray());streams.SelectedIndex=0;
        status.Text=$"{result.Count} perfil(es) encontrados. Presioná Ver cámara.";
    }
    async Task StartPlay()
    {
        var tile=tiles[activeSlot];
        if(tile.Busy||resolveCts!=null)return;
        if(string.IsNullOrWhiteSpace(url.Text)){await StartResolve();if(string.IsNullOrWhiteSpace(url.Text))return;}
        if(closing)return;
        var text=url.Text.Trim();
        if(!Uri.TryCreate(text,UriKind.Absolute,out var u)||!(u.Scheme is "rtsp" or "http" or "https")){status.Text="Ingresá una URL válida que empiece con rtsp://, http:// o https://.";return;}
        if(!string.IsNullOrEmpty(u.UserInfo)){status.Text="Escribí las credenciales en Usuario y Contraseña, y quitá usuario:contraseña@ de la URL.";return;}
        var login=new Login(user.Text.Trim(),password.Text);
        tile.Url=text;tile.Active=true;tile.CameraName=u.Host;
        tile.Camera=current?.Ip==u.Host?current:devices.GetValueOrDefault(u.Host,new Camera{Ip=u.Host});tile.Login=login;
        tile.Stream=choices.GetValueOrDefault(u.Host)?.FirstOrDefault(s=>s.Url==text);
        PersistSession();await ConnectTile(tile);
    }
    async Task ConnectTile(VideoTile tile)
    {
        if(closing||tile.Busy||!tile.Active||tile.Login==null||string.IsNullOrEmpty(tile.Url))return;
        tile.Busy=true;tile.Reconnect.Begin(Environment.TickCount64);tile.LastTime=-1;tile.HadVideo=false;tile.Started=DateTime.UtcNow;
        tile.Empty.Visible=true;tile.Empty.Text="Conectando…";tile.Caption.Text=$"  {tile.Index+1:00}   /   {tile.CameraName} · conectando";
        try
        {
            await ResetAi(tile);
            tile.Player??=new Player(tile.Surface.Handle);
            var player=tile.Player;var address=Net.WithCredentials(tile.Url,tile.Login);
            await Task.Run(()=>player.Play(address));
            player.Mute(audioMuted);
        }
        catch {tile.Reconnect.Failed=true;tile.Empty.Text="Sin conexión\nReintento automático en 15 segundos";}
        finally{tile.Busy=false;}
    }
    void SelectSlot(int n)
    {
        activeSlot=n;selected.Text=$"VISOR {n+1}";
        foreach(var t in tiles)t.BackColor=t.Index==n?Accent:Color.FromArgb(38,53,72);
    }
    void ToggleView()
    {
        SetView(viewCount==1?previousView:1);
    }
    public void SetView(int count)
    {
        if(count is not (1 or 4 or 6 or 9 or 16))throw new ArgumentOutOfRangeException(nameof(count));
        if(viewCount!=1)previousView=viewCount;
        viewCount=count;if(count!=1&&activeSlot>=count)SelectSlot(0);
        var index=Array.IndexOf(new[]{1,4,6,9,16},count);if(layout.SelectedIndex!=index)layout.SelectedIndex=index;
        ArrangeTiles();if(ready)PersistSession();
    }
    void ArrangeTiles()
    {
        int columns=viewCount==6?3:(int)Math.Sqrt(viewCount),rows=viewCount==6?2:columns,gap=fullscreen?1:4;
        int areaWidth=grid.ClientSize.Width,areaHeight=grid.ClientSize.Height;
        int cellWidth=areaWidth/columns,cellHeight=areaHeight/rows;
        int captionHeight=viewCount>=9?25:31;
        if(viewCount==6)
        {
            int imageWidth=Math.Max(4,Math.Min(cellWidth-2*gap-4,(cellHeight-2*gap-captionHeight-4)*4/3));
            imageWidth-=imageWidth%4;
            cellWidth=imageWidth+4+2*gap;cellHeight=imageWidth*3/4+captionHeight+4+2*gap;
        }
        int left=(areaWidth-cellWidth*columns)/2,top=(areaHeight-cellHeight*rows)/2;
        grid.SuspendLayout();
        foreach(var tile in tiles)
        {
            bool visible=viewCount==1?tile.Index==activeSlot:tile.Index<viewCount;tile.Visible=visible;if(!visible)continue;
            int index=viewCount==1?0:tile.Index,x=index%columns,y=index/columns;
            tile.Caption.Height=captionHeight;
            tile.Bounds=new Rectangle(left+x*cellWidth+gap,top+y*cellHeight+gap,Math.Max(1,cellWidth-2*gap),Math.Max(1,cellHeight-2*gap));
            tile.Empty.Font=viewCount>=9?tile.CompactFont:tile.RegularFont;tile.Caption.Font=tile.Empty.Font;tile.Caption.AutoEllipsis=true;
        }
        var exitCaption=tiles[viewCount==1?activeSlot:columns-1].Caption;
        if(exitFullscreen.Parent!=exitCaption)exitCaption.Controls.Add(exitFullscreen);
        exitFullscreen.Height=Math.Max(23,exitCaption.Height-2);exitFullscreen.Location=new Point(Math.Max(0,exitCaption.Width-exitFullscreen.Width-3),0);exitFullscreen.BringToFront();
        grid.ResumeLayout();
    }
    void OpenPtz()
    {
        var tile=tiles[activeSlot];
        if(tile.Camera==null||tile.Login==null){status.Text="Abrí primero la cámara en este visor para usar PTZ.";return;}
        var ai=aiStates[tile.Index];ai.Suspended=true;ai.Generation++;ai.Until=0;
        try{using var ptz=new PtzForm(tile.Camera,tile.Login,tile.Stream);ptz.ShowDialog(this);}
        finally{ai.Suspended=false;ai.Next=0;}
    }
    async Task StopTile(VideoTile t)
    {
        if(t.Busy)return;
        t.Active=false;t.Busy=true;PersistSession();
        try{await ResetAi(t);if(t.Player!=null)await Task.Run(t.Player.Stop);}
        finally{t.Busy=false;t.Empty.Text="Seleccioná una cámara\ny presioná Ver cámara";t.Empty.Visible=true;t.Caption.Text=$"  {t.Index+1:00}   /   SIN VIDEO";}
    }
    void RefreshPlayers()
    {
        if(closing)return;
        SyncAlertMonitors();
        var now=Environment.TickCount64;
        if(credentialsDirty&&now-credentialsChanged>=1500)PersistSession();
        foreach(var tile in tiles)
        {
            if(!tile.Active||tile.Busy)continue;
            if(tile.Player!=null)
            {
                var state=tile.Player.State;
                if(state==3&&tile.Player.HasVideo)
                {
                    var time=tile.Player.Time;
                    if(time!=tile.LastTime){tile.LastTime=time;tile.Reconnect.Advanced(now);}
                    tile.HadVideo=true;tile.Empty.Visible=false;tile.Caption.Text=$"  {tile.Index+1:00}   /   {tile.CameraName} · EN VIVO";
                }
                else if(state is 5 or 6 or 7)
                {
                    tile.Reconnect.Failed=true;tile.Caption.Text=$"  {tile.Index+1:00}   /   SIN CONEXIÓN";
                    tile.Empty.Text="Sin conexión\nReintentando automáticamente…";tile.Empty.Visible=true;
                }
            }
            // Start at most one reconnect each tick, so refreshes do not blank the whole grid together.
            if(tile.Reconnect.Due(now,tile.Active,tile.Busy)){_=ConnectTile(tile);break;}
        }
        PaintAlerts();
    }
    bool PersistSession()
    {
        if(testing||!ready)return true;
        try
        {
            var common=new Login(user.Text.Trim(),password.Text);
            if(credentialsDirty)
            {
                foreach(var tile in tiles.Where(t=>t.Login!=null&&(shared.Checked||t.Camera?.Ip==current?.Ip)))
                    if(tile.Login!=common){tile.Login=common;tile.Reconnect.Started=false;}
            }
            prefs.Shared=shared.Checked;prefs.CommonAccess=AccessVault.Seal(common);
            prefs.CameraAccess=logins.ToDictionary(p=>p.Key,p=>AccessVault.Seal(p.Value));
            prefs.Layout=viewCount;prefs.Fullscreen=fullscreen;
            prefs.Tiles=tiles.Where(t=>t.Camera!=null&&t.Login!=null&&!string.IsNullOrEmpty(t.Url)).Select(t=>new SavedTile{Index=t.Index,Camera=t.Camera!,Stream=t.Stream,Url=Net.WithoutCredentials(t.Url),Access=AccessVault.Seal(t.Login!),Enabled=t.Active}).ToList();
            prefs.Save();credentialsDirty=false;return true;
        }
        catch{credentialsDirty=false;status.Text="No se pudo guardar el acceso cifrado. Probá Guardar acceso nuevamente.";return false;}
    }
    void RestoreSession()
    {
        if(testing)return;loading=true;
        try
        {
            var common=AccessVault.Open(prefs.CommonAccess);user.Text=common.User;password.Text=common.Password;shared.Checked=prefs.Shared;
            foreach(var pair in prefs.CameraAccess??new())logins[pair.Key]=AccessVault.Open(pair.Value);
            foreach(var saved in prefs.Tiles??new())
            {
                if(saved.Index<0||saved.Index>=16||!Uri.TryCreate(saved.Url,UriKind.Absolute,out var u)||!(u.Scheme is "rtsp" or "http" or "https")||!string.IsNullOrEmpty(u.UserInfo))continue;
                var tile=tiles[saved.Index];tile.Camera=saved.Camera;tile.CameraName=u.Host;tile.Stream=saved.Stream;tile.Url=saved.Url;tile.Login=AccessVault.Open(saved.Access);tile.Active=saved.Enabled;
                tile.Caption.Text=$"  {tile.Index+1:00}   /   {u.Host} · guardada";AddCamera(saved.Camera);
            }
            SetView(prefs.Layout is 1 or 4 or 6 or 9 or 16?prefs.Layout:4);
        }
        catch{status.Text="No se pudo recuperar el acceso guardado. Ingresá usuario y contraseña nuevamente.";}
        finally{loading=false;}
    }
    void SaveFavorite()
    {
        if(current==null){status.Text="Seleccioná una cámara de la lista para guardarla.";return;}
        if(string.IsNullOrWhiteSpace(url.Text)){status.Text="Detectá el video o ingresá una URL antes de guardar.";return;}
        if(!Uri.TryCreate(url.Text,UriKind.Absolute,out var u)||!(u.Scheme is "rtsp" or "http" or "https")||!string.IsNullOrEmpty(u.UserInfo)){status.Text="Usá una URL de video válida, sin usuario ni contraseña en la dirección.";return;}
        current.SavedUrl=Net.WithoutCredentials(url.Text);
        prefs.Favorites.RemoveAll(c=>c.Ip==current.Ip);
        prefs.Favorites.Add(new Camera{Ip=current.Ip,Name=current.Name,Kind=current.Kind,Ports=current.Ports.ToList(),Endpoints=current.Endpoints.ToList(),SavedUrl=current.SavedUrl});
        if(TrySave())status.Text=$"{current.Ip} guardada en favoritas. Se cargará al abrir el programa.";
    }
    void RemoveFavorite()
    {
        if(current==null)return;
        prefs.Favorites.RemoveAll(c=>c.Ip==current.Ip);current.SavedUrl="";
        if(TrySave())status.Text="Favorita quitada. El dispositivo sigue disponible en esta búsqueda.";
    }
    bool TrySave(){if(testing)return true;try{prefs.Save();return true;}catch{status.Text="No se pudieron guardar las preferencias en esta PC.";return false;}}
    void ShowError(Exception e){status.Text=Program.SafeError(e);if(!closing)MessageBox.Show(this,Program.SafeError(e),"Visor IP",MessageBoxButtons.OK,MessageBoxIcon.Information);}
    async void HandleClosing(object? sender,FormClosingEventArgs e)
    {
        if(canClose)return;e.Cancel=true;if(closing)return;
        PersistSession();closing=true;Enabled=false;timer.Stop();scanCts?.Cancel();resolveCts?.Cancel();
        try
        {
            await StopAi();
            await StopAlertMonitors();
            await Task.WhenAll(new[]{scanTask,resolveTask}.Where(t=>t!=null).Cast<Task>()).ContinueWith(_=>{});
            while(tiles.Any(t=>t.Busy))await Task.Delay(50);
            await Task.WhenAll(tiles.Select(t=>Task.Run(()=>t.Player?.Dispose())));
        }
        finally{password.Clear();logins.Clear();canClose=true;Close();}
    }
    public void PopulatePreview()
    {
        AddCamera(new Camera{Ip="172.16.0.20",Kind="ONVIF"});AddCamera(new Camera{Ip="172.16.0.21",Kind="RTSP · cámara posible"});
        status.Text="Vista de prueba · datos simulados";
    }
    public Panel[] TestSurfaces()=>tiles.Select(t=>t.Surface).ToArray();
    public async Task TestRecovery(string file)
    {
        if(!testing)throw new InvalidOperationException();
        timer.Stop();SetView(6);var tile=tiles[0];tile.Url=new Uri(file).AbsoluteUri;tile.Login=new("","");tile.Camera=new Camera{Ip="local-test"};tile.Active=true;
        await ConnectTile(tile);
        async Task WaitVideo(){var deadline=Environment.TickCount64+10000;while(!tile.Player!.HasVideo||tile.Player.State!=3){if(Environment.TickCount64>deadline)throw new Exception("No volvió el video de prueba.");await Task.Delay(50);}}
        await WaitVideo();
        // A stopped decoder simulates a lost stream without changing the user's Wi-Fi.
        await Task.Run(tile.Player!.Stop);tile.Reconnect.Attempt=Environment.TickCount64-16000;
        RefreshPlayers();while(tile.Busy)await Task.Delay(25);await WaitVideo();
        var first=tile.Reconnect.Attempt;tile.Reconnect.Attempt=Environment.TickCount64-300001;
        RefreshPlayers();while(tile.Busy)await Task.Delay(25);await WaitVideo();
        if(tile.Reconnect.Attempt<=first)throw new Exception("No se refrescó al vencer los 5 minutos.");
        await StopTile(tile);tile.Reconnect.Attempt=Environment.TickCount64-400000;RefreshPlayers();await Task.Delay(100);
        if(tile.Active||tile.Busy||tile.Player.State is not (0 or 5))throw new Exception("Detener no respetó la pausa manual.");
    }
    public bool TestSixRatio()=>tiles.Take(6).All(t=>Math.Abs(t.Surface.Width*3-t.Surface.Height*4)<=4);
    public bool TestExitButton()=>exitFullscreen.Visible&&exitFullscreen.Parent!.Visible&&exitFullscreen.Parent.ClientRectangle.Contains(exitFullscreen.Bounds);
    public bool TestLayout(int count)
    {
        SetView(count);return tiles.Count(t=>t.Visible)==count&&tiles.Where(t=>t.Visible).All(t=>t.Width>0&&t.Height>0&&grid.ClientRectangle.Contains(t.Bounds));
    }
}
