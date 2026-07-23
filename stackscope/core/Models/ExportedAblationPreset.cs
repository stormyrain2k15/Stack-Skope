namespace StackScope.Core.Models;

/// <summary>
/// Portable, versioned JSON schema for an exported ablation preset.
/// Lives in <c>StackScope.Core</c> (not in the WPF desktop project) so
/// non-UI tooling — Core tests, headless bundlers, MCP server export
/// endpoints — can read/write the shared file format without pulling
/// in WPF dependencies.
///
/// <para>Keep <see cref="SchemaVersion"/> monotonically increasing.
/// Old versions of StackScope refuse to import a newer schema instead
/// of silently misinterpreting fields.</para>
/// </summary>
public sealed class ExportedAblationPreset
{
    public int    SchemaVersion  { get; set; } = 1;
    public string Name           { get; set; } = "";
    public int    LayerStart     { get; set; }
    public int    LayerEnd       { get; set; }
    public int    HeadStart      { get; set; }
    public int    HeadEnd        { get; set; }
    public string Prompt         { get; set; } = "";
    public ulong  Seed           { get; set; }
    public double SigmaThreshold { get; set; } = 1.0;
}
