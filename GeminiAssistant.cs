using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PublicHouse28Scheduler;

/// <summary>
/// Drives the schedule with Google's Gemini (Flash) over the REST generateContent API. Same idea
/// as <see cref="ClaudeAssistant"/> — a manual function-calling loop where OUR code runs every tool
/// via <see cref="SchedulerTools"/> — but spoken in Gemini's wire format. No SDK dependency: the
/// request/response are plain JSON over HttpClient.
/// </summary>
public class GeminiAssistant : IAssistant
{
    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models/";
    private const string DefaultModel = "gemini-2.5-flash";

    private static readonly HttpClient Http = new();

    private readonly SchedulerTools _tools;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly JsonArray _functionDeclarations;
    private readonly JsonArray _contents = new(); // conversation history (user/model turns)

    public bool HasApiKey { get; }
    public string ProviderName => "Gemini";
    public string ApiKeyEnvVar => "GEMINI_API_KEY";

    public GeminiAssistant(SchedulerService svc, Func<DateTime>? currentWeekStart = null)
    {
        _tools = new SchedulerTools(svc, currentWeekStart);
        _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
               ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        HasApiKey = !string.IsNullOrWhiteSpace(_apiKey);
        _model = Environment.GetEnvironmentVariable("GEMINI_MODEL") is { Length: > 0 } m ? m : DefaultModel;
        _functionDeclarations = BuildFunctionDeclarations();
    }

    public async Task<string> SendAsync(string userMessage)
    {
        if (!HasApiKey)
            return $"⚠️  No API key found. Set the {ApiKeyEnvVar} environment variable, then restart the app. " +
                   "You can get a key from https://aistudio.google.com/apikey.";

        _contents.Add(new JsonObject
        {
            ["role"] = "user",
            ["parts"] = new JsonArray { new JsonObject { ["text"] = userMessage } },
        });

        try
        {
            for (int iteration = 0; iteration < 10; iteration++)
            {
                var body = new JsonObject
                {
                    ["systemInstruction"] = new JsonObject
                    {
                        ["parts"] = new JsonArray { new JsonObject { ["text"] = SchedulerTools.SystemPrompt() } },
                    },
                    ["contents"] = _contents.DeepClone(),
                    ["tools"] = new JsonArray
                    {
                        new JsonObject { ["functionDeclarations"] = _functionDeclarations.DeepClone() },
                    },
                };

                using var req = new HttpRequestMessage(HttpMethod.Post, $"{Endpoint}{_model}:generateContent");
                req.Headers.Add("x-goog-api-key", _apiKey!);
                req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

                var resp = await Http.SendAsync(req);
                var respText = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    return $"⚠️  Gemini API error ({(int)resp.StatusCode}): {Truncate(respText)}";

                var content = JsonNode.Parse(respText)?["candidates"]?[0]?["content"];
                if (content?["parts"] is not JsonArray parts)
                    return "⚠️  Gemini returned no usable content. " + Truncate(respText);

                var turnText = new StringBuilder();
                var functionResponses = new JsonArray();

                foreach (var part in parts)
                {
                    if (part?["text"] is JsonNode textNode)
                        turnText.AppendLine(textNode.GetValue<string>());
                    else if (part?["functionCall"] is JsonObject call)
                    {
                        var name = call["name"]?.GetValue<string>() ?? "";
                        var id = call["id"]?.ToString();

                        JsonElement args = default;
                        if (call["args"] is JsonNode argsNode)
                            args = JsonSerializer.Deserialize<JsonElement>(argsNode.ToJsonString());

                        string result = _tools.Execute(name, args);

                        var fr = new JsonObject
                        {
                            ["name"] = name,
                            ["response"] = new JsonObject { ["result"] = result },
                        };
                        if (!string.IsNullOrEmpty(id)) fr["id"] = id; // Gemini 3 maps results back by id

                        functionResponses.Add(new JsonObject { ["functionResponse"] = fr });
                    }
                }

                // Echo the model's turn (incl. any functionCall parts) back into history.
                var modelTurn = content!.DeepClone();
                if (modelTurn["role"] is null) modelTurn["role"] = "model";
                _contents.Add(modelTurn);

                if (functionResponses.Count == 0)
                    return turnText.ToString().Trim();

                // Feed the tool results back and let the model continue.
                _contents.Add(new JsonObject { ["role"] = "user", ["parts"] = functionResponses });
            }

            return "(Stopped after several tool calls without a final answer.)";
        }
        catch (Exception ex)
        {
            return $"⚠️  Couldn't reach the assistant: {ex.Message}";
        }
    }

    /// <summary>Offline check hooks: build the Gemini schema from the shared catalogue without any key/network.</summary>
    public static int ToolSchemaCount() => BuildFunctionDeclarations().Count;
    public static string ToolSchemaJson() =>
        BuildFunctionDeclarations().ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Convert the shared tool catalogue into Gemini's functionDeclarations format.</summary>
    private static JsonArray BuildFunctionDeclarations()
    {
        var declarations = new JsonArray();
        foreach (var d in SchedulerTools.Definitions)
        {
            var fn = new JsonObject
            {
                ["name"] = d.Name,
                ["description"] = d.Description,
            };

            if (d.Properties.Count > 0)
            {
                var props = new JsonObject();
                foreach (var (key, schema) in d.Properties)
                    props[key] = JsonNode.Parse(schema.GetRawText());

                var parameters = new JsonObject { ["type"] = "object", ["properties"] = props };
                if (d.Required.Length > 0)
                {
                    var required = new JsonArray();
                    foreach (var r in d.Required) required.Add(r);
                    parameters["required"] = required;
                }
                fn["parameters"] = parameters;
            }

            declarations.Add(fn);
        }
        return declarations;
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500] + "…";
}
