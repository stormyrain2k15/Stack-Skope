using StackScope.Core.Models;

namespace StackScope.Adapters.Formats.TensorFlow;

/// <summary>
/// Reads a TensorFlow <c>SavedModel</c> directory:
///
///   savedmodel/
///     saved_model.pb                    (SavedModel protobuf)
///     variables/
///       variables.data-00000-of-00001
///       variables.index
///     assets/                            (optional)
///
/// We don't need every field of the SavedModel proto — we only enumerate
/// the top-level MetaGraphDef entries to list nodes/ops, so users can see
/// what's inside without depending on TensorFlow at runtime.
///
/// Field numbers used here are from tensorflow/core/protobuf/saved_model.proto:
///   SavedModel:
///     1  int32     saved_model_schema_version
///     2  repeated MetaGraphDef meta_graphs
///
///   MetaGraphDef:
///     1  MetaInfoDef meta_info_def
///     2  GraphDef    graph_def
///
///   GraphDef:
///     1  repeated NodeDef node
///     2  VersionDef versions
///     3  int32       version   (deprecated)
///     4  FunctionDefLibrary library
///
///   NodeDef:
///     1  string name
///     2  string op
///     3  repeated string input
///     4  string device
///     5  map<string, AttrValue> attr
/// </summary>
public sealed class SavedModelReader
{
    public sealed record SavedModelInfo(
        string SavedModelDir,
        int SchemaVersion,
        int MetaGraphCount,
        IReadOnlyList<GraphNode> Nodes,
        long WeightsBytes);

    public sealed record GraphNode(
        string Name,
        string Op,
        IReadOnlyList<string> Inputs,
        string Device);

    public static SavedModelInfo Read(string savedModelDir)
    {
        var pbPath = Path.Combine(savedModelDir, "saved_model.pb");
        if (!File.Exists(pbPath))
            throw new FileNotFoundException(
                $"tensorflow: saved_model.pb not found in {savedModelDir}.", pbPath);

        var bytes = File.ReadAllBytes(pbPath);
        var r = new ProtoReader(bytes);

        int schemaVersion = 0;
        int metaCount = 0;
        var nodes = new List<GraphNode>();

        while (!r.AtEnd)
        {
            var (fn, wt) = r.ReadTag();
            switch (fn)
            {
                case 1 when wt == ProtoReader.WireType.Varint:
                    schemaVersion = (int)r.ReadVarint();
                    break;

                case 2 when wt == ProtoReader.WireType.LengthDelimited:
                    metaCount++;
                    ReadMetaGraphDef(r.ReadLengthDelimited(), nodes);
                    break;

                default:
                    r.SkipField(wt);
                    break;
            }
        }

        long weightsBytes = 0;
        var varDir = Path.Combine(savedModelDir, "variables");
        if (Directory.Exists(varDir))
        {
            foreach (var f in Directory.GetFiles(varDir))
                weightsBytes += new FileInfo(f).Length;
        }

        return new SavedModelInfo(savedModelDir, schemaVersion, metaCount, nodes, weightsBytes);
    }

    private static void ReadMetaGraphDef(ReadOnlyMemory<byte> buf, List<GraphNode> nodes)
    {
        var r = new ProtoReader(buf);
        while (!r.AtEnd)
        {
            var (fn, wt) = r.ReadTag();
            if (fn == 2 && wt == ProtoReader.WireType.LengthDelimited)
            {
                ReadGraphDef(r.ReadLengthDelimited(), nodes);
            }
            else r.SkipField(wt);
        }
    }

    private static void ReadGraphDef(ReadOnlyMemory<byte> buf, List<GraphNode> nodes)
    {
        var r = new ProtoReader(buf);
        while (!r.AtEnd)
        {
            var (fn, wt) = r.ReadTag();
            if (fn == 1 && wt == ProtoReader.WireType.LengthDelimited)
                nodes.Add(ReadNodeDef(r.ReadLengthDelimited()));
            else r.SkipField(wt);
        }
    }

    private static GraphNode ReadNodeDef(ReadOnlyMemory<byte> buf)
    {
        var r = new ProtoReader(buf);
        string name = "", op = "", device = "";
        var inputs = new List<string>();
        while (!r.AtEnd)
        {
            var (fn, wt) = r.ReadTag();
            switch (fn)
            {
                case 1 when wt == ProtoReader.WireType.LengthDelimited: name = r.ReadString(); break;
                case 2 when wt == ProtoReader.WireType.LengthDelimited: op = r.ReadString(); break;
                case 3 when wt == ProtoReader.WireType.LengthDelimited: inputs.Add(r.ReadString()); break;
                case 4 when wt == ProtoReader.WireType.LengthDelimited: device = r.ReadString(); break;
                default: r.SkipField(wt); break;
            }
        }
        return new GraphNode(name, op, inputs, device);
    }

    /// <summary>
    /// Derive a coarse memory estimate from the on-disk variables.data
    /// files (the whole graph's serialized weight footprint).
    /// </summary>
    public static MemoryEstimate EstimateMemory(SavedModelInfo info)
        => new(WeightsBytes: info.WeightsBytes,
               KvCachePerTokenBytes: 0,
               ActivationsPerTokenBytes: 0,
               OverheadBytes: 64 * 1024 * 1024);
}
