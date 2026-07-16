namespace GPTino.Terminal;

internal sealed record CliArguments(Uri Endpoint, Guid SessionId, string Token, string Title)
{
    public const string TokenEnvironmentVariable = "GPTINO_API_TOKEN";

    public const string Usage =
        "Usage: GPTino.Terminal attach --endpoint <loopback-url> --session <guid> [--token <token>] [--title <text>] " +
        "(--token may be supplied through GPTINO_API_TOKEN)";

    public static CliParseResult Parse(IReadOnlyList<string> args) =>
        Parse(args, Environment.GetEnvironmentVariable(TokenEnvironmentVariable));

    internal static CliParseResult Parse(IReadOnlyList<string> args, string? environmentToken)
    {
        if (args.Count == 1 && IsHelp(args[0]))
        {
            return CliParseResult.Help();
        }

        if (args.Count == 0 || !string.Equals(args[0], "attach", StringComparison.OrdinalIgnoreCase))
        {
            return CliParseResult.Failure("The only supported command is 'attach'.");
        }

        string? endpoint = null;
        string? session = null;
        string? token = null;
        string? title = null;
        for (var index = 1; index < args.Count; index++)
        {
            var option = args[index];
            if (IsHelp(option))
            {
                return CliParseResult.Help();
            }

            if (option is not ("--endpoint" or "--session" or "--token" or "--title"))
            {
                return CliParseResult.Failure($"Unknown option '{option}'.");
            }

            if (++index >= args.Count)
            {
                return CliParseResult.Failure($"Option '{option}' requires a value.");
            }

            var value = args[index];
            switch (option)
            {
                case "--endpoint":
                    endpoint = value;
                    break;
                case "--session":
                    session = value;
                    break;
                case "--token":
                    token = value;
                    break;
                case "--title":
                    title = value;
                    break;
            }
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            !(string.Equals(endpointUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(endpointUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !endpointUri.IsLoopback ||
            !string.IsNullOrEmpty(endpointUri.UserInfo) ||
            !string.IsNullOrEmpty(endpointUri.Query) ||
            !string.IsNullOrEmpty(endpointUri.Fragment))
        {
            return CliParseResult.Failure("--endpoint must be an absolute HTTP loopback URL without credentials, query, or fragment.");
        }

        if (!Guid.TryParse(session, out var sessionId) || sessionId == Guid.Empty)
        {
            return CliParseResult.Failure("--session must be a non-empty GUID.");
        }

        token = string.IsNullOrWhiteSpace(token) ? environmentToken : token;
        if (string.IsNullOrWhiteSpace(token))
        {
            return CliParseResult.Failure($"API token is required via --token or {TokenEnvironmentVariable}.");
        }

        var displayTitle = string.IsNullOrWhiteSpace(title) ? $"Session {sessionId:D}" : title.Trim();
        var normalizedEndpoint = new Uri(endpointUri.AbsoluteUri.TrimEnd('/') + '/', UriKind.Absolute);
        return CliParseResult.Success(new CliArguments(normalizedEndpoint, sessionId, token, displayTitle));
    }

    private static bool IsHelp(string value) => value is "--help" or "-h" or "/?";
}

internal sealed record CliParseResult(CliArguments? Arguments, bool ShowHelp, string? Error)
{
    public bool IsSuccess => Arguments is not null;

    public static CliParseResult Success(CliArguments arguments) => new(arguments, false, null);

    public static CliParseResult Help() => new(null, true, null);

    public static CliParseResult Failure(string error) => new(null, false, error);
}
