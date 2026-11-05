using System.Text.Json;

namespace VisorIP;

internal static class TrainingTests
{
    public static void Run()
    {
        string output=Environment.GetEnvironmentVariable("VISOR_TEST_OUTPUT")??AppContext.BaseDirectory;
        string candidate=Environment.GetEnvironmentVariable("VISOR_TRAINED_MODEL")??throw new InvalidOperationException("Falta modelo de prueba");
        Directory.CreateDirectory(output);AiExamples.DataRoot=Path.Combine(output,"isolated-user-"+Guid.NewGuid().ToString("N"));
        using var form=new MainForm(true);
        form.Shown+=async(_,_)=>{try{await form.TestTraining(candidate,output);File.WriteAllText(Path.Combine(output,"training-integration.txt"),"PASS: ONNX entrenado compatible con el detector C#\nPASS: captura pendiente excluida del conjunto de entrenamiento\nPASS: corrección conserva fecha original y crea etiqueta\nPASS: instalación atómica y modelo anterior recuperable\nPASS: configuración por cámara conserva desactivación\nPASS: proceso local con pocos datos no promueve un candidato\nPASS: pausa y cierre cancelan el proceso de aprendizaje\nRESULTADO: OK");}catch(Exception e){File.WriteAllText(Path.Combine(output,"training-integration.txt"),e.ToString());Environment.ExitCode=1;}finally{form.Close();}};
        Application.Run(form);
    }
}

public sealed partial class MainForm
{
    public async Task TestTraining(string candidate,string output)
    {
        if(!testing)throw new InvalidOperationException();
        using var image=new Bitmap(Path.Combine(Environment.GetEnvironmentVariable("VISOR_AI_FIXTURES")!,"test-people.jpg"));
        using(var detector=new PersonDetector(candidate))AiTests.Check((await Task.Run(()=>detector.Detect(image,.45f))).Count>0,"ONNX entrenado no detecta referencia");
        string pending=AiExamples.SavePending(image,"cam");
        AiTests.Check(TrainingFiles().Length==0,"Captura sin revisar entró al entrenamiento");
        var sample=JsonSerializer.Deserialize<AiSample>(File.ReadAllText(pending))!;
        string reviewed=AiExamples.Save(image,new[]{new RectangleF(.2f,.2f,.3f,.5f)},"cam",capturedUtc:sample.CapturedUtc);
        var labeled=JsonSerializer.Deserialize<AiSample>(File.ReadAllText(reviewed))!;
        AiTests.Check(labeled.CapturedUtc==sample.CapturedUtc&&TrainingFiles().Length==1,"La corrección perdió fecha o etiqueta");
        prefs.Ai["cam"]=new(){Enabled=false,Confidence=60};
        await InstallLearnedModel(candidate,new[]{"cam"});
        AiTests.Check(File.Exists(AiExamples.LearnedModel)&&prefs.Ai["cam"].Model=="learned"&&!prefs.Ai["cam"].Enabled&&prefs.Ai["cam"].Confidence==60,"Instalación no conserva opciones");
        await InstallLearnedModel(candidate,new[]{"cam"});
        AiTests.Check(File.Exists(AiExamples.LearnedModel+".anterior"),"No guarda modelo anterior");
        string[] files=TrainingFiles();
        trainingWork=TrainLocal(files,TrainingSignature(files));await trainingWork;
        using(var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(trainingJob!,"report.json"))))
            AiTests.Check(!report.RootElement.GetProperty("approved").GetBoolean()&&report.RootElement.GetProperty("positive").GetInt32()==1,"Pocos datos activaron un modelo");
        TickTraining();AiTests.Check(trainingProcess==null,"Repite un conjunto ya procesado");
        using(var negative=new Bitmap(100,100))AiExamples.Save(negative,Array.Empty<RectangleF>(),"cam");
        nextTrainingScan=0;TickTraining();AiTests.Check(trainingProcess==null,"No respeta espera entre correcciones");
        trainingDue=Environment.TickCount64-1;nextTrainingScan=0;TickTraining();await trainingWork;
        using(var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(trainingJob!,"report.json"))))
            AiTests.Check(report.RootElement.GetProperty("negative").GetInt32()==1,"No inicia automáticamente al guardar correcciones nuevas");
        trainingWork=TrainLocal(files,"cancellation-check");
        AiTests.Check(trainingProcess!=null,"No arranca proceso local");
        await StopTraining();AiTests.Check(trainingWork.IsCompleted&&trainingProcess==null,"No detuvo el proceso");
        // Render the real learning dialog, with an isolated dataset and no cameras.
        using var capture=new System.Windows.Forms.Timer{Interval=800};
        capture.Tick+=(_,_)=>{capture.Stop();var dialog=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Text=="Aprender con mis capturas");if(dialog!=null){using var shot=new Bitmap(dialog.Width,dialog.Height);dialog.DrawToBitmap(shot,new Rectangle(Point.Empty,dialog.Size));shot.Save(Path.Combine(output,"aprendizaje-dialogo.png"));dialog.Close();}};
        capture.Start();OpenTraining();
    }
}
