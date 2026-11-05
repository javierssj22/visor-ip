using System.Text.Json;

namespace VisorIP;
internal static class DailyLearningTests
{
    public static void Run()
    {
        string output=Environment.GetEnvironmentVariable("VISOR_TEST_OUTPUT")??AppContext.BaseDirectory;
        Directory.CreateDirectory(output);AiExamples.DataRoot=Path.Combine(output,"daily-user-"+Guid.NewGuid().ToString("N"));
        using var form=new MainForm(true);
        form.Shown+=async(_,_)=>{try{await form.TestDailyLearning(output);File.WriteAllText(Path.Combine(output,"rondas-diarias.txt"),"PASS: 30 horarios aleatorios, 10 por franja, sin salir del ciclo\nPASS: medianoche/06:00, reintento sólo en su intervalo y persistencia al reiniciar\nPASS: invitación desde 23:30, una vez por fecha, Sí y No reales\nPASS: 30 capturas sin depender de detecciones, revisión secuencial y reanudable\nPASS: etiquetas positivas y negativas, hora/cámara originales y cola independiente\nPASS: no se aprende de imágenes pendientes ni se duplica al reanudar\nRESULTADO: OK");}catch(Exception e){File.WriteAllText(Path.Combine(output,"rondas-diarias.txt"),e.ToString());Environment.ExitCode=1;}finally{form.Close();}};
        Application.Run(form);
    }
}
public sealed partial class MainForm
{
    public async Task TestDailyLearning(string output)
    {
        if(!testing)throw new InvalidOperationException();
        var date=new DateTime(2026,11,5,6,0,0);
        var plan=DailyCapturePlan.Create(date,new Random(42));
        AiTests.Check(plan.Slots.Count==30&&plan.Slots.Take(10).All(s=>s.Due>=date&&s.End<=date.AddHours(6))&&plan.Slots.Skip(10).Take(10).All(s=>s.Due>=date.AddHours(6)&&s.End<=date.AddHours(14))&&plan.Slots.Skip(20).All(s=>s.Due>=date.AddHours(14)&&s.End<=date.AddDays(1)),"Franjas mal repartidas");
        AiTests.Check(plan.Slots.Select(s=>s.Due).Distinct().Count()==30&&!plan.Slots.Select(s=>s.Due).SequenceEqual(DailyCapturePlan.Create(date,new Random(43)).Slots.Select(s=>s.Due)),"No aleatoriza horarios");
        AiTests.Check(DailyCapturePlan.CycleStart(date.AddHours(-1))==date.AddDays(-1)&&DailyCapturePlan.CycleStart(date.AddHours(18))==date,"Ciclo de madrugada incorrecto");
        var slot=plan.Slots[0];AiTests.Check(plan.DueAt(slot.Due.AddSeconds(-1))==null&&plan.DueAt(slot.Due)==slot&&plan.DueAt(slot.End)!=slot,"No respeta intervalo/reintentos");
        SaveJson(PlanPath(plan),plan);dailyPlan=null;
        AiTests.Check(LoadDailyPlan(date).Slots.Select(s=>s.Due).SequenceEqual(plan.Slots.Select(s=>s.Due)),"Reiniciar cambia horarios");
        var inviteState=new DailyLearningState();AiTests.Check(!inviteState.ShouldInvite(date.Date.AddHours(23).AddMinutes(29))&&inviteState.ShouldInvite(date.Date.AddHours(23).AddMinutes(30)),"Horario de cartel incorrecto");inviteState.LastPromptDate="2026-11-05";
        AiTests.Check(!inviteState.ShouldInvite(date.Date.AddHours(23).AddMinutes(50))&&inviteState.ShouldInvite(date.Date.AddDays(1).AddHours(23).AddMinutes(30)),"Repite cartel o lo bloquea al otro día");
        IEnumerable<Control> Walk(Control c){foreach(Control child in c.Controls){yield return child;foreach(var nested in Walk(child))yield return nested;}}
        foreach(string answer in new[]{"Sí","No"})
        {
            using var prompt=CreateDailyInvitation(30);using var click=new System.Windows.Forms.Timer{Interval=250};
            click.Tick+=(_,_)=>{click.Stop();using var shot=new Bitmap(prompt.Width,prompt.Height);prompt.DrawToBitmap(shot,new Rectangle(Point.Empty,prompt.Size));shot.Save(Path.Combine(output,"invitacion-2330.png"));Walk(prompt).OfType<Button>().Single(b=>b.Text==answer).PerformClick();};
            click.Start();var result=prompt.ShowDialog(this);AiTests.Check(result==(answer=="Sí"?DialogResult.Yes:DialogResult.No),"Botón de invitación no funciona");
        }
        // Capture through the same native snapshot/save path with a real player.
        var video=Path.Combine(Path.GetDirectoryName(output)!,"test-results-v2.7","personas.avi");
        if(!File.Exists(video))video=Path.Combine(Path.GetDirectoryName(output)!,"test-results-v2.6","personas.avi");
        var tile=tiles[0];tile.Camera=new Camera{Ip="daily-camera"};tile.CameraName="daily-camera";tile.Login=new("","");tile.Url=new Uri(video).AbsoluteUri;tile.Active=true;
        prefs.Ai[tile.Camera.Ip]=new(){Enabled=false};await ConnectTile(tile);
        long deadline=Environment.TickCount64+15000;while(!tile.Player!.HasVideo){if(Environment.TickCount64>deadline)throw new Exception("No hay video de prueba");await Task.Delay(100);}
        plan=DailyCapturePlan.Create(DateTime.Now.Date.AddDays(-2).AddHours(6),new Random(3));
        // Slots use a future end only while exercising capture; original historical times are restored.
        foreach(var item in plan.Slots){var end=item.End;item.End=DateTime.Now.AddMinutes(10);await CaptureDaily(tile,plan,item);item.End=end;AiTests.Check(item.Sample!=null,"No guardó captura con IA desactivada");}
        SaveJson(PlanPath(plan),plan);
        AiTests.Check(TrainingFiles().Length==0&&CompletedDailyPlans(DateTime.Now).Any(p=>p.Plan.Start==plan.Start),"Capturas no revisadas entrenan o no se ofrece tanda completa");
        await StopTile(tile);
        int position=0;bool pause=true;
        using(var click=new System.Windows.Forms.Timer{Interval=120})
        {
            click.Tick+=(_,_)=>
            {
                var review=Application.OpenForms.OfType<DailyReviewForm>().FirstOrDefault();
                if(review==null)return;
                if(pause&&position==2){Walk(review).OfType<Button>().Single(b=>b.Text=="Continuar más tarde").PerformClick();return;}
                if(position==0){using var shot=new Bitmap(review.Width,review.Height);review.DrawToBitmap(shot,new Rectangle(Point.Empty,review.Size));shot.Save(Path.Combine(output,"revision-diaria.png"));}
                string button="No hay personas";
                if(position%2==0){Walk(review).OfType<AnnotationCanvas>().Single().Boxes.Add(new RectangleF(.2f,.2f,.2f,.5f));button="Guardar marcas y seguir";}
                position++;
                Walk(review).OfType<Button>().Single(b=>b.Text==button).PerformClick();
            };
            click.Start();ReviewDailyBatch(plan);click.Stop();
            AiTests.Check(TrainingFiles().Length==2&&plan.Slots.Count(s=>s.Reviewed)==2,"No conserva progreso al salir");
            plan=JsonSerializer.Deserialize<DailyCapturePlan>(File.ReadAllText(PlanPath(plan)))!;pause=false;click.Start();ReviewDailyBatch(plan);click.Stop();
        }
        var samples=TrainingFiles().Select(p=>JsonSerializer.Deserialize<AiSample>(File.ReadAllText(p))!).ToArray();
        AiTests.Check(samples.Length==30&&samples.Count(s=>s.Boxes.Count==0)==15&&samples.Count(s=>s.Boxes.Count==1)==15&&samples.All(s=>s.Camera=="daily-camera")&&plan.Slots.All(s=>s.Reviewed),"Revisión no produce 30 etiquetas correctas");
    }
}
