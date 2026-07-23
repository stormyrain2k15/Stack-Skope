namespace StackScope.Core.Models;

/// <summary>
/// Quantization scheme detected on a tensor.
/// </summary>
public enum QuantizationScheme
{
    None,
    F16,
    BF16,
    F32,
    F64,
    I8,
    Q8_0,
    Q4_0,
    Q4_1,
    Q4_K,
    Q5_0,
    Q5_1,
    Q5_K,
    Q6_K,
    Q2_K,
    Q3_K,
    IQ2_XXS,
    IQ2_XS,
    IQ3_XXS,
    IQ1_S
}

/// <summary>
/// Quantization details for a tensor. If <see cref="Scheme"/> is
/// <see cref="QuantizationScheme.None"/> the other fields are ignored.
/// </summary>
public sealed record QuantizationInfo(
    QuantizationScheme Scheme,
    int BlockSize,
    long BitsPerElement,
    bool HasScale,
    bool HasZeroPoint)
{
    public static readonly QuantizationInfo Fp32 =
        new(QuantizationScheme.F32, 1, 32, false, false);

    public static readonly QuantizationInfo Fp16 =
        new(QuantizationScheme.F16, 1, 16, false, false);

    public static readonly QuantizationInfo Bf16 =
        new(QuantizationScheme.BF16, 1, 16, false, false);
}
