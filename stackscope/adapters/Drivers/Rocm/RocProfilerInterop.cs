using System.Runtime.InteropServices;

namespace StackScope.Adapters.Drivers.Rocm;

/// <summary>
/// P/Invoke surface for AMD ROCm rocprofiler (v2 API, librocprofiler64.so
/// on Linux / rocprofiler64.dll on Windows via HIP SDK). This is the
/// analogue of CUPTI: async record buffers of kernel launches, memcpys,
/// and memory allocations.
///
/// The struct layouts match roctracer_ext.h and rocprofiler.h from ROCm
/// 6.x. Only the subset we use is declared here.
/// </summary>
internal static class RocProfilerInterop
{
    public const string RocProfilerDll = "rocprofiler64";
    public const string RocTracerDll   = "roctracer64";

    public enum RocStatus : int
    {
        Success = 0,
        ErrorGeneric = 1,
        ErrorInvalidArgument = 2,
        ErrorNotInitialized = 4
    }

    [DllImport(RocProfilerDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern RocStatus rocprofiler_initialize();

    [DllImport(RocProfilerDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern RocStatus rocprofiler_finalize();

    [DllImport(RocTracerDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern RocStatus roctracer_open_pool(IntPtr properties);

    [DllImport(RocTracerDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern RocStatus roctracer_start();

    [DllImport(RocTracerDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern RocStatus roctracer_stop();

    [DllImport(RocTracerDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern RocStatus roctracer_flush_activity();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ActivityCallback(
        IntPtr begin, IntPtr end, IntPtr userArg);

    // Activity record layout for HIP kernel dispatches (ROCm 6.x).
    // Mirrors roctracer_hip_api.h + hip_ops.h.
    [StructLayout(LayoutKind.Sequential)]
    public struct KernelRecord
    {
        public uint  domain;         // ACTIVITY_DOMAIN_HIP_OPS
        public uint  op;             // HIP_OP_ID_DISPATCH
        public uint  processId;
        public uint  threadId;
        public ulong beginNs;
        public ulong endNs;
        public ulong correlationId;
        public uint  deviceId;
        public ulong queueId;        // HSA queue / HIP stream
        public IntPtr kernelName;    // char*
        public uint gridX, gridY, gridZ;
        public uint blockX, blockY, blockZ;
        public uint sharedBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CopyRecord
    {
        public uint  domain;
        public uint  op;             // HIP_OP_ID_COPY
        public uint  processId;
        public uint  threadId;
        public ulong beginNs;
        public ulong endNs;
        public ulong correlationId;
        public uint  deviceId;
        public ulong queueId;
        public ulong bytes;
        public byte  copyKind;       // 0 H2D 1 D2H 2 D2D 3 Peer
        public byte  pad0, pad1, pad2;
    }
}
