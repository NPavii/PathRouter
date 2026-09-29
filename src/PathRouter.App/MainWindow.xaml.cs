using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using PathRouter.Core;
using System.Collections.Generic;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace PathRouter.App;

public sealed partial class MainWindow : Window
{
    private RouteRepository _repo;
    private RouteService _svc;

    // Полный список маршрутов (включая архивные/скрытые) — для watcher'ов и фоновой проверки.
    // Фильтрованный список для отображения — _routes.
    private List<Route> _allRoutes = new();
    private List<Route> _routes = new();
    private List<string> _pendingItems = new();
    private string? _pendingDestPath;
    private Route? _selectedRoute;

    // ---------- фоновое наблюдение ----------
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _dirtyLock = new();
    private readonly HashSet<string> _dirtyRouteIds = new();
    private readonly System.Threading.Timer _dirtyTimer;
    private readonly System.Threading.Timer _sweepTimer;
    private readonly Microsoft.UI.Xaml.DispatcherTimer _fileSearchTimer = new()
        { Interval = TimeSpan.FromMilliseconds(400) };
    private volatile bool _closed;
    private volatile bool _operationRunning;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Караван";

        _repo = new RouteRepository();
        _svc = new RouteService(_repo);

        Graph.RouteSelected += OnGraphRouteSelected;
        Graph.UpdateRequested += OnGraphUpdateRequested;
        Graph.FolderOpenRequested += OpenFolder;
        Graph.RouteCollapseToggled += OnGraphRouteCollapse;
        Graph.GroupCollapseToggled += name => ToggleGroup(name);
        Graph.ConservationToggled += OnToggleConservation;
        Graph.NoteEditRequested += OnNoteEditRequested;
        Graph.LayoutChanged += (key, y) =>
        {
            _repo.SaveLayoutPosition(key, y);
            Status("Раскладка сохранена. Перетаскивайте блоки за пустое место, чтобы навести порядок.");
        };

        ForceGraph.RouteSelected += OnGraphRouteSelected;
        ForceGraph.FolderOpenRequested += OpenFolder;
        ForceGraph.NodeMoved += (id, x, y) => _repo.SaveForceLayout(id, x, y);

        DropZone.DragOver += OnDropZoneDragOver;
        DropZone.Drop += OnDropZoneDrop;
        RouteNameBox.TextChanged += (_, _) => UpdatePutButton();
        Closed += OnWindowClosed;

