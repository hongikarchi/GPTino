using System.Net.Http.Json;
using System.Text.Json;

namespace GPTino.Terminal;

internal interface ITerminalApiClient
{
    Task<IReadOnlyList<ChatMessage>> ReadMessagesAsync(long after, CancellationToken cancellationToken);

    Task<AcceptedTurn> SendMessageAsync(string content, string clientMessageId, CancellationToken cancellationToken);

    Task SetPausedAsync(bool paused, CancellationToken cancellationToken);

    Task<SessionStatus> ReadStatusAsync(CancellationToken cancellationToken);
}

internal sealed class GptinoApiClient : ITerminalApiClient, IDisposable
{
    internal const string TokenHeaderName = "X-GPTino-Token";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly Guid _sessionId;
    private readonly bool _ownsClient;

    public GptinoApiClient(CliArguments arguments)
        : this(CreateHttpClient(arguments), arguments.SessionId, true)
    {
    }

    internal GptinoApiClient(CliArguments arguments, HttpMessageHandler handler)
        : this(CreateHttpClient(arguments, handler), arguments.SessionId, true)
    {
    }

    internal GptinoApiClient(HttpClient httpClient, Guid sessionId, bool ownsClient = false)
    {
        _httpClient = httpClient;
        _sessionId = sessionId;
        _ownsClient = ownsClient;
    }

    public async Task<IReadOnlyList<ChatMessage>> ReadMessagesAsync(long after, CancellationToken cancellationToken)
    {
        var path = $"api/v1/sessions/{_sessionId:D}/messages?after={after}&limit=250";
        using var response = await _httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<List<ChatMessage>>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new TerminalProtocolException("The messages response was empty.");
    }

    public async Task<AcceptedTurn> SendMessageAsync(
        string content,
        string clientMessageId,
        CancellationToken cancellationToken)
    {
        var path = $"api/v1/sessions/{_sessionId:D}/messages";
        using var response = await _httpClient.PostAsJsonAsync(
            path,
            new SendMessageRequest(content, clientMessageId),
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<AcceptedTurn>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new TerminalProtocolException("The accepted-turn response was empty.");
    }

    public async Task SetPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        var path = $"api/v1/sessions/{_sessionId:D}/pause";
        using var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(new SetPausedRequest(paused), options: JsonOptions),
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionStatus> ReadStatusAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync("api/v1/runtime", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return SessionStatus.Parse(document.RootElement, _sessionId);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private static HttpClient CreateHttpClient(CliArguments arguments, HttpMessageHandler? handler = null)
    {
        var client = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: true);
        client.BaseAddress = arguments.Endpoint;
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add(TokenHeaderName, arguments.Token);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GPTino.Terminal/1.0");
        return client;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        ApiError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // A reverse proxy or terminated host can return a non-JSON error body.
        }

        var detail = string.IsNullOrWhiteSpace(error?.Message)
            ? response.ReasonPhrase ?? "request failed"
            : error.Message;
        var code = string.IsNullOrWhiteSpace(error?.Code) ? null : $" ({error.Code})";
        throw new TerminalApiException((int)response.StatusCode, $"AgentHost returned {(int)response.StatusCode}{code}: {detail}");
    }
}

internal sealed class TerminalApiException : Exception
{
    public TerminalApiException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}
