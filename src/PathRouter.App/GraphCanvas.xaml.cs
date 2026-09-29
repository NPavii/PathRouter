using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PathRouter.Core;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace PathRouter.App;

/// <summary>Интерактивный граф маршрутов: панорама, зум, выбор, бейджи обновлений.</summary>
public sealed partial class GraphCanvas : UserControl
{
    private enum NodeKind { Source, Name, Dest, UpdateBadge, RouteToggle, GroupToggle }

    private sealed record NodeHit(Rect Rect, Route Route, RouteDestination? Dest, NodeKind Kind, string? GroupName = null);

    // Геометрия раскладки
    private const float SourceX = 30, SourceW = 270, NodeH = 58;
    private const float NameX = 420, NameW = 250;
    private const float DestX = 790, DestW = 270;
    private const float DestRowH = 82, BlockPad = 30, MinBlockH = 100;

    private static readonly Color Accent = Color.FromArgb(255, 79, 107, 237);
    private static readonly Color AccentDark = Color.FromArgb(255, 43, 74, 203);
    private static readonly Color SourceFill = Color.FromArgb(255, 238, 243, 255);
    private static readonly Color DestFill = Color.FromArgb(255, 244, 244, 244);
    private static readonly Color DestBorder = Color.FromArgb(255, 200, 200, 200);
    private static readonly Color EdgeColor = Color.FromArgb(255, 154, 167, 199);
    private static readonly Color UpdateColor = Color.FromArgb(255, 247, 99, 12);
    private static readonly Color ErrorRed = Color.FromArgb(255, 196, 43, 28);
    private static readonly Color TextDark = Color.FromArgb(255, 40, 45, 60);
    private static readonly Color TextGray = Color.FromArgb(255, 110, 115, 130);
    private static readonly Color GridDot = Color.FromArgb(60, 120, 130, 160);

    private List<Route> _routes = new();
    private readonly List<NodeHit> _hits = new();
    private readonly Dictionary<(string Text, float W), CanvasTextLayout> _textCache = new();

    private Vector2 _pan = new(20, 20);
    private float _zoom = 1.0f;
    private bool _isPanning;
    private Point _lastPointer;
    private CanvasTextFormat? _fmtTitle, _fmtPath, _fmtBadge, _fmtGlyph;

    public Route? SelectedRoute { get; private set; }

    public event Action<Route>? RouteSelected;
    public event Action<Route, RouteDestination>? UpdateRequested;
    public event Action<string>? FolderOpenRequested;
    public event Action<Route>? RouteCollapseToggled;
    public event Action<string>? GroupCollapseToggled;
    public event Action<Route, RouteDestination>? ConservationToggled;

    /// <summary>Свёрнутость путей (group_name -> collapsed), задаётся извне после загрузки маршрутов.</summary>
    private IReadOnlyDictionary<string, bool> _collapsedGroups =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    public void SetGroupStates(IReadOnlyDictionary<string, bool> collapsedGroups)
    {
        _collapsedGroups = collapsedGroups;
        Canvas.Invalidate();
    }

    private bool GroupCollapsed(string? name) =>
        name is not null && _collapsedGroups.TryGetValue(name, out var c) && c;

    /// <summary>Сохранённые Y-позиции блоков (ручная раскладка пользователя).</summary>
    private IReadOnlyDictionary<string, double> _layout =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    public void SetLayout(IReadOnlyDictionary<string, double> layout)
    {
        _layout = layout;
        Canvas.Invalidate();
    }

    public event Action<string, double>? LayoutChanged;

    // ---------- блоки и ручная раскладка ----------

    private sealed record Block(string Key, float H, bool IsPath, bool Collapsed, List<Route> Members);

    private List<(Block Block, float Y)>? _drawnBlocks;      // позиции последнего кадра
    private readonly List<(Rect Rect, string Key)> _blockRects = new(); // зоны захвата блоков
    private string? _dragBlockKey;
    private float _dragDeltaY;
    private bool _blockDragging;

