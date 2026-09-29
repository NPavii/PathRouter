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
                _svc.BeginPass(); // каждая папка сканируется один раз на проход
                try
                {
                    foreach (var route in routes)
                        _svc.CheckRoute(route);
                }
                finally { _svc.EndPass(); }
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

    // ---------- пути (группы), сворачивание ----------

    private List<Route> SelectedRoutes =>
        RoutesList.SelectedItems.OfType<Route>().ToList();

    private void OnRoutesRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Route r) return;

        // Ctrl+ПКМ — добавить/убрать из выделения (галочки Multiple тоже работают)
        var keyState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        bool ctrl = (keyState & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (ctrl)
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
        RenameGroupItem.IsEnabled = inGroup.Count > 0;
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

    /// <summary>Консервация/вскрытие ветви: проверка целостности и синхронизация вкл/выкл.</summary>
    private void OnToggleConservation(Route route, RouteDestination dest)
    {
        dest.IsConserved = !dest.IsConserved;
        _repo.SetConserved(dest.Id, dest.IsConserved);
        if (dest.IsConserved)
        {
            dest.Diff = null;
            dest.DestDiff = null;
        }
        _svc.CheckRoute(route);
        LoadRoutes();
        Status(dest.IsConserved
            ? $"Ветвь «{route.Name} → {dest.DestPath}» законсервирована: проверка и синхронизация отключены."
            : $"Ветвь «{route.Name} → {dest.DestPath}» вскрыта: проверка возобновлена.");
    }

    /// <summary>Заметка к ветви: многострочный текст, иконка ✎ на узле, hover — всплывающий просмотр.</summary>
    private async void OnNoteEditRequested(Route route, RouteDestination dest)
    {
        var box = new TextBox
        {
            Text = dest.Note ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 150,
            Width = 380,
            PlaceholderText = "Например: «сюда класть только релизные версии, договорились с Ивановым»…"
        };
        var dialog = new ContentDialog
        {
            Title = $"Заметка к назначению «{route.Name}»",
            Content = box,
            PrimaryButtonText = "Сохранить",
            SecondaryButtonText = "Удалить заметку",
            CloseButtonText = "Отмена",
            XamlRoot = Content.XamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };
        box.SelectAll();
        var result = await dialog.ShowAsync();

        string? note = dest.Note;
        if (result == ContentDialogResult.Primary) note = box.Text.Trim();
        else if (result == ContentDialogResult.Secondary) note = null;
        else return; // отмена

        dest.Note = string.IsNullOrWhiteSpace(note) ? null : note;
        _repo.SetDestinationNote(dest.Id, dest.Note);
        LoadRoutes();
        Status(dest.Note is not null ? "Заметка сохранена. Наведите курсор на ✎ на узле, чтобы прочитать." : "Заметка удалена.");
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

    private void OnGroupHeaderRename(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RouteGroupView g && !string.IsNullOrEmpty(g.Key))
            _ = RenameGroupAsync(g.Key);
    }

    private void OnRenameGroupMenu(object sender, RoutedEventArgs e)
    {
        var name = SelectedRoutes.FirstOrDefault(r => r.GroupName is not null)?.GroupName;
        if (name is not null) _ = RenameGroupAsync(name);
    }

    /// <summary>Переименовать путь: переносит маршруты, состояние и раскладку.</summary>
    private async Task RenameGroupAsync(string oldName)
    {
        var box = new TextBox { Text = oldName, Width = 320 };
        var dialog = new ContentDialog
        {
            Title = "Переименовать путь",
            Content = box,
            PrimaryButtonText = "Сохранить",
            CloseButtonText = "Отмена",
            XamlRoot = Content.XamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };
        box.SelectAll();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var newName = box.Text.Trim();
        if (newName.Length == 0 || string.Equals(newName, oldName, StringComparison.Ordinal)) return;
        if (_repo.GetGroups().Any(g => string.Equals(g.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError(new InvalidOperationException($"Путь «{newName}» уже существует — выберите другое имя."));
            return;
        }
        _repo.RenameGroup(oldName, newName);
        LoadRoutes();
        Status($"Путь «{oldName}» переименован в «{newName}».");
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

    // ---------- действия с маршрутом ----------

    private async void OnAddDestination(object sender, RoutedEventArgs e)
    {
        if (_selectedRoute is null) return;
        var folder = await PickFolderAsync();
        if (folder is null) return;
        await AddDestinationToRouteAsync(_selectedRoute, folder);
    }

    /// <summary>Добавить ветвь в маршрут: скопировать содержимое источника в указанную папку.</summary>
    private async Task AddDestinationToRouteAsync(Route route, string folder)
    {
        SetBusy(true, "Копирую файлы…");
        try
        {
            await Task.Run(() => _svc.AddDestination(route, folder));
            RefreshSelectedRoute();
            Status($"Добавлена ветвь: {route.Name} → {folder}");
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
            int conflicts = dests.Sum(d => d.Conflicts.Count);
            if (willDelete > 0 || conflicts > 0)
            {
                string text = willDelete > 0
                    ? $"Из папок назначения будет удалено {willDelete} файл(ов), которых нет в источнике."
                    : "";
                if (conflicts > 0)
                    text += (text.Length > 0 ? "\n\n" : "")
                          + $"⚠ {conflicts} файл(а) правились с обеих сторон — версии получателя будут сохранены как «имя.конфликт-ДАТА» рядом с файлами.";
                var confirm = new ContentDialog
                {
                    Title = "При синхронизации будут изменены файлы",
                    Content = text + "\n\nПродолжить?",
                    PrimaryButtonText = "Обновить",
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

    // ---------- Drag&Drop из проводника на маршрут ----------

    private void OnRoutesDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Положить в новую ветвь этого маршрута";
    }

    /// <summary>Перетащили файлы/папку из проводника на маршрут: новая ветвь с копией источника.</summary>
    private async void OnRoutesDrop(object sender, DragEventArgs e)
    {
        var route = (e.OriginalSource as FrameworkElement)?.DataContext as Route ?? _selectedRoute;
        if (route is null)
        {
            Status("Отпустите на конкретном маршруте (или выберите маршрут и отпустите в списке).");
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count == 0) return;

        try
        {
            // Перетащили одну папку — она и есть назначение; файлы/несколько элементов — их общая папка
            var dest = items.Count == 1 && items[0] is StorageFolder f
                ? f.Path
                : RouteService.DetermineSourcePath(items.Select(i => i.Path).ToList());
            await AddDestinationToRouteAsync(route, dest);
        }
        catch (Exception ex) { ShowError(ex); }
    }
}
