using System.Text;
using StackScope.Adapters.Formats.Gguf;
using Xunit;

namespace StackScope.Adapters.Tests;

public class GgufReaderTests
{
    [Fact]
    public void ComputesBlockQuantByteLength()
    {
        // 256-element Q4_K block = 144 bytes.
        Assert.Equal(144, GgufReader.ComputeTensorByteLength(GgmlType.Q4_K, new long[] { 256 }));
        Assert.Equal(288, GgufReader.ComputeTensorByteLength(GgmlType.Q4_K, new long[] { 512 }));

        // 32-element Q8_0 block = 34 bytes.
        Assert.Equal(34, GgufReader.ComputeTensorByteLength(GgmlType.Q8_0, new long[] { 32 }));
    }

    [Fact]
    public void ComputesFpByteLength()
    {
        Assert.Equal(4 * 1024, GgufReader.ComputeTensorByteLength(GgmlType.F32, new long[] { 1024 }));
        Assert.Equal(2 * 1024, GgufReader.ComputeTensorByteLength(GgmlType.F16, new long[] { 1024 }));
        Assert.Equal(2 * 1024, GgufReader.ComputeTensorByteLength(GgmlType.BF16, new long[] { 1024 }));
    }

    [Fact]
    public void RejectsBlockCountNotDivisible()
    {
        Assert.Throws<InvalidDataException>(
            () => GgufReader.ComputeTensorByteLength(GgmlType.Q4_K, new long[] { 100 }));
    }

    [Fact]
    public void ReadsSynthetic_v3_Header()
    {
        // Build the minimal legal GGUF v3 file: magic, version, tensor_count=0,
        // kv_count=1 with { "general.architecture": "test" }. No tensors.
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write((uint)0x46554747);  // 'GGUF'
        w.Write((uint)3);            // version
        w.Write((ulong)0);           // tensor_count
        w.Write((ulong)1);           // kv_count
        // KV pair: key
        var key = Encoding.UTF8.GetBytes("general.architecture");
        w.Write((ulong)key.Length);
        w.Write(key);
        // value_type = String (8)
        w.Write((uint)8);
        var val = Encoding.UTF8.GetBytes("test");
        w.Write((ulong)val.Length);
        w.Write(val);

        ms.Position = 0;
        var reader = new GgufReader(ms);
        var f = reader.Read();
        Assert.Equal(3u, f.Version);
        Assert.Empty(f.Tensors);
        Assert.True(f.Metadata.ContainsKey("general.architecture"));
        Assert.Equal("test", f.Metadata["general.architecture"]);
    }
}
