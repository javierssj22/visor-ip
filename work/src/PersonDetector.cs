using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VisorIP;

public sealed class AiSettings
{
    public bool Enabled { get; set; }=true;
    public int Confidence { get; set; }=45;
    public int IntervalMs { get; set; }=500;
    public string Model { get; set; }="precise";
    public RectangleF? Region { get; set; }
}
public record PersonBox(RectangleF Bounds,float Confidence);

// YOLOX: BGR pixels in [0,255], CHW, top-left letterbox (114), raw grid output.
// Reference implementation: Megvii-BaseDetection/YOLOX, Apache-2.0 (license included).
public sealed class PersonDetector : IDisposable
{
    readonly int Side;
    readonly int classes;
    InferenceSession session;
    public string Provider=>"CPU";
    public int InputSize=>Side;
    readonly string input;
    public PersonDetector(string model)
    {
        if(!File.Exists(model))throw new InvalidOperationException("Falta el modelo de IA. Conservá la carpeta ia junto al programa.");
        using var cpu=Options();session=new InferenceSession(model,cpu);
        var metadata=session.InputMetadata.Single();input=metadata.Key;
        var shape=metadata.Value.Dimensions;
        if(shape.Length!=4||shape[0]!=1||shape[1]!=3||shape[2]!=shape[3]||shape[2] is not (416 or 640))
        {session.Dispose();throw new InvalidOperationException("Modelo incompatible: se espera YOLOX 416 o 640.");}
        Side=shape[2];var output=session.OutputMetadata.First().Value.Dimensions;
        classes=output.Length==3?output[2]-5:0;
        if(classes is not (1 or 80)||output[1]!=new[]{8,16,32}.Sum(stride=>(Side/stride)*(Side/stride)))
        {session.Dispose();throw new InvalidOperationException("Se espera salida YOLOX sin decodificar, de personas o COCO.");}
    }
    static SessionOptions Options()
    {
        var options=new SessionOptions{IntraOpNumThreads=Math.Max(1,Math.Min(2,Environment.ProcessorCount)),InterOpNumThreads=1,ExecutionMode=ExecutionMode.ORT_SEQUENTIAL,GraphOptimizationLevel=GraphOptimizationLevel.ORT_ENABLE_ALL};
        options.AddSessionConfigEntry("session.intra_op.allow_spinning","0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning","0");
        return options;
    }
    public IReadOnlyList<PersonBox> DetectRegion(Bitmap frame,float threshold,RectangleF? region)
    {
        if(region==null)return Detect(frame,threshold);
        var r=RectangleF.Intersect(new RectangleF(0,0,1,1),region.Value);
        if(r.Width<.05f||r.Height<.05f)return Detect(frame,threshold);
        var crop=Rectangle.Intersect(new Rectangle(Point.Empty,frame.Size),Rectangle.Round(new RectangleF(r.X*frame.Width,r.Y*frame.Height,r.Width*frame.Width,r.Height*frame.Height)));
        using var detail=frame.Clone(crop,PixelFormat.Format24bppRgb);
        return Detect(detail,threshold).Select(b=>new PersonBox(new RectangleF((crop.X+b.Bounds.X*crop.Width)/frame.Width,(crop.Y+b.Bounds.Y*crop.Height)/frame.Height,b.Bounds.Width*crop.Width/frame.Width,b.Bounds.Height*crop.Height/frame.Height),b.Confidence)).ToArray();
    }
    public IReadOnlyList<PersonBox> Detect(Bitmap frame,float threshold)
    {
        float scale=Math.Min((float)Side/frame.Width,(float)Side/frame.Height);
        using var resized=new Bitmap(Side,Side,PixelFormat.Format24bppRgb);
        using(var g=Graphics.FromImage(resized))
        {
            g.Clear(Color.FromArgb(114,114,114));g.InterpolationMode=InterpolationMode.Bilinear;g.PixelOffsetMode=PixelOffsetMode.Half;
            g.DrawImage(frame,new Rectangle(0,0,(int)(frame.Width*scale),(int)(frame.Height*scale)),new Rectangle(0,0,frame.Width,frame.Height),GraphicsUnit.Pixel);
        }
        var tensor=new DenseTensor<float>(new[]{1,3,Side,Side});
        var bits=resized.LockBits(new Rectangle(0,0,Side,Side),ImageLockMode.ReadOnly,PixelFormat.Format24bppRgb);
        try
        {
            byte[] row=new byte[Side*3];var buffer=tensor.Buffer.Span;
            for(int y=0;y<Side;y++){Marshal.Copy(bits.Scan0+y*bits.Stride,row,0,row.Length);for(int x=0;x<Side;x++){int p=y*Side+x;buffer[p]=row[x*3];buffer[Side*Side+p]=row[x*3+1];buffer[2*Side*Side+p]=row[x*3+2];}}
        }
        finally{resized.UnlockBits(bits);}
        return Run(tensor,frame.Size,scale,threshold);
    }
    IReadOnlyList<PersonBox> Run(DenseTensor<float> tensor,Size size,float scale,float threshold)
    {
        using var output=session.Run(new[]{NamedOnnxValue.CreateFromTensor(input,tensor)});
        return Decode(output.First().AsTensor<float>().ToArray(),size,scale,threshold,Side,classes);
    }
    public static IReadOnlyList<PersonBox> Decode(float[] data,Size size,float scale,float threshold,int side=416,int classes=80)
    {
        int columns=classes+5;
        if(data.Length!=new[]{8,16,32}.Sum(stride=>(side/stride)*(side/stride))*columns||scale<=0||size.Width<=0||size.Height<=0)throw new ArgumentException("Dimensiones de detección inválidas");
        var candidates=new List<PersonBox>();int index=0;
        foreach(int stride in new[]{8,16,32})for(int y=0;y<side/stride;y++)for(int x=0;x<side/stride;x++,index++)
        {
            int p=index*columns;float score=data[p+4]*data[p+5];if(!float.IsFinite(score)||score<threshold)continue;
            bool person=true;for(int c=6;c<columns;c++)if(data[p+c]>data[p+5]){person=false;break;}if(!person)continue;
            float cx=(data[p]+x)*stride/scale,cy=(data[p+1]+y)*stride/scale;
            float w=MathF.Exp(data[p+2])*stride/scale,h=MathF.Exp(data[p+3])*stride/scale;
            if(!float.IsFinite(cx+cy+w+h)||w<=0||h<=0)continue;
            float left=Math.Clamp(cx-w/2,0,size.Width),top=Math.Clamp(cy-h/2,0,size.Height),right=Math.Clamp(cx+w/2,0,size.Width),bottom=Math.Clamp(cy+h/2,0,size.Height);
            if(right-left<1||bottom-top<1)continue;
            candidates.Add(new(new RectangleF(left/size.Width,top/size.Height,(right-left)/size.Width,(bottom-top)/size.Height),score));
        }
        var selected=new List<PersonBox>();
        foreach(var candidate in candidates.OrderByDescending(b=>b.Confidence).Take(200))
        {
            if(selected.Any(b=>Overlap(b.Bounds,candidate.Bounds)>0.45f))continue;
            selected.Add(candidate);if(selected.Count>=30)break;
        }
        return selected;
    }
    public static float Overlap(RectangleF a,RectangleF b)
    {
        var shared=RectangleF.Intersect(a,b);if(shared.Width<=0||shared.Height<=0)return 0;
        var area=shared.Width*shared.Height;return area/(a.Width*a.Height+b.Width*b.Height-area);
    }
    public static Bitmap DrawOverlay(Size size,IReadOnlyList<PersonBox> boxes)
    {
        var bitmap=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppArgb);
        using var g=Graphics.FromImage(bitmap);g.Clear(Color.Transparent);
        float stroke=Math.Max(3,size.Width/180f);using var pen=new Pen(Color.Lime,stroke);
        using var font=new Font("Segoe UI",Math.Max(11,size.Width/80f),FontStyle.Bold,GraphicsUnit.Pixel);
        foreach(var box in boxes)
        {
            var b=box.Bounds;var rect=new RectangleF(b.X*size.Width,b.Y*size.Height,b.Width*size.Width,b.Height*size.Height);
            g.DrawRectangle(pen,rect.X,rect.Y,rect.Width,rect.Height);
            const string label="PERSONA";var labelSize=g.MeasureString(label,font);float ly=Math.Max(0,rect.Top-labelSize.Height-4);
            g.FillRectangle(Brushes.Lime,rect.Left,ly,labelSize.Width+6,labelSize.Height+4);g.DrawString(label,font,Brushes.Black,rect.Left+3,ly+2);
        }
        return bitmap;
    }
    public void Dispose()=>session.Dispose();
}
