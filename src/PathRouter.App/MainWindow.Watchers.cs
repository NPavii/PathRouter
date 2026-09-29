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

public sealed partial class MainWindow
{
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
        _svc.BeginPass();
        try
        {
            foreach (var route in dirty)
                _svc.CheckRoute(route);
        }
        finally { _svc.EndPass(); }
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
            _svc.BeginPass();
            try
            {
                foreach (var route in _allRoutes)
                    _svc.CheckRoute(route);
            }
            finally { _svc.EndPass(); }
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
        // ВНИМАНИЕ: не трогаем RoutesList.SelectedItem/SelectedItems здесь — это метод
        // вызывается из LoadRoutes на каждой фоновой проверке, и принудительный выбор
        // одного элемента уничтожал бы мультивыделение пользователя. Список и граф
        // синхронизируются через события выбора, а не через этот метод.

        Graph.SelectRoute(_selectedRoute);
        ForceGraph.SelectRoute(_selectedRoute);
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

}
