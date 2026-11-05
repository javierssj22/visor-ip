using System.Text.Json;

namespace VisorIP;

public sealed class DailyCaptureSlot
{
    public DateTime Due {get;set;}
    public DateTime End {get;set;}
    public string? Sample {get;set;}
    public bool Reviewed {get;set;}
}
public sealed class DailyCapturePlan
{
    public DateTime Start {get;set;}
    public List<DailyCaptureSlot> Slots {get;set;}=new();
    public static DateTime CycleStart(DateTime local)=>local.Date.AddHours(6).AddDays(local.Hour<6?-1:0);
    public static DailyCapturePlan Create(DateTime start,Random random)
    {
        var plan=new DailyCapturePlan{Start=start};
        foreach(var (hour,length) in new[]{(0,6),(6,8),(14,10)})
            for(int i=0;i<10;i++)
            {
                double span=length*3600/10.0;
                var begin=start.AddHours(hour).AddSeconds(i*span);
                // One random moment in each tenth of the band; reserve a minute for retry.
                plan.Slots.Add(new(){Due=begin.AddSeconds(random.NextDouble()*(span-60)),End=begin.AddSeconds(span)});
            }
        return plan;
    }
    public DailyCaptureSlot? DueAt(DateTime local)=>Slots.FirstOrDefault(s=>s.Sample==null&&s.Due<=local&&local<s.End);
}
public sealed class DailyLearningState
{
    public string LastPromptDate {get;set;}="";
    public int CameraCursor {get;set;}
    public bool ShouldInvite(DateTime now)=>now.TimeOfDay>=new TimeSpan(23,30,0)&&LastPromptDate!=now.ToString("yyyy-MM-dd");
}

