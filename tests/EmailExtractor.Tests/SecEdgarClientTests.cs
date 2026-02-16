using System.Text.Json.Nodes;
using EmailExtractor.Lib.Overview;

namespace EmailExtractor.Tests;

public class SecEdgarClientTests
{
    [Fact]
    public void BuildOverview_DefaultsToFiveYears_AndUsesLatestYearForTopLevel()
    {
        var facts = BuildSampleFacts();

        var overview = SecEdgarClient.BuildOverview("TEST", facts, "0000000001");

        Assert.Equal(2025, overview.FiscalYear);
        Assert.Equal(5, overview.History.Count);
        Assert.Equal(new[] { 2025, 2024, 2023, 2022, 2021 }, overview.History.Select(x => x.FiscalYear).ToArray());
        Assert.Equal(600.0, overview.Revenue);
        Assert.Equal(120.0, overview.NetIncome);
    }

    [Fact]
    public void BuildOverview_RespectsHistoryYearsParameter()
    {
        var facts = BuildSampleFacts();

        var overview = SecEdgarClient.BuildOverview("TEST", facts, "0000000001", historyYears: 3);

        Assert.Equal(3, overview.History.Count);
        Assert.Equal(new[] { 2025, 2024, 2023 }, overview.History.Select(x => x.FiscalYear).ToArray());
    }

    private static JsonNode BuildSampleFacts()
    {
        return JsonNode.Parse(
            """
            {
              "entityName": "Test Co",
              "facts": {
                "us-gaap": {
                  "Revenues": {
                    "units": {
                      "USD": [
                        { "fy": 2020, "fp": "FY", "form": "10-K", "end": "2020-12-31", "val": 100.0 },
                        { "fy": 2021, "fp": "FY", "form": "10-K", "end": "2021-12-31", "val": 200.0 },
                        { "fy": 2022, "fp": "FY", "form": "10-K", "end": "2022-12-31", "val": 300.0 },
                        { "fy": 2023, "fp": "FY", "form": "10-K", "end": "2023-12-31", "val": 400.0 },
                        { "fy": 2024, "fp": "FY", "form": "10-K", "end": "2024-12-31", "val": 500.0 },
                        { "fy": 2025, "fp": "FY", "form": "10-K", "end": "2025-12-31", "val": 600.0 }
                      ]
                    }
                  },
                  "GrossProfit": {
                    "units": {
                      "USD": [
                        { "fy": 2021, "fp": "FY", "form": "10-K", "end": "2021-12-31", "val": 80.0 },
                        { "fy": 2022, "fp": "FY", "form": "10-K", "end": "2022-12-31", "val": 120.0 },
                        { "fy": 2023, "fp": "FY", "form": "10-K", "end": "2023-12-31", "val": 160.0 },
                        { "fy": 2024, "fp": "FY", "form": "10-K", "end": "2024-12-31", "val": 200.0 },
                        { "fy": 2025, "fp": "FY", "form": "10-K", "end": "2025-12-31", "val": 240.0 }
                      ]
                    }
                  },
                  "OperatingIncomeLoss": {
                    "units": {
                      "USD": [
                        { "fy": 2023, "fp": "FY", "form": "10-K", "end": "2023-12-31", "val": 90.0 },
                        { "fy": 2024, "fp": "FY", "form": "10-K", "end": "2024-12-31", "val": 100.0 },
                        { "fy": 2025, "fp": "FY", "form": "10-K", "end": "2025-12-31", "val": 110.0 }
                      ]
                    }
                  },
                  "NetIncomeLoss": {
                    "units": {
                      "USD": [
                        { "fy": 2023, "fp": "FY", "form": "10-K", "end": "2023-12-31", "val": 100.0 },
                        { "fy": 2024, "fp": "FY", "form": "10-K", "end": "2024-12-31", "val": 110.0 },
                        { "fy": 2025, "fp": "FY", "form": "10-K", "end": "2025-12-31", "val": 120.0 }
                      ]
                    }
                  }
                }
              }
            }
            """
        )!;
    }
}
