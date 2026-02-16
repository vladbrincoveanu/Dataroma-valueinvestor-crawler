using EmailExtractor.Lib.Agent;

namespace EmailExtractor.Commands;

public static class AgentLoop
{
    public static async Task<int> Run(string[] args)
    {
        _ = args; // env-driven
        var config = AgentConfig.FromEnv();

        var missing = config.ValidateRequired();
        if (missing.Count > 0)
        {
            Console.Error.WriteLine("Missing required configuration values (app settings/user secrets/env):");
            foreach (var name in missing)
                Console.Error.WriteLine($"  {name}");
            return 1;
        }

        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (!cts.IsCancellationRequested)
                cts.Cancel();
        };

        Console.WriteLine("Agent loop starting. Press Ctrl+C to stop.");
        Console.WriteLine(config.ToSafeSummary());

        using var http = new HttpClient();
        var telegram = new TelegramBotClient(config.TelegramBotToken, config.TelegramChatId, http);
        var openAi = new OpenAiClient(
            config.OpenAiApiKey,
            config.OpenAiBaseUrl,
            config.OpenAiModel,
            config.OpenAiMaxTokens,
            config.OpenAiTemperature,
            http);
        var orchestrator = new AgentOrchestrator(config, telegram, openAi);

        try
        {
            await orchestrator.RunAsync(cts.Token);
        }
        catch (OperationCanceledException) { }

        Console.WriteLine("Agent loop stopped.");
        return 0;
    }
}
