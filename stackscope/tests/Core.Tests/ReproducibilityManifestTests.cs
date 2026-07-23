using StackScope.Core.Models;
using Xunit;

namespace StackScope.Core.Tests;

public class ReproducibilityManifestTests
{
    [Fact]
    public void Round_Trips_Through_Json()
    {
        var m = new ReproducibilityManifest(
            StackScopeVersion: "0.1.0",
            BuildSha: "abcdef",
            PythonVersion: "3.11.9",
            Platform: "Windows-10",
            Processor: "AMD64",
            TorchVersion: "2.4.0",
            TorchCudaVersion: "12.5",
            TorchCudaAvailable: true,
            TorchBackends: new[] { "cuda" },
            TransformersVersion: "4.44.0",
            NumpyVersion: "1.26.0",
            CudaToolkitVersion: "12.5",
            CudnnVersion: "90200",
            RocmVersion: null,
            VulkanSdkVersion: "1.3.283",
            NvidiaDriverVersion: "555.99",
            AmdDriverVersion: null,
            Seed: 42,
            Dtype: "float16",
            Quantization: "q4_k_m",
            EnvSnapshot: new Dictionary<string, string> { ["CUDA_VISIBLE_DEVICES"] = "0" },
            User: "dev",
            Hostname: "workstation");

        var json = m.ToJson();
        Assert.Contains("stackscope_version", json);
        var back = ReproducibilityManifest.FromJson(json);
        Assert.Equal(m.BuildSha, back.BuildSha);
        Assert.Equal(m.Seed, back.Seed);
        Assert.Equal(m.EnvSnapshot["CUDA_VISIBLE_DEVICES"], back.EnvSnapshot["CUDA_VISIBLE_DEVICES"]);
    }

    [Fact]
    public void Parses_Python_Written_Snake_Case_Fields()
    {
        // Mirrors what manifest.py emits.
        const string json = """
        {
          "stackscope_version": "0.1.0",
          "stackscope_build_sha": "abc",
          "python_version": "3.11",
          "platform": "Linux",
          "processor": "x86_64",
          "torch_version": null,
          "torch_cuda_version": null,
          "torch_cuda_available": false,
          "torch_backends": [],
          "transformers_version": null,
          "numpy_version": null,
          "cuda_toolkit_version": null,
          "cudnn_version": null,
          "rocm_version": null,
          "vulkan_sdk_version": null,
          "nvidia_driver_version": null,
          "amd_driver_version": null,
          "seed": null,
          "dtype": null,
          "quantization": null,
          "env_snapshot": {},
          "user": "dev",
          "hostname": "box"
        }
        """;
        var m = ReproducibilityManifest.FromJson(json);
        Assert.Equal("0.1.0", m.StackScopeVersion);
        Assert.False(m.TorchCudaAvailable);
        Assert.Null(m.Seed);
    }
}
