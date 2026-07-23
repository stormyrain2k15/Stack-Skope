using System.Text.Json;
using StackScope.Adapters.Formats.SafeTensors;
using StackScope.Core.Models;

namespace StackScope.Adapters.Formats.Transformers;

/// <summary>
/// Reads a HuggingFace Transformers repository laid out on disk:
///
///   repo/
///     config.json
///     generation_config.json     (optional)
///     tokenizer.json             (fast tokenizer, optional)
///     tokenizer_config.json      (optional)
///     special_tokens_map.json    (optional)
///     model.safetensors          (or *.safetensors sharded, or *.bin)
///     model.safetensors.index.json (for sharded safetensors)
///
/// This parser reads the JSON side of the repo — the actual tensor data
/// is delegated to <see cref="SafeTensorsReader"/> so we only pay parse
/// cost once and can share validation.
/// </summary>
public sealed class TransformersRepoReader
{
    public sealed record RepoLayout(
        string RepoDir,
        IReadOnlyList<string> Files,
        JsonDocument Config,
        JsonDocument? GenerationConfig,
        JsonDocument? TokenizerJson,
        JsonDocument? TokenizerConfig,
        JsonDocument? SpecialTokensMap,
        IReadOnlyList<string> SafeTensorsShards,
        string? PytorchBinPath);

    public static RepoLayout Read(string repoDir)
    {
        if (!Directory.Exists(repoDir))
            throw new DirectoryNotFoundException(repoDir);

        var files = Directory.GetFiles(repoDir).Select(Path.GetFileName)!
                             .Where(n => n != null)
                             .Cast<string>()
                             .OrderBy(n => n, StringComparer.Ordinal)
                             .ToList();

        string configPath = Path.Combine(repoDir, "config.json");
        if (!File.Exists(configPath))
            throw new FileNotFoundException(
                "transformers: config.json is required at the repo root.", configPath);

        JsonDocument config = JsonDocument.Parse(File.ReadAllText(configPath));

        JsonDocument? Load(string name)
        {
            var p = Path.Combine(repoDir, name);
            return File.Exists(p) ? JsonDocument.Parse(File.ReadAllText(p)) : null;
        }

        var shards = new List<string>();
        var indexPath = Path.Combine(repoDir, "model.safetensors.index.json");
        if (File.Exists(indexPath))
        {
            using var idx = JsonDocument.Parse(File.ReadAllText(indexPath));
            if (idx.RootElement.TryGetProperty("weight_map", out var wm))
            {
                foreach (var kv in wm.EnumerateObject())
                {
                    var s = kv.Value.GetString();
                    if (s is not null && !shards.Contains(s))
                        shards.Add(s);
                }
            }
        }
        else
        {
            var single = Path.Combine(repoDir, "model.safetensors");
            if (File.Exists(single)) shards.Add("model.safetensors");
        }

        string? pytorchBin = null;
        foreach (var candidate in new[] { "pytorch_model.bin", "model.bin" })
        {
            var p = Path.Combine(repoDir, candidate);
            if (File.Exists(p)) { pytorchBin = candidate; break; }
        }

        return new RepoLayout(
            repoDir, files,
            config,
            Load("generation_config.json"),
            Load("tokenizer.json"),
            Load("tokenizer_config.json"),
            Load("special_tokens_map.json"),
            shards, pytorchBin);
    }

    public static (string architecture, int nLayers, int nHeads, int hiddenSize, int vocab)
        ExtractCoreShape(JsonDocument config)
    {
        var r = config.RootElement;

        string arch = "Unknown";
        if (r.TryGetProperty("architectures", out var archs) &&
            archs.ValueKind == JsonValueKind.Array &&
            archs.GetArrayLength() > 0)
        {
            arch = archs[0].GetString() ?? "Unknown";
        }
        else if (r.TryGetProperty("model_type", out var mt))
        {
            arch = mt.GetString() ?? "Unknown";
        }

        int nLayers = TryInt(r, "num_hidden_layers")
                   ?? TryInt(r, "n_layer")
                   ?? TryInt(r, "num_layers")
                   ?? 0;
        int nHeads  = TryInt(r, "num_attention_heads")
                   ?? TryInt(r, "n_head")
                   ?? 0;
        int hidden  = TryInt(r, "hidden_size")
                   ?? TryInt(r, "n_embd")
                   ?? TryInt(r, "d_model")
                   ?? 0;
        int vocab   = TryInt(r, "vocab_size") ?? 0;

        return (arch, nLayers, nHeads, hidden, vocab);
    }

    public static TokenizerInfo? ReadTokenizerInfo(RepoLayout repo)
    {
        // Prefer the fast tokenizer JSON; fall back to tokenizer_config.json.
        int vocabSize = 0;
        string kind = "unknown";
        if (repo.TokenizerJson is not null)
        {
            var root = repo.TokenizerJson.RootElement;
            if (root.TryGetProperty("model", out var model))
            {
                if (model.TryGetProperty("type", out var t))
                    kind = (t.GetString() ?? "unknown").ToLowerInvariant();
                if (model.TryGetProperty("vocab", out var v) && v.ValueKind == JsonValueKind.Object)
                {
                    // BPE / WordPiece: vocab is dict of token → id.
                    int max = -1;
                    foreach (var p in v.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var id))
                            if (id > max) max = id;
                    }
                    vocabSize = max + 1;
                }
                else if (model.TryGetProperty("vocab", out var va) && va.ValueKind == JsonValueKind.Array)
                {
                    // Unigram: vocab is array of [token, score].
                    vocabSize = va.GetArrayLength();
                }
            }
        }
        if (vocabSize == 0)
        {
            var (_, _, _, _, v) = ExtractCoreShape(repo.Config);
            vocabSize = v;
        }

        string? bos = null, eos = null, pad = null, unk = null;
        var specials = new List<string>();
        bool addBos = false, addEos = false;

        if (repo.TokenizerConfig is not null)
        {
            var r = repo.TokenizerConfig.RootElement;
            bos = StringOrTokenName(r, "bos_token");
            eos = StringOrTokenName(r, "eos_token");
            pad = StringOrTokenName(r, "pad_token");
            unk = StringOrTokenName(r, "unk_token");
            addBos = r.TryGetProperty("add_bos_token", out var b) && b.ValueKind == JsonValueKind.True;
            addEos = r.TryGetProperty("add_eos_token", out var e) && e.ValueKind == JsonValueKind.True;
        }
        if (repo.SpecialTokensMap is not null)
        {
            foreach (var p in repo.SpecialTokensMap.RootElement.EnumerateObject())
            {
                var s = StringOrTokenName(repo.SpecialTokensMap.RootElement, p.Name);
                if (s is not null && !specials.Contains(s)) specials.Add(s);
            }
        }

        if (vocabSize == 0 && kind == "unknown") return null;
        return new TokenizerInfo(kind, vocabSize, bos, eos, pad, unk, specials, addBos, addEos);
    }

    private static int? TryInt(JsonElement r, string name)
        => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt32(out var i) ? i : null;

    private static string? StringOrTokenName(JsonElement r, string name)
    {
        if (!r.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Object when v.TryGetProperty("content", out var c) => c.GetString(),
            _ => null
        };
    }
}
