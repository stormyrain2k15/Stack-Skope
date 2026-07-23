using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StackScope.Desktop.State;
using StackScope.Services;

namespace StackScope.Desktop.ViewModels;

/// <summary>
/// Project tree — a filesystem-and-transaction hybrid tree rooted at
/// the current project. Nodes: project root → captures/ → each txn
/// (partial or complete) → per-view shortcuts.
/// </summary>
public sealed partial class ProjectTreeViewModel : ObservableObject
{
    private readonly ProjectService _project;

    public ObservableCollection<Node> Nodes { get; } = new();
    [ObservableProperty] private Node? selectedNode;

    public string ProjectRoot => _project.RootDir;

    public ProjectTreeViewModel(ProjectService project)
    {
        _project = project;
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        Nodes.Clear();
        var root = new Node("Project", "📁", _project.RootDir, "root");
        var caps = new Node("Captures", "🎯", "", "captures");
        foreach (var t in _project.ListTransactions())
        {
            var status = t.Completed ? "✓ complete"
                        : t.Error is not null ? "⚠ recovered" : "… partial";
            var txnNode = new Node(t.TransactionId, "•", status, "transaction");
            txnNode.PayloadTransactionId = t.TransactionId;
            caps.Children.Add(txnNode);
        }
        root.Children.Add(caps);
        Nodes.Add(root);
    }

    partial void OnSelectedNodeChanged(Node? value)
    {
        if (value is null) return;
        if (value.Kind == "transaction" && value.PayloadTransactionId is not null)
        {
            SelectionState.Current.TransactionId = value.PayloadTransactionId;
            WorkspaceState.Current.CurrentTransactionId = value.PayloadTransactionId;
        }
    }

    public sealed class Node
    {
        public string Label { get; }
        public string Icon  { get; }
        public string Detail{ get; }
        public string Kind  { get; }
        public string? PayloadTransactionId { get; set; }
        public ObservableCollection<Node> Children { get; } = new();
        public Node(string label, string icon, string detail, string kind)
        {
            Label = label; Icon = icon; Detail = detail; Kind = kind;
        }
    }
}
