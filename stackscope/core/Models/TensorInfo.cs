namespace StackScope.Core.Models;

/// <summary>
/// One tensor discovered in a model archive. Immutable; carries just
/// enough metadata for the UI to display without touching the archive.
/// </summary>
public sealed record TensorInfo(
    string Name,
    IReadOnlyList<long> Shape,
    string DType,
    QuantizationInfo Quantization,
    long ByteOffset,
    long ByteLength,
    string? SourceFile = null,
    string? Sha256 = null)
{
    public long ElementCount
    {
        get
        {
            long n = 1;
            for (int i = 0; i < Shape.Count; i++) n *= Shape[i];
            return n;
        }
    }
}
