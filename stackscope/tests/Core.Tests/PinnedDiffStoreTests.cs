using StackScope.Core.Models;
using Xunit;

namespace StackScope.Core.Tests;

/// <summary>
/// Persistence + CRUD contract for the Diff Pin Board.
/// These tests are the source of truth for what
/// <see cref="PinnedDiffsViewModel"/> in the WPF layer can rely on.
/// </summary>
public class PinnedDiffStoreTests
{
    private static string FreshDbPath()
        => Path.Combine(Path.GetTempPath(), $"ss-pins-{Guid.NewGuid():N}.sqlite");

    private static PinnedDiff SamplePin(string left = "01TX_LEFT",
                                        string right = "02TX_RIGHT",
                                        double sigma = 1.5,
                                        string note = "why the head matters",
                                        string tags = "ablation,L5H3")
        => new PinnedDiff(
            Id: 0,
            LeftTransactionId: left,
            RightTransactionId: right,
            SigmaThreshold: sigma,
            CreatedAtNs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L,
            Note: note,
            Tags: tags);

    [Fact]
    public void Add_Then_List_Roundtrips_All_Fields()
    {
        var path = FreshDbPath();
        try
        {
            using var store = new PinnedDiffStore(path);
            var id = store.Add(SamplePin());
            Assert.True(id > 0);
            var pins = store.List();
            Assert.Single(pins);
            var p = pins[0];
            Assert.Equal(id, p.Id);
            Assert.Equal("01TX_LEFT",  p.LeftTransactionId);
            Assert.Equal("02TX_RIGHT", p.RightTransactionId);
            Assert.Equal(1.5, p.SigmaThreshold);
            Assert.Equal("why the head matters", p.Note);
            Assert.Equal("ablation,L5H3",        p.Tags);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void List_Returns_Newest_First()
    {
        var path = FreshDbPath();
        try
        {
            using var store = new PinnedDiffStore(path);
            var old = store.Add(SamplePin(note: "old") with {
                CreatedAtNs = 100 });
            var mid = store.Add(SamplePin(note: "mid") with {
                CreatedAtNs = 200 });
            var latest = store.Add(SamplePin(note: "latest") with {
                CreatedAtNs = 300 });
            var pins = store.List();
            Assert.Equal(3, pins.Count);
            Assert.Equal(latest, pins[0].Id);
            Assert.Equal(mid,    pins[1].Id);
            Assert.Equal(old,    pins[2].Id);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Add_Rejects_Empty_Transaction_Ids()
    {
        var path = FreshDbPath();
        try
        {
            using var store = new PinnedDiffStore(path);
            // Left blank
            Assert.Throws<ArgumentException>(() =>
                store.Add(SamplePin(left: "")));
            // Right blank
            Assert.Throws<ArgumentException>(() =>
                store.Add(SamplePin(right: "   ")));
            // Nothing was written on rejection.
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
            using var store = new PinnedDiffStore(path);
            var keep = store.Add(SamplePin(note: "keep"));
            var kill = store.Add(SamplePin(note: "kill"));
            Assert.Equal(1, store.Delete(kill));
            var remaining = store.List();
            Assert.Single(remaining);
            Assert.Equal(keep, remaining[0].Id);
            // Deleting a phantom id is a no-op, not an exception.
            Assert.Equal(0, store.Delete(99999));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void UpdateNote_Persists_Text_And_Tags()
    {
        var path = FreshDbPath();
        try
        {
            using var store = new PinnedDiffStore(path);
            var id = store.Add(SamplePin(note: "before", tags: "old"));
            Assert.Equal(1, store.UpdateNote(id, "after", "new,tags"));
            var pin = store.List().Single();
            Assert.Equal("after",    pin.Note);
            Assert.Equal("new,tags", pin.Tags);
            // Missing id -> 0 rows changed, no throw.
            Assert.Equal(0, store.UpdateNote(99999, "x", "y"));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Reopening_The_Db_Preserves_Pins()
    {
        var path = FreshDbPath();
        try
        {
            long id;
            using (var store = new PinnedDiffStore(path))
                id = store.Add(SamplePin(note: "durable"));

            using var reopened = new PinnedDiffStore(path);
            var pin = reopened.List().Single();
            Assert.Equal(id, pin.Id);
            Assert.Equal("durable", pin.Note);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
