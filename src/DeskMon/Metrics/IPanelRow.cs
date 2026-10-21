namespace DeskMon.Metrics;

/// <summary>
/// Anything the panel draws.
///
/// The one thing they all have to be able to do is re-read the settings and the
/// theme on demand, for the settings window: every size, colour and visibility
/// on these rows is a property computed from <see cref="AppSettings"/> or
/// <see cref="UiTheme"/>, so a colour or a column width changes on screen the
/// moment the bindings are told to look again.
///
/// They used to be cached in the constructor instead, which was fine while the
/// only way to change a setting was to edit the file and restart.
/// </summary>
public interface IPanelRow
{
    /// <summary>
    /// Tells every binding on this row to re-read its value. Implemented by
    /// raising PropertyChanged with no name, which is WPF's "all of them".
    /// </summary>
    void Refresh();
}