        // Быстрая реакция на изменения в источниках (watcher -> dirty -> проверка каждые 5 с)
        _dirtyTimer = new System.Threading.Timer(ProcessDirtyRoutes, null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        // Страховочный полный проход: ловит изменения внутри папок-назначений и исчезнувшие источники
        _sweepTimer = new System.Threading.Timer(_ => RunSilentSweep(), null,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

        _fileSearchTimer.Tick += (_, _) => RunFileSearch();

        LoadRoutes();
        _ = CheckAllUpdatesAsync(silent: true);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _dirtyTimer.Dispose();
        _sweepTimer.Dispose();
        foreach (var w in _watchers.Values) { w.EnableRaisingEvents = false; w.Dispose(); }
        _watchers.Clear();
    }

    // ---------- загрузка ----------

    private void LoadRoutes()
    {
        // Мультивыделение списка — источник команд (объединить и пр.) — сохраняем между перестройками
        var prevSelectedIds = RoutesList.SelectedItems.OfType<Route>().Select(r => r.Id).ToHashSet();

        // Сохраняем состояние диффов с предыдущих проверок — оно живёт в памяти
        // (в БД хранятся только манифесты), а пересоздание объектов его стирало бы.
        var prevAll = _allRoutes;
        _allRoutes = _repo.GetRoutes(includeArchived: true, includeHidden: true);
        foreach (var fresh in _allRoutes)
        {
            var old = prevAll.FirstOrDefault(p => p.Id == fresh.Id);
            if (old is null) continue;
            fresh.HasUpdates = old.HasUpdates;
            foreach (var fd in fresh.Destinations)
            {
                var od = old.Destinations.FirstOrDefault(d => d.Id == fd.Id);
                if (od is not null)
                {
                    fd.Diff = od.Diff;
                    fd.DestDiff = od.DestDiff;
                    fd.Conflicts = od.Conflicts;
                }
            }
        }
        SyncWatchers();

        IEnumerable<Route> query = _allRoutes;
        if (ShowArchived.IsChecked != true) query = query.Where(r => !r.IsArchived);
        if (ShowHidden.IsChecked != true) query = query.Where(r => !r.IsHidden);

        var filter = SearchBox.Text.Trim();
        if (filter.Length > 0)
            query = query.Where(r => r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                  || r.SourcePath.Contains(filter, StringComparison.OrdinalIgnoreCase));

        _routes = query.ToList();

        // Состояние путей (свёрнутость) — на граф и в группировку списка
        var groupStates = _repo.GetGroups()
            .ToDictionary(g => g.Name, g => g.IsCollapsed, StringComparer.OrdinalIgnoreCase);
        Graph.SetGroupStates(groupStates);
        Graph.SetLayout(_repo.GetLayout());

        // Группировка списка: пути — с заголовками, развёрнутые показывают маршруты,
        // свёрнутые показывают только заголовок; без пути — каждый маршрут отдельно.
        var view = new List<RouteGroupView>();
        foreach (var g in _routes.Where(r => !string.IsNullOrEmpty(r.GroupName))
                                 .GroupBy(r => r.GroupName!, StringComparer.OrdinalIgnoreCase)
                                 .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            bool collapsed = groupStates.TryGetValue(g.Key, out var c) && c;
            var vg = new RouteGroupView { Key = g.Key, IsCollapsed = collapsed };
            if (!collapsed) vg.AddRange(g);
            view.Add(vg);
        }
        foreach (var r in _routes.Where(r => string.IsNullOrEmpty(r.GroupName)))
        {
            var single = new RouteGroupView { Key = "" };
            single.Add(r);
            view.Add(single);
        }

        var cvs = new CollectionViewSource { IsSourceGrouped = true, Source = view };
        RoutesList.ItemsSource = cvs.View;

        // восстанавливаем мультивыделение (без событий, чтобы не дёргать выбор)
        if (prevSelectedIds.Count > 0)
        {
            RoutesList.SelectionChanged -= OnRouteSelectionChanged;
            foreach (var rg in view)
                foreach (var r in rg)
                    if (prevSelectedIds.Contains(r.Id))
                        RoutesList.SelectedItems.Add(r);
            RoutesList.SelectionChanged += OnRouteSelectionChanged;
        }

        Graph.SetRoutes(_routes);
        ForceGraph.SetRoutes(_routes);
        ForceGraph.SetSavedLayout(_repo.GetForceLayout());

        if (_selectedRoute is not null)
            _selectedRoute = _routes.FirstOrDefault(r => r.Id == _selectedRoute.Id)
                          ?? _allRoutes.FirstOrDefault(r => r.Id == _selectedRoute.Id);
        UpdateSelectionUi();
        Status($"Маршрутов: {_routes.Count}");
    }

    // ---------- выбор ----------

    private void OnRouteSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // В Multiple-режиме SelectedItem — один из выделенных; для панели действий его достаточно
        _selectedRoute = RoutesList.SelectedItem as Route;
        UpdateSelectionUi();
    }

    private void OnGraphRouteSelected(Route route)
    {
        _selectedRoute = route;
        // Клик по графу — намеренное действие: подсвечиваем этот маршрут и в списке
        RoutesList.SelectionChanged -= OnRouteSelectionChanged;
        RoutesList.SelectedItem = route;
        RoutesList.SelectionChanged += OnRouteSelectionChanged;
        UpdateSelectionUi();
    }

    // ---------- утилиты ----------

    private void OnResetView(object sender, RoutedEventArgs e) => Graph.ResetView();

    // ---------- переключение видов графа ----------

    private bool _forceViewActive;

    private void OnToggleView(object sender, RoutedEventArgs e)
    {
        _forceViewActive = !_forceViewActive;
        Graph.Visibility = _forceViewActive ? Visibility.Collapsed : Visibility.Visible;
        ForceGraph.Visibility = _forceViewActive ? Visibility.Visible : Visibility.Collapsed;
        ViewToggleText.Text = _forceViewActive ? "Вид: Граф" : "Вид: Слои";
        if (_forceViewActive)
        {
            ForceGraph.SetRoutes(_routes); // физика досчитает с текущих позиций
            ForceGraph.SetSavedLayout(_repo.GetForceLayout());
            Status("Силовой граф: точки — папки, линии — ветви. Точки можно перетаскивать.");
        }
        else
        {
            Status("Слоистый вид: источники слева, пути — в общих контурах.");
        }
    }

