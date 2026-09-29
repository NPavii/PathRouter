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
