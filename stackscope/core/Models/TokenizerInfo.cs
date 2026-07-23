namespace StackScope.Core.Models;

/// <summary>
/// Tokenizer metadata extracted from a HuggingFace repo's
/// <c>tokenizer.json</c> / <c>tokenizer_config.json</c>. Enough to
/// render the tokens view; not enough to actually tokenize (that job
/// belongs to the worker).
/// </summary>
public sealed record TokenizerInfo(
    string Kind,                  // "bpe" | "wordpiece" | "sentencepiece" | "unigram"
    int VocabSize,
    string? BosToken,
    string? EosToken,
    string? PadToken,
    string? UnkToken,
    IReadOnlyList<string> SpecialTokens,
    bool AddBosToken,
    bool AddEosToken);
