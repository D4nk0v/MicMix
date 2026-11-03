using System.Windows;
using MicMix.ViewModels;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace MicMix;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private Forms.NotifyIcon? _tray;
    private Drawing.Icon? _iconOn;
    private readonly Drawing.Icon _iconMuted = CreateTrayIcon(Drawing.Color.FromArgb(240, 100, 106));

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Helper copy that plays one OBS track; see ObsFeed.
        if (e.Args.Length >= 5 && e.Args[0] == Obs.ObsFeed.HelperArg)
        {
            Obs.ObsFeedHost.Run(this, e.Args[1..]);
            return;
        }

        // Second launch: ask the running instance to show itself, then quit.
        _singleInstance = new Mutex(true, "MicMix.SingleInstance", out bool isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "MicMix.Show");
        if (!isFirst)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }
        new Thread(() =>
        {
            while (_showSignal.WaitOne()) Dispatcher.BeginInvoke(ShowWindow);
        }) { IsBackground = true }.Start();

        // A failed click or hotkey must not take the mic down mid-stream: log it and keep running.
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject);

        _vm = new MainViewModel();
        _window = new MainWindow(_vm);
        CreateTray();

        if (!e.Args.Contains("--minimized")) ShowWindow();
    }

    private static void LogCrash(object error)
    {
        try
        {
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicMix");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "errors.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {error}{Environment.NewLine}");
        }
        catch { /* nowhere to report it */ }
    }

    private void CreateTray()
    {
        // Loaded here, not in a field initializer: pack:// URIs only work once the Application exists.
        _iconOn = new Drawing.Icon(GetResourceStream(new Uri("pack://application:,,,/Assets/MicMix.ico")).Stream,
            Forms.SystemInformation.SmallIconSize);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть MicMix", null, (_, _) => ShowWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());

        _tray = new Forms.NotifyIcon { Visible = true, ContextMenuStrip = menu };
        _tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) ShowWindow(); };
        UpdateTray();
        _vm!.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(MainViewModel.MicMuted)) UpdateTray(); };
    }

    /// <summary>Red icon while my voice is muted, so a hotkey press mid-game is visible at a glance.</summary>
    private void UpdateTray()
    {
        if (_tray == null || _vm == null) return;
        _tray.Icon = _vm.MicMuted ? _iconMuted : _iconOn;
        _tray.Text = _vm.MicMuted ? "MicMix — голос выключен" : "MicMix";
    }

    private void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApp()
    {
        if (_window != null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _vm?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>A small mic glyph drawn at runtime so the project needs no binary assets.</summary>
    private static Drawing.Icon CreateTrayIcon(Drawing.Color color)
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var fill = new Drawing.SolidBrush(color);
            using var pen = new Drawing.Pen(color, 3);
            using var body = new Drawing.Drawing2D.GraphicsPath();
            body.AddArc(10, 2, 12, 12, 180, 180);
            body.AddArc(10, 8, 12, 12, 0, 180);
            body.CloseFigure();
            g.FillPath(fill, body);
            g.DrawArc(pen, 6, 6, 20, 18, 0, 180);
            g.DrawLine(pen, 16, 24, 16, 29);
            g.DrawLine(pen, 10, 29, 22, 29);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }
}
