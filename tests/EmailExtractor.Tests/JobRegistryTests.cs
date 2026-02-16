using EmailExtractor.Lib.Agent;

namespace EmailExtractor.Tests;

public sealed class JobRegistryTests
{
    [Fact]
    public void All_ContainsExpectedJobs()
    {
        var jobs = JobRegistry.All();

        Assert.Equal(6, jobs.Count);
        Assert.Contains("foxland-format", jobs.Keys);
        Assert.Contains("dataroma-rss", jobs.Keys);
        Assert.Contains("extract-tickers", jobs.Keys);
        Assert.Contains("fetch-overview", jobs.Keys);
        Assert.Contains("vic-collect-links", jobs.Keys);
        Assert.Contains("vic-crawl", jobs.Keys);

        foreach (var (name, def) in jobs)
        {
            Assert.Equal(name, def.Name, ignoreCase: true);
            Assert.False(string.IsNullOrWhiteSpace(def.Description));
            Assert.NotNull(def.Execute);
        }
    }

    [Fact]
    public void Pipelines_DefinesDefaultAndFullInPlannedOrder()
    {
        var pipelines = JobRegistry.Pipelines();

        Assert.Equal(
            ["dataroma-rss", "extract-tickers", "fetch-overview"],
            pipelines["default"]);
        Assert.Equal(
            ["foxland-format", "dataroma-rss", "vic-collect-links", "vic-crawl", "extract-tickers", "fetch-overview"],
            pipelines["full"]);
    }

    [Fact]
    public void Registry_IsCaseInsensitive()
    {
        var jobs = JobRegistry.All();
        var pipelines = JobRegistry.Pipelines();

        Assert.True(jobs.ContainsKey("VIC-CRAWL"));
        Assert.True(pipelines.ContainsKey("FULL"));
    }
}
