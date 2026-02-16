using EmailExtractor.Lib;

namespace EmailExtractor.Tests;

public sealed class EnvTests
{
    [Fact]
    public void LoadDotEnv_LoadsValues_AndParsesQuotesAndComments()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, ".env");
        File.WriteAllText(path, """
            # comment
            OPENAI_API_KEY=key-123
            TELEGRAM_CHAT_ID="12345"
            USER_AGENT='Mozilla/5.0 # not a comment'
            DATAROMA_RSS_URL=https://example.com/feed # inline comment
            export OPENAI_MODEL=gpt-4o
            """);

        Clear("OPENAI_API_KEY");
        Clear("TELEGRAM_CHAT_ID");
        Clear("USER_AGENT");
        Clear("DATAROMA_RSS_URL");
        Clear("OPENAI_MODEL");

        Env.LoadDotEnv(path);

        Assert.Equal("key-123", Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        Assert.Equal("12345", Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID"));
        Assert.Equal("Mozilla/5.0 # not a comment", Environment.GetEnvironmentVariable("USER_AGENT"));
        Assert.Equal("https://example.com/feed", Environment.GetEnvironmentVariable("DATAROMA_RSS_URL"));
        Assert.Equal("gpt-4o", Environment.GetEnvironmentVariable("OPENAI_MODEL"));
    }

    [Fact]
    public void LoadDotEnv_DoesNotOverrideExistingVariable_ByDefault()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, ".env");
        File.WriteAllText(path, "OPENAI_API_KEY=from-file");

        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "from-shell");
        try
        {
            Env.LoadDotEnv(path);
            Assert.Equal("from-shell", Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        }
        finally
        {
            Clear("OPENAI_API_KEY");
        }
    }

    [Fact]
    public void LoadDotEnv_FindsFileInParentDirectories()
    {
        using var temp = new TempDir();
        var rootEnvPath = Path.Combine(temp.Path, ".env");
        File.WriteAllText(rootEnvPath, "TELEGRAM_CHAT_ID=from-parent");

        var child = Path.Combine(temp.Path, "src", "EmailExtractor");
        Directory.CreateDirectory(child);

        var previousCwd = Directory.GetCurrentDirectory();
        Clear("TELEGRAM_CHAT_ID");

        try
        {
            Directory.SetCurrentDirectory(child);
            Env.LoadDotEnv();
            Assert.Equal("from-parent", Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID"));
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Clear("TELEGRAM_CHAT_ID");
        }
    }

    private static void Clear(string key)
    {
        Environment.SetEnvironmentVariable(key, null);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"email_extractor_tests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
