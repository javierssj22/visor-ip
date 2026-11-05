namespace VisorIP;

public sealed partial class MainForm
{
    sealed class AlertWatch
    {
        public readonly CancellationTokenSource Cancel=new();
        public required Login Login;
        public Task Work=Task.CompletedTask;
    }
    readonly Dictionary<string,AlertWatch> alertWatches=new();
    readonly List<AlertWatch> retiredAlerts=new();
    readonly Dictionary<string,EventSupport> alertSupport=new();
    readonly Dictionary<string,string> alertStatus=new();
    readonly Dictionary<string,(string Kind,long Until)> currentAlerts=new();
    readonly List<string> alertHistory=new();
    long lastSound;
    public bool TestAlertIsolation(string output)
    {
        if(!testing)throw new InvalidOperationException();
        SetView(4);tiles[0].Camera=new(){Ip="172.16.0.22"};tiles[1].Camera=new(){Ip="172.16.0.76"};
        prefs.Alerts["172.16.0.22"]=new(){Enabled=true,People=true,Sound=false};prefs.Alerts["172.16.0.76"]=new(){Enabled=false,People=true,Sound=false};
        ShowDetection("172.16.0.76","Persona");if(alertHistory.Count!=0)return false;
        ShowDetection("172.16.0.22","Vehículo");if(alertHistory.Count!=0)return false;
        ShowDetection("172.16.0.22","Persona");
        if(alertHistory.Count!=1||tiles[0].BackColor!=Color.FromArgb(255,166,50)||tiles[1].BackColor==tiles[0].BackColor)return false;
        using(var image=new Bitmap(Width,Height)){DrawToBitmap(image,new Rectangle(Point.Empty,Size));image.Save(output);}
        prefs.Alerts["172.16.0.22"].Enabled=false;PaintAlerts();ShowDetection("172.16.0.22","Persona");
        return alertHistory.Count==1&&tiles[0].BackColor!=Color.FromArgb(255,166,50);
    }
    void InitializeAlertMenus()
    {
        prefs.Alerts??=new();
        foreach(var tile in tiles)
        {
            var menu=new ContextMenuStrip();
            menu.Items.Add("Mover cámara (PTZ)",null,(_,_)=>{SelectSlot(tile.Index);OpenPtz();});
            menu.Items.Add("IA local · personas",null,(_,_)=>{SelectSlot(tile.Index);OpenAi();});
            menu.Items.Add("Alertas de esta cámara",null,(_,_)=>{SelectSlot(tile.Index);OpenAlerts();});
            tile.ContextMenuStrip=tile.Caption.ContextMenuStrip=tile.Surface.ContextMenuStrip=tile.Empty.ContextMenuStrip=menu;
        }
    }
    void SyncAlertMonitors()
    {
        if(testing||closing)return;
        foreach(var old in retiredAlerts.Where(w=>w.Work.IsCompleted).ToArray()){old.Cancel.Dispose();retiredAlerts.Remove(old);}
        var wanted=tiles.Where(t=>t.Active&&t.Camera!=null&&t.Login!=null&&prefs.Alerts.TryGetValue(t.Camera.Ip,out var option)&&option.Enabled).GroupBy(t=>t.Camera!.Ip).ToDictionary(g=>g.Key,g=>g.First());
        foreach(var pair in alertWatches.ToArray())
            if(!wanted.TryGetValue(pair.Key,out var tile)||tile.Login!=pair.Value.Login){pair.Value.Cancel.Cancel();retiredAlerts.Add(pair.Value);alertWatches.Remove(pair.Key);currentAlerts.Remove(pair.Key);}
        foreach(var pair in wanted)
        {
            if(alertWatches.ContainsKey(pair.Key))continue;
            var watch=new AlertWatch{Login=pair.Value.Login!};alertWatches[pair.Key]=watch;
            var camera=pair.Value.Camera!;watch.Work=Task.Run(()=>WatchCamera(camera,watch));
        }
    }
    async Task WatchCamera(Camera camera,AlertWatch watch)
    {
        var ct=watch.Cancel.Token;var filter=new DetectionFilter();
        void Update(string text)=>Ui(()=>{if(alertWatches.GetValueOrDefault(camera.Ip)==watch)alertStatus[camera.Ip]=text;});
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var client=new Onvif(camera.Ip,watch.Login);Update("Consultando detecciones de la cámara…");
                var support=await client.FindEvents(camera,ct);
                Ui(()=>{if(alertWatches.GetValueOrDefault(camera.Ip)==watch)alertSupport[camera.Ip]=support;});
                if(!support.People&&!support.Vehicles){Update("La cámara no anuncia detección de personas ni vehículos.");return;}
                Update("Escuchando alertas de la cámara");
                await client.WatchEvents(support,d=>{
                    if(!filter.Accept(d,Environment.TickCount64))return;
                    Ui(()=>{if(alertWatches.GetValueOrDefault(camera.Ip)==watch&&!ct.IsCancellationRequested)ShowDetection(camera.Ip,d.Kind);});
                },ct);
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(UnauthorizedAccessException){Update("Acceso ONVIF rechazado. Revisá usuario y contraseña.");}
            catch{Update("Sin conexión a alertas · reintento automático en 15 segundos");}
            try{await Task.Delay(15000,ct);}catch(OperationCanceledException){break;}
        }
    }
    void ShowDetection(string ip,string kind)
    {
        if(!prefs.Alerts.TryGetValue(ip,out var option)||!option.Enabled||(kind=="Persona"?!option.People:!option.Vehicles))return;
        var now=Environment.TickCount64;currentAlerts[ip]=(kind,now+12000);
        var line=$"{DateTime.Now:HH:mm:ss} · {ip} · {kind} detectado/a";
        alertHistory.Insert(0,line);if(alertHistory.Count>200)alertHistory.RemoveAt(200);
        status.Text=line;
        if(option.Sound&&now-lastSound>4000){lastSound=now;System.Media.SystemSounds.Exclamation.Play();}
        PaintAlerts();
    }
    void PaintAlerts()
    {
        long now=Environment.TickCount64;
        foreach(var tile in tiles)
        {
            bool active=tile.Camera!=null&&currentAlerts.TryGetValue(tile.Camera.Ip,out var alert)&&alert.Until>now&&prefs.Alerts.TryGetValue(tile.Camera.Ip,out var option)&&option.Enabled;
            var ai=aiStates[tile.Index];
            bool person=CanAnalyze(tile)&&ai.People>0&&ai.AlertUntil>now;
            tile.BackColor=person||active?Color.Red:tile.Index==activeSlot?Accent:Color.FromArgb(38,53,72);
            if(person){tile.Caption.Text=$"  {tile.Index+1:00} / {tile.CameraName} · {ai.People} PERSONA(S) · IA";continue;}
            if(!active&&tile.Caption.Text.Contains("PERSONA(S) · IA"))tile.Caption.Text=$"  {tile.Index+1:00} / {tile.CameraName} · EN VIVO";
            if(active)tile.Caption.Text=$"  {tile.Index+1:00} / {tile.Camera!.Ip} · {currentAlerts[tile.Camera.Ip].Kind.ToUpperInvariant()} DETECTADO/A";
        }
        PaintAi();
    }
    async Task StopAlertMonitors()
    {
        var all=alertWatches.Values.Concat(retiredAlerts).ToArray();foreach(var watch in all)watch.Cancel.Cancel();
        await Task.WhenAll(all.Select(w=>w.Work));foreach(var watch in all)watch.Cancel.Dispose();alertWatches.Clear();retiredAlerts.Clear();
    }
    void OpenAlerts()
    {
        var tile=tiles[activeSlot];if(tile.Camera==null||tile.Login==null){status.Text="Abrí la cámara en este visor antes de configurar sus alertas.";return;}
        var camera=tile.Camera;var login=tile.Login;var option=prefs.Alerts.GetValueOrDefault(camera.Ip,new());
        using var dialog=new Form{Text=$"Alertas · {camera.Ip}",Size=new Size(570,540),MinimumSize=new Size(550,520),StartPosition=FormStartPosition.CenterParent,BackColor=Card,ForeColor=Color.White,Font=Font};
        var panel=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(18),ColumnCount=1,RowCount=9};
        foreach(int h in new[]{34,34,34,34,34,65,45,32})panel.RowStyles.Add(new(SizeType.Absolute,h));panel.RowStyles.Add(new(SizeType.Percent,100));
        panel.Controls.Add(new Label{Text=$"CÁMARA {camera.Ip}",Dock=DockStyle.Fill,ForeColor=Accent},0,0);
        CheckBox Box(string name,bool value,int row){var b=new CheckBox{Text=name,Checked=value,Dock=DockStyle.Fill};panel.Controls.Add(b,0,row);return b;}
        var enabled=Box("Activar alertas de esta cámara",option.Enabled,1);var people=Box("Personas",option.People,2);var vehicles=Box("Vehículos",option.Vehicles,3);var sound=Box("Aviso sonoro",option.Sound,4);
        var info=new Label{Text="Consultando las detecciones disponibles…",Dock=DockStyle.Fill};panel.Controls.Add(info,0,5);
        var save=Button("Guardar",true);save.Dock=DockStyle.Fill;panel.Controls.Add(save,0,6);
        panel.Controls.Add(new Label{Text="Avisos recientes (durante esta sesión)",Dock=DockStyle.Fill},0,7);
        var history=new ListBox{Dock=DockStyle.Fill,BackColor=Bg,ForeColor=Color.White,HorizontalScrollbar=true};panel.Controls.Add(history,0,8);
        dialog.Controls.Add(panel);using var ct=new CancellationTokenSource();
        void History(){history.Items.Clear();history.Items.AddRange(alertHistory.Where(x=>x.Contains(" · "+camera.Ip+" · ")).Take(50).Cast<object>().ToArray());}
        History();using var update=new System.Windows.Forms.Timer{Interval=1500};update.Tick+=(_,_)=>History();update.Start();
        save.Click+=(_,_)=>{prefs.Alerts[camera.Ip]=new(){Enabled=enabled.Checked,People=people.Checked,Vehicles=vehicles.Checked,Sound=sound.Checked};PersistSession();SyncAlertMonitors();PaintAlerts();dialog.Close();};
        dialog.Shown+=async(_,_)=>{
            try{using var client=new Onvif(camera.Ip,login);var support=await client.FindEvents(camera,ct.Token);if(ct.IsCancellationRequested)return;alertSupport[camera.Ip]=support;
                people.Enabled=support.People;vehicles.Enabled=support.Vehicles;
                if(!support.People)people.Checked=false;if(!support.Vehicles)vehicles.Checked=false;
                people.Text=support.People?"Personas":"Personas · no anunciado por la cámara";vehicles.Text=support.Vehicles?"Vehículos":"Vehículos · no anunciado por la cámara";
                info.Text="Activá también la detección correspondiente en la app de la cámara.\n"+(alertStatus.GetValueOrDefault(camera.Ip)??"Los avisos aparecen sobre el video, también en pantalla completa.");
            }catch(OperationCanceledException){}catch{if(!ct.IsCancellationRequested)info.Text="No se pudo consultar la cámara. Podés guardar las opciones y se reintentará al conectar.";}
        };
        dialog.FormClosing+=(_,_)=>ct.Cancel();dialog.ShowDialog(this);
    }
}
