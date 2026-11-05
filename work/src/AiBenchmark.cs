using System.Diagnostics;
using System.Text.Json;
namespace VisorIP;
static class AiBenchmark
{
    public static void Run()
    {
        var folder=Environment.GetEnvironmentVariable("VISOR_TEST_OUTPUT")!;Directory.CreateDirectory(folder);
        var results=new List<object>();
        foreach(var model in new[]{"yolox_s","yolox_m"})foreach(bool gpu in new[]{false})
        {
            var start=Stopwatch.StartNew();using var detector=new PersonDetector(Path.Combine(AppContext.BaseDirectory,"ia",model+".onnx"));
            long initialization=start.ElapsedMilliseconds;
            using var image=new Bitmap(Path.Combine(Environment.GetEnvironmentVariable("VISOR_AI_FIXTURES")!,"test-people.jpg"));
            for(int i=0;i<4;i++){start.Restart();var boxes=detector.Detect(image,.4f);results.Add(new{model,gpu,Provider=detector.Provider,initialization,run=i,ms=start.ElapsedMilliseconds,people=boxes.Count});File.WriteAllText(Path.Combine(folder,"benchmark-runtime.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));}
        }
    }
}
