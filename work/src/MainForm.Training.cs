using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VisorIP;

public sealed class TrainingPreferences
{
    public bool Automatic {get;set;}=true;
    public bool CaptureDetections {get;set;}=true;
    public bool DailyCaptures {get;set;}=true;
    public int DailyVersion {get;set;}
    public string Attempt {get;set;}="";
    public string Message {get;set;}="Guardá y revisá capturas para que la red aprenda.";
}

public sealed partial class MainForm
{
    static string TrainingRoot=>Path.Combine(Path.GetDirectoryName(AiExamples.Root)!,"aprendizaje");
    static string TrainingPreferencesPath=>Path.Combine(TrainingRoot,"opciones.json");
    readonly System.Windows.Forms.Timer trainingTimer=new(){Interval=2000};
    TrainingPreferences trainingPreferences=new();
    Task trainingWork=Task.CompletedTask;
    Process? trainingProcess;
    string? trainingJob;
    string trainingMessage="";
    string observedTrainingSignature="";
    long trainingDue;
    bool trainingForce;
    bool trainingStopped;

    void InitializeTraining()
    {
        if(testing)return;
        try{trainingPreferences=JsonSerializer.Deserialize<TrainingPreferences>(File.ReadAllText(TrainingPreferencesPath))??new();}catch{trainingPreferences=new();}
        if(trainingPreferences.DailyVersion<1){trainingPreferences.DailyCaptures=true;trainingPreferences.CaptureDetections=false;trainingPreferences.DailyVersion=1;SaveTrainingPreferences();}
        InitializeDailyLearning();
        trainingMessage=trainingPreferences.Message;
        UpdatePendingBadge();
        trainingTimer.Tick+=(_,_)=>TickTraining();trainingTimer.Start();
    }
    void SaveTrainingPreferences()
    {
        Directory.CreateDirectory(TrainingRoot);
        File.WriteAllText(TrainingPreferencesPath+".tmp",JsonSerializer.Serialize(trainingPreferences));
        File.Move(TrainingPreferencesPath+".tmp",TrainingPreferencesPath,true);
    }
    static string[] TrainingFiles()=>Directory.Exists(AiExamples.Root)?Directory.GetFiles(AiExamples.Root,"*.json").OrderBy(p=>p).ToArray():Array.Empty<string>();
    static string TrainingSignature(string[] files)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",files.Select(p=>{var f=new FileInfo(p);return f.Name+":"+f.Length+":"+f.LastWriteTimeUtc.Ticks;})))));
    void TickTraining()
    {
        if(closing)return;
        TickDailyInvitation();
        UpdatePendingBadge();
        if(!trainingWork.IsCompleted)
        {
            try{using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(trainingJob!,"status.json")));var r=doc.RootElement;trainingMessage=r.GetProperty("message").GetString()!;int total=r.GetProperty("total").GetInt32();if(total>0)trainingMessage+=$" ({r.GetProperty("step").GetInt32()}/{total})";}catch{}
            return;
        }
        if(!trainingPreferences.Automatic&&!trainingForce)return;
        if(dailyReviewOpen)return;
        if(!trainingForce&&Environment.TickCount64<nextTrainingScan)return;
        nextTrainingScan=Environment.TickCount64+30000;
        try
        {
            var files=TrainingFiles();if(files.Length==0)return;
            string signature=TrainingSignature(files);
            if(signature!=observedTrainingSignature){observedTrainingSignature=signature;trainingDue=Environment.TickCount64+30000;}
            if(!trainingForce&&(signature==trainingPreferences.Attempt||Environment.TickCount64<trainingDue))return;
            trainingForce=false;trainingWork=TrainLocal(files,signature);
        }
        catch{trainingMessage="No se pudieron leer las capturas. Revisá el espacio y los permisos de la carpeta de datos.";}
    }
    string ModelPath(string model)=>model=="learned"?AiExamples.LearnedModel:model=="personal"?AiExamples.PersonalModel:Path.Combine(AppContext.BaseDirectory,"ia",model=="balanced"?"yolox_s.onnx":"yolox_m.onnx");
    async Task TrainLocal(string[] files,string signature)
    {
        FileStream? guard=null;bool completed=false;
        try
        {
            Directory.CreateDirectory(TrainingRoot);
            try{guard=new FileStream(Path.Combine(TrainingRoot,"worker.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException){trainingMessage="Otra ventana de Visor IP está entrenando.";trainingDue=Environment.TickCount64+30000;return;}
            string runtime=Path.Combine(AppContext.BaseDirectory,"aprendizaje","runtime","python.exe");
            string script=Path.Combine(AppContext.BaseDirectory,"aprendizaje","train.py");
            if(!File.Exists(runtime)||!File.Exists(script))throw new InvalidOperationException("Falta la carpeta aprendizaje junto al programa.");
            trainingJob=Path.Combine(TrainingRoot,"sesiones",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(trainingJob);
            var baselines=prefs.Ai.ToDictionary(k=>k.Key,k=>ModelPath(k.Value.Model));baselines["default"]=ModelPath("precise");
            var thresholds=prefs.Ai.ToDictionary(k=>k.Key,k=>Math.Clamp(k.Value.Confidence,25,90)/100f);
            string request=Path.Combine(trainingJob,"request.json");
            File.WriteAllText(request,JsonSerializer.Serialize(new{Samples=AiExamples.Root,Files=files.Select(Path.GetFileName),Baselines=baselines,Thresholds=thresholds}));
            var info=new ProcessStartInfo(runtime){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(script)!};
            info.ArgumentList.Add(script);info.ArgumentList.Add("--request");info.ArgumentList.Add(request);
            info.Environment["PYTHONUTF8"]="1";
            trainingProcess=Process.Start(info)??throw new InvalidOperationException("No se pudo iniciar el entrenamiento.");
            try{trainingProcess.PriorityClass=ProcessPriorityClass.Idle;}catch{}
            trainingMessage="Preparando aprendizaje local…";
            await trainingProcess.WaitForExitAsync();
            if(closing)return;
            using var doc=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(trainingJob,"report.json")));
            var report=doc.RootElement;trainingMessage=report.GetProperty("message").GetString()??"Entrenamiento terminado";
            completed=!report.TryGetProperty("error",out var error)||error.GetString()!="InterruptedError";
            if(report.GetProperty("approved").GetBoolean()&&trainingProcess.ExitCode==0&&trainingPreferences.Automatic)
            {
                string[] cameras=report.GetProperty("cameras").EnumerateArray().Select(v=>v.GetString()!).ToArray();
                bool unchanged=cameras.All(ip=>ModelPath(prefs.Ai.GetValueOrDefault(ip,new()).Model)==baselines.GetValueOrDefault(ip,baselines["default"])&&Math.Clamp(prefs.Ai.GetValueOrDefault(ip,new()).Confidence,25,90)/100f==thresholds.GetValueOrDefault(ip,.45f));
                unchanged&=prefs.Ai.Where(p=>p.Value.Model=="learned").All(p=>cameras.Contains(p.Key));
                if(unchanged)
                {
                    bool installed=await InstallLearnedModel(Path.Combine(trainingJob,"candidate.onnx"),cameras);
                    trainingMessage=installed?"Nuevo modelo aprendido y activado en las cámaras corregidas. Podés volver a Precisa en IA local.":"Aprendizaje pausado antes de activar el candidato. Se conserva el detector actual.";
                }
                else trainingMessage+=" No se activó: cambió la configuración o faltan ejemplos de alguna cámara que usa Aprendida. Guardá correcciones y entrená de nuevo.";
            }
        }
        catch(Exception e){trainingMessage=e is InvalidOperationException?e.Message:"El entrenamiento no terminó. El detector anterior sigue activo; consultá el informe local o reintentá.";completed=true;}
        finally
        {
            trainingProcess?.Dispose();trainingProcess=null;guard?.Dispose();
            if(completed&&!closing){trainingPreferences.Attempt=signature;trainingPreferences.Message=trainingMessage;try{SaveTrainingPreferences();}catch{}}
            if(completed)PruneTrainingCandidates();
        }
    }
    static void PruneTrainingCandidates()
    {
        // Keep reports and corrected photos; bound only generated candidate models.
        try
        {
            string sessions=Path.Combine(TrainingRoot,"sesiones");
            foreach(string folder in Directory.GetDirectories(sessions).OrderByDescending(p=>p).Skip(3))
            {
                if(!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(folder),@"^\d{8}-\d{6}-[0-9a-f]{8}$"))continue;
                string model=Path.Combine(folder,"candidate.onnx");if(File.Exists(model))File.Delete(model);
            }
        }
        catch{/* Cleanup never changes the active model or interrupts monitoring. */}
    }
    async Task<bool> InstallLearnedModel(string candidate,string[] cameras)
    {
        await aiModelGate.WaitAsync();
        aiMaintenance=true;
        try
        {
            await aiWork;
            if(closing||!trainingPreferences.Automatic)return false;
            using(var detector=await Task.Run(()=>new PersonDetector(candidate))){using var blank=new Bitmap(640,480);await Task.Run(()=>detector.Detect(blank,.45f));}
            if(closing||!trainingPreferences.Automatic)return false;
            Directory.CreateDirectory(Path.GetDirectoryName(AiExamples.LearnedModel)!);
            if(File.Exists(AiExamples.LearnedModel))File.Copy(AiExamples.LearnedModel,AiExamples.LearnedModel+".anterior",true);
            File.Copy(candidate,AiExamples.LearnedModel+".tmp",true);File.Move(AiExamples.LearnedModel+".tmp",AiExamples.LearnedModel,true);
            if(aiModels.Remove("learned",out var old))old.Dispose();
            foreach(string ip in cameras){if(!prefs.Ai.TryGetValue(ip,out var option))prefs.Ai[ip]=option=new();option.Model="learned";}
            foreach(var tile in tiles.Where(t=>t.Camera!=null&&cameras.Contains(t.Camera.Ip))){aiStates[tile.Index].Generation++;aiStates[tile.Index].Next=0;await ClearAi(tile);}
            PersistSession();
            return true;
        }
        finally{aiMaintenance=false;aiModelGate.Release();}
    }
    async Task StopTraining()
    {
        if(trainingStopped)return;trainingStopped=true;
        trainingTimer.Stop();trainingTimer.Dispose();
        if(trainingProcess!=null)
        {
            try{File.WriteAllText(Path.Combine(trainingJob!,"cancel"),"");}catch{}
            if(await Task.WhenAny(trainingWork,Task.Delay(3000))!=trainingWork)try{trainingProcess?.Kill(true);}catch{}
            await trainingWork;
        }
    }
    void OpenTraining()
    {
        using var dialog=new Form{Text="Aprender con mis capturas",ClientSize=new Size(680,660),MinimumSize=new Size(690,650),StartPosition=FormStartPosition.CenterParent,BackColor=Card,ForeColor=Color.White,Font=Font};
        var panel=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(18),AutoScroll=true};
        var automatic=new CheckBox{Text="Entrenar automáticamente y usar el modelo si mejora",Checked=trainingPreferences.Automatic,AutoSize=true};
        var autoCapture=new CheckBox{Text="Guardar automáticamente las posibles personas detectadas",Checked=trainingPreferences.CaptureDetections,AutoSize=true};
        var daily=new CheckBox{Text="30 capturas aleatorias por día e invitación a las 23:30",Checked=trainingPreferences.DailyCaptures,AutoSize=true};
        daily.CheckedChanged+=(_,_)=>{trainingPreferences.DailyCaptures=daily.Checked;SaveTrainingPreferences();};
        var dailyReview=Button("Ayudar con las imágenes del día",true);dailyReview.Click+=(_,_)=>OpenDailyReview();
        var instructions=new Label{Width=630,Height=115,Text="Modo guardia: 10 imágenes entre 06–12, 10 entre 12–20 y 10 entre 20–06, en horarios aleatorios. A las 23:30 se ofrece la última tanda terminada.\nSólo aprende de las imágenes revisadas. Las opciones de abajo permiten además guardar avisos de la IA y hacer capturas manuales."};
        var capture=Button("Capturar ahora (opcional)");var review=Button("Revisar capturas pendientes",true);
        var counts=new Label{Width=630,Height=28};var progress=new Label{Width=630,Height=105};var captureState=new Label{Width=630,Height=70};
        var train=Button("Entrenar ahora / reintentar");var folder=Button("Abrir capturas e informes");var close=Button("Cerrar",true);
        panel.Controls.AddRange(new Control[]{daily,dailyReview,instructions,autoCapture,automatic,review,capture,counts,captureState,progress,train,folder,close});dialog.Controls.Add(panel);
        bool capturing=false;
        void RefreshStatus(){int pending=PendingCount();counts.Text=$"{TrainingFiles().Length} revisadas · {pending} pendientes";captureState.Text=autoCapture.Checked?(pending>=MaxPendingCaptures?"Bandeja llena: revisá o descartá pendientes para seguir guardando.":captureNotice.Length>0?captureNotice:"Capturas automáticas activas. Evita fotos seguidas: mínimo 1 minuto por cámara; cada 5 minutos si la detección continúa."):"Capturas automáticas desactivadas.";progress.Text=(trainingPreferences.Automatic?"":"Entrenamiento automático pausado. ")+trainingMessage;train.Enabled=trainingWork.IsCompleted&&trainingPreferences.Automatic;capture.Enabled=!capturing&&tiles[activeSlot].Camera!=null;review.Enabled=pending>0;UpdatePendingBadge();}
        autoCapture.CheckedChanged+=(_,_)=>{trainingPreferences.CaptureDetections=autoCapture.Checked;SaveTrainingPreferences();RefreshStatus();};
        automatic.CheckedChanged+=(_,_)=>{trainingPreferences.Automatic=automatic.Checked;SaveTrainingPreferences();if(!automatic.Checked&&trainingProcess!=null)try{File.WriteAllText(Path.Combine(trainingJob!,"cancel"),"");}catch{}RefreshStatus();};
        capture.Click+=async(_,_)=>{capturing=true;capture.Enabled=false;try{await CaptureForLearning(tiles[activeSlot]);}finally{capturing=false;RefreshStatus();}};
        dialog.FormClosing+=(_,e)=>{if(capturing)e.Cancel=true;};
        review.Click+=(_,_)=>{ReviewPending(dialog);RefreshStatus();};
        train.Click+=(_,_)=>{trainingForce=true;TickTraining();RefreshStatus();};
        folder.Click+=(_,_)=>{Directory.CreateDirectory(TrainingRoot);Process.Start(new ProcessStartInfo("explorer.exe"){ArgumentList={Path.GetDirectoryName(AiExamples.Root)!},UseShellExecute=true});};
        close.Click+=(_,_)=>dialog.Close();
        using var timer=new System.Windows.Forms.Timer{Interval=1000};timer.Tick+=(_,_)=>RefreshStatus();timer.Start();RefreshStatus();dialog.ShowDialog(this);
    }
    async Task CaptureForLearning(VideoTile tile)
    {
        if(tile.Camera==null||tile.Player==null||tile.Busy){status.Text="Conectá la cámara antes de capturar.";return;}
        var state=aiStates[tile.Index];state.Suspended=true;state.Generation++;string path=Path.Combine(aiCache,"pending.png");
        try{await aiWork;await ClearAi(tile);Directory.CreateDirectory(aiCache);var result=await Task.Run(()=>tile.Player.Snapshot(path,1920));if(result==null)throw new InvalidOperationException();using var image=result.Value.Image;AiExamples.SavePending(image,tile.Camera.Ip);trainingMessage="Captura guardada. Revisá las pendientes para enseñar a la red.";}
        catch{MessageBox.Show(this,"No se pudo capturar la imagen. Esperá a que haya video.");}
        finally{DeleteAiFile(path);state.Suspended=false;state.Next=0;}
    }
    void ReviewPending(IWin32Window owner)
    {
        if(!Directory.Exists(AiExamples.PendingRoot))return;
        foreach(string file in Directory.GetFiles(AiExamples.PendingRoot,"*.json").OrderBy(p=>p))
        {
            try
            {
                var sample=JsonSerializer.Deserialize<AiSample>(File.ReadAllText(file))!;
                if(Path.GetFileName(sample.Image)!=sample.Image)throw new InvalidDataException();
                string path=Path.Combine(AiExamples.PendingRoot,sample.Image);
                using(var image=new Bitmap(path))using(var review=new AiReviewForm(image,false,sample.SuggestedBoxes,true))
                {
                    review.Text=$"Revisar captura · {sample.Camera} · {sample.CapturedUtc.ToLocalTime():g}";
                    var decision=review.ShowDialog(owner);
                    if(decision==DialogResult.Cancel)return;
                    if(decision==DialogResult.OK){AiExamples.Save(image,review.Boxes,sample.Camera,capturedUtc:sample.CapturedUtc);trainingMessage="Corrección guardada. Se agregará al próximo entrenamiento.";}
                    else if(decision!=DialogResult.Abort)return;
                }
                File.Delete(file);File.Delete(path);captureNotice="";UpdatePendingBadge(true);
            }
            catch{MessageBox.Show(owner,"No se pudo revisar esta captura. Se conserva para volver a intentarlo.");return;}
        }
    }
}
