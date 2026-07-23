using StackScope.Core.Models;
using Xunit;

namespace StackScope.Core.Tests;

public class AnnotationStoreTests
{
    [Fact]
    public void Add_And_List_Round_Trip()
    {
        string db = Path.Combine(Path.GetTempPath(), $"anno-{Guid.NewGuid():N}.sqlite");
        try
        {
            using var s = new AnnotationStore(db);
            var id = s.Add(new SnapshotAnnotation(
                Id: 0, TransactionId: "01HKQ",
                EventId: 42UL, Layer: 3, Head: 2, Token: 0,
                CreatedAtNs: 1_700_000_000_000_000_000L,
                Author: "dev",
                Text: "This head zeros the subject-verb agreement.",
                Tags: "circuit,syntax"));
            Assert.True(id > 0);

            var all = s.List("01HKQ");
            Assert.Single(all);
            Assert.Equal("dev", all[0].Author);
            Assert.Equal(3, all[0].Layer);
            Assert.Equal(2, all[0].Head);
        }
        finally { try { File.Delete(db); } catch { } }
    }

    [Fact]
    public void ExportMarkdown_Contains_Location_And_Text()
    {
        string db = Path.Combine(Path.GetTempPath(), $"anno-{Guid.NewGuid():N}.sqlite");
        try
        {
            using var s = new AnnotationStore(db);
            s.Add(new SnapshotAnnotation(
                Id: 0, TransactionId: "01HKQ",
                EventId: 42UL, Layer: 3, Head: 2, Token: 0,
                CreatedAtNs: 1L, Author: "dev",
                Text: "Interesting pattern here.", Tags: "note"));
            var md = s.ExportMarkdown("01HKQ");
            Assert.Contains("StackScope research notes", md);
            Assert.Contains("L3", md);
            Assert.Contains("H2", md);
            Assert.Contains("Interesting pattern here.", md);
        }
        finally { try { File.Delete(db); } catch { } }
    }
}
