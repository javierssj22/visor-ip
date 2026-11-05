namespace VisorIP;

// Shared by camera IP, including when the same camera occupies two tiles.
public sealed class DetectionCaptureSchedule
{
    sealed class Entry { public long Seen; public long? Attempt,Saved; public bool NewEpisode; }
    readonly Dictionary<string,Entry> entries=new();
    public bool Observe(string camera,long now)
    {
        if(!entries.TryGetValue(camera,out var e))entries[camera]=e=new(){Seen=now-30001};
        if(now-e.Seen>=30000)e.NewEpisode=true;e.Seen=now;
        if(e.Attempt.HasValue&&now-e.Attempt.Value<60000)return false;
        if(e.Saved.HasValue&&!e.NewEpisode&&now-e.Saved.Value<300000)return false;
        e.Attempt=now;return true;
    }
    public void Saved(string camera,long now){if(entries.TryGetValue(camera,out var e)){e.Saved=now;e.NewEpisode=false;}}
}

public sealed partial class MainForm
{
    const int MaxPendingCaptures=200;
    readonly DetectionCaptureSchedule detectionCaptures=new();
    string captureNotice="";
    Button? aiLearningButton;
    long nextBadgeCheck;
    int PendingCount()=>Directory.Exists(AiExamples.PendingRoot)?Directory.GetFiles(AiExamples.PendingRoot,"*.json").Length:0;
    void UpdatePendingBadge(bool force=false)
    {
        if(!force&&Environment.TickCount64<nextBadgeCheck)return;nextBadgeCheck=Environment.TickCount64+30000;
        try{int count=PendingCount();if(aiLearningButton!=null)aiLearningButton.Text=count==0?"IA local":$"IA local · {count} pendientes";}catch{}
    }
    async Task QueueDetectionForReview(Bitmap frame,string camera,IReadOnlyList<PersonBox> boxes,DateTime capturedUtc)
    {
        if(!trainingPreferences.CaptureDetections||boxes.Count==0)return;
        long now=Environment.TickCount64;
        if(!detectionCaptures.Observe(camera,now))return;
        try
        {
            // Use the very frame that was analyzed, without painted overlays.
            bool saved=await Task.Run(()=>
            {
                Directory.CreateDirectory(AiExamples.PendingRoot);
                using var guard=new FileStream(Path.Combine(AiExamples.PendingRoot,"capture.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
                if(Directory.GetFiles(AiExamples.PendingRoot,"*.json").Length>=MaxPendingCaptures)return false;
                if(Directory.GetFiles(AiExamples.PendingRoot,"*.jpg").Sum(p=>new FileInfo(p).Length)>=500L*1024*1024)return false;
                AiExamples.SavePending(frame,camera,boxes.Select(b=>b.Bounds).ToArray(),capturedUtc);return true;
            });
            if(saved){detectionCaptures.Saved(camera,now);captureNotice="Detección guardada automáticamente. Revisá las pendientes para confirmar si era una persona.";}
            else captureNotice="La bandeja automática está llena (200 capturas o 500 MB). Revisá o descartá pendientes para seguir guardando.";
            UpdatePendingBadge(true);
        }
        catch{captureNotice="No se pudo guardar la detección para revisar. El análisis de video continúa; se reintentará más adelante.";}
    }
}
