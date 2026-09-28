using Microsoft.UI.Xaml;

namespace PathRouter.App;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            LogError("UnhandledException", e.Exception);
            e.Handled = true;
        };
    }

    /// <summary>Записать полный стек ошибки в %LocalAppData%\PathRouter\error.log — для разбора багов.</summary>
    public static void LogError(string where, Exception ex)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PathRouter");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}\r\n{ex}\r\n\r\n");
        }
        catch { /* логирование не должно ронять приложение */ }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        window.Activate();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

        // Иконка окна (заголовок, панель задач): грузим app.ico из папки приложения.
        // Загрузка из ресурсов exe (LoadImage с MAKEINTRESOURCE) на части конфигураций
        // WinApp SDK возвращает ноль — файл надёжнее.
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (System.IO.File.Exists(iconPath))
        {
            const uint LR_LOADFROMFILE = 0x0002;
            var small = LoadImage(IntPtr.Zero, iconPath, 1, 32, 32, LR_LOADFROMFILE);
            var big = LoadImage(IntPtr.Zero, iconPath, 1, 256, 256, LR_LOADFROMFILE);
            if (small != IntPtr.Zero) SendMessage(hwnd, 0x0080, IntPtr.Zero, small);
            if (big != IntPtr.Zero) SendMessage(hwnd, 0x0080, new IntPtr(1), big);
        }

        // Размер выставляем отложенно: платформа может сбрасывать геометрию после первого layout
        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var t = new System.Threading.Timer(_ =>
        {
            dq.TryEnqueue(() =>
            {
                SetWindowPos(hwnd, IntPtr.Zero, 40, 40, 1500, 900, 0x0040);
                SetWindowPos(hwnd, IntPtr.Zero, 40, 40, 1500, 900, 0x0040);
            });
        }, null, 300, System.Threading.Timeout.Infinite);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
