using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Media;
using Artsmodjo.Core;

namespace Artsmodjo.Widget;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private bool _exitScheduled;
    private MainWindow? _mainWindow;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = StartupOptions.Parse(e.Args);
        if (options.SoftwareRendering) RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var mutexName = options.ValidationDirectory is null ? "ArtsModjo.MonitoringWidget.Singleton" : $"ArtsModjo.MonitoringWidget.Validation.{Environment.ProcessId}";
        _instanceMutex = new Mutex(true, mutexName, out _ownsMutex);
        if (!_ownsMutex) { Shutdown(0); return; }
        if (options.ValidationDirectory is not null)
        {
            RuntimeValidation.ConfigureDiagnostics(options.ValidationDirectory);
            DispatcherUnhandledException += (_, args) => RuntimeValidation.RecordDiagnostic("DispatcherUnhandled", args.Exception);
            int exitCode;
            try { exitCode = await RuntimeValidation.RunAsync(options.ValidationDirectory); }
            catch (Exception error)
            {
                Directory.CreateDirectory(options.ValidationDirectory);
                File.WriteAllText(Path.Combine(options.ValidationDirectory, "startup-error.txt"), error.ToString());
                exitCode = 1;
            }
            Shutdown(exitCode); return;
        }
        var store = new SettingsStore(options.SettingsPath);
        WidgetSettings settings;
        var canPersist = true;
        try { settings = store.Load(); }
        catch (InvalidDataException)
        {
            settings = new WidgetSettings(); canPersist = false;
            MessageBox.Show("The settings file could not be read or uses another version. This session will use defaults and will not save changes. The original file is preserved.", "Widget settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _mainWindow = new MainWindow(settings, store, canPersist);
        MainWindow = _mainWindow;
        if (options.ReadyFile is string readyFile)
        {
            var written = false;
            _mainWindow.ContentRendered += async (_, _) =>
            {
                if (written) return;
                written = true;
                if (!await _mainWindow.ProbeStartupReadinessAsync()) { _mainWindow.Close(); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(readyFile))!);
                File.WriteAllText(readyFile, JsonSerializer.Serialize(new ReadyStatus(true, Environment.ProcessId, true, true, true, DateTimeOffset.UtcNow), ReadyStatusJsonContext.Default.ReadyStatus));
                ScheduleExit();
            };
        }
        _mainWindow.Show();
        if (options.ReadyFile is null) ScheduleExit();

        void ScheduleExit()
        {
            if (_exitScheduled) return;
            _exitScheduled = true;
            if (options.ExitAfterMilliseconds is not int delay) return;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
            timer.Tick += (_, _) => { timer.Stop(); _mainWindow.Close(); };
            timer.Start();
            if (options.ExerciseUi) ScheduleUiExercise();
        }

        void ScheduleUiExercise()
        {
            var ticks = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) =>
            {
                if (ticks % 2 == 0) _mainWindow.ViewModel.Open((ticks / 2) % 3);
                else _mainWindow.ViewModel.Close(force: true);
                ticks++;
                if (ticks == 60)
                {
                    timer.Stop();
                    File.WriteAllText(options.ReadyFile! + ".exercise-complete", "60");
                }
            };
            timer.Start();
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _mainWindow?.Dispose();
        if (_ownsMutex && _instanceMutex is not null) { _instanceMutex.ReleaseMutex(); _ownsMutex = false; }
        _instanceMutex?.Dispose(); base.OnExit(e);
    }
    private sealed record StartupOptions(string? ValidationDirectory, string? SettingsPath, string? ReadyFile, int? ExitAfterMilliseconds, bool SoftwareRendering, bool ExerciseUi)
    {
        public static StartupOptions Parse(string[] args)
        {
            string? validation = null, settings = null, ready = null;
            int? delay = null;
            var software = true; // Measured lower working set for this small, mostly static widget.
            var exerciseUi = false;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--software-rendering": software = true; break;
                    case "--exercise-ui": exerciseUi = true; break;
                    case "--validate": validation = Next(args, ref i); break;
                    case "--settings": settings = Next(args, ref i); break;
                    case "--ready-file": ready = Next(args, ref i); break;
                    case "--exit-after-ms":
                        if (!int.TryParse(Next(args, ref i), out var milliseconds) || milliseconds < 1) throw new ArgumentException("Invalid exit delay.");
                        delay = milliseconds; break;
                    default: throw new ArgumentException("Unknown startup argument: " + args[i]);
                }
            }
            if (exerciseUi && (settings is null || ready is null || delay is null)) throw new ArgumentException("UI exercise requires an explicit settings path, ready file, and positive exit delay.");
            return new(validation, settings, ready, delay, software, exerciseUi);
        }
        private static string Next(string[] args, ref int index)
        {
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Missing startup argument value.");
            return args[index];
        }
    }
}
