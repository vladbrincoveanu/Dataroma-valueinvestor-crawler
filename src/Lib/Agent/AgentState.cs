using System.Text.Json;
using System.Text.Json.Serialization;
using ValueInvestorCrawler.Lib;

namespace ValueInvestorCrawler.Lib.Agent;

public sealed class AgentState
{
    public DateTime LastCycleUtc { get; private set; } = DateTime.MinValue;
    public DateTime LastOpenAiCycleUtc { get; private set; } = DateTime.MinValue;
    public int CycleCount { get; private set; }
    public string LastAnalysis { get; private set; } = string.Empty;
    public List<ChatMessage> ConversationHistory { get; private set; } = [];
    public HashSet<string> SeenDataromaIds { get; private set; } = [];
    public HashSet<string> SeenVicIds { get; private set; } = [];

    private sealed record StateDto(
        [property: JsonPropertyName("last_cycle_utc")] DateTime LastCycleUtc,
        [property: JsonPropertyName("last_openai_cycle_utc")] DateTime LastOpenAiCycleUtc,
        [property: JsonPropertyName("cycle_count")] int CycleCount,
        [property: JsonPropertyName("last_analysis")] string LastAnalysis,
        [property: JsonPropertyName("conversation_history")] List<ChatMessageDto> ConversationHistory,
        [property: JsonPropertyName("seen_dataroma_ids")] List<string> SeenDataromaIds,
        [property: JsonPropertyName("seen_vic_ids")] List<string> SeenVicIds
    );

    private sealed record ChatMessageDto(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content
    );

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static AgentState Load(string path)
    {
        if (!File.Exists(path)) return new AgentState();

        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<StateDto>(json, SerializerOptions);
            if (dto is null) return new AgentState();

            return new AgentState
            {
                LastCycleUtc = dto.LastCycleUtc,
                LastOpenAiCycleUtc = dto.LastOpenAiCycleUtc,
                CycleCount = dto.CycleCount,
                LastAnalysis = dto.LastAnalysis,
                ConversationHistory = dto.ConversationHistory
                    .Select(m => new ChatMessage(m.Role, m.Content))
                    .ToList(),
                SeenDataromaIds = [.. dto.SeenDataromaIds],
                SeenVicIds = [.. dto.SeenVicIds],
            };
        }
        catch
        {
            return new AgentState();
        }
    }

    public void Save(string path)
    {
        var dto = new StateDto(
            LastCycleUtc: LastCycleUtc,
            LastOpenAiCycleUtc: LastOpenAiCycleUtc,
            CycleCount: CycleCount,
            LastAnalysis: LastAnalysis,
            ConversationHistory: ConversationHistory
                .Select(m => new ChatMessageDto(m.Role, m.Content))
                .ToList(),
            SeenDataromaIds: [.. SeenDataromaIds],
            SeenVicIds: [.. SeenVicIds]
        );
        TextUtil.WriteAtomic(path, JsonSerializer.Serialize(dto, SerializerOptions));
    }

    public void AddMessage(ChatMessage msg, int maxTurns)
    {
        ConversationHistory.Add(msg);
        if (maxTurns > 0 && ConversationHistory.Count > maxTurns)
            ConversationHistory.RemoveRange(0, ConversationHistory.Count - maxTurns);
    }

    public void MarkCycleComplete(string analysis)
    {
        LastCycleUtc = DateTime.UtcNow;
        CycleCount++;
        LastAnalysis = analysis ?? string.Empty;
    }

    public void MarkOpenAiCycleAttempt()
    {
        LastOpenAiCycleUtc = DateTime.UtcNow;
    }
}
