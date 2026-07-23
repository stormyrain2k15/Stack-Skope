using System.Runtime.InteropServices;

namespace StackScope.Adapters.Drivers.Cuda;

/// <summary>
/// P/Invoke surface for CUPTI 12.x (extras/CUPTI/lib64/cupti.dll on Windows).
/// Only the subset we actually use for activity + callback capture is
/// declared here. Struct layouts follow cupti_activity.h.
///
/// See NVIDIA CUPTI Programming Guide 12.x, sections:
///   - Activity API (asynchronous activity records)
///   - Callback API (synchronous callbacks around driver/runtime calls)
///
/// This file is Windows-only in practice — <see cref="CuptiCaptureBackend"/>
/// checks <c>RuntimeInformation.IsOSPlatform</c> before touching it.
/// </summary>
internal static class CuptiInterop
{
    public const string CuptiDll = "cupti64_2024.3.0"; // CUDA Toolkit 12.6 default

    // Activity record kinds we subscribe to.
    public enum CUpti_ActivityKind : uint
    {
        INVALID              = 0,
        MEMCPY               = 1,
        MEMSET               = 2,
        KERNEL               = 3,
        DRIVER               = 4,
        RUNTIME              = 5,
        DEVICE               = 9,
        NAME                 = 12,
        MARKER               = 13,
        MARKER_DATA          = 14,
        CONCURRENT_KERNEL    = 21,
        MEMCPY2              = 22,
        MEMORY               = 23,
        MEMORY2              = 24,
        MEMORY_POOL          = 25,
        NVLINK               = 26,
        SYNCHRONIZATION      = 32
    }

    public enum CUptiResult : uint
    {
        SUCCESS                    = 0,
        ERROR_INVALID_PARAMETER    = 1,
        ERROR_NOT_INITIALIZED      = 2,
        ERROR_NOT_READY            = 22
    }

    // Prototype for the activity buffer request callback.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void BufferRequested(
        out IntPtr buffer, out UIntPtr size, out UIntPtr maxNumRecords);

    // Prototype for the activity buffer completed callback.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void BufferCompleted(
        IntPtr context, uint streamId, IntPtr buffer,
        UIntPtr size, UIntPtr validSize);

    [DllImport(CuptiDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern CUptiResult cuptiActivityEnable(CUpti_ActivityKind kind);

    [DllImport(CuptiDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern CUptiResult cuptiActivityDisable(CUpti_ActivityKind kind);

    [DllImport(CuptiDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern CUptiResult cuptiActivityRegisterCallbacks(
        BufferRequested funcBufferRequested,
        BufferCompleted funcBufferCompleted);

    [DllImport(CuptiDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern CUptiResult cuptiActivityFlushAll(uint flag);

    [DllImport(CuptiDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern CUptiResult cuptiActivityGetNextRecord(
        IntPtr buffer, UIntPtr validBufferSizeBytes, out IntPtr record);

    [DllImport(CuptiDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern CUptiResult cuptiGetTimestamp(out ulong timestampNs);

    // Activity record headers all begin with this same 8-byte prefix
    // (kind + flags/pad). We only read the fields we need per-kind.
    [StructLayout(LayoutKind.Sequential)]
    public struct CUpti_ActivityKernel8
    {
        public CUpti_ActivityKind kind;
        public uint reserved0;
        public ulong start;
        public ulong end;
        public int deviceId;
        public int contextId;
        public int streamId;
        public uint gridX;
        public uint gridY;
        public uint gridZ;
        public uint blockX;
        public uint blockY;
        public uint blockZ;
        public uint dynamicSharedMemory;
        public uint staticSharedMemory;
        public uint localMemoryPerThread;
        public uint localMemoryTotal;
        public ulong correlationId;
        public IntPtr namePtr;         // char*
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CUpti_ActivityMemcpy5
    {
        public CUpti_ActivityKind kind;
        public byte copyKind;
        public byte srcKind;
        public byte dstKind;
        public byte flags;
        public ulong bytes;
        public ulong start;
        public ulong end;
        public int deviceId;
        public int contextId;
        public int streamId;
        public ulong correlationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CUpti_ActivityMemory3
    {
        public CUpti_ActivityKind kind;
        public byte memoryKind;
        public byte pad0;
        public ushort pad1;
        public ulong address;
        public ulong bytes;
        public ulong timestamp;
        public ulong PC;
        public int processId;
        public int deviceId;
        public int contextId;
        public int streamId;
        public ulong correlationId;
    }
}
