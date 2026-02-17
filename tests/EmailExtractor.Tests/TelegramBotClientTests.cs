using System.Net;
using System.Text;
using System.Text.Json;
using EmailExtractor.Lib.Agent;

namespace EmailExtractor.Tests;

public sealed class TelegramBotClientTests
{
    [Fact]
    public async Task PollUpdatesAsync_FiltersByAllowedChat_AndAdvancesOffset()
    {
        var requests = new List<Uri>();
        var responses = new Queue<string>();
        responses.Enqueue("""
        {"ok":true,"result":[
          {"update_id":100,"message":{"chat":{"id":"allowed"},"text":"hi","date":1700000000}},
          {"update_id":101,"message":{"chat":{"id":"other"},"text":"ignore","date":1700000001}}
        ]}
        """);
        responses.Enqueue("""{"ok":true,"result":[]}""");

        using var http = new HttpClient(new StubHttpHandler((req) =>
        {
            requests.Add(req.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses.Dequeue(), Encoding.UTF8, "application/json")
            };
        }));

        var sut = new TelegramBotClient("token", "allowed", http);
        var firstPoll = await sut.PollUpdatesAsync(1, CancellationToken.None);
        _ = await sut.PollUpdatesAsync(1, CancellationToken.None);

        Assert.Single(firstPoll);
        Assert.Equal("hi", firstPoll[0].Text);
        Assert.Contains("offset=102", requests.Last().ToString());
    }

    [Fact]
    public async Task SendMessageAsync_SplitsLargePayloadIntoTelegramSizedChunks()
    {
        var sentTexts = new List<string>();
        using var http = new HttpClient(new StubHttpHandler(async (req) =>
        {
            if (req.Method != HttpMethod.Post || req.RequestUri is null)
                return new HttpResponseMessage(HttpStatusCode.BadRequest);

            if (!req.RequestUri.ToString().Contains("sendMessage", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK);

            var body = await req.Content!.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            sentTexts.Add(doc.RootElement.GetProperty("text").GetString() ?? "");
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var sut = new TelegramBotClient("token", "allowed", http);
        var payload = new string('x', 5000);
        await sut.SendMessageAsync("allowed", payload, CancellationToken.None);

        Assert.Equal(2, sentTexts.Count);
        Assert.Equal(4096, sentTexts[0].Length);
        Assert.Equal(904, sentTexts[1].Length);
        Assert.Equal(payload, string.Concat(sentTexts));
    }

    [Fact]
    public async Task PollUpdatesAsync_AcceptsNormalizedSupergroupChatId()
    {
        using var http = new HttpClient(new StubHttpHandler(_ =>
        {
            var body = """
            {"ok":true,"result":[
              {"update_id":200,"message":{"chat":{"id":"-1003639865591"},"text":"ping","date":1700000002}}
            ]}
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }));

        var sut = new TelegramBotClient("token", "-3639865591", http);
        var poll = await sut.PollUpdatesAsync(1, CancellationToken.None);

        Assert.Single(poll);
        Assert.Equal("ping", poll[0].Text);
        Assert.Equal("-1003639865591", poll[0].ChatId);
    }

    [Fact]
    public async Task PollUpdatesAsync_AcceptsAnyConfiguredChatIdFromList()
    {
        using var http = new HttpClient(new StubHttpHandler(_ =>
        {
            var body = """
            {"ok":true,"result":[
              {"update_id":300,"message":{"chat":{"id":"chat-b"},"text":"hi-b","date":1700000003}}
            ]}
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }));

        var sut = new TelegramBotClient("token", "chat-a, chat-b", http);
        var poll = await sut.PollUpdatesAsync(1, CancellationToken.None);

        Assert.Single(poll);
        Assert.Equal("hi-b", poll[0].Text);
    }

    [Fact]
    public async Task PollUpdatesAsync_ReadsChannelPostPayload()
    {
        using var http = new HttpClient(new StubHttpHandler(_ =>
        {
            var body = """
            {"ok":true,"result":[
              {"update_id":400,"channel_post":{"chat":{"id":"-1003639865591"},"text":"hello from channel","date":1700000004}}
            ]}
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }));

        var sut = new TelegramBotClient("token", "-3639865591", http);
        var poll = await sut.PollUpdatesAsync(1, CancellationToken.None);

        Assert.Single(poll);
        Assert.Equal("hello from channel", poll[0].Text);
    }

    [Fact]
    public async Task PollUpdatesAsync_UsesCaptionWhenTextMissing()
    {
        using var http = new HttpClient(new StubHttpHandler(_ =>
        {
            var body = """
            {"ok":true,"result":[
              {"update_id":500,"message":{"chat":{"id":"allowed"},"caption":"photo caption","date":1700000005}}
            ]}
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }));

        var sut = new TelegramBotClient("token", "allowed", http);
        var poll = await sut.PollUpdatesAsync(1, CancellationToken.None);

        Assert.Single(poll);
        Assert.Equal("photo caption", poll[0].Text);
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = req => Task.FromResult(handler(req));
        }

        public StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }
}
