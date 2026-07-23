namespace StackScope.Core.Models;

/// <summary>
/// Detected transformer architecture family. Determines which
/// architecture adapter normalises the layer graph.
/// </summary>
public enum ArchitectureFamily
{
    Unknown,
    Llama,      // Llama, Llama2, Llama3, CodeLlama
    Gemma,      // Gemma, Gemma2
    Qwen2,      // Qwen2, Qwen2.5
    Mistral,    // Mistral, Mixtral (MoE)
    Gpt2,       // GPT-2 and derivatives
    Gptneo,
    Falcon,
    Phi,
    Bert,
    T5
}