public sealed partial class MainForm
{
    static string DailyRoot=>Path.Combine(AiExamples.DataRoot,"rondas-diarias");
    static string DailyStatePath=>Path.Combine(DailyRoot,"estado.json");
    DailyLearningState dailyState=new();
    DailyCapturePlan? dailyPlan;
    bool dailyReviewOpen;
    Form? dailyInvitation;
    long nextDailyCheck,nextDailyCapture,nextTrainingScan;
    string dailyNotice="30 capturas por ciclo: 10 de mañana, 10 de tarde y 10 de noche.";
    static void SaveJson<T>(string path,T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path+".tmp",JsonSerializer.Serialize(value));File.Move(path+".tmp",path,true);
    }
    static string PlanPath(DailyCapturePlan plan)=>Path.Combine(DailyRoot,plan.Start.ToString("yyyy-MM-dd"),"plan.json");
    void InitializeDailyLearning()
    {
        if(testing)return;
        try{dailyState=JsonSerializer.Deserialize<DailyLearningState>(File.ReadAllText(DailyStatePath))??new();}catch{dailyState=new();}
    }
    DailyCapturePlan LoadDailyPlan(DateTime now)
    {
        var start=DailyCapturePlan.CycleStart(now);
        if(dailyPlan?.Start==start)return dailyPlan;
        string path=Path.Combine(DailyRoot,start.ToString("yyyy-MM-dd"),"plan.json");
        // Do not overwrite a damaged existing plan and silently duplicate its captures.
        dailyPlan=File.Exists(path)?JsonSerializer.Deserialize<DailyCapturePlan>(File.ReadAllText(path))??throw new InvalidDataException():DailyCapturePlan.Create(start,Random.Shared);
        if(dailyPlan.Start!=start||dailyPlan.Slots.Count!=30)throw new InvalidDataException();
        if(!File.Exists(path))SaveJson(path,dailyPlan);
        return dailyPlan;
    }
    bool StartDailyCaptureIfDue()
    {
        if(testing||!trainingPreferences.DailyCaptures||Environment.TickCount64<nextDailyCapture)return false;
        nextDailyCapture=Environment.TickCount64+10000;
        try
        {
            var now=DateTime.Now;var plan=LoadDailyPlan(now);var slot=plan.DueAt(now);
            if(slot==null)return false;
            var cameras=tiles.Where(t=>t.Active&&!t.Busy&&t.Camera!=null&&t.Player!=null&&t.HadVideo&&!t.Reconnect.Failed&&Environment.TickCount64-t.Reconnect.Progress<5000&&!aiStates[t.Index].Suspended)
                .GroupBy(t=>t.Camera!.Ip).Select(g=>g.First()).OrderBy(t=>t.Camera!.Ip).ToArray();
            if(cameras.Length==0){dailyNotice="Sin video disponible para la captura horaria. Reintentando dentro de esta franja.";return false;}
            var tile=cameras[dailyState.CameraCursor%cameras.Length];dailyState.CameraCursor=(dailyState.CameraCursor+1)%cameras.Length;
            aiWork=CaptureDaily(tile,plan,slot);return true;
        }
        catch{dailyNotice="No se pudo leer el plan diario. Revisá el acceso a la carpeta rondas-diarias.";return false;}
    }
    async Task CaptureDaily(VideoTile tile,DailyCapturePlan plan,DailyCaptureSlot slot)
    {
        var player=tile.Player!;string camera=tile.Camera!.Ip;int generation=aiStates[tile.Index].Generation;
        string folder=Path.GetDirectoryName(PlanPath(plan))!;
        int index=plan.Slots.IndexOf(slot);string samplePath=Path.Combine(folder,$"captura-{index:00}.json");
        string snapshot=Path.Combine(aiCache,"daily.png");
        try
        {
            // Dedicated queue: false positive bursts cannot fill the daily review queue.
            int pending=await Task.Run(()=>Directory.Exists(DailyRoot)?Directory.GetFiles(DailyRoot,"captura-*.json",SearchOption.AllDirectories).Length:0);
            if(pending>=420){dailyNotice="Hay 14 tandas diarias pendientes. Revisalas para continuar la recopilación.";return;}
            if(File.Exists(samplePath)){slot.Sample=Path.GetFileName(samplePath);SaveJson(PlanPath(plan),plan);return;}
            Directory.CreateDirectory(aiCache);var utc=DateTime.UtcNow;
            var captured=await Task.Run(()=>player.Snapshot(snapshot,1280));
            if(captured==null)return;
            using var image=captured.Value.Image;
            if(closing||tile.Camera?.Ip!=camera||generation!=aiStates[tile.Index].Generation||!tile.Active||DateTime.Now>=slot.End)return;
            await Task.Run(()=>
            {
                Directory.CreateDirectory(folder);
                string name=$"captura-{index:00}.jpg";image.Save(Path.Combine(folder,name),System.Drawing.Imaging.ImageFormat.Jpeg);
                SaveJson(samplePath,new AiSample(name,image.Width,image.Height,camera,utc,new()));
            });
            slot.Sample=Path.GetFileName(samplePath);SaveJson(PlanPath(plan),plan);SaveJson(DailyStatePath,dailyState);
            dailyNotice=$"Hoy: {plan.Slots.Count(s=>s.Sample!=null)}/30 capturas guardadas. La revisión se ofrece a las 23:30.";
        }
        catch{dailyNotice="No se pudo guardar la captura diaria. Se reintentará mientras siga esta franja.";}
        finally{DeleteAiFile(snapshot);}
    }
    List<(DailyCapturePlan Plan,string Path)> CompletedDailyPlans(DateTime now)
    {
        var result=new List<(DailyCapturePlan,string)>();
        if(!Directory.Exists(DailyRoot))return result;
        foreach(string file in Directory.GetFiles(DailyRoot,"plan.json",SearchOption.AllDirectories))
        {
            try{var p=JsonSerializer.Deserialize<DailyCapturePlan>(File.ReadAllText(file))!;if(p.Start.AddDays(1)<=now&&p.Slots.Any(s=>s.Sample!=null&&!s.Reviewed))result.Add((p,file));}catch{}
        }
        return result.OrderByDescending(p=>p.Item1.Start).ToList();
    }
    void TickDailyInvitation()
    {
        if(testing||closing||dailyReviewOpen||dailyInvitation!=null||!trainingPreferences.DailyCaptures||Environment.TickCount64<nextDailyCheck)return;
        nextDailyCheck=Environment.TickCount64+15000;
        var now=DateTime.Now;
        if(!dailyState.ShouldInvite(now))return;
        // Defer until other settings/review dialogs close, without stealing their input.
        if(Application.OpenForms.Cast<Form>().Any(f=>f!=this&&f.Visible))return;
        try
        {
            var plan=CompletedDailyPlans(now).FirstOrDefault();if(plan.Plan==null)return;
            dailyState.LastPromptDate=now.ToString("yyyy-MM-dd");SaveJson(DailyStatePath,dailyState);
            dailyInvitation=CreateDailyInvitation(plan.Plan.Slots.Count(s=>s.Sample!=null&&!s.Reviewed));
            var invitation=dailyInvitation;
            invitation.FormClosed+=(_,_)=>
            {
                bool accepted=invitation.DialogResult==DialogResult.Yes;dailyInvitation=null;invitation.Dispose();
                if(accepted&&!closing)BeginInvoke(()=>ReviewDailyBatch(plan.Plan));
            };
            if(WindowState==FormWindowState.Minimized)WindowState=FormWindowState.Normal;
            invitation.Show(this);invitation.Activate();
        }
        catch{dailyNotice="No se pudo abrir la invitación. Podés revisar desde Ayudar con las imágenes.";}
    }
    internal static Form CreateDailyInvitation(int count)
    {
        var dialog=new Form{Text="Ayudar a reconocer personas",ClientSize=new Size(650,250),StartPosition=FormStartPosition.CenterScreen,TopMost=true,MaximizeBox=false,MinimizeBox=false,Font=new Font("Segoe UI",15),BackColor=Color.FromArgb(14,23,37),ForeColor=Color.White};
        var label=new Label{Dock=DockStyle.Top,Height=145,Padding=new Padding(22),Text=$"¿Quiere ayudar a reconocer personas?\n\nHay {count} imágenes para revisar, una por una."};
        var row=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=85,Padding=new Padding(18)};
        var yes=new Button{Text="Sí",Width=270,Height=55,BackColor=Color.Turquoise,ForeColor=Color.Black};
        var no=new Button{Text="No",Width=270,Height=55};
        yes.Click+=(_,_)=>{dialog.DialogResult=DialogResult.Yes;dialog.Close();};no.Click+=(_,_)=>{dialog.DialogResult=DialogResult.No;dialog.Close();};
        row.Controls.AddRange(new Control[]{yes,no});dialog.Controls.Add(row);dialog.Controls.Add(label);dialog.CancelButton=no;return dialog;
    }
    void OpenDailyReview()
    {
        try{var selected=CompletedDailyPlans(DateTime.Now).FirstOrDefault();if(selected.Plan==null){MessageBox.Show(this,"Todavía no hay una tanda terminada para revisar. Cada tanda va de las 06:00 hasta las 06:00 del día siguiente.\n\n"+dailyNotice,"Imágenes del día");return;}ReviewDailyBatch(selected.Plan);}
        catch{MessageBox.Show(this,"No se pudieron leer las imágenes del día. Se conservan para volver a intentar.");}
    }
    void ReviewDailyBatch(DailyCapturePlan plan)
    {
        if(dailyReviewOpen)return;dailyReviewOpen=true;
        try
        {
            var remaining=plan.Slots.Where(s=>s.Sample!=null&&!s.Reviewed).ToArray();int number=0;
            foreach(var slot in remaining)
            {
                if(closing)return;number++;
                if(Path.GetFileName(slot.Sample)!=slot.Sample)throw new InvalidDataException();
                string file=Path.Combine(Path.GetDirectoryName(PlanPath(plan))!,slot.Sample!);
                var sample=JsonSerializer.Deserialize<AiSample>(File.ReadAllText(file))!;
                if(Path.GetFileName(sample.Image)!=sample.Image)throw new InvalidDataException();
                string imagePath=Path.Combine(Path.GetDirectoryName(file)!,sample.Image);
                using(var image=new Bitmap(imagePath))using(var review=new DailyReviewForm(image,$"Imagen {number} de {remaining.Length} · Cámara {sample.Camera} · {sample.CapturedUtc.ToLocalTime():dd/MM HH:mm}"))
                {
                    if(review.ShowDialog(this)!=DialogResult.OK)return;
                    // Deterministic destination makes retry after interruption idempotent.
                    string name=$"diaria-{plan.Start:yyyyMMdd}-{plan.Slots.IndexOf(slot):00}";
                    Directory.CreateDirectory(AiExamples.Root);File.Copy(imagePath,Path.Combine(AiExamples.Root,name+".jpg"),true);
                    SaveJson(Path.Combine(AiExamples.Root,name+".json"),new AiSample(name+".jpg",sample.Width,sample.Height,sample.Camera,sample.CapturedUtc,review.Boxes.ToList()));
                }
                slot.Reviewed=true;SaveJson(PlanPath(plan),plan);File.Delete(file);File.Delete(imagePath);
            }
            if(!testing)MessageBox.Show(this,"¡Gracias! Las respuestas quedaron guardadas para mejorar la detección.","Revisión terminada");
        }
        catch{MessageBox.Show(this,"No se pudo completar la revisión. Lo que ya respondiste está guardado; podés continuar más tarde.");}
        finally{dailyReviewOpen=false;trainingDue=Environment.TickCount64+120000;nextTrainingScan=0;}
    }
}

