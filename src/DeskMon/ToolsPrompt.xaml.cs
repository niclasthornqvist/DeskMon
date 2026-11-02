using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeskMon.Sensors;
using DeskMon.Ui;

namespace DeskMon;

/// <summary>
/// The offer to install what DeskMon is missing, a yes or a no, on its first
/// start and from the versions window after that. Installing as administrator
/// is something to ask about every time, so nothing here runs without INSTALL.
///
/// Stays until answered, unlike the session note's card: a first start is when
/// someone is looking at the program, and missing this leaves readings at n/a.
/// </summary>
public partial class ToolsPrompt : Window
{
    private readonly AppSettings _settings;
    private readonly UiTheme _theme;
    private readonly IReadOnlyList<Prerequisites.Missing> _missing;
    private readonly Func<bool> _canRestart;
    private readonly Action _restart;
    private readonly Action _answered;

    /// <summary>Installing: not to be closed, as winget would carry on with nobody told how it went.</summary>
    private bool _busy;

    /// <summary>The install has ended, so NO THANKS has become CLOSE.</summary>
    private bool _finished;

    /// <summary>How long "Restarting" stays up: restarting at once would look like a crash.</summary>
    private static readonly TimeSpan RestartAfter = TimeSpan.FromSeconds(1.5);

    /// <param name="canRestart">Whether DeskMon may restart now - not during a game, which would cut its session short.</param>
    /// <param name="restart">Restarts DeskMon. It only looks for these tools as it starts.</param>
    /// <param name="answered">Told as soon as either button is pressed, so the question isn't asked again.</param>
    public ToolsPrompt(UiTheme theme, AppSettings settings, IReadOnlyList<Prerequisites.Missing> missing,
                       Func<bool> canRestart, Action restart, Action answered)
    {
        _theme = theme;
        _settings = settings;
        _missing = missing;
        _canRestart = canRestart;
        _restart = restart;
        _answered = answered;
        InitializeComponent();

        Card.Background = theme.Popup;
        Card.BorderBrush = theme.AccentAt(0x80);
        FontFamily = theme.PanelFont;
        FontSize = UiTheme.PanelFontSize;

        Heading.Text = missing.Count == 1
            ? "DeskMon can install a free tool it uses"
            : "DeskMon can install the free tools it uses";
        Heading.Foreground = theme.NotesText;
        Heading.FontWeight = FontWeights.SemiBold;
        Heading.FontSize = UiTheme.PanelFontSize + 2;

        foreach (var tool in missing)
            Tools.Children.Add(Row(tool.Name, tool.Gives));

        Fine.Text = "Installed with winget, the Windows package manager. Installing accepts their licence terms. "
                  + "Without them DeskMon still runs, and those readings show n/a.";
        Fine.Foreground = theme.Label;
        Fine.FontSize = Math.Max(9, UiTheme.PanelFontSize - 2);
        Status.Foreground = theme.NotesText;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_busy)
                No_Click(this, e);
        };
    }

    private StackPanel Row(string name, string text)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(new TextBlock { Text = name, Foreground = _theme.NotesText, FontWeight = FontWeights.SemiBold });
        row.Children.Add(new TextBlock { Text = text, Foreground = _theme.Label, TextWrapping = TextWrapping.Wrap });
        return row;
    }

    /// <summary>The window has no title bar to drag it by, and it opens over whatever was being looked at.</summary>
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void No_Click(object sender, RoutedEventArgs e)
    {
        if (!_finished)
            _answered();
        Close();
    }

    private void Restart_Click(object sender, RoutedEventArgs e) => _restart();

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        _answered();

        try
        {
            await InstallAsync();
        }
        catch (Exception ex)
        {
            // async void: let out, this would take the program down.
            App.Log("installing the tools", ex);
            Say("Something went wrong installing them. The Versions window has the commands to do it by hand.");
        }

        _busy = false;
        _finished = true;
        NoButton.IsEnabled = true;
    }

    private async Task InstallAsync()
    {
        InstallButton.Visibility = Visibility.Collapsed;
        NoButton.Content = "CLOSE";
        Fine.Visibility = Visibility.Collapsed;

        if (ToolInstaller.FindWinget() is not { } winget)
        {
            // Older or trimmed-down Windows. Not a dead end: the commands can
            // wait until it is there, and App Installer is what provides it.
            Say("winget isn't on this PC. Get \"App Installer\" from the Microsoft Store, then press INSTALL in the Versions window.");
            return;
        }

        _busy = true;
        NoButton.IsEnabled = false;

        var why = new Dictionary<string, string?>();
        for (int i = 0; i < _missing.Count; i++)
        {
            var tool = _missing[i];
            Say(_missing.Count == 1
                ? $"Installing {tool.Name}…"
                : $"Installing {tool.Name} ({i + 1} of {_missing.Count})…");
            why[tool.Name] = await ToolInstaller.InstallAsync(winget, tool);
        }

        // Asked again rather than trusting winget's exit code: what counts is
        // whether DeskMon can find the tool now.
        var still = await Task.Run(() => Prerequisites.Check(_settings));
        var failed = _missing.Where(tool => still.Any(s => s.Name == tool.Name)).ToList();
        ShowResults(failed, why);

        bool anyInstalled = failed.Count < _missing.Count;
        if (failed.Count > 0)
        {
            RestartButton.Visibility = anyInstalled && _canRestart() ? Visibility.Visible : Visibility.Collapsed;
            Say(anyInstalled ? "One didn't install. You can try it again from the Versions window." : "Nothing was installed.");
        }
        else if (_canRestart())
        {
            Say("Installed. Restarting DeskMon…");
            await Task.Delay(RestartAfter);
            _restart();
        }
        else
        {
            Say("Installed. Restart DeskMon after your game to start using them.");
        }
    }

    /// <summary>Each tool's row again with how it went, and the command for one that failed.</summary>
    private void ShowResults(List<Prerequisites.Missing> failed, Dictionary<string, string?> why)
    {
        Tools.Children.Clear();
        foreach (var tool in _missing)
        {
            bool bad = failed.Contains(tool);
            var row = Row(tool.Name, bad
                ? $"{why.GetValueOrDefault(tool.Name) ?? "It didn't install."}\nBy hand: {tool.Install}"
                : "✓ Installed");
            ((TextBlock)row.Children[1]).Foreground = bad ? _theme.Warn : _theme.Normal;
            Tools.Children.Add(row);
        }
    }

    private void Say(string text)
    {
        Status.Text = text;
        Status.Visibility = Visibility.Visible;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = _busy;
        base.OnClosing(e);
    }
}
