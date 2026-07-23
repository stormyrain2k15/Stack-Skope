using StackScope.Adapters.Drivers.Cpu;
using StackScope.Adapters.Drivers.Cuda;
using StackScope.Adapters.Drivers.Rocm;
using StackScope.Adapters.Drivers.Vulkan;
using StackScope.Services;
using Xunit;

namespace StackScope.Core.Tests;

public class CaptureBackendFactoryTests
{
    [Theory]
    [InlineData("cuda:0",   true,  "pytorch",     typeof(CuptiCaptureBackend))]
    [InlineData("cuda:1",   true,  "pytorch",     typeof(CuptiCaptureBackend))]
    [InlineData("hip:0",    true,  "pytorch",     typeof(RocmCaptureBackend))]
    [InlineData("rocm:0",   true,  "llamacpp",    typeof(RocmCaptureBackend))]
    [InlineData("vulkan:0", true,  "llamacpp",    typeof(VulkanCaptureBackend))]
    [InlineData("cpu",      true,  "pytorch",     typeof(CpuCaptureBackend))]
    // Torch-HIP quirk: reports cuda:N but driver stack is ROCm.
    [InlineData("cuda:0",   true,  "pytorch-hip", typeof(RocmCaptureBackend))]
    // Unverified placement → downgrade to CPU sampler (never lie to user
    // with an empty CUPTI stream). CPU is unchanged either way.
    [InlineData("cuda:0",   false, "pytorch",     typeof(CpuCaptureBackend))]
    [InlineData("hip:0",    false, "llamacpp",    typeof(CpuCaptureBackend))]
    [InlineData("cpu",      false, "pytorch",     typeof(CpuCaptureBackend))]
    // Unknown kinds fall back cleanly.
    [InlineData("metal:0",  true,  "llamacpp",    typeof(CpuCaptureBackend))]
    [InlineData("",         true,  "pytorch",     typeof(CpuCaptureBackend))]
    public void Selects_The_Right_Backend(string device, bool verified,
                                           string workerKind, Type expected)
    {
        var (backend, _) = CaptureBackendFactory.Create(device, verified, workerKind);
        Assert.IsType(expected, backend);
    }

    [Fact]
    public void Description_Explains_The_Choice()
    {
        var (_, d1) = CaptureBackendFactory.Create("cuda:1", true, "pytorch");
        Assert.Contains("CUPTI", d1);
        Assert.Contains("cuda:1", d1);

        var (_, d2) = CaptureBackendFactory.Create("hip:0", true, "pytorch");
        Assert.Contains("rocprofiler", d2);

        var (_, d3) = CaptureBackendFactory.Create("cuda:0", false, "pytorch");
        Assert.Contains("fallback", d3);
        Assert.Contains("unverified", d3);
    }

    [Fact]
    public void Parses_Device_Kind_And_Index()
    {
        Assert.Equal(("cuda", 3),  CaptureBackendFactory.ParseDevice("cuda:3"));
        Assert.Equal(("hip", 0),   CaptureBackendFactory.ParseDevice("hip:0"));
        Assert.Equal(("cpu", 0),   CaptureBackendFactory.ParseDevice("cpu"));
        Assert.Equal(("mps", 0),   CaptureBackendFactory.ParseDevice("mps"));
        Assert.Equal(("cpu", 0),   CaptureBackendFactory.ParseDevice(""));
        Assert.Equal(("vulkan", 2), CaptureBackendFactory.ParseDevice("vulkan:2"));
    }
}
