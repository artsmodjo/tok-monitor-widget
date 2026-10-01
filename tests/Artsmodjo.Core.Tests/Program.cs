using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Artsmodjo.Core;

internal static class Program
{
    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("settings validation and clone", () => RunSync(SettingsValidation)),
            ("settings round trip and concurrent saves", SettingsRoundTripAndConcurrentSaves),
            ("corrupt settings report a useful error", () => RunSync(CorruptSettings)),
            ("failed save preserves existing settings", () => RunSync(FailedSavePreservesExisting)),
            ("canceled save preserves existing settings", CanceledSavePreservesExisting),
            ("position clamp and normalized restore", () => RunSync(PositionGeometry)),
            ("short screen and negative-origin geometry", () => RunSync(ShortAndNegativeScreen)),
            ("view model state and command notifications", () => RunSync(ViewModelState)),
            ("percent arc contract", () => RunSync(PercentGeometry))
        };
        var failures = 0;
        foreach (var test in tests)
        {
            try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + error); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
        return failures == 0 ? 0 : 1;
    }
    private static Task RunSync(Action action) { action(); return Task.CompletedTask; }
    private static void SettingsValidation()
    {
        var source = new WidgetSettings { NormalizedY = double.NaN, MonitorDevice = null! };
        var copy = source.CloneSnapshot(); Equal(0.5, copy.NormalizedY); Equal(string.Empty, copy.MonitorDevice);
        source.NormalizedY = 3; Equal(1.0, source.Validate().NormalizedY);
        source.NormalizedY = -2; Equal(0.0, source.Validate().NormalizedY);
        Equal(0, (int)WidgetEdge.Right); Equal(1, (int)WidgetEdge.Left);
        Throws<ArgumentOutOfRangeException>(() => new WidgetSettings { Edge = (WidgetEdge)22 }.Validate());
    }
    private static async Task SettingsRoundTripAndConcurrentSaves()
    {
        var directory = NewDirectory();
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            await store.SaveAsync(new WidgetSettings { NormalizedY = 0.8, IsPinned = true, Edge = WidgetEdge.Left });
            var loaded = store.Load(); Equal(0.8, loaded.NormalizedY); True(loaded.IsPinned); Equal(WidgetEdge.Left, loaded.Edge);
            await Task.WhenAll(Enumerable.Range(0, 12).Select(index => store.SaveAsync(new WidgetSettings { NormalizedY = index / 11.0 })));
            var final = store.Load(); True(final.NormalizedY is >= 0 and <= 1); True(!Directory.GetFiles(directory, "*.tmp").Any());
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void CorruptSettings()
    {
        var directory = NewDirectory();
        try { var path = Path.Combine(directory, "settings.json"); File.WriteAllText(path, "{ invalid"); Throws<InvalidDataException>(() => new SettingsStore(path).Load()); }
        finally { Directory.Delete(directory, true); }
    }
    private static void FailedSavePreservesExisting()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json"); var store = new SettingsStore(path);
            store.SaveAsync(new WidgetSettings { IsPinned = true }).GetAwaiter().GetResult();
            Throws<IOException>(() => new SettingsStore(Path.Combine(path, "child.json")).SaveAsync(new WidgetSettings()).GetAwaiter().GetResult());
            True(store.Load().IsPinned);
        }
        finally { Directory.Delete(directory, true); }
    }
    private static async Task CanceledSavePreservesExisting()
    {
        var directory = NewDirectory();
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            await store.SaveAsync(new WidgetSettings { IsPinned = true });
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Throws<OperationCanceledException>(() => store.SaveAsync(new WidgetSettings { IsPinned = false }, cancellation.Token).GetAwaiter().GetResult());
            True(store.Load().IsPinned); True(!Directory.GetFiles(directory, "*.tmp").Any());
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void PositionGeometry()
    {
        var clamped = GeometryMath.ClampPosition(-100, 10000, 93, 528, -1920, 0, 1920, 1080);
        Equal(-100.0, clamped.Left); Equal(552.0, clamped.Top);
        Equal(276.0, GeometryMath.RestoreTopFromNormalized(0.5, 0, 1080, 528));
        Equal(0.5, GeometryMath.NormalizeTop(276, 0, 1080, 528));
    }
    private static void ShortAndNegativeScreen()
    {
        var clamped = GeometryMath.ClampPosition(100, 200, 93, 528, -1280, -200, 1280, 400);
        Equal(-93.0, clamped.Left); Equal(-200.0, clamped.Top); Equal(0.0, GeometryMath.NormalizeTop(-400, -200, 400, 528));
    }
    private static void ViewModelState()
    {
        var vm = new WidgetViewModel(new WidgetSettings()); var notifications = new List<string>();
        vm.PropertyChanged += (_, change) => notifications.Add(change.PropertyName!);
        vm.Open(1); Equal(WidgetIndicator.Codex, vm.SelectedIndicator); True(vm.IsExpanded);
        True(notifications.Contains(nameof(vm.SelectedIndicator))); True(notifications.Contains(nameof(vm.IsExpanded)));
        vm.IsPinned = true; vm.Close(); True(vm.IsExpanded); vm.Close(true); True(!vm.IsExpanded);
        var changes = 0; vm.CloseCommand.CanExecuteChanged += (_, _) => changes++;
        vm.ToggleExpandedCommand.Execute(null); True(vm.IsExpanded); True(changes > 0);
        var snapshot = vm.CloneSnapshot(); snapshot.IsPinned = false; True(vm.IsPinned);
        True(!vm.SelectIndicatorCommand.CanExecute(null)); True(!vm.SelectIndicatorCommand.CanExecute("bad"));
    }
    private static void PercentGeometry()
    {
        True(Math.Abs(RingPercent.ToSweepAngle(73)!.Value - 262.8) < 1e-9);
        Equal(0.0, RingPercent.ToSweepAngle(0)!.Value); Equal(360.0, RingPercent.ToSweepAngle(100)!.Value);
        Equal(360.0, RingPercent.ToSweepAngle(125)!.Value); Equal(0.0, RingPercent.ToSweepAngle(-20)!.Value);
        True(RingPercent.ToSweepAngle(null) is null); True(RingPercent.ToSweepAngle(double.NaN) is null);
        True(RingPercent.ToSweepAngle(double.PositiveInfinity) is null); Equal("73,21,52", string.Join(",", RingPercent.DemoFixture));
    }
    private static string NewDirectory() { var path = Path.Combine(Path.GetTempPath(), "Artsmodjo.Core.Tests." + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void True(bool condition) { if (!condition) throw new Exception("Expected true."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Throws<TException>(Action action) where TException : Exception { try { action(); } catch (TException) { return; } throw new Exception("Expected " + typeof(TException).Name); }
}
