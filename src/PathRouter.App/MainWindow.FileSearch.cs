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
