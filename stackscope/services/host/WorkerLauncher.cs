using System.Diagnostics;

namespace StackScope.Coordinator;

/// <summary>
/// Spawns worker processes when the coordinator can't find a pre-configured
/// endpoint for a given worker kind. Env variables take precedence; if no
/// endpoint is set, we launch the matching worker locally with a free port
/// and wait until it accepts connections.
/// </summary>
public sealed class WorkerLauncher
{
    private readonly ILogger<WorkerLauncher> _log;

    public WorkerLauncher(ILogger<WorkerLauncher> log) { _log = log; }

    public sealed record SpawnResult(string Endpoint, Process Process);

    public async Task<SpawnResult> SpawnAsync(string kind, CancellationToken ct)
    {
        int port = ReserveFreePort();
        string endpoint = $"127.0.0.1:{port}";

        var (fileName, args) = ResolveCommand(kind, endpoint);
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to launch worker '{kind}'.");
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) _log.LogInformation("[{Kind}] {Line}", kind, e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) _log.LogWarning("[{Kind}] {Line}", kind, e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (!await WaitForListenerAsync(port, TimeSpan.FromSeconds(30), ct))
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Worker '{kind}' did not become ready on {endpoint}.");
        }
        return new SpawnResult(endpoint, proc);
    }

    private static (string file, string[] args) ResolveCommand(string kind, string endpoint) => kind.ToLowerInvariant() switch
    {
        "pytorch"  => (Environment.GetEnvironmentVariable("STACKSCOPE_PYTHON") ?? "python",
                      new[] { "-m", "stackscope_worker.worker", "--endpoint", endpoint }),
        "llamacpp" => (Environment.GetEnvironmentVariable("STACKSCOPE_LLAMACPP_WORKER")
                       ?? "stackscope_llamacpp_worker",
                      new[] { "--endpoint", endpoint }),
        _ => throw new NotSupportedException($"Unknown worker kind: {kind}")
    };

    private static int ReserveFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<bool> WaitForListenerAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync(System.Net.IPAddress.Loopback, port, ct);
                return true;
            }
            catch { await Task.Delay(200, ct); }
        }
        return false;
    }
}
