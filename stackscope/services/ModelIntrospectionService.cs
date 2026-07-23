using System.Text.Json;
using StackScope.Adapters.Architectures;
using StackScope.Adapters.Formats.Gguf;
using StackScope.Adapters.Formats.SafeTensors;
using StackScope.Adapters.Formats.TensorFlow;
using StackScope.Adapters.Formats.Transformers;
using StackScope.Core.Models;

namespace StackScope.Services;

/// <summary>
/// Turns a filesystem path into a <see cref="UnifiedModelDescriptor"/>.
/// Detection is by directory layout / file extension:
///
///   *.safetensors        → SafeTensors, single-file
///   *.gguf               → GGUF
///   directory with config.json + model.safetensors[.index.json] → HF repo
///   directory with saved_model.pb → TensorFlow SavedModel
/// </summary>
public sealed class ModelIntrospectionService
{
    private readonly ArchitectureRegistry _arches;

    public ModelIntrospectionService(ArchitectureRegistry? arches = null)
    {
        _arches = arches ?? new ArchitectureRegistry();
    }

    public UnifiedModelDescriptor Introspect(string path)
    {
        if (Directory.Exists(path))
        {
            if (File.Exists(Path.Combine(path, "config.json"))) return ReadHfRepo(path);
            if (File.Exists(Path.Combine(path, "saved_model.pb"))) return ReadSavedModel(path);
            throw new InvalidDataException(
                $"'{path}' is a directory but neither a HuggingFace repo nor a TF SavedModel.");
        }
        if (!File.Exists(path))
            throw new FileNotFoundException("Model file not found.", path);

        return path.ToLowerInvariant() switch
        {
            var s when s.EndsWith(".safetensors") => ReadSafeTensorsFile(path),
            var s when s.EndsWith(".gguf")        => ReadGguf(path),
            _ => throw new InvalidDataException($"Unrecognized model file extension: {path}.")
        };
    }

    private UnifiedModelDescriptor ReadSafeTensorsFile(string path)
    {
        var tensors = SafeTensorsReader.ReadTensorInventory(path);
        var hash    = SafeTensorsReader.ComputeFileSha256(path);
        var input   = new ArchitectureInput(
            ModelId: hash,
            DisplayName: Path.GetFileNameWithoutExtension(path),
            SourceKind: "safetensors",
            SourcePath: path,
            ContentHash: hash,
            Config: new Dictionary<string, JsonElement>(),
            GgufMetadata: new Dictionary<string, object>(),
            Tensors: tensors,
            Tokenizer: null,
            Files: new[] { Path.GetFileName(path) });
        return Resolve(input);
    }

    private UnifiedModelDescriptor ReadGguf(string path)
    {
        var f = GgufReader.ReadFile(path);
        var tensors = GgufReader.ReadTensorInventory(path);
        var input = new ArchitectureInput(
            ModelId: SafeTensorsReader.ComputeFileSha256(path),
            DisplayName: Path.GetFileNameWithoutExtension(path),
            SourceKind: "gguf",
            SourcePath: path,
            ContentHash: SafeTensorsReader.ComputeFileSha256(path),
            Config: new Dictionary<string, JsonElement>(),
            GgufMetadata: f.Metadata,
            Tensors: tensors,
            Tokenizer: null,
            Files: new[] { Path.GetFileName(path) });
        return Resolve(input);
    }

    private UnifiedModelDescriptor ReadHfRepo(string dir)
    {
        var repo = TransformersRepoReader.Read(dir);

        var tensors = new List<TensorInfo>();
        foreach (var shard in repo.SafeTensorsShards)
        {
            var full = Path.Combine(dir, shard);
            tensors.AddRange(SafeTensorsReader.ReadTensorInventory(full));
        }
        var tokenizer = TransformersRepoReader.ReadTokenizerInfo(repo);

        var flatConfig = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in repo.Config.RootElement.EnumerateObject())
            flatConfig[p.Name] = p.Value.Clone();

