using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace DeskMon.Metrics;

/// <summary>
/// The two lines at the top naming the actual hardware being measured.
///
/// The names arrive a moment after the window appears, because opening the
/// hardware library happens off the UI thread, hence the change notification
/// and the placeholder text.
/// </summary>
public sealed class HeaderRow : INotifyPropertyChanged, IPanelRow
{
    private readonly UiTheme _theme;
    private readonly AppSettings _settings;
    private string _cpuName = "Detecting hardware…";
    private string _gpuName = string.Empty;

    public HeaderRow(UiTheme theme, AppSettings settings)
    {
        _theme = theme;
        _settings = settings;
    }

    public string CpuName
    {
        get => _cpuName;
        private set => Set(ref _cpuName, value, nameof(CpuName));
    }

    public string GpuName
    {
        get => _gpuName;
        private set => Set(ref _gpuName, value, nameof(GpuName));
    }

    /// <summary>Hidden until there is a name, so there's no empty gap at startup.</summary>
    public Visibility GpuVisibility => string.IsNullOrEmpty(_gpuName)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Brush Brush => _theme.Header;

    /// <summary>Slightly smaller than the metric rows - it's a caption, not data.</summary>
    public double FontSize => System.Math.Max(8, _settings.FontSize - 1);

    /// <summary>Re-reads everything, for a settings change.</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public void SetHardware(string? cpuName, string? gpuName)
    {
        CpuName = string.IsNullOrWhiteSpace(cpuName) ? "Unknown CPU" : cpuName!.Trim();
        GpuName = string.IsNullOrWhiteSpace(gpuName) ? string.Empty : gpuName!.Trim();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GpuVisibility)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, string name)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
