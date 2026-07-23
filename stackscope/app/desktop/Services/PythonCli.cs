using System.Diagnostics;
using System.IO;
using System.Text;

namespace StackScope.Desktop.Services;

/// <summary>
/// Runs a StackScope Python CLI as a subprocess. Captures stdout/stderr,
/// streams them line-by-line via callbacks, and supports cancellation.
/// This is the ONE way the WPF UI invokes any Python-side tool so we
/// have a single place for env resolution, error handling, and
/// argument quoting.
///
/// The Python side is the same code the GitHub canaries and the CI
/// tests run — the UI just wraps it in a friendly button.
/// </summary>
public sealed class PythonCli
{
    public sealed record RunResult(int ExitCode, string StdOut, string StdErr, TimeSpan Elapsed);

    private readonly string _python;

    public PythonCli(string? pythonExecutable = null)
    {
        _python = pythonExecutable
                  ?? Environment.GetEnvironmentVariable("STACKSCOPE_PYTHON")
                  ?? ResolveBundledPython()
                  ?? "python";
    }

    /// <summary>
    /// Run a StackScope module (e.g. "stackscope_worker.dry_run") with
    /// the given argument list. Streams output through the callbacks
    /// as soon as each line arrives.
    /// </summary>
    public async Task<RunResult> RunModuleAsync(
        string module,
        IEnumerable<string> args,
        Action<string>? onStdOut = null,
        Action<string>? onStdErr = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(_python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add(module);
        foreach (var a in args) psi.ArgumentList.Add(a);

        var sw = Stopwatch.StartNew();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            onStdOut?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            onStdErr?.Invoke(e.Data);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* best-effort */ }
            throw;
        }

        return new RunResult(proc.ExitCode, stdout.ToString(), stderr.ToString(), sw.Elapsed);
    }

    /// <summary>Convenience: capture the whole stdout, throw on non-zero exit.</summary>
    public async Task<string> CaptureAsync(string module, IEnumerable<string> args,
        CancellationToken ct = default)
    {
        var r = await RunModuleAsync(module, args, ct: ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
            throw new InvalidOperationException(
                $"{module} exited {r.ExitCode}: {r.StdErr.TrimEnd()}");
        return r.StdOut;
    }

    private static string? ResolveBundledPython()
    {
        // MSI-installed layout: %ProgramFiles%\StackScope\python\python.exe
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var bundled = Path.Combine(pf, "StackScope", "python", "python.exe");
        return File.Exists(bundled) ? bundled : null;
    }
}
