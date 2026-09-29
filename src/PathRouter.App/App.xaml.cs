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
        AppTheme.Load(); // тема из settings.json — до создания окна
        var window = new MainWindow();
        window.Activate();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

        // Иконка окна (заголовок, панель задач). LoadImage на этой системе не работает
        // (даже с системными .ico) — берём иконку через PrivateExtractIcons, это надёжный путь.
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (System.IO.File.Exists(iconPath))
        {
            var small = new IntPtr[1];
            var big = new IntPtr[1];
            PrivateExtractIcons(iconPath, 0, 32, 32, small, null, 1, 0);
            PrivateExtractIcons(iconPath, 0, 256, 256, big, null, 1, 0);
            if (small[0] != IntPtr.Zero) SendMessage(hwnd, 0x0080, IntPtr.Zero, small[0]);
            if (big[0] != IntPtr.Zero) SendMessage(hwnd, 0x0080, new IntPtr(1), big[0]);
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
    private static extern uint PrivateExtractIcons(string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        [System.Runtime.InteropServices.Out] IntPtr[]? phicon,
        [System.Runtime.InteropServices.Out] uint[]? piconid,
        uint nIcons, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
