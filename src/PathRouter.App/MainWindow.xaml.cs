using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PathRouter.Core;
using System.Collections.Generic;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PathRouter.App;

public sealed partial class MainWindow : Window
{
    private readonly RouteRepository _repo;
    private readonly RouteService _svc;

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
        _allRoutes = _repo.GetRoutes(includeArchived: true, includeHidden: true);
        SyncWatchers();

        IEnumerable<Route> query = _allRoutes;
        if (ShowArchived.IsChecked != true) query = query.Where(r => !r.IsArchived);
        if (ShowHidden.IsChecked != true) query = query.Where(r => !r.IsHidden);

        var filter = SearchBox.Text.Trim();
        if (filter.Length > 0)
            query = query.Where(r => r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                  || r.SourcePath.Contains(filter, StringComparison.OrdinalIgnoreCase));

        _routes = query.ToList();
        RoutesList.ItemsSource = null;
        RoutesList.ItemsSource = _routes;
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

    /// <summary>Обновляет иконки статуса в списке без полной перезагрузки данных.</summary>
    private void RefreshListIcons()
    {
        RoutesList.ItemsSource = null;
        RoutesList.ItemsSource = _routes;
    }

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
            RoutesList.ItemsSource = null;
            RoutesList.ItemsSource = _routes;
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

    private static void OpenFolder(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowErrorStatic($"Не удалось открыть папку:\n{ex.Message}");
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

    private static void ShowErrorStatic(string message) =>
        System.Diagnostics.Debug.WriteLine(message);

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
}
