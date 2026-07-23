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
        bool completed, string modelHandle = "m-0", long startedNs = 0,
        int ablateLayerEnd = -1, int ablateHeadEnd = -1)
    {
        using var store = project.OpenOrCreateStore(txid);
        store.Index.SetMeta("transaction_id", txid);
        store.Index.SetMeta("model_handle", modelHandle);
        store.Index.SetMeta("architecture", "test-arch");
        store.Index.SetMeta("started_ns", startedNs.ToString());
        store.Index.SetMeta("ended_ns",   (startedNs + 1).ToString());
        store.Index.SetMeta("completed",  completed ? "true" : "false");
        store.Index.SetMeta("prompt", prompt);
        store.Index.SetMeta("ablate_layer",     ablateLayer.ToString());
        store.Index.SetMeta("ablate_head",      ablateHead.ToString());
        store.Index.SetMeta("ablate_layer_end", ablateLayerEnd.ToString());
        store.Index.SetMeta("ablate_head_end",  ablateHeadEnd.ToString());
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
        var t1 = new TransactionMetadata("t1", "m", "arch", 0, 0, true, null, "p", -1, -1, null);
        var t2 = new TransactionMetadata("t2", "m", "arch", 0, 0, true, null, "p",  5, -1, null);
        var t3 = new TransactionMetadata("t3", "m", "arch", 0, 0, true, null, "p", -1,  2, null);
        var t4 = new TransactionMetadata("t4", "m", "arch", 0, 0, true, null, "p",  5,  2, null);
        Assert.False(t1.WasAblated);
        Assert.False(t2.WasAblated);
        Assert.False(t3.WasAblated);
        Assert.True (t4.WasAblated);
    }

    [Fact]
    public void IsAblationRange_Detects_Rectangular_Ranges()
    {
        // Single cell — WasAblated true, IsAblationRange false.
        var single = new TransactionMetadata("t", "m", "arch", 0, 0, true, null, "p",
            AblateLayer: 3, AblateHead: 1, CaptureCeiling: null,
            AblateLayerEnd: -1, AblateHeadEnd: -1);
        Assert.True (single.WasAblated);
        Assert.False(single.IsAblationRange);

        // Layer range only.
        var layers = single with { AblateLayerEnd = 5 };
        Assert.True(layers.IsAblationRange);

        // Head range only.
        var heads = single with { AblateHeadEnd = 4 };
        Assert.True(heads.IsAblationRange);

        // Full rectangle.
        var rect = single with { AblateLayerEnd = 5, AblateHeadEnd = 4 };
        Assert.True(rect.IsAblationRange);

        // Ends equal to starts — degenerate, still just single cell.
        var degenerate = single with { AblateLayerEnd = 3, AblateHeadEnd = 1 };
        Assert.False(degenerate.IsAblationRange);
    }

    [Fact]
    public void HasCaptureCeiling_Reflects_Meta_Row()
    {
        var t1 = new TransactionMetadata("t1", "m", "arch", 0, 0, true, null, "p", -1, -1, null);
        var t2 = new TransactionMetadata("t2", "m", "arch", 0, 0, true, null, "p", -1, -1, "");
        var t3 = new TransactionMetadata("t3", "m", "arch", 0, 0, true, null, "p", -1, -1,
            "stackscope.capture_ceiling: llama.cpp SIMPLE only");
        Assert.False(t1.HasCaptureCeiling);
        Assert.False(t2.HasCaptureCeiling);
        Assert.True (t3.HasCaptureCeiling);
    }

    [Fact]
    public void ListTransactions_Roundtrips_CaptureCeiling_Meta()
    {
        var root = FreshProjectRoot();
        try
        {
            var project = new ProjectService(root);
            using (var store = project.OpenOrCreateStore("01CEILING"))
            {
                store.Index.SetMeta("model_handle", "m");
                store.Index.SetMeta("architecture", "arch");
                store.Index.SetMeta("started_ns", "1");
                store.Index.SetMeta("ended_ns",   "2");
                store.Index.SetMeta("completed",  "true");
                store.Index.SetMeta("prompt", "hi");
                store.Index.SetMeta("ablate_layer", "-1");
                store.Index.SetMeta("ablate_head",  "-1");
                store.Index.SetMeta("ablate_layer_end", "-1");
                store.Index.SetMeta("ablate_head_end",  "-1");
                store.Index.SetMeta("capture_ceiling",
                    "stackscope.capture_ceiling: llama.cpp SIMPLE only");
                store.Flush();
            }
            var t = project.ListTransactions().Single(x => x.TransactionId == "01CEILING");
            Assert.True(t.HasCaptureCeiling);
            Assert.Contains("SIMPLE only", t.CaptureCeiling!);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ListTransactions_Roundtrips_Ablation_Range_Meta()
    {
        var root = FreshProjectRoot();
        try
        {
            var project = new ProjectService(root);
            WriteCapture(project, "01RANGE", "prompt",
                ablateLayer: 4, ablateHead: 0, completed: true,
                ablateLayerEnd: 6, ablateHeadEnd: 3);
            var t = project.ListTransactions().Single(x => x.TransactionId == "01RANGE");
            Assert.Equal(4, t.AblateLayer);
            Assert.Equal(0, t.AblateHead);
            Assert.Equal(6, t.AblateLayerEnd);
            Assert.Equal(3, t.AblateHeadEnd);
            Assert.True(t.IsAblationRange);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