    private static string BlockKey(Route r) =>
        string.IsNullOrEmpty(r.GroupName) ? "S:" + SourceKey(r) : "G:" + r.GroupName;

    public GraphCanvas()
    {
        InitializeComponent();
        Canvas.SizeChanged += (_, _) => TryFitToContent();
        Canvas.Unloaded += (_, _) =>
        {
            Canvas.Draw -= OnDraw;
            foreach (var l in _textCache.Values) l.Dispose();
            _textCache.Clear();
            _fmtTitle?.Dispose(); _fmtPath?.Dispose(); _fmtBadge?.Dispose(); _fmtGlyph?.Dispose();
            Canvas.RemoveFromVisualTree();
        };
    }

    public void SetRoutes(List<Route> routes)
    {
        _routes = routes;
        foreach (var l in _textCache.Values) l.Dispose();
        _textCache.Clear();
        EmptyHint.Visibility = routes.Count == 0 ? Microsoft.UI.Xaml.Visibility.Visible
                                                 : Microsoft.UI.Xaml.Visibility.Collapsed;
        Canvas.Invalidate();
    }

    public void SelectRoute(Route? route)
    {
        SelectedRoute = route;
        Canvas.Invalidate();
    }

    public void ResetView()
    {
        _fitDone = false;
        TryFitToContent();
        Canvas.Invalidate();
    }

    private bool _fitDone;

    /// <summary>Подгоняет масштаб так, чтобы вся ширина графа помещалась в канвас.</summary>
    private void TryFitToContent()
    {
        if (_fitDone || Canvas.ActualWidth <= 0 || _routes.Count == 0) return;
        float contentW = DestX + DestW + 90;
        float z = Math.Clamp((float)(Canvas.ActualWidth - 16) / contentW, 0.05f, 1.0f);
        _zoom = z;
        _pan = new Vector2(8, 8);
        _fitDone = true;
        Canvas.Invalidate();
    }

    public void InvalidateGraph() => Canvas.Invalidate();

