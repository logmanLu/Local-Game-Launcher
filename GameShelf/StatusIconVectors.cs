using System.Drawing.Drawing2D;
using System.Text.Json;

namespace GameShelf;

/// <summary>Small persisted white-on-colour vector format used by status lights.</summary>
public sealed class StatusIconShape
{
    public string Type { get; set; } = "line";
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public List<VectorPoint> Points { get; set; } = [];
}

public sealed class VectorPoint { public float X { get; set; } public float Y { get; set; } }

public static class StatusIconVectors
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public static string FilledCircle { get; } = Define(("ellipse-fill", 24, 24, 52, 52));
    public static string OutlineCircle { get; } = Define(("ellipse", 24, 24, 52, 52));
    public static string Cross { get; } = Define(("line", 27, 27, 46, 46), ("line", 73, 27, -46, 46));
    public static string OutlineCloud { get; } = Define(("cloud", 17, 28, 66, 46));
    public static string OutlineSquare { get; } = Define(("rectangle", 24, 24, 52, 52));
    public static string HalfSquare { get; } = Define(("rectangle-fill", 24, 50, 52, 26), ("rectangle", 24, 24, 52, 52));
    public static string FilledSquare { get; } = Define(("rectangle-fill", 24, 24, 52, 52));
    public static string DefaultFor(StatusKind kind) => kind == StatusKind.Play ? OutlineSquare : OutlineCircle;

    public static string Define(params (string type, float x, float y, float width, float height)[] shapes) =>
        JsonSerializer.Serialize(shapes.Select(shape => new StatusIconShape { Type = shape.type, X = shape.x, Y = shape.y, Width = shape.width, Height = shape.height }).ToList(), Json);
    public static List<StatusIconShape> Parse(string? vector)
    {
        try { return JsonSerializer.Deserialize<List<StatusIconShape>>(vector ?? "", Json)?.Select(Clone).ToList() ?? []; }
        catch { return []; }
    }
    public static string Serialize(IEnumerable<StatusIconShape> shapes) => JsonSerializer.Serialize(shapes.Select(Clone).ToList(), Json);
    public static void Draw(Graphics graphics, Rectangle bounds, string? vector)
    {
        var shapes = Parse(vector);
        var side = Math.Min(bounds.Width, bounds.Height);
        var target = new Rectangle(bounds.Left + (bounds.Width - side) / 2, bounds.Top + (bounds.Height - side) / 2, side, side);
        using var bitmap = new Bitmap(Math.Max(1, target.Width), Math.Max(1, target.Height));
        using (var canvas = Graphics.FromImage(bitmap))
        {
            canvas.SmoothingMode = SmoothingMode.AntiAlias;
            var local = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            foreach (var shape in shapes.Where(shape => shape.Type != "flood")) DrawShape(canvas, local, shape);
            foreach (var shape in shapes.Where(shape => shape.Type == "flood")) FloodFill(bitmap, shape.X * bitmap.Width / 100f, shape.Y * bitmap.Height / 100f);
        }
        graphics.DrawImageUnscaled(bitmap, target.Location);
    }
    public static void DrawShape(Graphics graphics, Rectangle bounds, StatusIconShape shape)
    {
        var x = bounds.Left + bounds.Width * shape.X / 100f;
        var y = bounds.Top + bounds.Height * shape.Y / 100f;
        var width = bounds.Width * shape.Width / 100f;
        var height = bounds.Height * shape.Height / 100f;
        var rect = RectangleF.FromLTRB(Math.Min(x, x + width), Math.Min(y, y + height), Math.Max(x, x + width), Math.Max(y, y + height));
        using var pen = new Pen(Color.White, Math.Max(2, Math.Min(bounds.Width, bounds.Height) * .065f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(Color.White);
        switch (shape.Type)
        {
            case "line":
                if (shape.Points.Count >= 2)
                {
                    var points = shape.Points.Select(point => new PointF(bounds.Left + bounds.Width * point.X / 100f, bounds.Top + bounds.Height * point.Y / 100f)).ToArray();
                    graphics.DrawLines(pen, points);
                }
                else graphics.DrawLine(pen, x, y, x + width, y + height);
                break;
            case "straight": graphics.DrawLine(pen, x, y, x + width, y + height); break;
            case "ellipse": graphics.DrawEllipse(pen, rect); break;
            case "ellipse-fill": graphics.FillEllipse(brush, rect); break;
            case "rectangle": graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height); break;
            case "rectangle-fill": graphics.FillRectangle(brush, rect); break;
            case "triangle": graphics.DrawPolygon(pen, [new PointF(rect.Left + rect.Width / 2, rect.Top), new PointF(rect.Right, rect.Bottom), new PointF(rect.Left, rect.Bottom)]); break;
            case "cloud": DrawCloud(graphics, pen, rect); break;
        }
    }
    private static void DrawCloud(Graphics graphics, Pen pen, RectangleF rect)
    {
        using var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y + rect.Height * .28f, rect.Width * .45f, rect.Height * .55f, 145, 145);
        path.AddArc(rect.X + rect.Width * .22f, rect.Y, rect.Width * .48f, rect.Height * .72f, 180, 180);
        path.AddArc(rect.X + rect.Width * .54f, rect.Y + rect.Height * .22f, rect.Width * .44f, rect.Height * .62f, 215, 145);
        path.AddLine(rect.Right - rect.Width * .12f, rect.Bottom, rect.X + rect.Width * .18f, rect.Bottom);
        graphics.DrawPath(pen, path);
    }
    private static void FloodFill(Bitmap bitmap, float x, float y)
    {
        var startX = Math.Clamp((int)Math.Round(x), 0, bitmap.Width - 1); var startY = Math.Clamp((int)Math.Round(y), 0, bitmap.Height - 1);
        if (bitmap.GetPixel(startX, startY).A != 0) return;
        var queue = new Queue<Point>(); queue.Enqueue(new Point(startX, startY));
        while (queue.Count > 0)
        {
            var point = queue.Dequeue(); if (point.X < 0 || point.Y < 0 || point.X >= bitmap.Width || point.Y >= bitmap.Height || bitmap.GetPixel(point.X, point.Y).A != 0) continue;
            bitmap.SetPixel(point.X, point.Y, Color.White); queue.Enqueue(new Point(point.X - 1, point.Y)); queue.Enqueue(new Point(point.X + 1, point.Y)); queue.Enqueue(new Point(point.X, point.Y - 1)); queue.Enqueue(new Point(point.X, point.Y + 1));
        }
    }
    private static StatusIconShape Clone(StatusIconShape shape) => new() { Type = shape.Type, X = shape.X, Y = shape.Y, Width = shape.Width, Height = shape.Height, Points = shape.Points.Select(point => new VectorPoint { X = point.X, Y = point.Y }).ToList() };
}

