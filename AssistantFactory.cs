namespace PublicHouse28Scheduler;

/// <summary>
/// Picks which LLM provider drives the assistant. Set SCHEDULER_AI_PROVIDER to "claude" or
/// "gemini" to force one; otherwise we auto-pick based on which API key is present, defaulting
/// to Claude. This is the one place that knows about concrete providers — the rest of the app
/// only sees <see cref="IAssistant"/>.
/// </summary>
public static class AssistantFactory
{
    public static IAssistant Create(SchedulerService svc)
    {
        var provider = (Environment.GetEnvironmentVariable("SCHEDULER_AI_PROVIDER") ?? "").Trim().ToLowerInvariant();
        return provider switch
        {
            "gemini" or "google" => new GeminiAssistant(svc),
            "claude" or "anthropic" => new ClaudeAssistant(svc),
            _ => AutoSelect(svc),
        };
    }

    /// <summary>No explicit choice: use Gemini only if it's the one with a key, else default to Claude.</summary>
    private static IAssistant AutoSelect(SchedulerService svc)
    {
        bool hasGemini = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY"))
                      || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_API_KEY"));
        bool hasClaude = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

        if (hasGemini && !hasClaude) return new GeminiAssistant(svc);
        return new ClaudeAssistant(svc);
    }
}
