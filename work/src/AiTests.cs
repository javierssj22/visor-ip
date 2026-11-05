using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;

namespace VisorIP;

internal static class AiTests
{
    internal static void Check(bool value,string message){if(!value)throw new Exception(message);}
    public static void RealCameras()
    {
        string output=Environment.GetEnvironmentVariable("VISOR_TEST_OUTPUT")??AppContext.BaseDirectory;
        Directory.CreateDirectory(output);using var form=new MainForm(true);
        form.Shown+=async(_,_)=>{try{await form.TestSavedCameras(output);}catch(Exception e){File.WriteAllText(Path.Combine(output,"ia-camaras.txt"),"No se completó: "+e.GetType().Name);Environment.ExitCode=1;}finally{form.Close();}};
        Application.Run(form);
    }
    public static void Run()
    {
        string output=Environment.GetEnvironmentVariable("VISOR_TEST_OUTPUT")??AppContext.BaseDirectory;
        string assets=Environment.GetEnvironmentVariable("VISOR_AI_FIXTURES")??Path.Combine(AppContext.BaseDirectory,"ia");
        Directory.CreateDirectory(output);AiExamples.DataRoot=Path.Combine(output,"ai-user-"+Guid.NewGuid().ToString("N"));var log=new List<string>();
        using var form=new MainForm(true);
        form.Shown+=async(_,_)=>{
            try
            {
                using var detector=new PersonDetector(Path.Combine(AppContext.BaseDirectory,"ia","yolox_m.onnx"));
                using var people=new Bitmap(Path.Combine(assets,"test-people.jpg"));
                using var dog=new Bitmap(Path.Combine(assets,"test-no-person.jpg"));
                using var blank=new Bitmap(640,480);
                var watch=Stopwatch.StartNew();
                var boxes=await Task.Run(()=>detector.Detect(people,.45f));
                Check(boxes.Count>=1,"No detectó a la persona en la imagen de referencia");
                log.Add($"PASS: modelo real, {boxes.Count} persona(s), primera inferencia {watch.ElapsedMilliseconds} ms");
                Check(boxes.All(b=>b.Bounds.X>=0&&b.Bounds.Y>=0&&b.Bounds.Right<=1&&b.Bounds.Bottom<=1),"Coordenadas fuera del video");
                using(var overlay=PersonDetector.DrawOverlay(people.Size,boxes))using(var annotated=new Bitmap(people))
                {using(var g=Graphics.FromImage(annotated))g.DrawImageUnscaled(overlay,0,0);annotated.Save(Path.Combine(output,"deteccion-referencia.png"));}
                Check((await Task.Run(()=>detector.Detect(dog,.45f))).Count==0,"Falso positivo en referencia sin personas");
                Check((await Task.Run(()=>detector.Detect(blank,.45f))).Count==0,"Falso positivo en imagen vacía");
                log.Add("PASS: imagen sin personas y cuadro negro sin detecciones");
                var raw=new float[3549*85];
                void Candidate(int index,float center){int p=index*85;raw[p]=center;raw[p+1]=20;raw[p+2]=3;raw[p+3]=3;raw[p+4]=.9f;raw[p+5]=.9f;}
                Candidate(0,20);Candidate(1,19);
                Check(PersonDetector.Decode(raw,new Size(416,416),1,.45f).Count==1,"NMS no elimina duplicado");
                raw[5]=float.NaN;raw[85+6]=1;
                Check(PersonDetector.Decode(raw,new Size(416,416),1,.45f).Count==0,"Aceptó NaN u otra clase");
                var restored=JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(new Preferences{Ai=new(){["one"]=new(){Enabled=false,Confidence=65}}}))!;
                Check(!restored.Ai["one"].Enabled&&restored.Ai["one"].Confidence==65&&new Preferences().Ai.Count==0,"Preferencias IA no se conservan");
                log.Add("PASS: NMS, coordenadas, NaN, otras clases y preferencias por cámara");
                Check(AiResultTiming.Lifetime(2001)==0&&AiResultTiming.Lifetime(1500)==500&&AiResultTiming.Lifetime(100)==900,"Edad de resultados incorrecta");
                var region=new RectangleF(.5f,0,.5f,1);
                var cropped=await Task.Run(()=>detector.DetectRegion(people,.45f,region));
                Check(cropped.Any(b=>boxes.Any(full=>PersonDetector.Overlap(b.Bounds,full.Bounds)>.5f)),"Recorte no vuelve a coordenadas originales");
                string samples=Path.Combine(output,"examples-"+Guid.NewGuid().ToString("N"));
                AiExamples.Save(people,new[]{boxes[0].Bounds},"test-camera",samples);AiExamples.Save(blank,Array.Empty<RectangleF>(),"test-camera",samples);
                string exported=AiExamples.Export(output,samples);
                using(var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(exported,"annotations","instances_all.json"))))
                {Check(document.RootElement.GetProperty("images").GetArrayLength()==2&&document.RootElement.GetProperty("annotations").GetArrayLength()==1,"Exportacion de positivos/negativos incorrecta");}
                Check(!AiExamples.Valid(new RectangleF(float.NaN,0,1,1)),"Acepta ejemplo NaN");
                using(var review=new AiReviewForm(people,false)){review.Show(form);Application.DoEvents();using var shot=new Bitmap(review.Width,review.Height);review.DrawToBitmap(shot,new Rectangle(Point.Empty,review.Size));shot.Save(Path.Combine(output,"editor-ejemplos.png"));review.Close();}
                log.Add("PASS: caducidad, coordenadas ROI, ejemplos positivos/negativos y exportacion COCO");
                var file=Path.Combine(output,"personas.avi");MakeVideo(file,File.ReadAllBytes(Path.Combine(assets,"test-people.jpg")),people.Size);
                File.WriteAllLines(Path.Combine(output,"avance-ia.txt"),log);
                await form.TestAiIntegration(file,output,log);
                await form.TestRecovery(file);log.Add("PASS: reconexión, refresco de 5 minutos y detención manual");
                File.WriteAllLines(Path.Combine(output,"pruebas-ia.txt"),log.Append("RESULTADO: OK"));
            }
            catch(Exception e){File.WriteAllLines(Path.Combine(output,"pruebas-ia.txt"),log.Append(e.ToString()));Environment.ExitCode=1;}
            finally{form.Close();}
        };
        Application.Run(form);
    }
    static void MakeVideo(string path,byte[] frame,Size size)
    {
        using var f=File.Create(path);using var w=new BinaryWriter(f);
        void Four(string s)=>w.Write(Encoding.ASCII.GetBytes(s));
        long Begin(string name){Four(name);w.Write(0);return f.Position;}
        void End(long p){long end=f.Position;f.Position=p-4;w.Write((int)(end-p));f.Position=end;if(end%2!=0)w.Write((byte)0);}
        var riff=Begin("RIFF");Four("AVI ");var hdrl=Begin("LIST");Four("hdrl");var avih=Begin("avih");
        w.Write(200000);w.Write(frame.Length*5);w.Write(0);w.Write(0);w.Write(600);w.Write(0);w.Write(1);w.Write(frame.Length);w.Write(size.Width);w.Write(size.Height);for(int i=0;i<4;i++)w.Write(0);End(avih);
        var strl=Begin("LIST");Four("strl");var strh=Begin("strh");Four("vids");Four("MJPG");w.Write(0);w.Write((short)0);w.Write((short)0);w.Write(0);w.Write(1);w.Write(5);w.Write(0);w.Write(600);w.Write(frame.Length);w.Write(-1);w.Write(0);w.Write((short)0);w.Write((short)0);w.Write((short)size.Width);w.Write((short)size.Height);End(strh);
        var strf=Begin("strf");w.Write(40);w.Write(size.Width);w.Write(size.Height);w.Write((short)1);w.Write((short)24);Four("MJPG");w.Write(frame.Length);for(int i=0;i<4;i++)w.Write(0);End(strf);End(strl);End(hdrl);
        var movi=Begin("LIST");Four("movi");for(int i=0;i<600;i++){var chunk=Begin("00dc");w.Write(frame);End(chunk);}End(movi);End(riff);
    }
}

