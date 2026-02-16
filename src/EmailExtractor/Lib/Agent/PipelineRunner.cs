using System.Text;
using EmailExtractor.Commands;

namespace EmailExtractor.Lib.Agent;

public sealed record PipelineStageResult(string StageName, bool Success, string? Error, int ExitCode);

public sealed record PipelineResult(bool AllSucceeded, List<PipelineStageResult> Stages, DateTime CompletedAt)
{
    public string Summary
    {
        get
        {
            var succeeded = Stages.Count(s => s.Success);
            var sb = new StringBuilder();
            sb.AppendLine($"Pipeline: {succeeded}/{Stages.Count} stages succeeded");
            foreach (var stage in Stages)
            {
                sb.AppendLine(stage.Success
                    ? $"  {stage.StageName}: OK"
                    : $"  {stage.StageName}: FAILED: {stage.Error}");
            }
            return sb.ToString().TrimEnd();
        }
    }
}

public static class PipelineRunner
{
    public static async Task<PipelineResult> RunFullPipelineAsync(AgentConfig config, CancellationToken ct)
    {
        _ = config; // env-driven
        var stages = new List<PipelineStageResult>();

        if (ct.IsCancellationRequested)
            return new PipelineResult(false, stages, DateTime.UtcNow);

        stages.Add(await RunAsync("dataroma-rss", () => DataromaRssExport.Run([])));

        if (ct.IsCancellationRequested)
        {
            stages.Add(new PipelineStageResult("extract-tickers", false, "Cancelled", -1));
            stages.Add(new PipelineStageResult("fetch-overview", false, "Cancelled", -1));
            return new PipelineResult(false, stages, DateTime.UtcNow);
        }

        stages.Add(RunSync("extract-tickers", () => ExtractImportantTickers.Run([])));

        if (ct.IsCancellationRequested)
        {
            stages.Add(new PipelineStageResult("fetch-overview", false, "Cancelled", -1));
            return new PipelineResult(false, stages, DateTime.UtcNow);
        }

        stages.Add(await RunAsync("fetch-overview", () => FetchFinancialOverview.Run([])));

        return new PipelineResult(stages.All(s => s.Success), stages, DateTime.UtcNow);
    }

    private static async Task<PipelineStageResult> RunAsync(string name, Func<Task<int>> action)
    {
        try
        {
            var code = await action();
            return new PipelineStageResult(name, code == 0, code == 0 ? null : $"exit code {code}", code);
        }
        catch (Exception ex)
        {
            return new PipelineStageResult(name, false, ex.Message, -1);
        }
    }

    private static PipelineStageResult RunSync(string name, Func<int> action)
    {
        try
        {
            var code = action();
            return new PipelineStageResult(name, code == 0, code == 0 ? null : $"exit code {code}", code);
        }
        catch (Exception ex)
        {
            return new PipelineStageResult(name, false, ex.Message, -1);
        }
    }
}
