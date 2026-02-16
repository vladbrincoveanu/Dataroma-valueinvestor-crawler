namespace EmailExtractor.Lib.Agent;

public interface ITelegramClient
{
    Task<List<TelegramMessage>> PollUpdatesAsync(int timeoutSec, CancellationToken ct);
    Task SendMessageAsync(string chatId, string text, CancellationToken ct);
    Task SendTypingAsync(string chatId, CancellationToken ct);
}

public interface IOpenAiClient
{
    Task<ChatCompletion> ChatAsync(List<ChatMessage> messages, CancellationToken ct = default);
}
