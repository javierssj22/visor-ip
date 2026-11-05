using System.Drawing.Imaging;

namespace VisorIP;

public sealed partial class MainForm
{
    sealed class AiTileState
    {
        public long Next,Until,AlertUntil;
        public bool Suspended;
        public int People,Generation;
        public string? Overlay;
        public string Status="Esperando video";
    }
    readonly AiTileState[] aiStates=Enumerable.Range(0,16).Select(_=>new AiTileState()).ToArray();
    readonly System.Windows.Forms.Timer aiTimer=new(){Interval=100};
    readonly string aiCache=Path.Combine(Path.GetTempPath(),"VisorIP-AI-"+Guid.NewGuid().ToString("N"));
    Task aiWork=Task.CompletedTask;
    readonly Dictionary<string,PersonDetector> aiModels=new();
    int aiCursor;
    long aiModelRetry;
    bool aiMaintenance;
    readonly SemaphoreSlim aiModelGate=new(1,1);
    AiSettings AiOption(VideoTile tile)=>tile.Camera==null?new(){Enabled=false}:prefs.Ai.GetValueOrDefault(tile.Camera.Ip,new());
    void InitializeAi()
    {
        prefs.Ai??=new();
        if(prefs.AiVersion<2){foreach(var value in prefs.Ai.Values)if(value.IntervalMs==1500)value.IntervalMs=500;prefs.AiVersion=2;}
        aiTimer.Tick+=(_,_)=>TickAi();
        if(!testing)aiTimer.Start();
        InitializeTraining();
    }
    bool CanAnalyze(VideoTile tile)=>!closing&&!aiStates[tile.Index].Suspended&&!tile.Busy&&tile.Active&&tile.Visible&&WindowState!=FormWindowState.Minimized&&
        AiOption(tile).Enabled&&tile.Player!=null&&tile.HadVideo&&!tile.Reconnect.Failed&&Environment.TickCount64-tile.Reconnect.Progress<5000;
    void TickAi()
    {
        if(closing)return;
        PaintAlerts();
        if(aiMaintenance||!aiWork.IsCompleted)return;
        var now=Environment.TickCount64;
        // Remove stale boxes before processing another frame. Only one native/inference job runs at a time.
        foreach(var tile in tiles)
        {
            var state=aiStates[tile.Index];
            if(!tile.Busy&&state.Overlay!=null&&(!CanAnalyze(tile)||state.Until<=now))
            {aiWork=ClearAi(tile,!CanAnalyze(tile));return;}
        }
        if(StartDailyCaptureIfDue())return;
        if(now<aiModelRetry)return;
        for(int n=0;n<tiles.Length;n++)
        {
            int index=(aiCursor+n)%tiles.Length;var tile=tiles[index];
            if(!CanAnalyze(tile)||aiStates[index].Next>now)continue;
            aiCursor=(index+1)%tiles.Length;aiWork=AnalyzeAi(tile);return;
        }
    }
    async Task AnalyzeAi(VideoTile tile)
    {
        var state=aiStates[tile.Index];var generation=state.Generation;var player=tile.Player!;
        var option=AiOption(tile);var ip=tile.Camera!.Ip;long began=Environment.TickCount64;
        string snapshot=Path.Combine(aiCache,"frame.png"),overlay=Path.Combine(aiCache,Guid.NewGuid().ToString("N")+".png");
        bool Current()=>CanAnalyze(tile)&&generation==state.Generation&&tile.Camera?.Ip==ip;
        try
        {
            state.Status="Analizando video";
            string model=option.Model is "balanced" or "personal" or "learned"?option.Model:"precise";
            if(!aiModels.TryGetValue(model,out var detector))
            {
                state.Status="Cargando red neuronal…";
                string path=ModelPath(model);
                detector=await Task.Run(()=>new PersonDetector(path));aiModels[model]=detector;
            }
            if(!Current())return;
            Directory.CreateDirectory(aiCache);
            long capturedAt=Environment.TickCount64;
            var capturedUtc=DateTime.UtcNow;
            var captured=await Task.Run(()=>player.Snapshot(snapshot));
            if(captured==null){await ClearAi(tile);state.Status="Esperando una imagen de la cámara";return;}
            using var frame=captured.Value.Image;
            if(!Current())return;
            long start=Environment.TickCount64;
            var boxes=await Task.Run(()=>detector.DetectRegion(frame,Math.Clamp(option.Confidence,25,90)/100f,option.Region));
            if(!Current())return;
            int lifetime=AiResultTiming.Lifetime(Environment.TickCount64-capturedAt);
            if(lifetime==0){await ClearAi(tile);state.Status="Análisis atrasado: elegí Equilibrado o menos cámaras con IA";return;}
            if(boxes.Count>0)
            {
                await Task.Run(()=>{using var drawing=PersonDetector.DrawOverlay(captured.Value.VideoSize,boxes);drawing.Save(overlay,ImageFormat.Png);player.SetAiOverlay(overlay,lifetime);});
                if(!Current()){await ClearAi(tile);return;}
                DeleteAiFile(state.Overlay);state.Overlay=overlay;
                state.People=boxes.Count;state.Until=Environment.TickCount64+lifetime;
                state.AlertUntil=Environment.TickCount64+15000;
                await QueueDetectionForReview(frame,ip,boxes,capturedUtc);
            }
            else await ClearAi(tile);
            state.Status=$"IA activa · {boxes.Count} persona(s) · análisis {Environment.TickCount64-start} ms";
        }
        catch
        {
            await ClearAi(tile);
            state.Status="No se pudo analizar. Revisar el modelo elegido y la carpeta ia.";
            aiModelRetry=Environment.TickCount64+10000;status.Text=state.Status;
        }
        finally
        {
            DeleteAiFile(snapshot);if(state.Overlay!=overlay)DeleteAiFile(overlay);
            state.Next=Math.Max(Environment.TickCount64,began+Math.Clamp(option.IntervalMs,500,5000));
            if(!closing)PaintAlerts();
        }
    }
    static void DeleteAiFile(string? path){if(path!=null)try{File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}}
    async Task ClearAi(VideoTile tile,bool clearAlert=true)
    {
        var state=aiStates[tile.Index];state.Until=0;if(clearAlert){state.People=0;state.AlertUntil=0;}
        try{if(tile.Player!=null)await Task.Run(()=>tile.Player.SetAiOverlay(null));}
        catch{/* Playback recovery remains independent of detection. */}
        DeleteAiFile(state.Overlay);state.Overlay=null;
    }
    async Task ResetAi(VideoTile tile)
    {
        aiStates[tile.Index].Generation++;
        await aiWork;await ClearAi(tile);aiStates[tile.Index].Next=0;
    }
    void PaintAi()
    {
        long now=Environment.TickCount64;
        foreach(var tile in tiles)
        {
            var state=aiStates[tile.Index];
            if(!CanAnalyze(tile)||state.People==0||state.AlertUntil<=now)continue;
            tile.BackColor=Color.Red;
            tile.Caption.Text=$"  {tile.Index+1:00} / {tile.CameraName} · {state.People} PERSONA(S) · IA";
        }
    }
    async Task StopAi()
    {
        aiTimer.Stop();dailyInvitation?.Close();await StopTraining();await aiWork;
        foreach(var tile in tiles)await ClearAi(tile);
        foreach(var detector in aiModels.Values)detector.Dispose();aiModels.Clear();aiTimer.Dispose();
        // This unique directory belongs only to this process; never remove other temporary folders.
        try{if(Directory.Exists(aiCache))Directory.Delete(aiCache,false);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    void OpenAi()
    {
        var tile=tiles[activeSlot];if(tile.Camera==null){OpenTraining();return;}
        string ip=tile.Camera.Ip;var option=AiOption(tile);RectangleF? region=option.Region;
        using var dialog=new Form{Text=$"Red neuronal local · {ip}",ClientSize=new Size(580,650),FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,StartPosition=FormStartPosition.CenterParent,BackColor=Card,ForeColor=Color.White,Font=Font};
        var panel=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new Padding(18)};
        var enabled=new CheckBox{Text="Detectar personas en esta cámara",Checked=option.Enabled,AutoSize=true};
        var models=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=530};models.Items.AddRange(new object[]{"Precisa · YOLOX-M · mayor consumo de CPU","Equilibrada · YOLOX-S · menor consumo","Personalizada · modelo importado","Aprendida · entrenada y validada con tus correcciones"});models.SelectedIndex=option.Model=="balanced"?1:option.Model=="personal"?2:option.Model=="learned"?3:0;
        var confidence=new NumericUpDown{Minimum=25,Maximum=90,Value=Math.Clamp(option.Confidence,25,90),Width=90};
        var interval=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=250};interval.Items.AddRange(new object[]{"Continuo (según capacidad de la PC)","Cada 3 segundos","Cada 5 segundos"});interval.SelectedIndex=option.IntervalMs>=5000?2:option.IntervalMs>=3000?1:0;
        var zone=Button(region==null?"Zona: imagen completa":"Cambiar zona a vigilar");
        var examples=Button("Corregir / guardar ejemplo");var export=Button("Exportar ejemplos");var import=Button("Cargar modelo entrenado");
        var learning=Button("Aprender con mis capturas");learning.Click+=(_,_)=>OpenTraining();
        var stateLabel=new Label{Width=510,Height=42,Text=aiStates[tile.Index].Status};var save=Button("Guardar",true);
        panel.Controls.AddRange(new Control[]{enabled,models,new Label{Text="Confianza mínima (%)",AutoSize=true},confidence,interval,zone,new Label{Width=530,Height=45,Text="Los recuadros caducan rápido para no marcar posiciones antiguas.\nMás cámaras o mayor precisión aumentan el tiempo entre análisis."},examples,learning,export,import,new Label{Width=530,Height=44,Text="Las correcciones alimentan el entrenamiento automático local.\nEl modelo aprendido se activa sólo si supera la validación."},stateLabel,save});
        dialog.Controls.Add(panel);
        using var update=new System.Windows.Forms.Timer{Interval=1000};update.Tick+=(_,_)=>stateLabel.Text=enabled.Checked?aiStates[tile.Index].Status:"IA desactivada para esta cámara";update.Start();
        zone.Click+=async(_,_)=>{var result=await ReviewAiFrame(tile,true);if(result!=null){region=result.Boxes.Count==0?null:result.Boxes[0];zone.Text=region==null?"Zona: imagen completa":"Cambiar zona a vigilar";}};
        examples.Click+=async(_,_)=>{await ReviewAiFrame(tile,false);};
        export.Click+=(_,_)=>{using var folder=new FolderBrowserDialog{Description="Elegí dónde exportar los ejemplos para entrenar"};if(folder.ShowDialog(dialog)==DialogResult.OK)try{var path=AiExamples.Export(folder.SelectedPath);MessageBox.Show(dialog,"Ejemplos exportados a:\n"+path,"Datos para entrenamiento");}catch(Exception e){MessageBox.Show(dialog,e.Message,"No se pudo exportar");}};
        import.Click+=async(_,_)=>{using var file=new OpenFileDialog{Filter="Modelo neuronal ONNX|*.onnx",Title="Elegí un YOLOX entrenado y exportado sin decodificar"};if(file.ShowDialog(dialog)!=DialogResult.OK)return;import.Enabled=false;await aiModelGate.WaitAsync();aiMaintenance=true;try{await aiWork;using(var check=await Task.Run(()=>new PersonDetector(file.FileName))){using var blank=new Bitmap(640,480);await Task.Run(()=>check.Detect(blank,.5f));}Directory.CreateDirectory(Path.GetDirectoryName(AiExamples.PersonalModel)!);if(Path.GetFullPath(file.FileName)!=Path.GetFullPath(AiExamples.PersonalModel)){string temp=AiExamples.PersonalModel+".tmp";File.Copy(file.FileName,temp,true);File.Move(temp,AiExamples.PersonalModel,true);}if(aiModels.Remove("personal",out var old))old.Dispose();models.SelectedIndex=2;stateLabel.Text="Modelo cargado. Guardá para usarlo en esta cámara.";}catch{MessageBox.Show(dialog,"El archivo no es un YOLOX compatible de personas/COCO. Conservá el modelo anterior.","No se pudo cargar");}finally{aiMaintenance=false;aiModelGate.Release();import.Enabled=true;}};
        dialog.FormClosing+=(_,e)=>{if(!import.Enabled)e.Cancel=true;};
        save.Click+=(_,_)=>{if(models.SelectedIndex==2&&!File.Exists(AiExamples.PersonalModel)){stateLabel.Text="Primero cargá un modelo entrenado.";return;}if(models.SelectedIndex==3&&!File.Exists(AiExamples.LearnedModel)){stateLabel.Text="Todavía no hay un modelo aprendido que haya superado la validación.";return;}prefs.Ai[ip]=new(){Enabled=enabled.Checked,Confidence=(int)confidence.Value,IntervalMs=new[]{500,3000,5000}[interval.SelectedIndex],Model=new[]{"precise","balanced","personal","learned"}[models.SelectedIndex],Region=region};foreach(var t in tiles.Where(t=>t.Camera?.Ip==ip)){aiStates[t.Index].Generation++;aiStates[t.Index].Next=0;aiStates[t.Index].Until=0;}PersistSession();TickAi();dialog.Close();};
        dialog.ShowDialog(this);
    }
    async Task<AiReviewResult?> ReviewAiFrame(VideoTile tile,bool zone)
    {
        var state=aiStates[tile.Index];if(tile.Busy||tile.Player==null){status.Text="Esperá a que termine de conectar la cámara.";return null;}
        state.Suspended=true;state.Generation++;string path=Path.Combine(aiCache,"review.png");
        try
        {
            await aiWork;await ClearAi(tile);Directory.CreateDirectory(aiCache);
            var capture=await Task.Run(()=>tile.Player.Snapshot(path,1920));
            if(capture==null){MessageBox.Show(this,"No hay una imagen disponible para corregir.");return null;}
            var capturedUtc=DateTime.UtcNow;
            using var frame=capture.Value.Image;using var review=new AiReviewForm(frame,zone);
            if(review.ShowDialog(this)!=DialogResult.OK)return null;
            var result=new AiReviewResult(review.Boxes.ToArray());
            if(!zone){AiExamples.Save(frame,result.Boxes,tile.Camera!.Ip,capturedUtc:capturedUtc);status.Text=trainingMessage="Corrección guardada. Se agrega al próximo entrenamiento local.";}
            return result;
        }
        catch{MessageBox.Show(this,"No se pudo obtener o guardar el ejemplo.");return null;}
        finally{DeleteAiFile(path);state.Suspended=false;state.Next=0;}
    }
}
public record AiReviewResult(IReadOnlyList<RectangleF> Boxes);
public static class AiResultTiming
{
    // Never display an old detection for another full multi-camera round.
    public static int Lifetime(long ageMs)=>ageMs>=2000?0:(int)Math.Min(900,2000-Math.Max(0,ageMs));
}
