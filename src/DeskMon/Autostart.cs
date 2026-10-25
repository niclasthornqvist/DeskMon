using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace DeskMon;

/// <summary>
/// Starting DeskMon when you log in, switched on and off from the settings.
///
/// A scheduled task rather than the Startup folder or a Run key, for the
/// reason Install-Autostart.ps1 gives: DeskMon runs as administrator for the
/// CPU temperature driver, and both of those would show a UAC prompt at every
/// logon. A task registered to run with the highest privileges starts it
/// silently. This makes exactly the task that script makes - same name, same
/// settings - so the two can be used interchangeably, and Build.ps1 still
/// finds it to restart DeskMon after a build.
///
/// The state lives in Windows, not settings.json: whether the task exists is
/// the only answer that can't go stale, so it is asked for each time.
/// </summary>
internal static class Autostart
{
    private const string TaskName = "DeskMon";

    /// <summary>Whether the logon task is registered.</summary>
    public static bool IsEnabled() => SchTasks("/Query", "/TN", TaskName).ExitCode == 0;

    /// <summary>
    /// Registers the task to start this copy of DeskMon - wherever it is now,
    /// so turning it on again after moving the folder points it at the new
    /// place. Returns null on success, or why it failed.
    /// </summary>
    public static string? Enable()
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
            return "Could not tell where DeskMon.exe is.";

        // schtasks reads the definition from a file; UTF-16 is what it expects.
        string file = Path.Combine(Path.GetTempPath(), $"DeskMon-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, TaskXml(exe), Encoding.Unicode);
            var result = SchTasks("/Create", "/TN", TaskName, "/XML", file, "/F");
            return result.ExitCode == 0 ? null : Explain(result.Error);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            try { File.Delete(file); } catch { /* temp folder; Windows cleans it */ }
        }
    }

    /// <summary>
    /// The account the logon task starts DeskMon for, as Windows names it
    /// ("PC\name") - or null with no task, or none that says. For moving the
    /// old data out of app\ only into the %APPDATA% of the person it belongs
    /// to: see DataFolder.
    /// </summary>
    public static string? TaskUser()
    {
        var info = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "/Query", "/TN", TaskName, "/XML" })
            info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info)!;
            var error = process.StandardError.ReadToEndAsync();   // drained too - see SchTasks
            var output = process.StandardOutput.ReadToEndAsync();
            if (!Exited(process))
                return null;
            string xml = output.Result;
            _ = error.Result;
            if (process.ExitCode != 0)
                return null;

            var match = System.Text.RegularExpressions.Regex.Match(xml, @"<UserId>([^<]+)</UserId>");
            return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value.Trim()) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Removes the task. Null on success - including when there was none to remove.</summary>
    public static string? Disable()
    {
        if (!IsEnabled())
            return null;

        var result = SchTasks("/Delete", "/TN", TaskName, "/F");
        return result.ExitCode == 0 ? null : Explain(result.Error);
    }

    /// <summary>
    /// The task Install-Autostart.ps1 registers, written out: at logon for
    /// this user, 15 seconds after the desktop appears - starting mid-logon
    /// only makes the logon feel slower - elevated, no time limit, allowed on
    /// battery, and never a second copy if one is already running.
    /// </summary>
    private static string TaskXml(string exe)
    {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        string command = SecurityElement.Escape(exe);
        string folder = SecurityElement.Escape(Path.GetDirectoryName(exe) ?? string.Empty);

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Desktop hardware monitor widget (CPU/RAM/SWAP/temps).</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT15S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <WorkingDirectory>{folder}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>
    /// The one failure worth putting in plain words: a copy of DeskMon not
    /// running as administrator - started from a debugger, say - is not
    /// allowed to register an elevated task.
    /// </summary>
    private static string Explain(string error)
        => error.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            ? "Windows refused: DeskMon has to be running as administrator to change this."
            : string.IsNullOrWhiteSpace(error) ? "Windows refused the change." : error.Trim();

    /// <summary>
    /// Waits up to 10 s for schtasks, and stops it past that. The streams are
    /// read in the background by the callers: read to the end first, the wait
    /// only began once schtasks had exited, so a hung one held its caller -
    /// at startup, the UI thread - for good and the limit never fired.
    /// </summary>
    private static bool Exited(Process process)
    {
        if (process.WaitForExit(10_000))
            return true;

        try { process.Kill(); } catch { /* exited just now */ }
        return false;
    }

    /// <summary>
    /// schtasks.exe rather than the Task Scheduler COM API: it ships with
    /// every Windows, takes the same task XML the scheduler itself exports,
    /// and needs no interop assembly for three calls made once in a while.
    /// </summary>
    private static (int ExitCode, string Error) SchTasks(params string[] arguments)
    {
        var info = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info)!;
            // Both drained at once: read one to the end first and a full pipe
            // on the other would leave schtasks and DeskMon waiting on each other.
            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            if (!Exited(process))
                return (-1, "schtasks didn't answer within 10 seconds.");
            return (process.ExitCode, error.Result);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
