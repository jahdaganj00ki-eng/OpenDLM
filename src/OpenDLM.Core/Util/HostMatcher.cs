namespace OpenDLM.Core.Util;

/// <summary>
/// Host matching shared by the proxy exception list and the per-site connection
/// rules, so "example.com" means the same thing in both places.
/// </summary>
public static class HostMatcher
{
    /// <summary>
    /// True when <paramref name="host"/> is the configured host or a sub-domain of it.
    /// A leading dot is accepted and ignored, so ".example.com" and "example.com"
    /// behave identically.
    /// </summary>
    public static bool Matches(string? configured, string? host)
    {
        var value = Normalize(configured);
        if (value.Length == 0 || string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var candidate = host.Trim().Trim('.').ToLowerInvariant();
        return candidate == value || candidate.EndsWith("." + value, StringComparison.Ordinal);
    }

    /// <summary>True when the host matches any entry of the list.</summary>
    public static bool MatchesAny(IEnumerable<string>? configured, string? host)
    {
        if (configured is null || string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        foreach (var entry in configured)
        {
            if (Matches(entry, host))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Lowercases, trims whitespace and drops a leading dot or wildcard.</summary>
    public static string Normalize(string? configured)
    {
        var value = (configured ?? string.Empty).Trim().TrimStart('*').TrimStart('.').Trim();
        return value.ToLowerInvariant();
    }
}
