using System;
using System.ComponentModel;
using System.Windows.Input;

namespace Artsmodjo.Core;

public sealed class WidgetViewModel : INotifyPropertyChanged
{
    private readonly WidgetSettings _settings;
    private bool _isExpanded;
    public WidgetViewModel(WidgetSettings settings)
    {
        _settings = settings.CloneSnapshot();
        ToggleExpandedCommand = new DelegateCommand(_ => { if (IsExpanded) Close(true); else Open((int)SelectedIndicator); });
        TogglePinCommand = new DelegateCommand(_ => { IsPinned = !IsPinned; if (IsPinned) IsExpanded = true; });
        CloseCommand = new DelegateCommand(_ => Close(), _ => IsExpanded);
        SelectIndicatorCommand = new DelegateCommand(parameter => Open(Convert.ToInt32(parameter)), parameter => TryGetIndex(parameter, out var index) && index >= 0 && index <= 2);
        _isExpanded = IsPinned;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public WidgetSettings Settings => CloneSnapshot();
    public bool IsTopmost { get => _settings.IsTopmost; set => SetSetting(_settings.IsTopmost, value, v => _settings.IsTopmost = v, nameof(IsTopmost)); }
    public bool AcrylicEnabled { get => _settings.AcrylicEnabled; set => SetSetting(_settings.AcrylicEnabled, value, v => _settings.AcrylicEnabled = v, nameof(AcrylicEnabled)); }
    public bool DemoDataEnabled { get => _settings.DemoDataEnabled; set => SetSetting(_settings.DemoDataEnabled, value, v => _settings.DemoDataEnabled = v, nameof(DemoDataEnabled)); }
    public bool ReducedMotion { get => _settings.ReducedMotion; set => SetSetting(_settings.ReducedMotion, value, v => _settings.ReducedMotion = v, nameof(ReducedMotion)); }
    public WidgetEdge Edge
    {
        get => _settings.Edge;
        set { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); SetSetting(_settings.Edge, value, v => _settings.Edge = v, nameof(Edge)); }
    }
    public string MonitorDevice { get => _settings.MonitorDevice; set => SetSetting(_settings.MonitorDevice, value?.Trim() ?? string.Empty, v => _settings.MonitorDevice = v, nameof(MonitorDevice)); }
    public double NormalizedY { get => _settings.NormalizedY; set => SetSetting(_settings.NormalizedY, double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.5, v => _settings.NormalizedY = v, nameof(NormalizedY)); }
    public WidgetIndicator SelectedIndicator
    {
        get => _settings.SelectedIndicator;
        set { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); SetSetting(_settings.SelectedIndicator, value, v => _settings.SelectedIndicator = v, nameof(SelectedIndicator)); }
    }
    public bool IsPinned { get => _settings.IsPinned; set => SetSetting(_settings.IsPinned, value, v => _settings.IsPinned = v, nameof(IsPinned)); }
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; OnPropertyChanged(nameof(IsExpanded)); ((DelegateCommand)CloseCommand).RaiseCanExecuteChanged(); }
    }
    public ICommand ToggleExpandedCommand { get; }
    public ICommand TogglePinCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand SelectIndicatorCommand { get; }
    public WidgetSettings CloneSnapshot() => _settings.CloneSnapshot();
    public void Open(int index) { if (index < 0 || index > 2) throw new ArgumentOutOfRangeException(nameof(index)); SelectedIndicator = (WidgetIndicator)index; IsExpanded = true; }
    public void Close(bool force = false) { if (force || !IsPinned) IsExpanded = false; }
    private void SetSetting<T>(T current, T value, Action<T> assign, string propertyName)
    {
        if (Equals(current, value)) return;
        assign(value); OnPropertyChanged(propertyName); OnPropertyChanged(nameof(Settings));
    }
    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    private static bool TryGetIndex(object? parameter, out int index)
    {
        index = -1;
        return parameter is not null && int.TryParse(parameter.ToString(), out index);
    }
    private sealed class DelegateCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;
        public DelegateCommand(Action<object?> execute, Predicate<object?>? canExecute = null) { _execute = execute; _canExecute = canExecute; }
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) { if (CanExecute(parameter)) _execute(parameter); }
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
