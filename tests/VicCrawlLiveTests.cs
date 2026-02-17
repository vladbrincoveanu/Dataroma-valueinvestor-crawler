using ValueInvestorCrawler.Commands;

namespace ValueInvestorCrawler.Tests;

public class VicCrawlLiveTests
{
    [Fact]
    public async Task VicCrawl_WritesContext_FromLivePage_WhenEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_LIVE_VIC_TESTS"), "1", StringComparison.Ordinal))
            return;

        var root = Path.Combine(Path.GetTempPath(), "vic_live_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var linksPath = Path.Combine(root, "links.txt");
        var outJson = Path.Combine(root, "vic_ideas.jsonl");
        var outCtx = Path.Combine(root, "vic_context.txt");
        var testUrl = Environment.GetEnvironmentVariable("VIC_TEST_URL")
            ?? "https://www.valueinvestorsclub.com/idea/InPost/5698302853";

        await File.WriteAllTextAsync(linksPath, testUrl + Environment.NewLine);

        var exitCode = await VicCrawlIdeas.Run(
            [
                "--links-file", linksPath,
                "--out", outJson,
                "--out-ctx", outCtx,
                "--limit", "1",
                "--delay-ms", "0",
                "--login", "0"
            ]
        );

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(outCtx), "Expected context file to be created.");

        var text = await File.ReadAllTextAsync(outCtx);
        Assert.Contains("=== DOC vic/", text);
    }
}