    // ---------- ресурсы ----------

    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        _fmtTitle ??= new CanvasTextFormat { FontSize = 13, FontFamily = "Segoe UI", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, WordWrapping = CanvasWordWrapping.NoWrap };
        _fmtPath ??= new CanvasTextFormat { FontSize = 11, FontFamily = "Segoe UI", WordWrapping = CanvasWordWrapping.NoWrap };
        _fmtBadge ??= new CanvasTextFormat { FontSize = 10, FontFamily = "Segoe UI", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, WordWrapping = CanvasWordWrapping.NoWrap };
        _fmtGlyph ??= new CanvasTextFormat { FontSize = 13, FontFamily = "Segoe UI Symbol", WordWrapping = CanvasWordWrapping.NoWrap };
    }

    private CanvasTextLayout Text(string text, CanvasTextFormat fmt, float width)
    {
        var key = (text, width);
        if (!_textCache.TryGetValue(key, out var layout))
        {
            layout = new CanvasTextLayout(Canvas, text, fmt, width, 0);
            _textCache[key] = layout;
        }
        return layout;
    }

    // ---------- раскладка ----------

    /// <summary>Высота стека маршрута: его узел-имя + ветви назначений.</summary>
    private static float RouteStackH(Route r) => r.IsCollapsed
        ? 74
        : MathF.Max(MinBlockH, r.Destinations.Count * DestRowH + 24);

    private const float RoutePad = 26;

    /// <summary>Высота блока группы: все маршруты с ОДНИМ источником рисуются вокруг одного узла-источника.</summary>
    private static float GroupHeight(IReadOnlyList<Route> group)
    {
        float stacks = group.Sum(RouteStackH) + (group.Count - 1) * RoutePad;
        return MathF.Max(MinBlockH + 10, stacks);
    }

    private const float GroupTitleH = 30;
    private const float PathPad = 12;       // внутренний отступ контура пути
    private const float CollapsedGroupH = 56;

    /// <summary>Высота блока пути (группы маршрутов с общим контуром).</summary>
    private static float PathGroupHeight(IReadOnlyList<Route> members)
    {
        var clusters = members.GroupBy(SourceKey, StringComparer.OrdinalIgnoreCase).ToList();
        float inner = clusters.Sum(c => GroupHeight(c.ToList())) + (clusters.Count - 1) * 10;
        return PathPad + GroupTitleH + inner + PathPad;
    }

    private static string SourceKey(Route r) => r.SourcePath.TrimEnd('\\');

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        ds.Clear(Color.FromArgb(255, 250, 251, 253));
        ds.Transform = Matrix3x2.CreateScale(_zoom) * Matrix3x2.CreateTranslation(_pan);

        _hits.Clear();
        _blockRects.Clear();

        // --- собираем блоки в натуральном порядке: пути сверху, затем обычные группы по источникам ---
        var blocks = new List<Block>();
        foreach (var g in _routes.Where(r => !string.IsNullOrEmpty(r.GroupName))
                                 .GroupBy(r => r.GroupName!, StringComparer.OrdinalIgnoreCase)
                                 .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var members = g.ToList();
            bool collapsed = GroupCollapsed(g.Key);
            blocks.Add(new Block("G:" + g.Key,
                                 collapsed ? CollapsedGroupH : PathGroupHeight(members),
                                 IsPath: true, collapsed, members));
        }
        foreach (var g in _routes.Where(r => string.IsNullOrEmpty(r.GroupName))
                                 .GroupBy(SourceKey, StringComparer.OrdinalIgnoreCase))
        {
            var members = g.ToList();
            blocks.Add(new Block("S:" + SourceKey(members[0]), GroupHeight(members),
                                 IsPath: false, Collapsed: false, members));
        }

        // --- назначаем Y: сохранённая позиция пользователя, с уплотнением (без наложений) ---
        var positions = new List<(Block Block, float Y)>();
        float y = 40;
        foreach (var b in blocks)
        {
            float by = y;
            if (_layout.TryGetValue(b.Key, out var saved)) by = Math.Max((float)saved, y);
            if (b.Key == _dragBlockKey) by += _dragDeltaY; // живой перенос за курсором
            positions.Add((b, by));
            y = by + b.H + BlockPad;
        }
        _drawnBlocks = positions;

        // Фоновая сетка (точки) — ограничена контентом
        float contentH = positions.Count > 0
            ? positions[^1].Y + positions[^1].Block.H + 40
            : 80;
        for (float gx = 0; gx < DestX + DestW + 120; gx += 40)
            for (float gy = 0; gy < contentH; gy += 40)
                ds.FillCircle(gx, gy, 1.2f, GridDot);

        // --- рисуем блоки и запоминаем их зоны для drag&drop ---
        foreach (var (b, by) in positions)
        {
            DrawBlock(ds, b, by);
            _blockRects.Add((new Rect(SourceX - 16, by, DestX + DestW + 32 - SourceX, b.H), b.Key));
        }
    }

    private void DrawBlock(CanvasDrawingSession ds, Block b, float y)
    {
        if (b.IsPath)
        {
            if (b.Collapsed) DrawCollapsedGroup(ds, b.Key[2..], b.Members, y);
            else DrawPathContour(ds, b.Key[2..], b.Members, y);
        }
        else
        {
            DrawGroup(ds, b.Members, y);
        }
    }

    /// <summary>Свёрнутый путь: одна компактная плашка с названием и счётчиком.</summary>
    private void DrawCollapsedGroup(CanvasDrawingSession ds, string name, List<Route> members, float y)
    {
        int withUpdates = members.Count(r => r.HasUpdates);
        byte alpha = members.All(r => r.IsArchived) ? (byte)110 : (byte)255;
        float w = DestX + DestW + 16 - (SourceX - 16);
        var rect = new Rect(SourceX - 16, y, w, CollapsedGroupH - 14);

        ds.FillRoundedRectangle(rect, 12, 12, WithAlpha(Color.FromArgb(255, 237, 240, 250), alpha));
        ds.DrawRoundedRectangle(rect, 12, 12, WithAlpha(Color.FromArgb(255, 150, 165, 220), alpha), 1.5f);
        string text = $"▸  {Shorten(name, 44)}  —  {members.Count} {(members.Count == 1 ? "маршрут" : "маршрута")}";
        if (withUpdates > 0) text += $"  •  обновлений: {withUpdates}";
        ds.DrawTextLayout(Text(text, _fmtTitle!, w - 40), SourceX, y + 12,
                          WithAlpha(Color.FromArgb(255, 60, 70, 110), alpha));
        _hits.Add(new NodeHit(rect, members[0], null, NodeKind.GroupToggle, name));
    }

    /// <summary>Контур пути: общая рамка с названием, внутри — обычные блоки по источникам.</summary>
    private void DrawPathContour(CanvasDrawingSession ds, string name, List<Route> members, float y)
    {
        float h = PathGroupHeight(members);
        float x = SourceX - 16, w = DestX + DestW + 16 - x;

        ds.FillRoundedRectangle(new Rect(x, y, w, h), 14, 14, Color.FromArgb(255, 242, 245, 255));
        ds.DrawRoundedRectangle(new Rect(x, y, w, h), 14, 14, Accent, 2f);

        // заголовок пути + зона клика для сворачивания
        ds.DrawTextLayout(Text($"▼  {Shorten(name, 50)}", _fmtTitle!, w - 40), SourceX, y + 8, AccentDark);
        _hits.Add(new NodeHit(new Rect(x, y, w, GroupTitleH + 2), members[0], null, NodeKind.GroupToggle, name));

        float inner = y + PathPad + GroupTitleH;
        foreach (var cluster in members.GroupBy(SourceKey, StringComparer.OrdinalIgnoreCase))
        {
            var group = cluster.ToList();
            DrawGroup(ds, group, inner);
            inner += GroupHeight(group) + 10;
        }
    }

    private static string Shorten(string path, int max)
    {
        if (path.Length <= max) return path;
        int keep = max - 3;
        int head = keep / 2, tail = keep - head;
        return path[..head] + "..." + path[^tail..];
    }

    /// <summary>
    /// Рисует блок: один общий узел-источник слева, из него веером — узлы-названия маршрутов,
    /// из каждого названия — ветви назначений. Несколько маршрутов с одной папкой-источником
    /// сходятся в одной точке — это и есть «все пути из одного места».
    /// </summary>
    private void DrawGroup(CanvasDrawingSession ds, List<Route> group, float blockY)
    {
        float h = GroupHeight(group);
        float blockCenterY = blockY + h / 2;

        bool anyArchived = group.All(r => r.IsArchived);
        byte alpha = anyArchived ? (byte)110 : (byte)255;

        // Источник недоступен? (по результатам проверки, либо прямой probing, если ещё не проверяли)
        bool sourceMissing = group.Any(r => r.Destinations.Count == 0
            ? !System.IO.Directory.Exists(r.SourcePath)
            : r.Destinations.Any(d => d.Diff?.SourceMissing == true));

        bool groupSelected = SelectedRoute is not null
                          && group.Any(r => ReferenceEquals(r, SelectedRoute));

        // Палитра: обычная либо «тревожная» (красная)
        var srcFill = sourceMissing ? Color.FromArgb(255, 253, 231, 233) : SourceFill;
        var srcBorder = sourceMissing ? ErrorRed : (groupSelected ? AccentDark : Accent);

        // --- общий узел-источник ---
        float srcY = blockCenterY - NodeH / 2;
        var srcRect = new Rect(SourceX, srcY, SourceW, NodeH);
        ds.FillRoundedRectangle(srcRect, 8, 8, WithAlpha(srcFill, alpha));
        ds.DrawRoundedRectangle(srcRect, 8, 8, WithAlpha(srcBorder, alpha), groupSelected ? 2.5f : 1.5f);
        ds.DrawTextLayout(Text("📁 " + Shorten(group[0].SourcePath, 38), _fmtPath!, SourceW - 24), SourceX + 12, srcY + 12, WithAlpha(TextGray, alpha));
        string srcSub = group.Count == 1 ? "1 маршрут" : $"{group.Count} маршрута/маршрутов";
        if (sourceMissing) srcSub = "источник не найден";
        ds.DrawTextLayout(Text(srcSub, _fmtPath!, SourceW - 24), SourceX + 26, srcY + 30, WithAlpha(sourceMissing ? ErrorRed : TextDark, alpha));
        _hits.Add(new NodeHit(srcRect, group[0], null, NodeKind.Source));

        // --- стеки маршрутов внутри блока ---
        float totalStacks = group.Sum(RouteStackH) + (group.Count - 1) * RoutePad;
        float stackY = blockY + (h - totalStacks) / 2;

        foreach (var route in group)
        {
            float stackH = RouteStackH(route);
            float nameCy = stackY + stackH / 2;
            DrawEdge(ds, SourceX + SourceW, blockCenterY, NameX, nameCy, alpha);
            DrawRouteStack(ds, route, nameCy, stackY, stackH, sourceMissing, alpha);
            stackY += stackH + RoutePad;
        }
    }

    /// <summary>Один маршрут: узел-название посередине и его ветви назначений справа.
    /// Свёрнутый маршрут рисуется только узлом-названием с маркером ▸.</summary>
    private void DrawRouteStack(CanvasDrawingSession ds, Route route, float nameCy, float stackY, float stackH,
                                bool sourceMissing, byte alpha)
    {
        bool selected = ReferenceEquals(route, SelectedRoute);
        var nameFill = sourceMissing ? ErrorRed : Accent;

        // --- имя маршрута ---
        float nameY = nameCy - NodeH / 2 - 4;
        var nameRect = new Rect(NameX, nameY, NameW, NodeH + 8);
        ds.FillRoundedRectangle(nameRect, 10, 10, WithAlpha(nameFill, alpha));
        ds.DrawRoundedRectangle(nameRect, 10, 10, WithAlpha(selected ? AccentDark : nameFill, alpha), selected ? 2.5f : 1f);

        if (route.IsCollapsed)
        {
            ds.DrawTextLayout(Text("▸ " + Shorten(route.Name, 26), _fmtTitle!, NameW - 40), NameX + 12, nameY + 12,
                              Color.FromArgb(alpha, 255, 255, 255));
            string sub = sourceMissing ? "источник не найден"
                       : $"свёрнут • {route.Destinations.Count} {(route.Destinations.Count == 1 ? "папка" : "папки")}"
                         + (route.HasUpdates ? " • есть обновления" : "");
            ds.DrawTextLayout(Text(sub, _fmtBadge!, NameW - 24), NameX + 12, nameY + 34,
                              Color.FromArgb((byte)(alpha == 255 ? 200 : 110), 255, 255, 255));
            _hits.Add(new NodeHit(nameRect, route, null, NodeKind.Name));

            // шеврон сворачивания/разворачивания у правого края узла
            var tgl = new Rect(NameX + NameW - 26, nameY + 10, 22, NodeH - 4);
            ds.DrawTextLayout(Text("–", _fmtGlyph!, 20), NameX + NameW - 21, nameY + 14,
                              Color.FromArgb(alpha, 255, 255, 255));
            _hits.Add(new NodeHit(tgl, route, null, NodeKind.RouteToggle));
            return;
        }

        ds.DrawTextLayout(Text(Shorten(route.Name, 30), _fmtTitle!, NameW - 24), NameX + 12, nameY + 12, Color.FromArgb(alpha, 255, 255, 255));
        string sub2 = sourceMissing ? "источник не найден"
                   : route.Destinations.Count == 0 ? "нет ветвей"
                   : $"{route.Destinations.Count} {(route.Destinations.Count == 1 ? "папка" : "папки")}";
        if (!sourceMissing && route.HasUpdates) sub2 += "  •  есть обновления";
        ds.DrawTextLayout(Text(sub2, _fmtBadge!, NameW - 24), NameX + 12, nameY + 34, Color.FromArgb((byte)(alpha == 255 ? 200 : 110), 255, 255, 255));
        _hits.Add(new NodeHit(nameRect, route, null, NodeKind.Name));

        // --- назначения ---
        if (route.Destinations.Count == 0)
        {
            // заглушка «нет ветвей» — серый пунктирный стуб
            ds.DrawTextLayout(Text("— нет ветвей —", _fmtPath!, 160), DestX, nameCy - 8, WithAlpha(TextGray, alpha));
            return;
        }

        // шеврон сворачивания маршрута
        var toggle = new Rect(NameX + NameW - 26, nameY + 10, 22, NodeH - 4);
        ds.DrawTextLayout(Text("▾", _fmtGlyph!, 20), NameX + NameW - 21, nameY + 14,
                          Color.FromArgb((byte)(alpha == 255 ? 220 : 110), 255, 255, 255));
        _hits.Add(new NodeHit(toggle, route, null, NodeKind.RouteToggle));

        float destTop = nameCy - (route.Destinations.Count * DestRowH) / 2 + DestRowH / 2;
        for (int i = 0; i < route.Destinations.Count; i++)
        {
            var dest = route.Destinations[i];
            float cy = destTop + i * DestRowH;
            float dy = cy - NodeH / 2;
            var destRect = new Rect(DestX, dy, DestW, NodeH);

            if (dest.IsConserved)
            {
                // законсервировано: пунктирный контур, «холодное» состояние, без бейджей
                ds.FillRoundedRectangle(destRect, 8, 8, WithAlpha(Color.FromArgb(255, 238, 241, 247), alpha));
                using var dash = new Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle
                    { DashStyle = Microsoft.Graphics.Canvas.Geometry.CanvasDashStyle.Dash };
                ds.DrawRoundedRectangle(destRect, 8, 8,
                                        WithAlpha(Color.FromArgb(255, 140, 155, 185), alpha), 1.5f, dash);
                ds.DrawTextLayout(Text("📁 " + Shorten(dest.DestPath, 38), _fmtPath!, DestW - 24), DestX + 12, dy + 12,
                                  WithAlpha(TextGray, alpha));
                ds.DrawTextLayout(Text("❆ в консервации — проверка отключена", _fmtBadge!, DestW - 24), DestX + 26, dy + 32,
                                  WithAlpha(Color.FromArgb(255, 120, 140, 175), alpha));
                _hits.Add(new NodeHit(destRect, route, dest, NodeKind.Dest));
                DrawEdge(ds, NameX + NameW, nameCy + 6, DestX, cy, alpha);
                continue;
            }

            ds.FillRoundedRectangle(destRect, 8, 8, WithAlpha(DestFill, alpha));
            ds.DrawRoundedRectangle(destRect, 8, 8, WithAlpha(DestBorder, alpha), 1.5f);
            ds.DrawTextLayout(Text("📁 " + Shorten(dest.DestPath, 38), _fmtPath!, DestW - 24), DestX + 12, dy + 12, WithAlpha(TextGray, alpha));
            string state;
            Color stateColor;
            if (dest.Diff is null)
            {
                state = $"синхр. {dest.LastSyncUtc.ToLocalTime():dd.MM.yyyy HH:mm}";
                stateColor = TextGray;
            }
            else if (dest.HasConflicts)
            {
                state = $"⚠ конфликт: {dest.Conflicts.Count} файл(а) правились с обеих сторон";
                stateColor = ErrorRed;
            }
            else if (dest.Diff.SourceMissing)
            {
                state = "источник не найден";
                stateColor = ErrorRed;
            }
            else if (dest.Diff.Changed)
            {
                state = "источник: " + dest.Diff.Describe();
                stateColor = UpdateColor;
            }
            else if (dest.DestDiff?.Changed == true)
            {
                state = "наши файлы: " + dest.DestDiff.Describe();
                stateColor = ErrorRed;
            }
            else
            {
                state = "актуально";
                stateColor = Color.FromArgb(255, 60, 160, 90);
            }
            ds.DrawTextLayout(Text(state, _fmtBadge!, DestW - 24), DestX + 26, dy + 32, WithAlpha(stateColor, alpha));
            _hits.Add(new NodeHit(destRect, route, dest, NodeKind.Dest));

            // ребро имя -> назначение
            DrawEdge(ds, NameX + NameW, nameCy + 6, DestX, cy, alpha);

            // бейдж обновления над назначением: ↻ — изменился источник, ≠ — изменился получатель
            if (dest.HasUpdates)
            {
                bool sourceSide = dest.Diff?.Changed == true;
                var badgeColor = sourceSide ? UpdateColor : ErrorRed;
                string glyph = sourceSide ? "↻" : "≠";
                float bx = DestX + DestW - 4, by = dy - 4;
                ds.FillCircle(bx, by, 13, WithAlpha(badgeColor, alpha));
                ds.DrawCircle(bx, by, 13, Color.FromArgb(alpha, 255, 255, 255), 1.5f);
                ds.DrawTextLayout(Text(glyph, _fmtGlyph!, 20), bx - 6, by - 8, Color.FromArgb(alpha, 255, 255, 255));
                _hits.Add(new NodeHit(new Rect(bx - 16, by - 16, 32, 32), route, dest, NodeKind.UpdateBadge));
            }
        }
    }

    private static void DrawEdge(CanvasDrawingSession ds, float x1, float y1, float x2, float y2, byte alpha)
    {
        var color = WithAlpha(EdgeColor, alpha);
        float mx = (x1 + x2) / 2;
        using var path = new Microsoft.Graphics.Canvas.Geometry.CanvasPathBuilder(ds);
        path.BeginFigure(x1, y1);
        path.AddCubicBezier(new Vector2(mx, y1), new Vector2(mx, y2), new Vector2(x2 - 10, y2));
        path.EndFigure(Microsoft.Graphics.Canvas.Geometry.CanvasFigureLoop.Open);
        using var geo = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePath(path);
        ds.DrawGeometry(geo, color, 2f);

        // стрелка
        ds.DrawLine(x2 - 10, y2, x2 - 18, y2 - 5, color, 2f);
        ds.DrawLine(x2 - 10, y2, x2 - 18, y2 + 5, color, 2f);
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb((byte)(c.A * a / 255), c.R, c.G, c.B);

    // ---------- ввод ----------

    private NodeHit? HitTest(Point p)
    {
        var v = ScreenToWorld(p);
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].Rect.Contains(v))
                return _hits[i];
        return null;
    }

    private Point ScreenToWorld(Point p) => new((p.X - _pan.X) / _zoom, (p.Y - _pan.Y) / _zoom);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Canvas);
        var pos = e.GetCurrentPoint(Canvas).Position;
        _lastPointer = pos;

        var hit = HitTest(pos);
        if (hit is not null)
        {
            switch (hit.Kind)
            {
                case NodeKind.UpdateBadge:
                    UpdateRequested?.Invoke(hit.Route, hit.Dest!);
                    break;
                case NodeKind.RouteToggle:
                    RouteCollapseToggled?.Invoke(hit.Route);
                    break;
                case NodeKind.GroupToggle:
                    GroupCollapseToggled?.Invoke(hit.GroupName ?? string.Empty);
                    break;
                default:
                    SelectedRoute = hit.Route;
                    RouteSelected?.Invoke(hit.Route);
                    Canvas.Invalidate();
                    break;
            }
            return;
        }

        SelectedRoute = null;
        RouteSelected?.Invoke(null!);

        // перенос блока: захват за пустое место внутри блока (не за узел)
        var wpos = ScreenToWorld(pos);
        for (int i = _blockRects.Count - 1; i >= 0; i--)
        {
            if (_blockRects[i].Rect.Contains(wpos))
            {
                _blockDragging = true;
                _dragBlockKey = _blockRects[i].Key;
                _dragDeltaY = 0;
                _lastPointer = pos;
                Canvas.CapturePointer(e.Pointer);
                Canvas.Invalidate();
                return;
            }
        }

        _isPanning = true;
        Canvas.CapturePointer(e.Pointer);
        Canvas.Invalidate();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(Canvas).Position;
        if (_blockDragging)
        {
            _dragDeltaY += (float)((pos.Y - _lastPointer.Y) / _zoom); // мировые единицы
            _lastPointer = pos;
            Canvas.Invalidate();
            return;
        }
        if (!_isPanning) return;
        _pan = new Vector2((float)(_pan.X + pos.X - _lastPointer.X), (float)(_pan.Y + pos.Y - _lastPointer.Y));
        _lastPointer = pos;
        Canvas.Invalidate();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_blockDragging)
        {
            _blockDragging = false;
            if (_dragBlockKey is not null && _drawnBlocks is not null)
            {
                // запоминаем конечную позицию (с учётом уплотнения она могла слегка съехать)
                var entry = _drawnBlocks.FirstOrDefault(p => p.Block.Key == _dragBlockKey);
                if (entry.Block is not null)
                    LayoutChanged?.Invoke(_dragBlockKey, entry.Y);
            }
            _dragBlockKey = null;
            _dragDeltaY = 0;
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
        var props = point.Properties;
        if (props.IsHorizontalMouseWheel) return;

        float oldZoom = _zoom;
        float factor = props.MouseWheelDelta > 0 ? 1.12f : 1 / 1.12f;
        float newZoom = Math.Clamp(oldZoom * factor, 0.08f, 4.0f);

        var pos = point.Position;
        // масштабирование от курсора: мировая точка под курсором не сдвигается
        var world = ScreenToWorld(pos);
        _pan = new Vector2((float)(pos.X - world.X * newZoom), (float)(pos.Y - world.Y * newZoom));
        _zoom = newZoom;
        Canvas.Invalidate();
        e.Handled = true;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var hit = HitTest(e.GetPosition(Canvas));
        if (hit is null) return;
        string? path = hit.Kind switch
        {
            NodeKind.Source => hit.Route.SourcePath,
            NodeKind.Dest => hit.Dest!.DestPath,
            _ => null
        };
        if (path is not null) FolderOpenRequested?.Invoke(path);
    }

    /// <summary>ПКМ по ветви назначения — консервация/вскрытие (без проверки целостности).</summary>
    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var hit = HitTest(e.GetPosition(Canvas));
        if (hit?.Kind != NodeKind.Dest || hit.Dest is null) return;

        var route = hit.Route;
        var dest = hit.Dest;
        var flyout = new MenuFlyout();
        var item = new MenuFlyoutItem
        {
            Text = dest.IsConserved ? "Вскрыть — возобновить проверку" : "Законсервировать — не проверять",
            Icon = new FontIcon { Glyph = dest.IsConserved ? "\uE7C3" : "❆" }
        };
        item.Click += (_, _) => ConservationToggled?.Invoke(route, dest);
        flyout.Items.Add(item);
        flyout.ShowAt(Canvas, e.GetPosition(Canvas));
        e.Handled = true;
    }
}
