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

        Graph.SetRoutes(_routes);

        if (_selectedRoute is not null)
            _selectedRoute = _routes.FirstOrDefault(r => r.Id == _selectedRoute.Id)
                          ?? _allRoutes.FirstOrDefault(r => r.Id == _selectedRoute.Id);
        UpdateSelectionUi();
        Status($"Маршрутов: {_routes.Count}");
    }

    // ---------- фоновое наблюдение (watcher'ы + таймеры) ----------

    /// <summary>Создаёт watcher'ы на каждый уникальный существующий источник; лишние — отключает.</summary>
    private void SyncWatchers()
    {
        var wanted = _allRoutes.Select(r => r.SourcePath.TrimEnd('\\'))
            .Where(System.IO.Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var key in _watchers.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            try { _watchers[key].Dispose(); } catch { /* уже мёртв */ }
            _watchers.Remove(key);
        }

        foreach (var path in wanted)
            if (!_watchers.ContainsKey(path))
                _watchers[path] = CreateWatcher(path);
    }

    private FileSystemWatcher CreateWatcher(string path)
    {
        var w = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        w.Created += OnWatchedChanged;
        w.Changed += OnWatchedChanged;
        w.Deleted += OnWatchedChanged;
        w.Renamed += OnWatchedChanged;
        w.Error += (_, _) => MarkDirty(path);
        w.EnableRaisingEvents = true;
        return w;
    }

    private void OnWatchedChanged(object sender, FileSystemEventArgs e) => MarkDirty(((FileSystemWatcher)sender).Path);

    private void MarkDirty(string sourcePath)
    {
        lock (_dirtyLock)
            foreach (var r in _allRoutes)
                if (string.Equals(r.SourcePath.TrimEnd('\\'), sourcePath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    _dirtyRouteIds.Add(r.Id);
    }

    /// <summary>Проверяет только «грязные» маршруты (реакция на watcher ~5 с).</summary>
    private void ProcessDirtyRoutes(object? state)
    {
        if (_closed || _operationRunning) return;
        List<Route> dirty;
        lock (_dirtyLock)
        {
            if (_dirtyRouteIds.Count == 0) return;
            dirty = _allRoutes.Where(r => _dirtyRouteIds.Contains(r.Id)).ToList();
            _dirtyRouteIds.Clear();
        }
        foreach (var route in dirty)
            _svc.CheckRoute(route);
        DispatcherQueue.TryEnqueue(() =>
        {
            Graph.InvalidateGraph();
            RefreshListIcons();
        });
    }

    /// <summary>Полный тихий проход по всем маршрутам (раз в 60 с) — ловит правки внутри назначений.</summary>
    private void RunSilentSweep()
    {
        if (_closed || _operationRunning) return;
        try
        {
            foreach (var route in _allRoutes)
                _svc.CheckRoute(route);
            DispatcherQueue.TryEnqueue(() =>
            {
                Graph.InvalidateGraph();
                RefreshListIcons();
            });
        }
        catch (Exception ex) { App.LogError("RunSilentSweep", ex); }
    }

    /// <summary>Обновляет иконки статуса в списке (пересобирает сгруппированное представление).</summary>
    private void RefreshListIcons() => LoadRoutes();

    private void UpdateSelectionUi()
    {
        RoutesList.SelectionChanged -= OnRouteSelectionChanged;
        RoutesList.SelectedItem = _selectedRoute;
        RoutesList.SelectionChanged += OnRouteSelectionChanged;

        Graph.SelectRoute(_selectedRoute);
        bool has = _selectedRoute is not null;
        RouteActions.IsEnabled = has;
        if (_selectedRoute is not null)
        {
            SelectedRouteTitle.Text = _selectedRoute.Name;
            ArchiveButton.Content = _selectedRoute.IsArchived ? "Разархивировать" : "Архивировать";
            HideButton.Content = _selectedRoute.IsHidden ? "Показать" : "Скрыть";
        }
        else
        {
            SelectedRouteTitle.Text = "Маршрут не выбран";
        }
    }

    // ---------- создание маршрута ----------

    private void OnDropZoneDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Отпустите, чтобы запомнить начало пути";
    }

    private async void OnDropZoneDrop(object sender, DragEventArgs e)
    {
        var items = await e.DataView.GetStorageItemsAsync();
        _pendingItems = items.Select(i => i.Path).ToList();
        if (_pendingItems.Count == 0) return;

        try
        {
            string source = RouteService.DetermineSourcePath(_pendingItems);
            DropHint.Text = $"Источник: {source}  ({_pendingItems.Count} элемент.)";
            Status($"Начало пути запомнено: {source}");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        UpdatePutButton();
    }

    private async void OnPickSource(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder is null) return;
        _pendingItems = new List<string> { folder };
        DropHint.Text = $"Источник: {folder}";
        Status($"Начало пути запомнено: {folder}");
        UpdatePutButton();
    }

    private async void OnPickDest(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder is null) return;
        _pendingDestPath = folder;
        DestPathText.Text = folder;
        UpdatePutButton();
    }

    private void UpdatePutButton()
        => PutButton.IsEnabled = _pendingItems.Count > 0
                              && !string.IsNullOrWhiteSpace(RouteNameBox.Text)
                              && _pendingDestPath is not null;

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => LoadRoutes();
    private void OnFilterChanged(object sender, RoutedEventArgs e) => LoadRoutes();

    private async void OnPut(object sender, RoutedEventArgs e)
    {
        string name = RouteNameBox.Text.Trim();
        if (_pendingDestPath is null || _pendingItems.Count == 0) return;

        SetBusy(true, "Перемещаю файлы…");
        try
        {
            Route route;
            var items = _pendingItems;
            var destPath = _pendingDestPath!;
            route = await Task.Run(() =>
            {
                return _svc.CreateRoute(items, name, destPath);
            });

            _pendingItems = new();
            _pendingDestPath = null;
            RouteNameBox.Text = string.Empty;
            DropHint.Text = "Перетащите сюда файлы или папку";
            DestPathText.Text = "Выбрать папку назначения…";
            UpdatePutButton();

            _selectedRoute = route;
            LoadRoutes();
            Status($"Маршрут «{route.Name}» создан, файлы скопированы (источник сохранён).");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally { SetBusy(false); }
    }

    // ---------- выбор ----------

    private void OnRouteSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedRoute = RoutesList.SelectedItem as Route;
        UpdateSelectionUi();
    }

    private void OnGraphRouteSelected(Route route)
    {
        _selectedRoute = route;
        UpdateSelectionUi();
    }

    // ---------- действия с маршрутом ----------

    private async void OnAddDestination(object sender, RoutedEventArgs e)
    {
        if (_selectedRoute is null) return;
        var folder = await PickFolderAsync();
        if (folder is null) return;

        SetBusy(true, "Копирую файлы…");
        try
        {
            var route = _selectedRoute!;
            await Task.Run(() =>
            {
                _svc.AddDestination(route, folder);
            });
            RefreshSelectedRoute();
            Status($"Добавлена ветвь: {_selectedRoute!.Name} → {folder}");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async void OnUpdateAll(object sender, RoutedEventArgs e)
    {
        if (_selectedRoute is null) return;
        await UpdateDestinationsAsync(_selectedRoute, _selectedRoute.Destinations.Where(d => d.HasUpdates).ToList());
    }

    private async void OnGraphUpdateRequested(Route route, RouteDestination dest)
        => await UpdateDestinationsAsync(route, new List<RouteDestination> { dest });

    private async Task UpdateDestinationsAsync(Route route, List<RouteDestination> dests)
    {
        if (dests.Count == 0)
        {
            Status("Изменений нет — ветви актуальны.");
            return;
        }
        SetBusy(true, "Обновляю ветви…");
        try
        {
            // Защита от случайной потери данных: при синхронизации из назначения удаляются
            // только файлы, которые маршрут сам туда положил и которых больше нет в источнике.
            int willDelete = dests.Sum(d => d.Diff?.Deleted ?? 0);
            if (willDelete > 0)
            {
                var confirm = new ContentDialog
                {
                    Title = "При синхронизации файлы будут удалены",
                    Content = $"Из папок назначения будет удалено {willDelete} файл(ов), которых нет в источнике. Продолжить?",
                    PrimaryButtonText = "Удалить и обновить",
                    CloseButtonText = "Отмена",
                    XamlRoot = Content.XamlRoot,
                    DefaultButton = ContentDialogButton.Close
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                {
                    SetBusy(false);
                    Status("Обновление отменено.");
                    return;
                }
            }
            foreach (var dest in dests)
                _svc.UpdateDestination(route, dest);
            _svc.CheckRoute(route);
            Graph.InvalidateGraph();
            Status($"Обновлено ветвей: {dests.Count}");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private void OnOpenSource(object sender, RoutedEventArgs e)
    {
        if (_selectedRoute is not null) OpenFolder(_selectedRoute.SourcePath);
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (_selectedRoute is null) return;
        var box = new TextBox { Text = _selectedRoute.Name, Width = 320 };
        var dialog = new ContentDialog
        {
            Title = "Переименовать маршрут",
            Content = box,
            PrimaryButtonText = "Сохранить",
            CloseButtonText = "Отмена",
            XamlRoot = Content.XamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };
        box.SelectAll();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        string newName = box.Text.Trim();
        if (newName.Length == 0) return;
        _repo.RenameRoute(_selectedRoute.Id, newName);
        _selectedRoute.Name = newName;
        LoadRoutes();
        Status($"Маршрут переименован в «{newName}».");
    }

    private void OnToggleArchive(object sender, RoutedEventArgs e)
    {
        if (_selectedRoute is null) return;
        _repo.SetArchived(_selectedRoute.Id, !_selectedRoute.IsArchived);
        _selectedRoute.IsArchived = !_selectedRoute.IsArchived;
        LoadRoutes();
    }

    private void OnToggleHide(object sender, RoutedEventArgs e)
    {
        if (_selectedRoute is null) return;
        _repo.SetHidden(_selectedRoute.Id, !_selectedRoute.IsHidden);
        _selectedRoute.IsHidden = !_selectedRoute.IsHidden;
        LoadRoutes();
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_selectedRoute is null) return;
        var dialog = new ContentDialog
        {
            Title = "Удалить маршрут?",
            Content = $"«{_selectedRoute.Name}» будет удалён из графа. Файлы на диске не пострадают.",
            PrimaryButtonText = "Удалить",
            CloseButtonText = "Отмена",
            XamlRoot = Content.XamlRoot,
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _repo.DeleteRoute(_selectedRoute.Id);
        _selectedRoute = null;
        LoadRoutes();
        Status("Маршрут удалён.");
    }

    // ---------- проверка обновлений ----------

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
        => await CheckAllUpdatesAsync(silent: false);

    private async Task CheckAllUpdatesAsync(bool silent)
    {
        SetBusy(true, "Проверяю изменения…");
        try
        {
            var routes = _routes;
            await Task.Run(() =>
            {
                foreach (var route in routes)
                    _svc.CheckRoute(route);
            });
            Graph.InvalidateGraph();
            LoadRoutes();
            int withUpdates = routes.Count(r => r.HasUpdates);
            if (!silent)
                Status(withUpdates == 0 ? "Все маршруты актуальны." : $"Обновления доступны: {withUpdates} маршрут(ов).");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private void RefreshSelectedRoute()
    {
        if (_selectedRoute is null) { LoadRoutes(); return; }
        List<Route> fresh;
        fresh = _repo.GetRoutes(true, true);
        _selectedRoute = fresh.FirstOrDefault(r => r.Id == _selectedRoute.Id);
        LoadRoutes();
    }

    // ---------- утилиты ----------

    private void OnResetView(object sender, RoutedEventArgs e) => Graph.ResetView();

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

    // ---------- поиск файла по всем путям ----------

    private void OnFileSearchChanged(object sender, TextChangedEventArgs e)
    {
        _fileSearchTimer.Stop();
        FileResultsPanel.Visibility = Visibility.Collapsed;
        if (FileSearchBox.Text.Trim().Length >= 2) _fileSearchTimer.Start();
    }

    private async void RunFileSearch()
    {
        _fileSearchTimer.Stop();
        var query = FileSearchBox.Text.Trim();
        if (query.Length < 2) return;

        List<FileHit> hits;
        try { hits = await Task.Run(() => _repo.SearchFiles(query)); }
        catch (Exception ex) { App.LogError("RunFileSearch", ex); return; }

        if (!query.Equals(FileSearchBox.Text.Trim(), StringComparison.Ordinal)) return; // устарел ответ
        FileResultsList.ItemsSource = hits;
        FileResultsPanel.Visibility = hits.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (hits.Count == 0) Status($"Файл «{query}» ни в одном манифесте не найден.");
        else Status($"Копий найдено: {hits.Count}. Клик — показать маршрут, двойной — открыть папку.");
    }

    private void OnFileResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FileHit hit) return;
        var route = _allRoutes.FirstOrDefault(r => r.Id == hit.RouteId);
        if (route is null) return;

        // скрытый/архивный маршрут сначала делаем видимым, чтобы его можно было выбрать
        if (route.IsArchived) { ShowArchived.IsChecked = true; }
        if (route.IsHidden) { ShowHidden.IsChecked = true; }
        _selectedRoute = route;
        LoadRoutes();
        FileResultsPanel.Visibility = Visibility.Collapsed;
        Status($"«{hit.FileName}» лежит в «{hit.RouteName}» → {hit.DestPath}");
    }

    private void OnFileResultDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileHit hit)
            OpenFolder(hit.DestPath);
    }

    // ---------- пути (группы), сворачивание ----------

    private List<Route> SelectedRoutes =>
        RoutesList.SelectedItems.OfType<Route>().ToList();

    private void OnRoutesRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Route r) return;

        // Ctrl+ПКМ — добавить/убрать из выделения (галочки Multiple тоже работают)
        var keyState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        bool ctrl = (keyState & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        {
            if (RoutesList.SelectedItems.Contains(r)) RoutesList.SelectedItems.Remove(r);
            else RoutesList.SelectedItems.Add(r);
            e.Handled = true;
            return;
        }
        // ПКМ по невыделенному элементу выделяет его одного
        if (!RoutesList.SelectedItems.Contains(r))
            RoutesList.SelectedItem = r;
    }

    private void OnRouteMenuOpening(object sender, object e)
    {
        var selected = SelectedRoutes;
        var single = selected.Count == 1 ? selected[0] : null;
        MergeMenuItem.IsEnabled = selected.Count >= 2;
        CollapseRouteItem.IsEnabled = single is not null;
        CollapseRouteItem.Text = single?.IsCollapsed == true ? "Развернуть маршрут" : "Свернуть маршрут";
        var inGroup = selected.Where(r => r.GroupName is not null).ToList();
        CollapseGroupItem.IsEnabled = inGroup.Count > 0;
        CollapseGroupItem.Text = inGroup.Any(r => GroupCollapsed(r.GroupName!)) ? "Развернуть путь" : "Свернуть путь";
        UngroupItem.IsEnabled = inGroup.Count > 0;
    }

    private bool GroupCollapsed(string groupName)
    {
        var g = _repo.GetGroups().FirstOrDefault(x => string.Equals(x.Name, groupName, StringComparison.OrdinalIgnoreCase));
        return g?.IsCollapsed == true;
    }

    private void ToggleGroup(string groupName)
    {
        _repo.SetGroupCollapsed(groupName, !GroupCollapsed(groupName));
        LoadRoutes();
    }

    private void OnGraphRouteCollapse(Route route)
    {
        route.IsCollapsed = !route.IsCollapsed;
        _repo.SetRouteCollapsed(route.Id, route.IsCollapsed);
        LoadRoutes();
    }

    private async void OnMergeRoutes(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRoutes;
        if (selected.Count < 2)
        {
            Status("Отметьте несколько маршрутов галочками (или Ctrl+ПКМ), затем «Объединить в путь…».");
            return;
        }
        var box = new TextBox { Text = selected[0].GroupName ?? string.Empty, Width = 320 };
        var dialog = new ContentDialog
        {
            Title = $"Объединить {selected.Count} маршрутов в путь",
            Content = new StackPanel { Spacing = 8 }.Also(p =>
            {
                p.Children.Add(new TextBlock { Text = "Название пути (напр. «А» — маршруты станут А.1, А.2…):", TextWrapping = TextWrapping.Wrap });
                p.Children.Add(box);
            }),
            PrimaryButtonText = "Объединить",
            CloseButtonText = "Отмена",
            XamlRoot = Content.XamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };
        box.SelectAll();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var name = box.Text.Trim();
        if (name.Length == 0) return;
        _repo.SetGroup(selected.Select(r => r.Id), name);
        LoadRoutes();
        Status($"Объединено в путь «{name}»: {selected.Count} маршрутов.");
    }

    private void OnToggleRouteCollapse(object sender, RoutedEventArgs e)
    {
        var single = SelectedRoutes.FirstOrDefault();
        if (single is null) return;
        OnGraphRouteCollapse(single);
    }

    private void OnToggleGroupCollapse(object sender, RoutedEventArgs e)
    {
        var route = SelectedRoutes.FirstOrDefault(r => r.GroupName is not null);
        if (route?.GroupName is not null) ToggleGroup(route.GroupName);
    }

    private void OnUngroupSelected(object sender, RoutedEventArgs e)
    {
        var ids = SelectedRoutes.Where(r => r.GroupName is not null).Select(r => r.Id).ToList();
        if (ids.Count == 0) return;
        _repo.Ungroup(ids);
        LoadRoutes();
        Status("Маршруты убраны из пути.");
    }

    private void OnGroupHeaderCollapse(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RouteGroupView g && !string.IsNullOrEmpty(g.Key))
            ToggleGroup(g.Key);
    }

    private void OnGroupHeaderUngroup(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RouteGroupView g || string.IsNullOrEmpty(g.Key)) return;
        var ids = _allRoutes.Where(r => string.Equals(r.GroupName, g.Key, StringComparison.OrdinalIgnoreCase))
                            .Select(r => r.Id).ToList();
        if (ids.Count == 0) return;
        _repo.Ungroup(ids);
        LoadRoutes();
        Status($"Путь «{g.Key}» расформирован.");
    }

    // ---------- импорт / экспорт базы ----------

    private async void OnExportDb(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Экспортирую базу…");
        try
        {
            var picker = new FileSavePicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("База данных Каравана", new List<string> { ".db" });
            picker.SuggestedFileName = "karavan-routes";
            var file = await picker.PickSaveFileAsync();
            if (file is null) { SetBusy(false); return; }

            await Task.Run(() =>
            {
                _repo.Checkpoint(); // дожать WAL в основной файл перед копированием
                File.Copy(RouteRepository.DefaultDbPath, file.Path, overwrite: true);
            });
            Status($"База экспортирована: {file.Path}");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async void OnImportDb(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".db");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            if (!RouteRepository.IsValidDatabase(file.Path))
            {
                ShowError(new InvalidDataException("Выбранный файл не является базой Каравана (нет таблиц routes/destinations/manifest)."));
                return;
            }

            var confirm = new ContentDialog
            {
                Title = "Импорт базы",
                Content = "Текущие маршруты будут полностью заменены содержимым выбранного файла. Файлы на диске не пострадают. Продолжить?",
                PrimaryButtonText = "Импортировать",
                CloseButtonText = "Отмена",
                XamlRoot = Content.XamlRoot,
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            SetBusy(true, "Импортирую базу…");
            string? error = null;
            await Task.Run(() =>
            {
                try
                {
                    foreach (var w in _watchers.Values) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
                    _watchers.Clear();
                    lock (_dirtyLock) _dirtyRouteIds.Clear();
                    _repo.Dispose();
                    File.Copy(file.Path, RouteRepository.DefaultDbPath, overwrite: true);
                    _repo = new RouteRepository();
                    _svc = new RouteService(_repo);
                }
                catch (Exception ex) { error = ex.Message; }
            });
            if (error is not null)
            {
                ShowError(new IOException("Импорт не удался: " + error));
                return;
            }
            _selectedRoute = null;
            LoadRoutes();
            _ = CheckAllUpdatesAsync(silent: true);
            Status("База импортирована.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
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
