namespace GPTino.AgentHost.Security;

public static class RequestOriginPolicy
{
    public static bool AllowsPresentedOrigin(
        IReadOnlyList<string> values,
        string requestScheme,
        string requestAuthority)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != 1 ||
            !Uri.TryCreate(values[0], UriKind.Absolute, out var origin) ||
            !origin.IsLoopback ||
            !string.Equals(origin.Scheme, requestScheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(origin.Authority, requestAuthority, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(origin.UserInfo) ||
            origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment))
        {
            return false;
        }

        return true;
    }
}
