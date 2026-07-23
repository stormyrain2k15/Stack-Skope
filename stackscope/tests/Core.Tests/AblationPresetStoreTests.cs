using StackScope.Core.Models;
using Xunit;

namespace StackScope.Core.Tests;

/// <summary>
/// Persistence + upsert contract for the Ablation Preset library.
/// Source of truth for what <see cref="AblationPresetsViewModel"/>
/// in the WPF layer can rely on.
/// </summary>
public class AblationPresetStoreTests
{
    private static string FreshDbPath()
        => Path.Combine(Path.GetTempPath(), $"ss-presets-{Guid.NewGuid():N}.sqlite");

    private static AblationPreset Sample(string name = "L5 sweep",
                                         int ls = 4, int le = 6,
                                         int hs = 0, int he = 3,
                                         string prompt = "hello world",
                                         double sigma = 1.25)
        => new AblationPreset(
            Id: 0, Name: name,
            LayerStart: ls, LayerEnd: le,
            HeadStart:  hs, HeadEnd:  he,
            Prompt: prompt, Seed: 42, SigmaThreshold: sigma,
            CreatedAtNs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L);

    [Fact]
    public void Upsert_And_List_Roundtrips_All_Fields()
    {
        var path = FreshDbPath();
        try
        {
            using var store = new AblationPresetStore(path);
            var id = store.Upsert(Sample());
            Assert.True(id > 0);
            var got = store.List().Single();
            Assert.Equal(id, got.Id);
            Assert.Equal("L5 sweep",   got.Name);
            Assert.Equal(4,  got.LayerStart);
            Assert.Equal(6,  got.LayerEnd);
            Assert.Equal(0,  got.HeadStart);
            Assert.Equal(3,  got.HeadEnd);
            Assert.Equal("hello world", got.Prompt);
            Assert.Equal(42ul, got.Seed);
            Assert.Equal(1.25, got.SigmaThreshold);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Upsert_Replaces_On_Duplicate_Name_Without_Duplicating_Row()
    {
        var path = FreshDbPath();
        try
        {
            using var store = new AblationPresetStore(path);
            var first  = store.Upsert(Sample(name: "study", ls: 1, le: 2));
            var second = store.Upsert(Sample(name: "study", ls: 10, le: 20));
            Assert.Equal(first, second);           // same row id, upsert path
            var rows = store.List();
            Assert.Single(rows);                    // no duplicate
            Assert.Equal(10, rows[0].LayerStart);   // new values won
            Assert.Equal(20, rows[0].LayerEnd);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Upsert_Rejects_Blank_Name()
    {
        var path = FreshDbPath();
        try
        {
            using var store = new AblationPresetStore(path);
            Assert.Throws<ArgumentException>(() => store.Upsert(Sample(name: "")));
            Assert.Throws<ArgumentException>(() => store.Upsert(Sample(name: "   ")));
            Assert.Empty(store.List());
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Delete_Removes_Only_The_Targeted_Row()
    {
        var path = FreshDbPath();
        try
        {
            using var store = new AblationPresetStore(path);
            var keep = store.Upsert(Sample(name: "keep"));
            var kill = store.Upsert(Sample(name: "kill"));
            Assert.Equal(1, store.Delete(kill));
            Assert.Single(store.List());
            Assert.Equal(keep, store.List()[0].Id);
            Assert.Equal(0, store.Delete(99999));   // missing id → no-op
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Reopening_Db_Preserves_Presets()
    {
        var path = FreshDbPath();
        try
        {
            long id;
            using (var store = new AblationPresetStore(path))
                id = store.Upsert(Sample(name: "durable"));
            using var reopened = new AblationPresetStore(path);
            var got = reopened.List().Single();
            Assert.Equal(id, got.Id);
            Assert.Equal("durable", got.Name);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
