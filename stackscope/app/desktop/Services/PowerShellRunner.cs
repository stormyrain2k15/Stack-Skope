using System.Diagnostics;
using System.IO;

namespace StackScope.Desktop.Services;

/// <summary>
/// Runs an arbitrary PowerShell script fragment. Used by top-level
/// operations that must call into Windows shell tooling (opening the
/// captures folder in Explorer, launching the MSI installer's repair
/// UI, running Get-CimInstance for GPU probing, etc.).
///
/// Never exposed to user input directly — every caller constructs
/// the command from typed parameters, so this is not a shell-injection
/// surface.
/// </summary>
public sealed class PowerShellRunner
{
    private readonly string _psExe;

    public PowerShellRunner(string? psExecutable = null)
    {
        _psExe = psExecutable
                 ?? Environment.GetEnvironmentVariable("STACKSCOPE_POWERSHELL")
                 ?? "powershell.exe";
    }

    public sealed record RunResult(int ExitCode, string StdOut, string StdErr);

    public async Task<RunResult> RunScriptAsync(string script, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(_psExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return new RunResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Open a folder in Windows Explorer.</summary>
    public static void OpenInExplorer(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    /// <summary>Copy text to the clipboard via PowerShell — avoids a
    /// dependency on <c>System.Windows.Clipboard</c> from services.</summary>
    public Task CopyToClipboardAsync(string text, CancellationToken ct = default)
        => RunScriptAsync($"Set-Clipboard -Value \"{text.Replace("\"", "`\"")}\"", ct);
}
