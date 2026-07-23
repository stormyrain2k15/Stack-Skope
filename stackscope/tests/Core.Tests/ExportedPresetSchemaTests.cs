using System.Text.Json;
using StackScope.Core.Models;
using Xunit;

namespace StackScope.Core.Tests;

/// <summary>
/// Wire-schema contract for exported ablation presets. The
/// <see cref="ExportedAblationPreset"/> DTO is what travels across
/// teams — the tests here pin down the JSON shape so future refactors
/// don't silently break existing shared preset files.
/// </summary>
public class ExportedPresetSchemaTests
{
    [Fact]
    public void Roundtrips_All_Fields()
    {
        var src = new ExportedAblationPreset
        {
            SchemaVersion  = 1,
            Name = "study-A",
            LayerStart = 4, LayerEnd = 6,
            HeadStart  = 0, HeadEnd  = 3,
            Prompt = "hello world",
            Seed = 42,
            SigmaThreshold = 1.25,
        };
        var json = JsonSerializer.Serialize(src);
        var back = JsonSerializer.Deserialize<ExportedAblationPreset>(json);
        Assert.NotNull(back);
        Assert.Equal(1,           back!.SchemaVersion);
        Assert.Equal("study-A",   back.Name);
        Assert.Equal(4,           back.LayerStart);
        Assert.Equal(6,           back.LayerEnd);
        Assert.Equal(0,           back.HeadStart);
        Assert.Equal(3,           back.HeadEnd);
        Assert.Equal("hello world", back.Prompt);
        Assert.Equal(42ul,        back.Seed);
        Assert.Equal(1.25,        back.SigmaThreshold);
    }

    [Fact]
    public void Schema_Version_Field_Is_Named_SchemaVersion_In_Wire_Form()
    {
        var src = new ExportedAblationPreset { SchemaVersion = 1, Name = "n" };
        var json = JsonSerializer.Serialize(src);
        Assert.Contains("\"SchemaVersion\"", json);
        Assert.Contains("\"Name\":\"n\"",    json);
    }

    [Fact]
    public void Missing_Optional_Fields_Deserialise_To_Defaults()
    {
        var json = "{\"SchemaVersion\":1,\"Name\":\"tiny\"}";
        var back = JsonSerializer.Deserialize<ExportedAblationPreset>(json)!;
        Assert.Equal("tiny", back.Name);
        Assert.Equal(0, back.LayerStart);
        Assert.Equal(0, back.LayerEnd);
        Assert.Equal("", back.Prompt);
        Assert.Equal(0ul, back.Seed);
        Assert.Equal(1.0, back.SigmaThreshold);
    }
}
