namespace StackScope.Core.Transactions;

/// <summary>
/// Kind of a captured event. Mirrors the protobuf enum so we can convert
/// losslessly at the gRPC boundary while keeping the domain layer free of
/// protobuf types.
/// </summary>
public enum EventKind : byte
{
    Unknown          = 0,
    TokenBegin       = 1,
    TokenEnd         = 2,
    LayerBegin       = 3,
    LayerEnd         = 4,
    AttentionQkv     = 5,
    AttentionScores  = 6,
    AttentionOutput  = 7,
    Activation       = 8,
    TensorRead       = 9,
    TensorWrite      = 10,
    KernelLaunch     = 11,
    KernelEnd        = 12,
    Memcpy           = 13,
    Alloc            = 14,
    Free             = 15,
    Logits           = 16,
    Sample           = 17,
    Marker           = 18
}