public sealed class StatusIconCanvas : Control
{
    private readonly List<StatusIconShape> _shapes;
    private Point _start;
    private Point _current;
    private bool _dragging;
    private readonly List<Point> _stroke = [];
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Tool { get; set; } = "line";
    public IReadOnlyList<StatusIconShape> Shapes => _shapes;

    public StatusIconCanvas(Color background, string? vector)
    {
        _shapes = StatusIconVectors.Parse(vector); BackColor = background; DoubleBuffered = true; Cursor = Cursors.Cross;
    }
    public void Clear() { _shapes.Clear(); Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _start = e.Location; _current = e.Location; _stroke.Clear(); _stroke.Add(e.Location); _dragging = true; Capture = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_dragging) { _current = e.Location; if (Tool == "line" && (_stroke.Count == 0 || _stroke[^1] != e.Location)) _stroke.Add(e.Location); Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging && e.Button == MouseButtons.Left)
        {
            _current = e.Location; _dragging = false; Capture = false;
            if (Tool == "flood") _shapes.Add(ToShape(_start, _current));
            else if (Math.Abs(_current.X - _start.X) + Math.Abs(_current.Y - _start.Y) > 6) _shapes.Add(Tool == "line" ? ToFreeLine() : ToShape(_start, _current));
            Invalidate();
        }
        base.OnMouseUp(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(BackColor);
        StatusIconVectors.Draw(e.Graphics, ClientRectangle, StatusIconVectors.Serialize(_shapes));
        if (_dragging) StatusIconVectors.DrawShape(e.Graphics, ClientRectangle, ToShape(_start, _current));
        using var border = new Pen(Color.White, 2); e.Graphics.DrawRectangle(border, 1, 1, Math.Max(0, Width - 3), Math.Max(0, Height - 3));
    }
    private StatusIconShape ToShape(Point start, Point end) => new()
    {
        Type = Tool,
        X = start.X * 100f / Math.Max(1, ClientSize.Width), Y = start.Y * 100f / Math.Max(1, ClientSize.Height),
        Width = (end.X - start.X) * 100f / Math.Max(1, ClientSize.Width), Height = (end.Y - start.Y) * 100f / Math.Max(1, ClientSize.Height)
    };
    private StatusIconShape ToFreeLine() => new() { Type = "line", Points = _stroke.Select(point => new VectorPoint { X = point.X * 100f / Math.Max(1, ClientSize.Width), Y = point.Y * 100f / Math.Max(1, ClientSize.Height) }).ToList() };
}
