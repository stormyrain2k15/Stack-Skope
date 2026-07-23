using System.Windows.Input;

namespace StackScope.Desktop.Commands;

/// <summary>
/// The complete list of user-facing commands. Every one has a hotkey
/// and appears in the Command Palette (Ctrl+Shift+P). Adding a command
/// here automatically wires it into the palette and the keyboard system.
/// </summary>
public static class Commands
{
    public static readonly RoutedUICommand OpenProject       = New("Open Project…",       "OpenProject",       Key.O, ModifierKeys.Control);
    public static readonly RoutedUICommand OpenModel         = New("Open Model…",         "OpenModel",         Key.M, ModifierKeys.Control);
    public static readonly RoutedUICommand StartCapture      = New("Start Capture",       "StartCapture",      Key.F5);
    public static readonly RoutedUICommand StopCapture       = New("Stop Capture",        "StopCapture",       Key.F5, ModifierKeys.Shift);
    public static readonly RoutedUICommand FocusOverview     = New("View: Overview",      "FocusOverview",     Key.D1, ModifierKeys.Control);
    public static readonly RoutedUICommand FocusTokens       = New("View: Tokens",        "FocusTokens",       Key.D2, ModifierKeys.Control);
    public static readonly RoutedUICommand FocusLayers       = New("View: Layers",        "FocusLayers",       Key.D3, ModifierKeys.Control);
    public static readonly RoutedUICommand FocusAttention    = New("View: Attention",     "FocusAttention",    Key.D4, ModifierKeys.Control);
    public static readonly RoutedUICommand FocusActivations  = New("View: Activations",   "FocusActivations",  Key.D5, ModifierKeys.Control);
    public static readonly RoutedUICommand FocusTensors      = New("View: Tensors",       "FocusTensors",      Key.D6, ModifierKeys.Control);
    public static readonly RoutedUICommand FocusDriver       = New("View: Driver",        "FocusDriver",       Key.D7, ModifierKeys.Control);
    public static readonly RoutedUICommand FocusKernels      = New("View: Kernels",       "FocusKernels",      Key.D8, ModifierKeys.Control);
    public static readonly RoutedUICommand FocusMemory       = New("View: Memory",        "FocusMemory",       Key.D9, ModifierKeys.Control);
    public static readonly RoutedUICommand FocusTimeline     = New("View: Timeline",      "FocusTimeline",     Key.D0, ModifierKeys.Control);
    public static readonly RoutedUICommand FocusCompare      = New("View: Compare",       "FocusCompare",      Key.OemMinus, ModifierKeys.Control);
    public static readonly RoutedUICommand FocusLibrary      = New("View: Capture Library","FocusLibrary",     Key.OemPlus, ModifierKeys.Control);
    public static readonly RoutedUICommand BackSelection     = New("Selection: Back",     "BackSelection",     Key.Left,  ModifierKeys.Alt);
    public static readonly RoutedUICommand ForwardSelection  = New("Selection: Forward",  "ForwardSelection",  Key.Right, ModifierKeys.Alt);
    public static readonly RoutedUICommand DisclosureSimple  = New("Mode: Simple",        "DisclosureSimple",  Key.F1);
    public static readonly RoutedUICommand DisclosureAdvanced= New("Mode: Advanced",      "DisclosureAdvanced",Key.F2);
    public static readonly RoutedUICommand DisclosureForensic= New("Mode: Forensic",      "DisclosureForensic",Key.F3);
    public static readonly RoutedUICommand OpenPalette       = New("Command Palette",    "OpenPalette",       Key.P, ModifierKeys.Control | ModifierKeys.Shift);
    public static readonly RoutedUICommand SaveLayout        = New("Layout: Save",       "SaveLayout",        Key.S, ModifierKeys.Control | ModifierKeys.Shift);
    public static readonly RoutedUICommand ResetLayout       = New("Layout: Reset",      "ResetLayout",       Key.R, ModifierKeys.Control | ModifierKeys.Shift);

    private static RoutedUICommand New(string text, string name, Key key,
                                       ModifierKeys mods = ModifierKeys.None)
    {
        var input = new InputGestureCollection { new KeyGesture(key, mods) };
        return new RoutedUICommand(text, name, typeof(Commands), input);
    }

    public static IEnumerable<RoutedUICommand> All => new[]
    {
        OpenProject, OpenModel, StartCapture, StopCapture,
        FocusOverview, FocusTokens, FocusLayers, FocusAttention, FocusActivations,
        FocusTensors, FocusDriver, FocusKernels, FocusMemory, FocusTimeline,
        FocusCompare, FocusLibrary,
        BackSelection, ForwardSelection,
        DisclosureSimple, DisclosureAdvanced, DisclosureForensic,
        OpenPalette, SaveLayout, ResetLayout
    };
}