        // Content hash: SHA-256 over the sorted list of "shardname:filesize".
        var meta = string.Join('\n',
            repo.SafeTensorsShards
                .OrderBy(s => s, StringComparer.Ordinal)
                .Select(s => $"{s}:{new FileInfo(Path.Combine(dir, s)).Length}"));
        var contentHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(meta))).ToLowerInvariant();

        var input = new ArchitectureInput(
            ModelId: contentHash,
            DisplayName: Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar,
                                                     Path.AltDirectorySeparatorChar)),
            SourceKind: "hf_repo",
            SourcePath: dir,
            ContentHash: contentHash,
            Config: flatConfig,
            GgufMetadata: new Dictionary<string, object>(),
            Tensors: tensors,
            Tokenizer: tokenizer,
            Files: repo.Files);
        return Resolve(input);
    }

    private UnifiedModelDescriptor ReadSavedModel(string dir)
    {
        var info = SavedModelReader.Read(dir);
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                             .Select(f => Path.GetRelativePath(dir, f))
                             .OrderBy(f => f, StringComparer.Ordinal)
                             .ToList();
        var mem = SavedModelReader.EstimateMemory(info);

        // Build a shallow layer graph: one node per unique op namespace.
        var opsByScope = info.Nodes
            .GroupBy(n => n.Name.Contains('/') ? n.Name[..n.Name.IndexOf('/')] : "root")
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var blocks = opsByScope.Select((g, i) => new LayerNode
        {
            Path = $"savedmodel.{g.Key}",
            Name = g.Key,
            ModuleType = "OpScope",
            Depth = 1,
            LayerIndex = i,
            Children = g.Select(n => new LayerNode
            {
                Path = "savedmodel." + n.Name,
                Name = n.Name.Split('/').Last(),
                ModuleType = n.Op,
                Depth = 2
            }).ToList()
        }).ToList();

        var graph = new LayerGraph
        {
            Root = new LayerNode
            {
                Path = "savedmodel",
                Name = "savedmodel",
                ModuleType = "SavedModel",
                Depth = 0,
                Children = blocks
            },
            NumLayers = blocks.Count,
            NumHeads = 0,
            HiddenSize = 0,
            HeadDim = 0
        };

        return new UnifiedModelDescriptor(
            ModelId: dir,
            DisplayName: Path.GetFileName(dir),
            SourceKind: "savedmodel",
            SourcePath: dir,
            ContentHashSha256: "",
            Architecture: ArchitectureFamily.Unknown,
            ArchitectureName: "TF SavedModel",
            Layers: graph,
            Tensors: Array.Empty<TensorInfo>(),
            DominantQuantization: QuantizationInfo.Fp32,
            Tokenizer: null,
            MemoryEstimate: mem,
            RawConfig: new Dictionary<string, string>(),
            Files: files);
    }

    private UnifiedModelDescriptor Resolve(ArchitectureInput input)
    {
        var adapter = _arches.Resolve(input);
        if (adapter is not null) return adapter.Normalize(input);

        // Unknown architecture — still return a valid descriptor with a
        // minimal graph so the UI can render the tensor inventory.
        var minimal = new LayerGraph
        {
            Root = new LayerNode
            {
                Path = "unknown", Name = "unknown",
                ModuleType = "Unknown", Depth = 0
            },
            NumLayers = 0, NumHeads = 0, HiddenSize = 0, HeadDim = 0
        };
        return new UnifiedModelDescriptor(
            ModelId: input.ModelId,
            DisplayName: input.DisplayName,
            SourceKind: input.SourceKind,
            SourcePath: input.SourcePath,
            ContentHashSha256: input.ContentHash,
            Architecture: ArchitectureFamily.Unknown,
            ArchitectureName: "Unknown",
            Layers: minimal,
            Tensors: input.Tensors,
            DominantQuantization: AdapterDominant(input.Tensors),
            Tokenizer: input.Tokenizer,
            MemoryEstimate: new MemoryEstimate(0, 0, 0, 0),
            RawConfig: new Dictionary<string, string>(),
            Files: input.Files);
    }

    private static QuantizationInfo AdapterDominant(IReadOnlyList<TensorInfo> tensors)
    {
        if (tensors.Count == 0) return QuantizationInfo.Fp32;
        var g = tensors.GroupBy(t => t.Quantization.Scheme)
                       .OrderByDescending(x => x.Count())
                       .First();
        return g.First().Quantization;
    }
}
