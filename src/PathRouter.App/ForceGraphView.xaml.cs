using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PathRouter.Core;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace PathRouter.App;

/// <summary>
/// Вид «как в Obsidian»: точки-папки (источники и назначения), линии-ветви маршрутов,
/// силовая раскладка (отталкивание точек + пружины на рёбрах + гравитация к центру).
/// Общий источник с 10 заказчиками — одна точка с десятью лучами.
/// </summary>
public sealed partial class ForceGraphView : UserControl
{
    private sealed class FNode
    {
        public string Id = "";            // "F:<путь папки>"
        public string Path = "";
        public bool IsSource;
        public bool Missing;
        public bool HasUpdates;
        public Vector2 Pos, Vel, Force;
        public bool Pinned;               // перетащена пользователем — физика не двигает
        public bool Hovered;
        public readonly List<string> RouteIds = new();
    }

    private sealed class FEdge
    {
        public FNode A = null!, B = null!;
        public string RouteId = "", RouteName = "";
        public bool Conserved;
    }

    // --- физика (настраивается ползунками) ---
    public float Repulsion { get; set; } = 9000f;   // отталкивание, ~1/d²
    public float RestLength { get; set; } = 150f;   // длина связи
    public float Gravity { get; set; } = 0.015f;    // тяга к центру полотна
    private const float Damping = 0.8f;
    private const float MaxSpeed = 45f;
    private const float NodeR = 10f;

    /// <summary>Разбросать точки заново — физика пересобирает картину с чистого листа.</summary>
    public void Shuffle()
    {
        for (int i = 0; i < _nodes.Count; i++)
        {
            float r = 40f + 26f * MathF.Sqrt(i);
            float a = i * 2.39996f + (float)Random.Shared.NextDouble();
            _nodes[i].Pos = new Vector2(r * MathF.Cos(a), r * MathF.Sin(a));
            _nodes[i].Vel = Vector2.Zero;
        }
        Canvas.Invalidate();
    }

