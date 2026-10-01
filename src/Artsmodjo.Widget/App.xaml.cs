using System;
using System.Threading;
using System.Windows;
using Artsmodjo.Core;

namespace Artsmodjo.Widget;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    internal SettingsStore Settings { get; private set; } = null!;
    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "ArtsModjo.MonitoringWidget.Singleton", out _ownsMutex);
        if (!_ownsMutex) { Shutdown(); return; }
        Settings = new SettingsStore();
        var window = new MainWindow(Settings.Load(), Settings);
        MainWindow = window;
        window.Show();
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow is IDisposable disposable) disposable.Dispose();
        if (_ownsMutex && _mutex is not null) { _mutex.ReleaseMutex(); _ownsMutex = false; }
        _mutex?.Dispose();
        _mutex = null;
        base.OnExit(e);
    }
}
