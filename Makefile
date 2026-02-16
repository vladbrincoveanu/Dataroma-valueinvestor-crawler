.PHONY: build test run clean

build:
	dotnet build EmailExtractor.sln

test:
	dotnet test EmailExtractor.sln

run:
	dotnet run --project src/EmailExtractor/EmailExtractor.csproj -- $(CMD)

clean:
	dotnet clean EmailExtractor.sln
