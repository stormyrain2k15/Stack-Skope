using StackScope.Adapters.Drivers.Cpu;
using StackScope.Adapters.Drivers.Cuda;
using StackScope.Adapters.Drivers.Rocm;
using StackScope.Adapters.Drivers.Vulkan;
using StackScope.Core.Capture;

namespace StackScope.Services;

/// <summary>
/// Selects the correct <see cref="ICaptureBackend"/> for the device
/// llama.cpp / torch actually landed on.
///
/// This is the piece that closes the loop between GPU detection and
/// GPU-specific capture: without it, <see cref="LoadModelReply.ResolvedDevice"/>
/// would be display-only. With it, selecting <c>hip:0</c> in the dropdown
/// attaches <see cref="RocmCaptureBackend"/> instead of
/// <see cref="CuptiCaptureBackend"/> so the transaction actually records
/// kernel launches, memcopies, and driver calls from the right driver
/// stack — not just placement.
///
/// The mapping is deliberately conservative:
///   • unverified placement → CPU capture only (never speculatively
///     attach CUPTI/ROCm when we don't know the model is actually on
///     that GPU — that produces confusing empty traces),
///   • Vulkan placement     → Vulkan backend even on NVIDIA/AMD,
///   • CUDA on ROCm-torch   → ROCm backend (torch-hip surfaces as
///     <c>cuda:N</c> but the driver stack is rocprofiler),
///   • Metal / SYCL / CANN  → CPU capture (backends not implemented).
/// </summary>
public static class CaptureBackendFactory
{
    /// <param name="deviceId">
    /// The resolved device string from <c>LoadModelReply</c>
    /// (e.g. <c>cuda:0</c>, <c>hip:1</c>, <c>vulkan:0</c>, <c>cpu</c>).
    /// </param>
    /// <param name="verified">
    /// True if the worker read placement back from the runtime. When
    /// false, we downgrade to CPU capture so nobody looks at an empty
    /// CUPTI stream and blames StackScope.
    /// </param>
    /// <param name="workerKind">
    /// The worker family — used to disambiguate <c>cuda:N</c> between a
    /// native CUDA build (CUPTI) and a torch-hip build (rocprofiler).
    /// </param>
    /// <returns>
    /// A ready-to-<c>StartAsync</c> capture backend plus a human string
    /// describing what was chosen and why. The description is surfaced
    /// in <c>WorkspaceState.CaptureBackendLabel</c> so the UI shows
    /// e.g. "rocprofiler / hip:0" next to the device badge.
    /// </returns>
    public static (ICaptureBackend Backend, string Description) Create(
        string deviceId, bool verified, string workerKind)
    {
        var (kind, index) = ParseDevice(deviceId);

        if (!verified && kind != "cpu")
            return (new CpuCaptureBackend(),
                    $"cpu-sampler (fallback: '{deviceId}' unverified)");

        // Torch-HIP quirk: the model reports 'cuda:N' but the driver
        // stack is ROCm/HIP. workerKind lets us disambiguate.
        if (kind == "cuda" && workerKind.Equals("pytorch-hip", StringComparison.OrdinalIgnoreCase))
            return (new RocmCaptureBackend(index),
                    $"rocprofiler / cuda:{index} (torch-hip build)");

        return kind switch
        {
            "cuda"   => (new CuptiCaptureBackend(index),  $"CUPTI / cuda:{index}"),
            "hip"    => (new RocmCaptureBackend(index),   $"rocprofiler / hip:{index}"),
            "rocm"   => (new RocmCaptureBackend(index),   $"rocprofiler / rocm:{index}"),
            "vulkan" => (new VulkanCaptureBackend(),      $"vulkan debug-utils / vulkan:{index}"),
            "cpu"    => (new CpuCaptureBackend(),         "cpu-sampler / cpu"),
            _        => (new CpuCaptureBackend(),         $"cpu-sampler (unknown kind '{kind}')"),
        };
    }

    internal static (string Kind, int Index) ParseDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return ("cpu", 0);
        if (deviceId == "cpu") return ("cpu", 0);
        var colon = deviceId.IndexOf(':');
        if (colon < 0) return (deviceId.ToLowerInvariant(), 0);
        var kind = deviceId[..colon].ToLowerInvariant();
        return int.TryParse(deviceId[(colon + 1)..], out var idx) ? (kind, idx) : (kind, 0);
    }
}
