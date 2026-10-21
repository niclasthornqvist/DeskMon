namespace DeskMon.Metrics;

/// <summary>
/// A titled group of rows, e.g. "Usage" or "Temperature". Sections are drawn in
/// order, with a separator line above every section except the first.
/// </summary>
/// <param name="Title">Shown above the group.</param>
/// <param name="Metrics">The rows in the group, in order.</param>
public sealed record MetricSection(string Title, params Metric[] Metrics)
{
    /// <summary>
    /// Marks the section as being about the running game: it is titled after
    /// the game, and hidden while none runs if HideFpsWithoutGame is on (it
    /// stays, reading n/a, if not). Written as an object initializer:
    /// <c>new MetricSection("Game", ...) { OnlyWhileGaming = true }</c>
    /// </summary>
    public bool OnlyWhileGaming { get; init; }

    /// <summary>
    /// Optional. A small icon drawn before the heading, as WPF path data on an
    /// 11 x 11 grid - the same mini-language as the toolbar's icons, so one
    /// can be copied between them. Stroked, not filled, in the heading's
    /// colour. Leave it null and the heading is text alone, as before.
    ///
    /// Path data rather than a glyph from an icon font, for the reason the
    /// toolbar gives: a font that isn't installed draws boxes.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Optional. What the section is about, in a sentence, shown when the
    /// mouse is over its heading - above the line saying what a click does.
    /// </summary>
    public string? Tooltip { get; init; }
}
