using System.Text;
using System.Text.Json;
using StackScope.Adapters.Formats.SafeTensors;
using Xunit;

namespace StackScope.Adapters.Tests;

public class SafeTensorsReaderTests
{
    /// <summary>
    /// Builds a real, minimal safetensors file in-memory containing two
    /// tensors and verifies the parser recovers dtype, shape, and offsets.
    /// </summary>
    private static byte[] BuildSyntheticFile(out int tensorAreaStart)
    {
        var header = new
        {
            weight_a = new { dtype = "F32", shape = new[] { 4L }, data_offsets = new[] { 0L, 16L } },
            weight_b = new { dtype = "F16", shape = new[] { 2L, 3L }, data_offsets = new[] { 16L, 28L } },
            __metadata__ = new { format = "test", note = "unit-test fixture" }
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(header);
        var lenBytes = BitConverter.GetBytes((long)json.Length);
        int tensorBytes = 16 + 12;

        using var ms = new MemoryStream();
        ms.Write(lenBytes, 0, 8);
        ms.Write(json, 0, json.Length);
        // Fill with recognizable, distinct payload bytes.
        for (int i = 0; i < tensorBytes; i++) ms.WriteByte((byte)(i + 1));
        tensorAreaStart = 8 + json.Length;
        return ms.ToArray();
    }

    [Fact]
    public void Parses_Header_And_Tensor_Offsets()
    {
        byte[] file = BuildSyntheticFile(out int tensorAreaStart);
        using var ms = new MemoryStream(file);
        var header = SafeTensorsReader.ReadHeader(ms);

        Assert.Equal(2, header.Tensors.Count);
        Assert.True(header.Metadata.ContainsKey("format"));
        Assert.Equal("test", header.Metadata["format"]);

        var a = header.Tensors["weight_a"];
        Assert.Equal("F32", a.DType);
        Assert.Equal(new[] { 4L }, a.Shape);
        Assert.Equal(0,  a.BeginOffset);
        Assert.Equal(16, a.EndOffset);

        var b = header.Tensors["weight_b"];
        Assert.Equal("F16", b.DType);
        Assert.Equal(new[] { 2L, 3L }, b.Shape);
        Assert.Equal(16, b.BeginOffset);
        Assert.Equal(28, b.EndOffset);
    }

    [Fact]
    public void Enumerates_Tensor_Inventory_With_Absolute_Offsets()
    {
        byte[] file = BuildSyntheticFile(out int tensorAreaStart);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, file);
            var inventory = SafeTensorsReader.ReadTensorInventory(path);
            Assert.Equal(2, inventory.Count);
            var a = inventory.Single(t => t.Name == "weight_a");
            var b = inventory.Single(t => t.Name == "weight_b");
            Assert.Equal(tensorAreaStart + 0,  a.ByteOffset);
            Assert.Equal(tensorAreaStart + 16, b.ByteOffset);
            Assert.Equal(16, a.ByteLength);
            Assert.Equal(12, b.ByteLength);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Validate_Rejects_Truncated_File()
    {
        byte[] file = BuildSyntheticFile(out _);
        var path = Path.GetTempFileName();
        try
        {
            // Chop the last 4 bytes.
            File.WriteAllBytes(path, file.AsSpan(0, file.Length - 4).ToArray());
            Assert.Throws<InvalidDataException>(() => SafeTensorsReader.Validate(path));
        }
        finally { File.Delete(path); }
    }
}
