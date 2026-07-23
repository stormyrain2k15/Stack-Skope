using StackScope.Core.Storage;
using StackScope.Services;
using Xunit;

namespace StackScope.Core.Tests;

/// <summary>
/// Proves the auto-compare flow: after an ablated capture,
/// <see cref="ProjectService.FindLatestNonAblatedBaseline"/> must
/// return the newest completed non-ablated run of the same prompt on
/// the same model, and must return null when no such baseline exists.
/// These are the two branches WPF's OnStartCapture depends on to
/// decide whether to seed CompareVm.
/// </summary>
public class ProjectServiceAutoCompareTests
{
    private static string FreshProjectRoot()
        => Path.Combine(Path.GetTempPath(), "ss-project-" + Guid.NewGuid().ToString("N"));

    private static void WriteCapture(ProjectService project, string txid,
        string prompt, int ablateLayer, int ablateHead,
        bool completed, string modelHandle = "m-0", long startedNs = 0)
    {
        using var store = project.OpenOrCreateStore(txid);
        store.Index.SetMeta("transaction_id", txid);
        store.Index.SetMeta("model_handle", modelHandle);
        store.Index.SetMeta("architecture", "test-arch");
        store.Index.SetMeta("started_ns", startedNs.ToString());
        store.Index.SetMeta("ended_ns",   (startedNs + 1).ToString());
        store.Index.SetMeta("completed",  completed ? "true" : "false");
        store.Index.SetMeta("prompt", prompt);
        store.Index.SetMeta("ablate_layer", ablateLayer.ToString());
        store.Index.SetMeta("ablate_head",  ablateHead.ToString());
        store.Flush();
    }

    [Fact]
    public void Finds_Newest_Non_Ablated_Baseline_With_Same_Prompt()
    {
        var root = FreshProjectRoot();
        try
        {
            var project = new ProjectService(root);

            // Baselines and ablated runs interleaved. Ablated run is
            // newest but we want the newest *non-ablated* peer.
            WriteCapture(project, "01OLD", "hello world", -1, -1, completed: true, startedNs: 100);
            WriteCapture(project, "02NEW_BASE", "hello world", -1, -1, completed: true, startedNs: 200);
            WriteCapture(project, "03OTHER_PROMPT", "different", -1, -1, completed: true, startedNs: 300);
            WriteCapture(project, "04ABLATED", "hello world", 5, 2, completed: true, startedNs: 400);

            var txns = project.ListTransactions();
            var ablated = txns.Single(t => t.TransactionId == "04ABLATED");
            Assert.True(ablated.WasAblated);
            Assert.Equal(5, ablated.AblateLayer);
            Assert.Equal(2, ablated.AblateHead);

            var baseline = project.FindLatestNonAblatedBaseline(ablated);
            Assert.NotNull(baseline);
            Assert.Equal("02NEW_BASE", baseline!.TransactionId);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Ignores_Partial_Captures_When_Selecting_Baseline()
    {
        var root = FreshProjectRoot();
        try
        {
            var project = new ProjectService(root);
            // Partial (completed=false) baseline is newer but must be
            // skipped — auto-compare should never point at a truncated run.
            WriteCapture(project, "01COMPLETE", "prompt", -1, -1, completed: true,  startedNs: 100);
            WriteCapture(project, "02PARTIAL",  "prompt", -1, -1, completed: false, startedNs: 200);
            WriteCapture(project, "03ABLATED",  "prompt",  1,  0, completed: true,  startedNs: 300);

            var ablated = project.ListTransactions()
                .Single(t => t.TransactionId == "03ABLATED");
            var baseline = project.FindLatestNonAblatedBaseline(ablated);
            Assert.NotNull(baseline);
            Assert.Equal("01COMPLETE", baseline!.TransactionId);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Returns_Null_When_No_Matching_Baseline_Exists()
    {
        var root = FreshProjectRoot();
        try
        {
            var project = new ProjectService(root);
            // Only ablated runs and a different-prompt baseline.
            WriteCapture(project, "01WRONG_PROMPT", "different", -1, -1, completed: true, startedNs: 100);
            WriteCapture(project, "02ABLATED_A",    "target",     3,  1, completed: true, startedNs: 200);
            WriteCapture(project, "03ABLATED_B",    "target",     3,  2, completed: true, startedNs: 300);

            var ablated = project.ListTransactions()
                .Single(t => t.TransactionId == "03ABLATED_B");
            var baseline = project.FindLatestNonAblatedBaseline(ablated);
            Assert.Null(baseline);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Prefers_Same_Model_When_Both_Sides_Have_A_Handle()
    {
        var root = FreshProjectRoot();
        try
        {
            var project = new ProjectService(root);
            // Newer baseline on a different model must be rejected; the
            // older baseline on the same model wins.
            WriteCapture(project, "01SAME_MODEL",  "prompt", -1, -1, completed: true,
                         modelHandle: "m-A", startedNs: 100);
            WriteCapture(project, "02OTHER_MODEL","prompt", -1, -1, completed: true,
                         modelHandle: "m-B", startedNs: 200);
            WriteCapture(project, "03ABLATED",    "prompt",  0,  0, completed: true,
                         modelHandle: "m-A", startedNs: 300);

            var ablated = project.ListTransactions()
                .Single(t => t.TransactionId == "03ABLATED");
            var baseline = project.FindLatestNonAblatedBaseline(ablated);
            Assert.NotNull(baseline);
            Assert.Equal("01SAME_MODEL", baseline!.TransactionId);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void WasAblated_Is_True_Only_When_Both_Indices_Are_Set()
    {
        var t1 = new TransactionMetadata("t1", "m", "arch", 0, 0, true, null, "p", -1, -1);
        var t2 = new TransactionMetadata("t2", "m", "arch", 0, 0, true, null, "p",  5, -1);
        var t3 = new TransactionMetadata("t3", "m", "arch", 0, 0, true, null, "p", -1,  2);
        var t4 = new TransactionMetadata("t4", "m", "arch", 0, 0, true, null, "p",  5,  2);
        Assert.False(t1.WasAblated);
        Assert.False(t2.WasAblated);
        Assert.False(t3.WasAblated);
        Assert.True (t4.WasAblated);
    }
}
