using System.IO;
using Microsoft.Win32;

namespace StackScope.Desktop.Services;

/// <summary>
/// Thin wrappers over WPF file/folder dialogs. All bundle- and
/// capture-path pickers in the UI go through this so we get a
/// consistent experience and one place to change the file filters.
/// </summary>
public static class FilePicker
{
    public static string? PickBundle(string title = "Open .stackscope bundle")
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = "StackScope bundle|*.stackscope|Zip|*.zip|All|*.*",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public static string? SaveBundle(string suggestedName = "capture.stackscope")
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save .stackscope bundle",
            Filter = "StackScope bundle|*.stackscope",
            FileName = suggestedName,
            DefaultExt = ".stackscope",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public static string? PickFolder(string title = "Choose folder")
    {
        var dlg = new OpenFolderDialog { Title = title };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    public static string? PickJsonlFile()
    {
        var dlg = new OpenFileDialog { Filter = "JSONL|*.jsonl|All|*.*" };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
