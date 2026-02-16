using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EmailExtractor.Lib.Agent;

public sealed record ChatMessage(string Role, string Content);

public sealed record ChatCompletion(string Content, int PromptTokens, int CompletionTokens);

public sealed class OpenAiClient : IOpenAiClient
{
    private const string CompletionsEndpoint = "https://api.openai.com/v1/chat/completions";

    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _maxTokens;
    private readonly double _temperature;
    private readonly HttpClient _http;

    public OpenAiClient(
        string apiKey,
        string model,
        int maxTokens,
        double temperature,
        HttpClient? http = null)
    {
        _apiKey = apiKey;
        _model = model;
        _maxTokens = maxTokens;
        _temperature = temperature;
        _http = http ?? new HttpClient();
    }

    public async Task<ChatCompletion> ChatAsync(List<ChatMessage> messages, CancellationToken ct = default)
    {
        var requestBody = new
        {
            model = _model,
            max_tokens = _maxTokens,
            temperature = _temperature,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, CompletionsEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"OpenAI API returned {(int)response.StatusCode} {response.ReasonPhrase}: {responseBody}",
                null,
                response.StatusCode);

        return ParseResponse(responseBody);
    }

    private static ChatCompletion ParseResponse(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new InvalidOperationException("OpenAI response missing 'choices'.");

        var message = choices[0].GetProperty("message");
        var content = message.GetProperty("content").GetString()
            ?? throw new InvalidOperationException("OpenAI response 'content' is null.");

        var promptTokens = 0;
        var completionTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var ct2)) completionTokens = ct2.GetInt32();
        }

        return new ChatCompletion(content, promptTokens, completionTokens);
    }
}
