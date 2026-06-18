using System.Text;
using Anthropic;
using Anthropic.Models.Messages;

namespace PublicHouse28Scheduler;

/// <summary>
/// Drives the schedule with Anthropic's Claude. The user types plain English; Claude decides which
/// scheduling tool to call; we execute it via <see cref="SchedulerTools"/> and feed the result back.
/// This is a manual tool-use loop, so OUR code performs every change to the schedule.
/// </summary>
public class ClaudeAssistant : IAssistant
{
    private readonly SchedulerTools _tools;
    private readonly AnthropicClient? _client;
    private readonly List<ToolUnion> _apiTools;
    private readonly List<MessageParam> _messages = new();

    public bool HasApiKey { get; }
    public string ProviderName => "Claude";
    public string ApiKeyEnvVar => "ANTHROPIC_API_KEY";

    public ClaudeAssistant(SchedulerService svc, Func<DateTime>? currentWeekStart = null)
    {
        _tools = new SchedulerTools(svc, currentWeekStart);
        var apiKey = Environment.GetEnvironmentVariable(ApiKeyEnvVar);
        HasApiKey = !string.IsNullOrWhiteSpace(apiKey);
        if (HasApiKey)
            _client = new AnthropicClient { ApiKey = apiKey };
        _apiTools = BuildTools();
    }

    public async Task<string> SendAsync(string userMessage)
    {
        if (!HasApiKey || _client is null)
            return $"⚠️  No API key found. Set the {ApiKeyEnvVar} environment variable, then restart the app. " +
                   "You can get a key from https://console.anthropic.com/.";

        _messages.Add(new MessageParam { Role = Role.User, Content = userMessage });

        var finalText = new StringBuilder();

        try
        {
        for (int iteration = 0; iteration < 10; iteration++)
        {
            var parameters = new MessageCreateParams
            {
                Model = Model.ClaudeSonnet4_6,
                MaxTokens = 4096,
                System = SchedulerTools.SystemPrompt(),
                Tools = _apiTools,
                Messages = _messages,
            };

            Message response = await _client.Messages.Create(parameters);

            var assistantContent = new List<ContentBlockParam>();
            var toolResults = new List<ContentBlockParam>();
            bool calledTool = false;

            foreach (ContentBlock block in response.Content)
            {
                if (block.TryPickText(out TextBlock? text))
                {
                    assistantContent.Add(new TextBlockParam { Text = text!.Text });
                    finalText.AppendLine(text.Text);
                }
                else if (block.TryPickToolUse(out ToolUseBlock? toolUse))
                {
                    calledTool = true;
                    assistantContent.Add(new ToolUseBlockParam
                    {
                        ID = toolUse!.ID,
                        Name = toolUse.Name,
                        Input = toolUse.Input,
                    });

                    string result = _tools.Execute(toolUse.Name, SchedulerTools.ToElement(toolUse.Input));
                    toolResults.Add(new ToolResultBlockParam
                    {
                        ToolUseID = toolUse.ID,
                        Content = result,
                    });
                }
            }

            // Echo the assistant turn back into history.
            _messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });

            if (!calledTool)
                return finalText.ToString().Trim();

            // Feed tool results back and let the model continue.
            _messages.Add(new MessageParam { Role = Role.User, Content = toolResults });
            finalText.Clear(); // keep only the model's final narrative, not interim "let me..." text
        }

        return finalText.Length > 0 ? finalText.ToString().Trim()
                                    : "(Stopped after several tool calls without a final answer.)";
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("credit balance", StringComparison.OrdinalIgnoreCase))
                return "⚠️  The Anthropic account is out of credit. Add credits at "
                     + "console.anthropic.com → Plans & Billing, then try again.";
            return $"⚠️  Couldn't reach the assistant: {ex.Message}";
        }
    }

    /// <summary>Offline check hook: how many tools were built from the shared catalogue.</summary>
    public static int ToolSchemaCount() => BuildTools().Count;

    /// <summary>Convert the shared tool catalogue into Anthropic's tool schema format.</summary>
    private static List<ToolUnion> BuildTools() =>
        SchedulerTools.Definitions.Select(d => (ToolUnion)new Tool
        {
            Name = d.Name,
            Description = d.Description,
            InputSchema = new()
            {
                Properties = d.Properties,
                Required = [.. d.Required],
            },
        }).ToList();
}
