namespace PublicHouse28Scheduler;

/// <summary>
/// A plain-English scheduling assistant backed by some LLM provider. Implementations wrap a
/// specific API (Claude, Gemini, …) but expose the same surface to the UI, so the provider can
/// be swapped without the rest of the app caring.
/// </summary>
public interface IAssistant
{
    /// <summary>Whether an API key for this provider was found in the environment.</summary>
    bool HasApiKey { get; }

    /// <summary>Human-friendly provider name, e.g. "Claude" or "Gemini".</summary>
    string ProviderName { get; }

    /// <summary>The environment variable this provider reads its API key from.</summary>
    string ApiKeyEnvVar { get; }

    /// <summary>Send one user message, run any tool calls to completion, return the reply text.</summary>
    Task<string> SendAsync(string userMessage);
}