    // ---------- сворачивание панели маршрутов ----------

    private bool _panelCollapsed;

    private void OnTogglePanel(object sender, RoutedEventArgs e)
    {
        _panelCollapsed = !_panelCollapsed;
        RoutesColumn.Width = _panelCollapsed ? new GridLength(0) : new GridLength(410);
        TogglePanelIcon.Glyph = _panelCollapsed ? "≫" : "≪";
        ToolTipService.SetToolTip(TogglePanelButton, _panelCollapsed
            ? "Развернуть панель маршрутов"
            : "Свернуть панель маршрутов");
        Graph.ResetView(); // пересчитать авто-масштаб под новую ширину канваса
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add("*");
        StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private void OpenFolder(string path)
    {
        try
        {
            if (!System.IO.Directory.Exists(path))
            {
                Status($"Папка не найдена: {path}");
                ShowError(new System.IO.DirectoryNotFoundException(
                    "Папка не найдена (возможно, диск отключён или папка переименована):\n" + path));
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            Status($"Открыта папка: {path}");
        }
        catch (Exception ex)
        {
            Status($"Не удалось открыть папку: {path}");
            ShowError(ex);
        }
    }

    private void SetBusy(bool busy, string? text = null)
    {
        _operationRunning = busy;
        BusyRing.IsActive = busy;
        PutButton.IsEnabled = !busy && _pendingItems.Count > 0
                            && !string.IsNullOrWhiteSpace(RouteNameBox.Text)
                            && _pendingDestPath is not null;
        if (text is not null) Status(text);
    }

    private void Status(string text) => StatusText.Text = text;

    private void ShowError(Exception ex)
    {
        StatusText.Text = "Ошибка: " + ex.Message;
        App.LogError("ShowError", ex);
        _ = new ContentDialog
        {
            Title = "Ошибка",
            Content = ex.Message,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot
        }.ShowAsync();
    }

    // ---------- тема ----------

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = AppTheme.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        LeftPanel.Background = AppTheme.Brush(AppTheme.PanelBg);
        RouteActionsBorder.Background = AppTheme.Brush(AppTheme.ActionsBg);
        DividerRect.Fill = AppTheme.Brush(AppTheme.Divider);
        DropZone.Background = AppTheme.Brush(AppTheme.DropZoneBg);
        DropZone.BorderBrush = AppTheme.Brush(AppTheme.DropZoneBrush);
        Graph.InvalidateGraph();
        ForceGraph.InvalidateGraph();
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        AppTheme.Toggle();
        ApplyTheme();
        // диапазоны и значения слайдеров физики — в коде: в XAML парсер Slider падает,
        // если Minimum/Maximum/Value попадают не в тот порядок
        PhysRepel.Minimum = 2000; PhysRepel.Maximum = 30000; PhysRepel.StepFrequency = 500;
        PhysRepel.Value = 9000;
        PhysLink.Minimum = 60; PhysLink.Maximum = 400; PhysLink.StepFrequency = 10;
        PhysLink.Value = 150;
        PhysGravity.Minimum = 0; PhysGravity.Maximum = 80; PhysGravity.StepFrequency = 1;
        PhysGravity.Value = 15;
        Status(AppTheme.IsDark ? "Тёмная тема." : "Светлая тема.");
    }

    // ---------- физика силового вида ----------

    private void OnPhysicsChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        ForceGraph.Repulsion = (float)PhysRepel.Value;
        ForceGraph.RestLength = (float)PhysLink.Value;
        ForceGraph.Gravity = (float)PhysGravity.Value / 1000f;
    }

    private void OnShuffleForce(object sender, RoutedEventArgs e)
    {
        ForceGraph.Shuffle();
        Status("Точки разбросаны заново — физика пересобирает картину.");
    }

}

/// <summary>Группа маршрутов для отображения: Key — название пути, сама группа — список маршрутов.
/// Пустой Key означает «без пути» (заголовок скрывается конвертером).</summary>
public sealed class RouteGroupView : List<Route>
{
    public string Key { get; set; } = string.Empty;
    public bool IsCollapsed { get; set; }
}

/// <summary>Мини-хелпер для конструирования UI в коде.</summary>
internal static class UiHelpers
{
    public static T Also<T>(this T self, Action<T> block) where T : notnull
    {
        block(self);
        return self;
    }
}