    private List<Route> _routes = new();
    private List<FNode> _nodes = new();
    private List<FEdge> _edges = new();
    private readonly Dictionary<string, FNode> _byId = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, (double X, double Y)> _savedLayout =
        new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);

    private string? _selectedRouteId;

    private Vector2 _pan = new(0, 0);
    private float _zoom = 1.0f;
    private bool _isPanning, _isDraggingNode;
    private FNode? _dragged, _hovered;
    private Point _lastPointer;
    private CanvasTextFormat? _fmtLabel;
    private readonly Microsoft.UI.Xaml.DispatcherTimer _timer = new()
        { Interval = TimeSpan.FromMilliseconds(33) };

    public event Action<Route>? RouteSelected;
    public event Action<string>? FolderOpenRequested;
    public event Action<string, double, double>? NodeMoved; // id, x, y — для сохранения в БД

    public ForceGraphView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) =>
        {
            if (Visibility != Microsoft.UI.Xaml.Visibility.Visible) return;
            StepPhysics();
            Canvas.Invalidate();
        };
        Canvas.SizeChanged += (_, _) =>
        {
            // стартуем с центрированного вида (один раз, дальше — позиция пользователя)
            if (!_centered && Canvas.ActualWidth > 0)
            {
                _pan = new Vector2((float)Canvas.ActualWidth / 2, (float)Canvas.ActualHeight / 2);
                _centered = true;
                Canvas.Invalidate();
            }
        };
        Canvas.Unloaded += (_, _) =>
        {
            _timer.Stop();
            Canvas.Draw -= OnDraw;
            _fmtLabel?.Dispose();
            Canvas.RemoveFromVisualTree();
        };
    }

    private bool _centered;

    // ---------- данные ----------

    public void SetRoutes(List<Route> routes)
    {
        _routes = routes;
        EmptyHint.Visibility = routes.Count == 0 ? Microsoft.UI.Xaml.Visibility.Visible
                                                 : Microsoft.UI.Xaml.Visibility.Collapsed;
        Rebuild();
        _timer.Start(); // при скрытом виде тик сам пропускается
        Canvas.Invalidate();
    }

    public void SetSavedLayout(IReadOnlyDictionary<string, (double X, double Y)> layout) => _savedLayout = layout;

    public void SelectRoute(Route? route)
    {
        _selectedRouteId = route?.Id;
        Canvas.Invalidate();
    }

    public void InvalidateGraph() => Canvas.Invalidate();

    private void Rebuild()
    {
        _nodes = new List<FNode>();
        _edges = new List<FEdge>();
        _byId.Clear();

        FNode GetNode(string path, bool isSource)
        {
            var id = "F:" + path;
            if (!_byId.TryGetValue(id, out var n))
            {
                n = new FNode { Id = id, Path = path, IsSource = isSource };
                // позиция: сохранённая (точка «прибита») или золотая спираль от центра для новых
                if (_savedLayout.TryGetValue(id, out var p))
                {
                    n.Pos = new Vector2((float)p.X, (float)p.Y);
                    n.Pinned = true;
                }
                else
                {
                    float i = _nodes.Count;
                    float r = 40f + 26f * MathF.Sqrt(i);
                    float a = i * 2.39996f; // золотой угол — новые точки не накладываются
                    n.Pos = new Vector2(r * MathF.Cos(a), r * MathF.Sin(a));
                }
                _byId[id] = n;
                _nodes.Add(n);
            }
            if (isSource) n.IsSource = true;
            return n;
        }

        foreach (var route in _routes)
        {
            var src = GetNode(route.SourcePath, isSource: true);
            if (!src.RouteIds.Contains(route.Id)) src.RouteIds.Add(route.Id);
            src.HasUpdates |= route.HasUpdates;
            src.Missing = route.Destinations.Count == 0
                ? !System.IO.Directory.Exists(route.SourcePath)
                : route.Destinations.Any(d => d.Diff?.SourceMissing == true);

            foreach (var dest in route.Destinations)
            {
                var dst = GetNode(dest.DestPath, isSource: false);
                if (!dst.RouteIds.Contains(route.Id)) dst.RouteIds.Add(route.Id);
                dst.HasUpdates |= dest.HasUpdates;
                _edges.Add(new FEdge
                {
                    A = src, B = dst,
                    RouteId = route.Id, RouteName = route.Name,
                    Conserved = dest.IsConserved
                });
            }
        }
    }

    // ---------- физика ----------

    private void StepPhysics()
    {
        if (_nodes.Count == 0) return;

        // отталкивание всех пар
        for (int i = 0; i < _nodes.Count; i++)
            for (int j = i + 1; j < _nodes.Count; j++)
            {
                var a = _nodes[i]; var b = _nodes[j];
                var d = a.Pos - b.Pos;
                float dist2 = MathF.Max(30f * 30f, d.LengthSquared());
                float f = Repulsion / dist2;
                var push = Vector2.Normalize(d) * f;
                a.Force += push;
                b.Force -= push;
            }

        // пружины на рёбрах
        foreach (var e in _edges)
        {
            var d = e.B.Pos - e.A.Pos;
            float dist = MathF.Max(1f, d.Length());
            var f = Vector2.Normalize(d) * (0.06f * (dist - RestLength));
            e.A.Force += f;
            e.B.Force -= f;
        }

        // гравитация к центру видимой области (как в Obsidian: кластер держится по центру окна)
        var center = new Vector2(
            (float)((Canvas.ActualWidth / 2 - _pan.X) / _zoom),
            (float)((Canvas.ActualHeight / 2 - _pan.Y) / _zoom));
        foreach (var n in _nodes)
        {
            if (n.Pinned) { n.Vel = Vector2.Zero; n.Force = Vector2.Zero; continue; }
            n.Force += (center - n.Pos) * Gravity;
            n.Vel = (n.Vel + n.Force) * Damping;
            if (n.Vel.Length() > MaxSpeed) n.Vel = Vector2.Normalize(n.Vel) * MaxSpeed;
            n.Pos += n.Vel;
            n.Force = Vector2.Zero;
        }
    }

    // ---------- рендер ----------


    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
        => _fmtLabel ??= new CanvasTextFormat { FontSize = 10, FontFamily = "Segoe UI", WordWrapping = CanvasWordWrapping.NoWrap };

    private bool IsEdgeActive(FEdge e)
        => e.RouteId == _selectedRouteId || e.A.Hovered || e.B.Hovered;

    private static Color Fade(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>Есть ли фокус (hover/выбор) — тогда не-соседи затемняются, как в Obsidian.</summary>
    private bool DimmedMode => _hovered is not null || _dragged is not null
                              || !string.IsNullOrEmpty(_selectedRouteId);

    private bool IsNodeActive(FNode n) => n.Hovered || ReferenceEquals(n, _dragged)
                                         || (_selectedRouteId is not null && n.RouteIds.Contains(_selectedRouteId));

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        ds.Clear(AppTheme.Bg);
        ds.Transform = Matrix3x2.CreateScale(_zoom) * Matrix3x2.CreateTranslation(_pan);

        bool dimmed = DimmedMode;

        // рёбра
        foreach (var e in _edges)
        {
            bool active = IsEdgeActive(e);
            var color = active ? AppTheme.Accent
                      : e.Conserved ? Fade(AppTheme.ConservedBr, 120)
                      : AppTheme.EdgeColor;
            if (dimmed && !active) color = Fade(color, 40);
            float w = active ? 2.5f : 1.25f;
            if (e.Conserved && !active)
            {
                using var dash = new Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle
                    { DashStyle = Microsoft.Graphics.Canvas.Geometry.CanvasDashStyle.Dash };
                ds.DrawLine(e.A.Pos, e.B.Pos, color, w, dash);
            }
            else
            {
                ds.DrawLine(e.A.Pos, e.B.Pos, color, w);
            }

            // стрелка направления у целевой точки
            var dir = e.B.Pos - e.A.Pos;
            float len = MathF.Max(1f, dir.Length());
            dir /= len;
            float rB = (e.B.IsSource ? NodeR + 3 : NodeR) + 2;
            var tip = e.B.Pos - dir * rB;
            var perp = new Vector2(-dir.Y, dir.X) * 4.5f;
            ds.DrawLine(tip, tip - dir * 9 + perp, color, w);
            ds.DrawLine(tip, tip - dir * 9 - perp, color, w);

            // подпись маршрута на рёбрах выбранного/наведённого
            if (active && !string.IsNullOrEmpty(e.RouteName))
            {
                var mid = (e.A.Pos + e.B.Pos) / 2;
                using var layout = new CanvasTextLayout(Canvas, Shorten(e.RouteName, 24), _fmtLabel!, 220, 0);
                ds.DrawTextLayout(layout, mid.X - (float)layout.DrawBounds.Width / 2, mid.Y - 16, AppTheme.AccentDark);
            }
        }

        // точки
        foreach (var n in _nodes)
        {
            float r = n.IsSource ? NodeR + 3 : NodeR;
            var fill = n.Missing ? AppTheme.ErrorRed
                     : n.IsSource ? AppTheme.Accent
                     : Color.FromArgb(255, 255, 255, 255);
            var border = n.Missing ? AppTheme.ErrorRed
                       : n.IsSource ? AppTheme.AccentDark
                       : AppTheme.ConservedBr;

            if (n.Hovered) r += 2;
            if (dimmed && !IsNodeActive(n))
            {
                fill = Fade(fill, 45);
                border = Fade(border, 45);
            }
            ds.FillCircle(n.Pos, r, fill);
            ds.DrawCircle(n.Pos, r, border, n.Hovered ? 2.5f : 1.5f);

            if (n.HasUpdates) ds.DrawCircle(n.Pos, r + 3.5f, AppTheme.UpdateColor, 2.5f);
            if (n.RouteIds.Contains(_selectedRouteId))
                ds.DrawCircle(n.Pos, r + 7f, Color.FromArgb(90, AppTheme.Accent.R, AppTheme.Accent.G, AppTheme.Accent.B), 2f);

            var labelColor = n.IsSource ? AppTheme.AccentDark : AppTheme.TextGray;
            if (dimmed && !IsNodeActive(n)) labelColor = Fade(labelColor, 45);
            using var lbl = new CanvasTextLayout(Canvas, Shorten(System.IO.Path.GetFileName(n.Path.TrimEnd('\\')), 24),
                                                 _fmtLabel!, 220, 0);
            float lx = n.Pos.X - (float)lbl.DrawBounds.Width / 2;
            ds.DrawTextLayout(lbl, lx, n.Pos.Y + r + 5, labelColor);
        }
    }

    private static string Shorten(string s, int max)
    {
        if (s.Length <= max) return s;
        int keep = max - 3, head = keep / 2, tail = keep - head;
        return s[..head] + "..." + s[^tail..];
    }

    // ---------- ввод ----------

    private FNode? HitNode(Point screen)
    {
        var w = ScreenToWorld(screen);
        var wv = new Vector2((float)w.X, (float)w.Y);
        for (int i = _nodes.Count - 1; i >= 0; i--)
        {
            float r = (_nodes[i].IsSource ? NodeR + 3 : NodeR) + 5;
            if ((wv - _nodes[i].Pos).LengthSquared() <= r * r) return _nodes[i];
        }
        return null;
    }

    private Point ScreenToWorld(Point p) => new((p.X - _pan.X) / _zoom, (p.Y - _pan.Y) / _zoom);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(Canvas).Position;
        _lastPointer = pos;
        var node = HitNode(pos);
        if (node is not null)
        {
            _isDraggingNode = true;
            _dragged = node;
            node.Pinned = true;      // прибили: физика больше не двигает
            node.Vel = Vector2.Zero;
            Canvas.CapturePointer(e.Pointer);
            Canvas.Invalidate();
            return;
        }
        _isPanning = true;
        Canvas.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(Canvas).Position;

        if (_isDraggingNode && _dragged is not null)
        {
            var w = ScreenToWorld(pos);
            _dragged.Pos = new Vector2((float)w.X, (float)w.Y);
            _dragged.Vel = Vector2.Zero;
            _lastPointer = pos;
            Canvas.Invalidate();
            return;
        }
        if (_isPanning)
        {
            _pan = new Vector2((float)(_pan.X + pos.X - _lastPointer.X), (float)(_pan.Y + pos.Y - _lastPointer.Y));
            _lastPointer = pos;
            Canvas.Invalidate();
            return;
        }

        // hover-подсветка без нажатия
        var h = HitNode(pos);
        if (!ReferenceEquals(h, _hovered))
        {
            if (_hovered is not null) _hovered.Hovered = false;
            _hovered = h;
            if (h is not null) h.Hovered = true;
            Canvas.Invalidate();
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingNode && _dragged is not null)
        {
            // без смещения — это клик: выбираем первый связанный маршрут
            var moved = (e.GetCurrentPoint(Canvas).Position.Y != _lastPointer.Y)
                     || (e.GetCurrentPoint(Canvas).Position.X != _lastPointer.X);
            if (!moved)
            {
                var route = _routes.FirstOrDefault(r => _dragged.RouteIds.Contains(r.Id));
                RouteSelected?.Invoke(route);
            }
            NodeMoved?.Invoke(_dragged.Id, _dragged.Pos.X, _dragged.Pos.Y);
            _isDraggingNode = false;
            _dragged = null;
            Canvas.ReleasePointerCapture(e.Pointer);
            Canvas.Invalidate();
            return;
        }
        _isPanning = false;
        Canvas.ReleasePointerCapture(e.Pointer);
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Canvas);
        if (point.Properties.IsHorizontalMouseWheel) return;
        float factor = point.Properties.MouseWheelDelta > 0 ? 1.12f : 1 / 1.12f;
        float newZoom = Math.Clamp(_zoom * factor, 0.08f, 4.0f);
        var pos = point.Position;
        var world = ScreenToWorld(pos);
        _pan = new Vector2((float)(pos.X - world.X * newZoom), (float)(pos.Y - world.Y * newZoom));
        _zoom = newZoom;
        Canvas.Invalidate();
        e.Handled = true;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var node = HitNode(e.GetPosition(Canvas));
        if (node is not null) FolderOpenRequested?.Invoke(node.Path);
    }
}
