using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ValueInvestorCrawler.Lib.Agent;

public sealed record TelegramMessage(long UpdateId, string ChatId, string Text, DateTime SentAt);

public sealed class TelegramBotClient : ITelegramClient
{
    private readonly string _baseUrl;
    private readonly string _allowedChatId;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private long _offset;

    public TelegramBotClient(string botToken, string allowedChatId, HttpClient? http = null)
    {
        _baseUrl = $"https://api.telegram.org/bot{botToken}/";
        _allowedChatId = allowedChatId;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
    }

    public async Task<List<TelegramMessage>> PollUpdatesAsync(int timeoutSec, CancellationToken ct)
    {
        var url = $"{_baseUrl}getUpdates?timeout={timeoutSec}&offset={_offset}";
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, ct);
        }
        catch (OperationCanceledException)
        {
            return [];
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var messages = new List<TelegramMessage>();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("ok", out var okEl) || !okEl.GetBoolean())
            return messages;

        if (!root.TryGetProperty("result", out var results))
            return messages;

        foreach (var update in results.EnumerateArray())
        {
            var updateId = update.TryGetProperty("update_id", out var uid) ? uid.GetInt64() : 0;
            _offset = Math.Max(_offset, updateId + 1);

            if (!update.TryGetProperty("message", out var msg))
                continue;

            var chatId = "";
            if (msg.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var cid))
                chatId = cid.ToString();

            // Security: ignore messages from other chats
            if (chatId != _allowedChatId)
                continue;

            var text = msg.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "";
            var unixDate = msg.TryGetProperty("date", out var dateEl) ? dateEl.GetInt64() : 0;
            var sentAt = DateTimeOffset.FromUnixTimeSeconds(unixDate).UtcDateTime;

            messages.Add(new TelegramMessage(updateId, chatId, text, sentAt));
        }

        return messages;
    }

    public async Task SendMessageAsync(string chatId, string text, CancellationToken ct)
    {
        const int maxLen = 4096;
        if (text.Length <= maxLen)
        {
            await SendChunkAsync(chatId, text, ct);
            return;
        }

        for (var offset = 0; offset < text.Length; offset += maxLen)
        {
            var chunk = text.Substring(offset, Math.Min(maxLen, text.Length - offset));
            await SendChunkAsync(chatId, chunk, ct);
        }
    }

    public async Task SendTypingAsync(string chatId, CancellationToken ct)
    {
        var url = $"{_baseUrl}sendChatAction";
        var payload = JsonSerializer.Serialize(new { chat_id = chatId, action = "typing" });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        try
        {
            await _http.PostAsync(url, content, ct);
        }
        catch (OperationCanceledException) { }
    }

    private async Task SendChunkAsync(string chatId, string text, CancellationToken ct)
    {
        var url = $"{_baseUrl}sendMessage";
        var payload = JsonSerializer.Serialize(new { chat_id = chatId, text });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        await _http.PostAsync(url, content, ct);
    }
}
