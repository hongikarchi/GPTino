using System.Net;
using System.Text;
using System.Text.Json;

namespace GPTino.Terminal.Tests;

public sealed class GptinoApiClientTests
{
    [Fact]
    public async Task ReadMessagesUsesSessionCursorAndAuthenticationHeader()
    {
        var sessionId = Guid.NewGuid();
        RequestSnapshot? captured = null;
        using var client = CreateClient(sessionId, async request =>
        {
            captured = await RequestSnapshot.CreateAsync(request);
            return JsonResponse("[]");
        });

        var messages = await client.ReadMessagesAsync(42, CancellationToken.None);

        Assert.Empty(messages);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Equal($"/api/v1/sessions/{sessionId:D}/messages?after=42&limit=250", captured.Uri.PathAndQuery);
        Assert.Equal("local-secret", captured.Token);
    }

    [Fact]
    public async Task SendMessagePostsClientMessageId()
    {
        var sessionId = Guid.NewGuid();
        RequestSnapshot? captured = null;
        using var client = CreateClient(sessionId, async request =>
        {
            captured = await RequestSnapshot.CreateAsync(request);
            return JsonResponse($$"""{"sessionId":"{{sessionId:D}}","messageId":7,"state":"idle"}""");
        });

        var accepted = await client.SendMessageAsync("Add two sliders", "message-123", CancellationToken.None);

        Assert.Equal(7, accepted.MessageId);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured.Method);
        using var body = JsonDocument.Parse(captured.Body!);
        Assert.Equal("Add two sliders", body.RootElement.GetProperty("content").GetString());
        Assert.Equal("message-123", body.RootElement.GetProperty("clientMessageId").GetString());
    }

    [Fact]
    public async Task SetPausedUsesPutWithBooleanPayload()
    {
        var sessionId = Guid.NewGuid();
        RequestSnapshot? captured = null;
        using var client = CreateClient(sessionId, async request =>
        {
            captured = await RequestSnapshot.CreateAsync(request);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        await client.SetPausedAsync(true, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Put, captured.Method);
        Assert.Equal($"/api/v1/sessions/{sessionId:D}/pause", captured.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(captured.Body!);
        Assert.True(body.RootElement.GetProperty("paused").GetBoolean());
    }

    [Fact]
    public async Task ReadStatusSelectsAttachedSession()
    {
        var sessionId = Guid.NewGuid();
        using var client = CreateClient(sessionId, _ => Task.FromResult(JsonResponse($$"""
            {
              "projectName": "Tower",
              "health": "connected",
              "paused": false,
              "sessions": [
                {
                  "id": "{{sessionId:D}}",
                  "title": "Core",
                  "status": "running",
                  "modelProfile": "high-assurance",
                  "effectiveModel": "gpt-test",
                  "paused": true
                }
              ]
            }
            """)));

        var status = await client.ReadStatusAsync(CancellationToken.None);

        Assert.Equal("Tower", status.ProjectName);
        Assert.Equal("Core", status.SessionTitle);
        Assert.Equal("running", status.SessionStatusValue);
        Assert.Equal("gpt-test", status.EffectiveModel);
        Assert.True(status.SessionPaused);
    }

    [Fact]
    public async Task ApiErrorIsConvertedToSafeTypedException()
    {
        using var client = CreateClient(Guid.NewGuid(), _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("{\"code\":\"session_paused\",\"message\":\"Session is paused.\"}", Encoding.UTF8, "application/json"),
        }));

        var exception = await Assert.ThrowsAsync<TerminalApiException>(
            () => client.SendMessageAsync("hello", "id", CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
        Assert.Contains("session_paused", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("local-secret", exception.Message, StringComparison.Ordinal);
    }

    private static GptinoApiClient CreateClient(
        Guid sessionId,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory)
    {
        var arguments = new CliArguments(new Uri("http://127.0.0.1:51999/"), sessionId, "local-secret", "Test");
        return new GptinoApiClient(arguments, new StubHandler(responseFactory));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responseFactory;

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _responseFactory(request);
    }

    private sealed record RequestSnapshot(HttpMethod Method, Uri Uri, string? Token, string? Body)
    {
        public static async Task<RequestSnapshot> CreateAsync(HttpRequestMessage request)
        {
            var token = request.Headers.TryGetValues(GptinoApiClient.TokenHeaderName, out var values)
                ? values.Single()
                : null;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return new RequestSnapshot(request.Method, request.RequestUri!, token, body);
        }
    }
}
