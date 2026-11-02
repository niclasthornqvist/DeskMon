using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeskMon.Sensors;

/// <summary>
/// Installs what Prerequisites found missing, through winget, when asked to -
/// see ToolsPrompt. On a fresh PC that was a list of commands to type into
/// PowerShell, for readings that just said n/a until then.
/// </summary>
internal static class ToolInstaller
{
    /// <summary>A slow download finishes well inside this; past it winget is stuck on something.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The winget Windows puts in the user's WindowsApps folder, or null.
    ///
    /// This runs as administrator, so never "winget" off PATH, where anything
    /// running as the user can get a program of that name in first. That folder
    /// is the user's to write too, and the signature can't vouch for it as it
    /// does for PresentMon: the real file is an app execution alias, which
    /// Windows won't open to read. A plain program planted there isn't one,
    /// though, so an alias is all that is accepted. Only ever started by a click.
    /// </summary>
    public static string? FindWinget()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                       @"Microsoft\WindowsApps\winget.exe");
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Runs winget for the tool, and says what went wrong, or null if nothing
    /// seemed to. Whether it really is installed is for Prerequisites to say
    /// afterwards: winget has several exit codes that aren't failures.
    /// </summary>
    public static async Task<string?> InstallAsync(string winget, Prerequisites.Missing tool)
    {
        var info = new ProcessStartInfo(winget)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        // Exact id, winget's own source, and nothing that can stop to ask:
        // there is no window for it to ask in, so a question would hang until
        // the limit. The agreements are accepted here because the offer says so.
        foreach (string arg in new[]
                 {
                     "install", "--id", tool.Package, "--exact", "--source", "winget", "--silent",
                     "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity",
                 })
        {
            info.ArgumentList.Add(arg);
        }

        string? lastLine = null;
        try
        {
            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, e) =>
            {
                // winget ends with what happened, after lines of progress.
                if (!string.IsNullOrWhiteSpace(e.Data))
                    lastLine = e.Data.Trim();
            };
            process.ErrorDataReceived += (_, _) => { };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var limit = new CancellationTokenSource(Limit);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return $"It took over {Limit.TotalMinutes:0} minutes, so it was stopped.";
            }

            return process.ExitCode == 0
                ? null
                : lastLine ?? $"winget stopped with code 0x{process.ExitCode:X8}.";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            App.Log("installing " + tool.Name, ex);
            return "winget could not be started.";
        }
    }
}
