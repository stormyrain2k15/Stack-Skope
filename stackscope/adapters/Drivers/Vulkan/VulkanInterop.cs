using System.Runtime.InteropServices;

namespace StackScope.Adapters.Drivers.Vulkan;

/// <summary>
/// Minimal Vulkan P/Invoke surface needed by <see cref="VulkanCaptureBackend"/>.
/// We do not embed a full Vulkan binding here — only what we need:
/// enumerating the instance, hooking <c>VK_EXT_debug_utils</c> callbacks
/// so worker-emitted labels ("stackscope.layer.7", "stackscope.head.3")
/// show up as events, and reading GPU timestamps via
/// <c>VK_EXT_calibrated_timestamps</c> so we can align to host clock.
///
/// The vulkan-1.dll shipped by the Vulkan SDK / GPU vendor driver on
/// Windows exposes these entry points.
/// </summary>
internal static class VulkanInterop
{
    public const string VkDll = "vulkan-1";

    public enum VkResult : int
    {
        VK_SUCCESS = 0,
        VK_NOT_READY = 1,
        VK_TIMEOUT = 2,
        VK_ERROR_OUT_OF_HOST_MEMORY = -1,
        VK_ERROR_INITIALIZATION_FAILED = -3
    }

    // vkGetInstanceProcAddr — used to find debug-utils extension entry
    // points at runtime without hard-linking them.
    [DllImport(VkDll, EntryPoint = "vkGetInstanceProcAddr",
        CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern IntPtr vkGetInstanceProcAddr(IntPtr instance, string name);

    [StructLayout(LayoutKind.Sequential)]
    public struct VkDebugUtilsLabelEXT
    {
        public int    sType;         // VK_STRUCTURE_TYPE_DEBUG_UTILS_LABEL_EXT = 1000128002
        public IntPtr pNext;
        public IntPtr pLabelName;    // const char*
        public float  color0;
        public float  color1;
        public float  color2;
        public float  color3;
    }

    // Debug messenger callback signature (partial — we only read severity,
    // type, and the trailing pCallbackData->pMessage for label events).
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint DebugUtilsMessengerCallbackEXT(
        uint messageSeverity,
        uint messageType,
        IntPtr pCallbackData,
        IntPtr pUserData);
}
