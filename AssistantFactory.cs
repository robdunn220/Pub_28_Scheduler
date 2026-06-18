namespace PublicHouse28Scheduler;

/// <summary>
/// Picks which LLM provider drives the assistant. Set SCHEDULER_AI_PROVIDER to "claude" or
/// "gemini" to force one; otherwise we auto-pick based on which API key is present, defaulting
/// to Claude. This is the one place that knows about concrete providers — the rest of the app
/// only sees <see cref="IAssistant"/>.
/// </summary>
public static class AssistantFactory
{
    /// <param name="currentWeekStart">
    /// Optional accessor for the Monday of the week the UI is currently showing, so the assistant's
    /// export tool can default to "the schedule in view".
    /// </param>
    public static IAssistant Create(SchedulerService svc, Func<DateTime>? currentWeekStart = null)
    {
        var provider = (Environment.GetEnvironmentVariable("SCHEDULER_AI_PROVIDER") ?? "").Trim().ToLowerInvariant();
        return provider switch
        {
            "gemini" or "google" => new GeminiAssistant(svc, currentWeekStart),
            "claude" or "anthropic" => new ClaudeAssistant(svc, currentWeekStart),
            _ => AutoSelect(svc, currentWeekStart),
        };
    }

    /// <summary>No explicit choice: use Gemini only if it's the one with a key, else default to Claude.</summary>
    private static IAssistant AutoSelect(SchedulerService svc, Func<DateTime>? currentWeekStart)
    {
        bool hasGemini = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY"))
                      || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_API_KEY"));
        bool hasClaude = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

        if (hasGemini && !hasClaude) return new GeminiAssistant(svc, currentWeekStart);
        return new ClaudeAssistant(svc, currentWeekStart);
    }
}
