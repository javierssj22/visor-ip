using System.Text.Json;

namespace VisorIP;

internal static class AutoCaptureTests
{
    public static void Run()
    {
        string output=Environment.GetEnvironmentVariable("VISOR_TEST_OUTPUT")??AppContext.BaseDirectory;
        Directory.CreateDirectory(output);AiExamples.DataRoot=Path.Combine(output,"auto-user-"+Guid.NewGuid().ToString("N"));
        using var form=new MainForm(true);
        form.Shown+=async(_,_)=>{try{await form.TestAutoCaptures(output);File.WriteAllText(Path.Combine(output,"auto-capturas.txt"),"PASS: captura automática guarda imagen limpia y sugerencias sin etiquetas de entrenamiento\nPASS: no duplica la misma cámara; respeta intervalo y episodios\nPASS: opción de captura independiente del entrenamiento\nPASS: revisión real Sí/No/Descartar/Más tarde\nPASS: confirmación conserva fecha y sólo entrena con etiquetas revisadas\nPASS: bandeja llena conserva pendientes y pausa capturas nuevas\nRESULTADO: OK");}catch(Exception e){File.WriteAllText(Path.Combine(output,"auto-capturas.txt"),e.ToString());Environment.ExitCode=1;}finally{form.Close();}};
        Application.Run(form);
    }
}

public sealed partial class MainForm
{
    public async Task TestAutoCaptures(string output)
    {
        if(!testing)throw new InvalidOperationException();
        var schedule=new DetectionCaptureSchedule();
        AiTests.Check(schedule.Observe("a",0),"No captura la primera detección");schedule.Saved("a",0);
        for(long now=1000;now<300000;now+=1000)AiTests.Check(!schedule.Observe("a",now),"Duplica detección continua");
        AiTests.Check(schedule.Observe("a",300000),"No renueva una detección persistente");schedule.Saved("a",300000);
        AiTests.Check(!schedule.Observe("a",350000)&&schedule.Observe("a",360000),"No respeta mínimo/episodio pendiente");
        AiTests.Check(schedule.Observe("b",360000),"Mezcla cámaras distintas");
        using var image=new Bitmap(Path.Combine(Environment.GetEnvironmentVariable("VISOR_AI_FIXTURES")!,"test-people.jpg"));
        var box=new RectangleF(.2f,.2f,.3f,.5f);var boxes=new[]{new PersonBox(box,.8f)};var captured=DateTime.UtcNow.AddMinutes(-10);
        trainingPreferences.CaptureDetections=false;
        await QueueDetectionForReview(image,"disabled",boxes,captured);AiTests.Check(PendingCount()==0,"Ignora captura desactivada");
        trainingPreferences.CaptureDetections=true;trainingPreferences.Automatic=false;
        await QueueDetectionForReview(image,"cam-a",Array.Empty<PersonBox>(),captured);AiTests.Check(PendingCount()==0,"Guarda imagen sin detección");
        await QueueDetectionForReview(image,"cam-a",boxes,captured);
        await QueueDetectionForReview(image,"cam-a",boxes,captured);AiTests.Check(PendingCount()==1,"Duplicado por cámara");
        foreach(string camera in new[]{"cam-b","cam-c","cam-d"})await QueueDetectionForReview(image,camera,boxes,captured);
        AiTests.Check(PendingCount()==4&&TrainingFiles().Length==0,"Aprende de etiquetas no revisadas");
        foreach(string file in Directory.GetFiles(AiExamples.PendingRoot,"*.json"))
        {
            var sample=JsonSerializer.Deserialize<AiSample>(File.ReadAllText(file))!;
            AiTests.Check(sample.Boxes.Count==0&&sample.SuggestedBoxes?.Count==1&&sample.CapturedUtc==captured,"Mezcla sugerencias con etiquetas");
        }
        AiTests.Check(aiLearningButton!.Text.Contains("4 pendientes"),"No muestra pendientes");
        string[] decisions={"Sí, son personas","No hay personas","Descartar (no aprender)","Más tarde"};int decision=0;
        IEnumerable<Control> Walk(Control c){foreach(Control child in c.Controls){yield return child;foreach(var nested in Walk(child))yield return nested;}}
        using(var click=new System.Windows.Forms.Timer{Interval=250})
        {
            click.Tick+=(_,_)=>
            {
                var review=Application.OpenForms.OfType<AiReviewForm>().FirstOrDefault();if(review==null)return;
                AiTests.Check(review.Boxes.Count==1,"No precarga recuadros sugeridos");
                if(decision==0){using var shot=new Bitmap(review.Width,review.Height);review.DrawToBitmap(shot,new Rectangle(Point.Empty,review.Size));shot.Save(Path.Combine(output,"revision-automatica.png"));}
                string selected=decisions[decision++];Walk(review).OfType<Button>().Single(b=>b.Text==selected).PerformClick();
            };
            click.Start();ReviewPending(this);click.Stop();
        }
        var corrected=TrainingFiles().Select(p=>JsonSerializer.Deserialize<AiSample>(File.ReadAllText(p))!).ToArray();
        AiTests.Check(decision==4&&corrected.Length==2&&corrected.Count(s=>s.Boxes.Count==1)==1&&corrected.Count(s=>s.Boxes.Count==0)==1&&corrected.All(s=>s.CapturedUtc==captured&&s.SuggestedBoxes==null)&&PendingCount()==1,"Revisión incorrecta o aprende de descartadas");
        var placeholders=new List<string>();
        for(int i=1;i<MaxPendingCaptures;i++){string file=Path.Combine(AiExamples.PendingRoot,$"capacity-test-{i}.json");File.WriteAllText(file,"{}");placeholders.Add(file);}
        await QueueDetectionForReview(image,"full-camera",boxes,captured);
        AiTests.Check(PendingCount()==MaxPendingCaptures&&captureNotice.Contains("llena"),"No respeta capacidad");
        foreach(string file in placeholders)File.Delete(file);
        captureNotice="Una detección espera tu revisión.";
        using var capture=new System.Windows.Forms.Timer{Interval=600};capture.Tick+=(_,_)=>{var dialog=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Text=="Aprender con mis capturas");if(dialog==null)return;capture.Stop();using var shot=new Bitmap(dialog.Width,dialog.Height);dialog.DrawToBitmap(shot,new Rectangle(Point.Empty,dialog.Size));shot.Save(Path.Combine(output,"bandeja-automatica.png"));dialog.Close();};
        capture.Start();OpenTraining();
    }
}