public sealed class DailyReviewForm : Form
{
    readonly AnnotationCanvas canvas;
    public IReadOnlyList<RectangleF> Boxes=>canvas.Boxes;
    public DailyReviewForm(Bitmap image,string caption)
    {
        Text=caption;WindowState=FormWindowState.Maximized;StartPosition=FormStartPosition.CenterParent;Font=new Font("Segoe UI",15);BackColor=Color.FromArgb(14,23,37);ForeColor=Color.White;
        var panel=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=3,ColumnCount=1,Padding=new Padding(15)};
        panel.RowStyles.Add(new(SizeType.Absolute,100));panel.RowStyles.Add(new(SizeType.Percent,100));panel.RowStyles.Add(new(SizeType.Absolute,120));
        panel.Controls.Add(new Label{Dock=DockStyle.Fill,Text=caption+"\n¿Hay personas? Mantené apretado el botón izquierdo y arrastrá para dibujar un recuadro alrededor de CADA persona. Si no hay ninguna, tocá «No hay personas»."},0,0);
        canvas=new AnnotationCanvas(image,false);panel.Controls.Add(canvas,0,1);
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=true};
        var yes=new Button{Text="Guardar marcas y seguir",AutoSize=true,Height=50,Padding=new Padding(10),BackColor=Color.Turquoise,ForeColor=Color.Black};
        yes.Click+=(_,_)=>{if(canvas.Boxes.Count==0){MessageBox.Show(this,"Dibujá un recuadro alrededor de cada persona o tocá «No hay personas».");return;}DialogResult=DialogResult.OK;};
        var no=new Button{Text="No hay personas",AutoSize=true,Padding=new Padding(10)};no.Click+=(_,_)=>{canvas.Clear();DialogResult=DialogResult.OK;};
        var undo=new Button{Text="Deshacer marca",AutoSize=true,Padding=new Padding(10)};undo.Click+=(_,_)=>canvas.Undo();
        var later=new Button{Text="Continuar más tarde",AutoSize=true,Padding=new Padding(10),DialogResult=DialogResult.Cancel};
        buttons.Controls.AddRange(new Control[]{yes,no,undo,later});panel.Controls.Add(buttons,0,2);Controls.Add(panel);CancelButton=later;
    }
}
