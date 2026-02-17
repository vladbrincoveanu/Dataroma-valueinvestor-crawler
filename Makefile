.PHONY: build test run clean

build:
	dotnet build ValueInvestorCrawler.sln

test:
	dotnet test ValueInvestorCrawler.sln

run:
	dotnet run --project src/ValueInvestorCrawler.csproj -- $(CMD)

clean:
	dotnet clean ValueInvestorCrawler.sln
