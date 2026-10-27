using System;
using System.Linq;
using System.Threading;
using System.Windows;
using ClaudeMultiAccount.Core;
using ClaudeMultiAccount.UI;

namespace ClaudeMultiAccount;

public partial class App : Application
{
    const string InstanceMutex = @"Local\ClaudeMultiAccount.Instance";
    const string ShowEvent = @"Local\ClaudeMultiAccount.Show";

    Mutex? _instance;
    EventWaitHandle? _showSignal;
    MainWindow? _main;

    public TrayIcon? Tray { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Elevated helper mode: do the admin task and exit, no UI.
        if (e.Args.Length == 3 && e.Args[0] == Elevation.Flag)
        {
            Shutdown(Elevation.RunElevated(e.Args[1], e.Args[2]));
            return;
        }

        // Single instance: a second launch just brings the first window forward.
        _instance = new Mutex(true, InstanceMutex, out bool isFirst);
        if (!isFirst)
        {
            try { EventWaitHandle.OpenExisting(ShowEvent).Set(); } catch { }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
        new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.Invoke(ShowMain);
        }) { IsBackground = true }.Start();

        var store = ProfileStore.Load();
        _main = new MainWindow(store);
        Tray = new TrayIcon(store, ShowMain, p => _ = _main.LaunchAsync(p), ExitApp);

        if (!e.Args.Contains("--tray"))
            _main.Show();
    }

    public void ShowMain()
    {
        if (_main is null)
            return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized)
            _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void ExitApp()
    {
        Tray?.Dispose();
        _main?.CloseForExit();
        Shutdown();
    }
}