public sealed partial class MainForm
{
    public async Task TestAiIntegration(string video,string output,List<string> log)
    {
        if(!testing)throw new InvalidOperationException();timer.Stop();SetView(6);
        // Keep video progress current while the first model is loaded, as the
        // production playback timer does. The test disables that timer above.
        using var progressTimer=new System.Windows.Forms.Timer{Interval=250};
        progressTimer.Tick+=(_,_)=>RefreshPlayers();progressTimer.Start();
        foreach(var tile in tiles.Take(6))
        {
            tile.Camera=new Camera{Ip="test-"+tile.Index};tile.CameraName=tile.Camera.Ip;tile.Url=new Uri(video).AbsoluteUri;tile.Login=new("","");tile.Active=true;
            prefs.Ai[tile.Camera.Ip]=new(){Model="balanced"};
            await ConnectTile(tile);File.AppendAllText(Path.Combine(output,"avance-ia.txt"),$"\nConectado {tile.Index}");
        }
        var until=Environment.TickCount64+15000;
        while(!tiles.Take(6).All(t=>t.Player!.HasVideo&&t.Player.State==3))
        {if(Environment.TickCount64>until)throw new Exception("No comenzaron los seis videos");await Task.Delay(100);}
        File.AppendAllText(Path.Combine(output,"avance-ia.txt"),"\nSeis videos activos");RefreshPlayers();
        long start=Environment.TickCount64;
        foreach(var tile in tiles.Take(6)){RefreshPlayers();aiWork=AnalyzeAi(tile);await aiWork;File.AppendAllText(Path.Combine(output,"avance-ia.txt"),$"\nAnalizado {tile.Index}: {aiStates[tile.Index].Status}");if(aiStates[tile.Index].People==0&&aiStates[tile.Index].Status.StartsWith("Análisis atrasado")){log.Add("INFO: se descartó el primer cuadro atrasado; se comprueba el siguiente cuadro fresco");RefreshPlayers();aiWork=AnalyzeAi(tile);await aiWork;}AiTests.Check(aiStates[tile.Index].People>0&&tile.Player!.AiOverlayEnabled,"Faltan persona/recuadro nativo en cámara "+tile.Index+": "+aiStates[tile.Index].Status);}
        log.Add($"PASS: seis videos con detección real Equilibrada y overlay VLC, ronda {Environment.TickCount64-start} ms");
        AiTests.Check(PendingCount()==6&&TrainingFiles().Length==0,"No capturó las seis detecciones o entrenó sin revisión");
        log.Add("PASS: seis cámaras guardan automáticamente pendientes sin entrenar con ellas");
        var first=tiles[0];RefreshPlayers();aiWork=AnalyzeAi(first);await aiWork;
        AiTests.Check(PendingCount()==6,"Repite captura en cada fotograma");
        bool allRed=true;until=Environment.TickCount64+1200;
        while(Environment.TickCount64<until){PaintAlerts();allRed&=first.BackColor==Color.Red;await Task.Delay(80);}
        AiTests.Check(allRed,"El borde rojo se interrumpe entre análisis");
        await ClearAi(first,false);PaintAlerts();AiTests.Check(first.BackColor==Color.Red&&!first.Player!.AiOverlayEnabled,"Caducar recuadros borra también el aviso");
        log.Add("PASS: borde rojo fijo entre análisis, independiente de los recuadros caducados");
        SetFullscreen(true);await Task.Delay(1000);RefreshPlayers();aiWork=AnalyzeAi(first);await aiWork;
        if(!first.Player!.AiOverlayEnabled&&aiStates[first.Index].Status.StartsWith("Análisis atrasado")){RefreshPlayers();aiWork=AnalyzeAi(first);await aiWork;}
        AiTests.Check(TestSixRatio()&&TestFullscreenArea()&&first.Player!.AiOverlayEnabled,$"Falló fullscreen con IA: ratio={TestSixRatio()} área={TestFullscreenArea()} overlay={first.Player!.AiOverlayEnabled} {aiStates[first.Index].Status}");
        BringToFront();Activate();await Task.Delay(120);
        using(var shot=new Bitmap(Width,Height))
        {
            using(var graphics=Graphics.FromImage(shot))graphics.CopyFromScreen(Location,Point.Empty,Size);
            shot.Save(Path.Combine(output,"ia-seis-pantalla-completa.png"));
            int green=0;for(int y=0;y<shot.Height;y++)for(int x=0;x<shot.Width;x++){var c=shot.GetPixel(x,y);if(c.G>200&&c.R<50&&c.B<50)green++;}
            AiTests.Check(green>300,"La salida visible de VLC no contiene recuadros verdes");
            log.Add($"PASS: captura de la ventana real contiene {green} píxeles verde flúor del overlay");
        }
        // VLC snapshots exclude subpictures, so subsequent inference does not see its own labels.
        var path=Path.Combine(output,"vlc-con-recuadro.png");var capture=await Task.Run(()=>first.Player!.Snapshot(path));capture?.Image.Dispose();
        SetFullscreen(false);await Task.Delay(300);AiTests.Check(TestSixRatio(),"Se perdió formato 4:3");
        log.Add("PASS: seis superficies 4:3 y pantalla completa conservan overlay");
        RefreshPlayers();aiWork=AnalyzeAi(first);await aiWork;
        AiTests.Check(first.Player!.AiOverlayEnabled,"No aparece el recuadro nuevo");
        await Task.Delay(1100);AiTests.Check(!first.Player.AiOverlayEnabled,"El recuadro no caduca mientras el analizador esta ocupado");
        log.Add("PASS: overlay caduca por su propio reloj, sin esperar otra ronda de IA");
        prefs.Ai[first.Camera!.Ip]=new(){Enabled=false};await ResetAi(first);PaintAlerts();
        AiTests.Check(!first.Player!.AiOverlayEnabled&&aiStates[0].People==0&&AiOption(tiles[1]).Enabled,"Desactivación por cámara incorrecta");
        prefs.Ai[first.Camera.Ip]=new(){Model="balanced"};RefreshPlayers();aiWork=AnalyzeAi(first);await aiWork;
        AiTests.Check(first.Player.AiOverlayEnabled&&aiStates[0].People>0,"No vuelve al activar IA");
        aiStates[0].Until=0;TickAi();await aiWork;AiTests.Check(!first.Player.AiOverlayEnabled,"No elimina recuadros vencidos");
        log.Add("PASS: desactivar/activar por cámara y retirar detecciones vencidas");
        progressTimer.Stop();
        RefreshPlayers();aiWork=AnalyzeAi(first);await aiWork;
        first.Reconnect.Progress=Environment.TickCount64-6000;TickAi();await aiWork;
        AiTests.Check(!first.Player.AiOverlayEnabled&&aiStates[0].People==0,"Conserva detecciones de video congelado");
        log.Add("PASS: video sin progreso retira las detecciones");
        foreach(var tile in tiles.Take(6))await StopTile(tile);
        AiTests.Check(tiles.Take(6).All(t=>!t.Player!.AiOverlayEnabled&&aiStates[t.Index].People==0),"Quedó alerta tras detener");
        log.Add("PASS: detener elimina overlays y alertas");
    }
    public async Task TestSavedCameras(string output)
    {
        if(!testing)throw new InvalidOperationException();timer.Stop();SetView(1);
        var report=new List<string>();var saved=Preferences.Load();
        foreach(var camera in saved.Tiles.GroupBy(t=>t.Camera.Ip).Select(g=>g.First()).Take(16))
        {
            if(!Uri.TryCreate(camera.Url,UriKind.Absolute,out var address)||address.Scheme!="rtsp")continue;
            using var timeout=new CancellationTokenSource(1500);
            bool reachable=false;try{reachable=await Net.PortOpen(address.Host,address.IsDefaultPort?554:address.Port,timeout.Token);}catch{}
            if(!reachable){report.Add(camera.Camera.Ip+": sin acceso a RTSP en esta red");continue;}
            var tile=tiles[0];tile.Camera=camera.Camera;tile.CameraName=camera.Camera.Ip;tile.Url=camera.Url;tile.Login=AccessVault.Open(camera.Access);tile.Active=true;
            await ConnectTile(tile);long until=Environment.TickCount64+12000;
            while(!tile.Player!.HasVideo&&Environment.TickCount64<until)await Task.Delay(100);
            RefreshPlayers();aiWork=AnalyzeAi(tile);await aiWork;
            report.Add(camera.Camera.Ip+": "+aiStates[0].Status+"; video="+tile.Player.HasVideo);
            await StopTile(tile);
        }
        if(report.Count==0)report.Add("No hay cámaras RTSP guardadas para comprobar.");
        File.WriteAllLines(Path.Combine(output,"ia-camaras.txt"),report);
    }
}
