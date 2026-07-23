using System.Text.Json;
using System.Text.Json.Serialization;

namespace StackScope.Core.Models;

/// <summary>
/// C# twin of the Python <c>ReproducibilityManifest</c>. Kept in sync
/// with <c>workers/inference_worker_py/src/stackscope_worker/manifest.py</c>.
/// Read from the manifest.json inside a .stackscope bundle.
/// </summary>
public sealed record ReproducibilityManifest(
    [property: JsonPropertyName("stackscope_version")]      string StackScopeVersion,
    [property: JsonPropertyName("stackscope_build_sha")]    string BuildSha,
    [property: JsonPropertyName("python_version")]          string PythonVersion,
    [property: JsonPropertyName("platform")]                string Platform,
    [property: JsonPropertyName("processor")]               string Processor,
    [property: JsonPropertyName("torch_version")]           string? TorchVersion,
    [property: JsonPropertyName("torch_cuda_version")]      string? TorchCudaVersion,
    [property: JsonPropertyName("torch_cuda_available")]    bool TorchCudaAvailable,
    [property: JsonPropertyName("torch_backends")]          IReadOnlyList<string> TorchBackends,
    [property: JsonPropertyName("transformers_version")]    string? TransformersVersion,
    [property: JsonPropertyName("numpy_version")]           string? NumpyVersion,
    [property: JsonPropertyName("cuda_toolkit_version")]    string? CudaToolkitVersion,
    [property: JsonPropertyName("cudnn_version")]           string? CudnnVersion,
    [property: JsonPropertyName("rocm_version")]            string? RocmVersion,
    [property: JsonPropertyName("vulkan_sdk_version")]      string? VulkanSdkVersion,
    [property: JsonPropertyName("nvidia_driver_version")]   string? NvidiaDriverVersion,
    [property: JsonPropertyName("amd_driver_version")]      string? AmdDriverVersion,
    [property: JsonPropertyName("seed")]                    int? Seed,
    [property: JsonPropertyName("dtype")]                   string? Dtype,
    [property: JsonPropertyName("quantization")]            string? Quantization,
    [property: JsonPropertyName("env_snapshot")]            IReadOnlyDictionary<string, string> EnvSnapshot,
    [property: JsonPropertyName("user")]                    string User,
    [property: JsonPropertyName("hostname")]                string Hostname)
{
    public static ReproducibilityManifest FromJson(string text)
        => JsonSerializer.Deserialize<ReproducibilityManifest>(text, JsonOpts)!;

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}
