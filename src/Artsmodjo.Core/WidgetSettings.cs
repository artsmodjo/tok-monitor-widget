using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Artsmodjo.Core;

public enum WidgetEdge { Right = 0, Left = 1 }
public enum WidgetIndicator { Claude, Codex, Ram }

public sealed class WidgetSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool IsTopmost { get; set; } = true;
    public bool AcrylicEnabled { get; set; } = true;
    public bool DemoDataEnabled { get; set; } = true;
    public bool ReducedMotion { get; set; }
    public WidgetEdge Edge { get; set; } = WidgetEdge.Right;
    public string MonitorDevice { get; set; } = string.Empty;
    public double NormalizedY { get; set; } = 0.5;
    public WidgetIndicator SelectedIndicator { get; set; } = WidgetIndicator.Claude;
    public bool IsPinned { get; set; }

    public WidgetSettings CloneSnapshot() => new WidgetSettings
    {
        SchemaVersion = SchemaVersion, IsTopmost = IsTopmost, AcrylicEnabled = AcrylicEnabled,
        DemoDataEnabled = DemoDataEnabled, ReducedMotion = ReducedMotion, Edge = Edge,
        MonitorDevice = MonitorDevice ?? string.Empty, NormalizedY = NormalizedY,
        SelectedIndicator = SelectedIndicator, IsPinned = IsPinned
    }.Validate();

    public WidgetSettings Validate()
    {
        if (!Enum.IsDefined(Edge)) throw new ArgumentOutOfRangeException(nameof(Edge));
        if (!Enum.IsDefined(SelectedIndicator)) throw new ArgumentOutOfRangeException(nameof(SelectedIndicator));
        MonitorDevice ??= string.Empty;
        NormalizedY = double.IsFinite(NormalizedY) ? Math.Clamp(NormalizedY, 0, 1) : 0.5;
        return this;
    }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsStore(string? filePath = null)
    {
        _path = Path.GetFullPath(filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArtsModjo", "settings.json"));
    }

    public WidgetSettings Load()
    {
        if (!File.Exists(_path)) return new WidgetSettings();
        try
        {
            return (JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(_path), JsonOptions)
                ?? throw new InvalidDataException("The settings file contained no settings.")).Validate();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("Could not read the widget settings file.", error);
        }
    }

    public async Task SaveAsync(WidgetSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var snapshot = settings.CloneSnapshot();
        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, JsonOptions));
        var directory = Path.GetDirectoryName(_path) ?? throw new IOException("The settings file has no parent directory.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(directory);
            temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _path, true);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _gate.Release();
        }
    }
}
