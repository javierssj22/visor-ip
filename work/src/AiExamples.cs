using System.Drawing.Imaging;
using System.Text.Json;

namespace VisorIP;

public record AiSample(string Image,int Width,int Height,string Camera,DateTime CapturedUtc,List<RectangleF> Boxes)
{
    public List<RectangleF>? SuggestedBoxes {get;init;}
}
public static class AiExamples
{
    internal static string DataRoot {get;set;}=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Visor IP");
    public static string Root=>Path.Combine(DataRoot,"entrenamiento");
    public static string PersonalModel=>Path.Combine(DataRoot,"modelos","personal.onnx");
    public static string LearnedModel=>Path.Combine(DataRoot,"modelos","aprendido.onnx");
    public static string PendingRoot=>Path.Combine(Root,"pendientes");
    public static string Save(Bitmap frame,IReadOnlyList<RectangleF> boxes,string camera,string? root=null,DateTime? capturedUtc=null,IReadOnlyList<RectangleF>? suggestions=null)
    {
        root??=Root;Directory.CreateDirectory(root);
        if(boxes.Any(b=>!Valid(b))||suggestions?.Any(b=>!Valid(b))==true)throw new ArgumentException("Recuadro inválido");
        string name=DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N");
        string image=Path.Combine(root,name+".jpg"),json=Path.Combine(root,name+".json");
        try{frame.Save(image,ImageFormat.Jpeg);File.WriteAllText(json+".tmp",JsonSerializer.Serialize(new AiSample(name+".jpg",frame.Width,frame.Height,camera,capturedUtc??DateTime.UtcNow,boxes.ToList()){SuggestedBoxes=suggestions?.ToList()},new JsonSerializerOptions{WriteIndented=true}));File.Move(json+".tmp",json);}
        catch{if(File.Exists(image))File.Delete(image);throw;}
        return json;
    }
    public static string SavePending(Bitmap frame,string camera,IReadOnlyList<RectangleF>? suggestions=null,DateTime? capturedUtc=null)
    {
        // Pending files live in a separate folder and are never training negatives.
        return Save(frame,Array.Empty<RectangleF>(),camera,PendingRoot,capturedUtc,suggestions);
    }
    public static bool Valid(RectangleF b)=>float.IsFinite(b.X+b.Y+b.Width+b.Height)&&b.X>=0&&b.Y>=0&&b.Width>0&&b.Height>0&&b.Right<=1.00001f&&b.Bottom<=1.00001f;
    public static string Export(string destination,string? root=null)
    {
        root??=Root;var files=Directory.Exists(root)?Directory.GetFiles(root,"*.json"):Array.Empty<string>();
        if(files.Length==0)throw new InvalidOperationException("Todavía no hay ejemplos corregidos para exportar.");
        string target=Path.Combine(destination,"VisorIP-ejemplos-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(target,"images"));Directory.CreateDirectory(Path.Combine(target,"annotations"));
        var images=new List<object>();var annotations=new List<object>();int id=0,annotation=0;
        foreach(string file in files.OrderBy(f=>f))
        {
            var sample=JsonSerializer.Deserialize<AiSample>(File.ReadAllText(file))??throw new InvalidDataException("Ejemplo incompleto");
            if(Path.GetFileName(sample.Image)!=sample.Image||sample.Width<=0||sample.Height<=0||sample.Boxes.Any(b=>!Valid(b)))throw new InvalidDataException("Ejemplo inválido");
            File.Copy(Path.Combine(root,sample.Image),Path.Combine(target,"images",sample.Image));id++;
            images.Add(new{id,file_name=sample.Image,width=sample.Width,height=sample.Height,camera=sample.Camera,date_captured=sample.CapturedUtc.ToString("O")});
            foreach(var b in sample.Boxes){float x=b.X*sample.Width,y=b.Y*sample.Height,w=b.Width*sample.Width,h=b.Height*sample.Height;annotations.Add(new{id=++annotation,image_id=id,category_id=1,bbox=new[]{x,y,w,h},area=w*h,iscrowd=0});}
        }
        File.WriteAllText(Path.Combine(target,"annotations","instances_all.json"),JsonSerializer.Serialize(new{images,annotations,categories=new[]{new{id=1,name="person",supercategory="person"}}},new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(target,"LEEME.txt"),"Ejemplos corregidos manualmente, formato COCO. No es un modelo entrenado.\nIncluye imágenes sin personas con cero anotaciones. Separar entrenamiento y validación por secuencia/fecha; no repartir fotogramas casi iguales entre ambos.\nRevisar todas las personas visibles y conservar ejemplos nocturnos, diurnos y falsos avisos antes de entrenar.\nNo contiene contraseñas. Las imágenes y la dirección local de cámara son datos privados: no se suben automáticamente.\n");
        return target;
    }
}

public sealed class AiReviewForm : Form
{
    readonly AnnotationCanvas canvas;
    public IReadOnlyList<RectangleF> Boxes=>canvas.Boxes;
    public AiReviewForm(Bitmap frame,bool zone,IReadOnlyList<RectangleF>? suggestions=null,bool pending=false)
    {
        Text=zone?"Zona a vigilar":"Corregir ejemplo para entrenamiento";Size=new Size(1100,800);MinimumSize=new Size(700,500);StartPosition=FormStartPosition.CenterParent;
        BackColor=Color.FromArgb(14,23,37);ForeColor=Color.White;Font=new Font("Segoe UI",10);
        var panel=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=3,ColumnCount=1,Padding=new Padding(12)};
        panel.RowStyles.Add(new(SizeType.Absolute,80));panel.RowStyles.Add(new(SizeType.Percent,100));panel.RowStyles.Add(new(SizeType.Absolute,85));
        panel.Controls.Add(new Label{Dock=DockStyle.Fill,Text=zone?"Arrastrá para elegir el área donde buscar personas. Fuera de esa zona no se analizará.\nImagen completa elimina el recorte. Si movés el domo, revisá la zona.":pending?"¿El visor acertó? Confirmá las marcas o elegí No hay personas si toda la imagen está vacía.\nSi alguna marca está mal, quitála con clic derecho. Arrastrá para marcar TODAS las personas que falten.\nSi la imagen no es clara, descartala sin enseñar nada a la red.":"Arrastrá un recuadro alrededor de CADA persona visible, incluso si está parcialmente tapada.\nSi no hay ninguna, usá Guardar sin personas. Tu corrección se agrega al aprendizaje local."},0,0);
        canvas=new AnnotationCanvas(frame,zone);if(suggestions!=null)canvas.Boxes.AddRange(suggestions.Where(AiExamples.Valid));panel.Controls.Add(canvas,0,1);
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=true};
        var undo=new Button{Text="Deshacer",AutoSize=true};undo.Click+=(_,_)=>canvas.Undo();
        var clear=new Button{Text="Borrar marcas",AutoSize=true};clear.Click+=(_,_)=>canvas.Clear();
        var save=new Button{Text=zone?"Usar esta zona":pending?"Sí, son personas":"Guardar personas marcadas",AutoSize=true};save.Click+=(_,_)=>{if(canvas.Boxes.Count==0){MessageBox.Show(this,"Marcá al menos un recuadro o usá la otra opción de guardado.");return;}if(zone&&(canvas.Boxes[0].Width<.05f||canvas.Boxes[0].Height<.05f)){MessageBox.Show(this,"Elegí una zona más grande: al menos el 5% del ancho y del alto de la imagen.");return;}DialogResult=DialogResult.OK;};
        var empty=new Button{Text=zone?"Imagen completa":pending?"No hay personas":"Guardar sin personas",AutoSize=true};empty.Click+=(_,_)=>{canvas.Clear();DialogResult=DialogResult.OK;};
        var cancel=new Button{Text=pending?"Más tarde":"Cancelar",AutoSize=true,DialogResult=DialogResult.Cancel};actions.Controls.AddRange(new Control[]{undo,clear,save,empty});
        if(pending)actions.Controls.Add(new Button{Text="Descartar (no aprender)",AutoSize=true,DialogResult=DialogResult.Abort});
        actions.Controls.Add(cancel);panel.Controls.Add(actions,0,2);Controls.Add(panel);CancelButton=cancel;
    }
}
public sealed class AnnotationCanvas : Control
{
    readonly Bitmap image;readonly bool single;
    public List<RectangleF> Boxes {get;}=new();
    PointF? start;PointF end;
    public AnnotationCanvas(Bitmap image,bool single){this.image=image;this.single=single;SetStyle(ControlStyles.ResizeRedraw,true);Dock=DockStyle.Fill;DoubleBuffered=true;BackColor=Color.Black;Cursor=Cursors.Cross;}
    RectangleF View
    {
        get{float scale=Math.Min((float)ClientSize.Width/image.Width,(float)ClientSize.Height/image.Height);return new((Width-image.Width*scale)/2,(Height-image.Height*scale)/2,image.Width*scale,image.Height*scale);}
    }
    PointF ToUnit(Point p){var r=View;return new(Math.Clamp((p.X-r.X)/r.Width,0,1),Math.Clamp((p.Y-r.Y)/r.Height,0,1));}
    static RectangleF Rect(PointF a,PointF b)=>RectangleF.FromLTRB(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y),Math.Max(a.X,b.X),Math.Max(a.Y,b.Y));
    protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(!View.Contains(e.Location))return;if(e.Button==MouseButtons.Right){int i=Boxes.FindLastIndex(b=>b.Contains(ToUnit(e.Location)));if(i>=0){Boxes.RemoveAt(i);Invalidate();}return;}if(e.Button!=MouseButtons.Left)return;start=end=ToUnit(e.Location);Capture=true;}
    protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(start==null)return;end=ToUnit(e.Location);Invalidate();}
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);if(start==null)return;var r=Rect(start.Value,ToUnit(e.Location));if(r.Width>=.005&&r.Height>=.005){if(single)Boxes.Clear();Boxes.Add(r);}start=null;Capture=false;Invalidate();}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);var view=View;e.Graphics.DrawImage(image,view);using var pen=new Pen(Color.Lime,3);
        void Draw(RectangleF r)=>e.Graphics.DrawRectangle(pen,view.X+r.X*view.Width,view.Y+r.Y*view.Height,r.Width*view.Width,r.Height*view.Height);
        foreach(var box in Boxes)Draw(box);if(start!=null)Draw(Rect(start.Value,end));
    }
    public void Undo(){if(Boxes.Count>0)Boxes.RemoveAt(Boxes.Count-1);Invalidate();}
    public void Clear(){Boxes.Clear();Invalidate();}
}
