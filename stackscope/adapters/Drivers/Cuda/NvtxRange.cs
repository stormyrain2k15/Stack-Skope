using System.Runtime.InteropServices;

namespace StackScope.Adapters.Drivers.Cuda;

/// <summary>
/// Thin managed wrapper over NVTX (nvToolsExt64_1.dll) for injecting
/// range markers from managed code. Workers do the same from Python via
/// <c>torch.cuda.nvtx</c>. The correlation engine uses the range IDs to
/// tie kernel launches back to semantic regions.
/// </summary>
public static class NvtxRange
{
    private const string NvtxDll = "nvToolsExt64_1";

    [DllImport(NvtxDll, EntryPoint = "nvtxRangePushA",
        CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int nvtxRangePushA(string message);

    [DllImport(NvtxDll, EntryPoint = "nvtxRangePop",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvtxRangePop();

    public static IDisposable Push(string name)
    {
        try { nvtxRangePushA(name); } catch { /* NVTX absent → no-op */ }
        return new Popper();
    }

    private sealed class Popper : IDisposable
    {
        public void Dispose() { try { nvtxRangePop(); } catch { } }
    }
}
