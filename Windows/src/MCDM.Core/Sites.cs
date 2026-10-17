namespace MCDM;

/// Sites whose downloads stay in the browser: the same keys and matching as the extension (`siteKey`, `hostListed`).
public static class Sites
{
    public const int MaxEntries = 500;

    /// Site key of a host or an http(s) URL: lower-case ASCII host (IDN → punycode), no "www." prefix, no trailing dot.
    /// null for empty input, non-http(s) URLs, hosts with a path/port/user info/spaces, or anything longer than 253 chars.
    public static string? Key(string? hostOrUrl)
    {
        if (string.IsNullOrWhiteSpace(hostOrUrl)) return null;
        var trimmed = hostOrUrl.Trim();
        try
        {
            string host;
            if (trimmed.Contains("://", StringComparison.Ordinal))
            {
                if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
                if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    return null;
                // An IPv6 literal ("[::1]") could not be read back from the list as a bare host.
                if (uri.HostNameType == UriHostNameType.IPv6) return null;
                host = uri.IdnHost;
            }
            else
            {
                if (trimmed.Any(char.IsWhiteSpace) || trimmed.IndexOfAny(['/', '\\', '@', ':', '?', '#']) >= 0) return null;
                if (!Uri.TryCreate("http://" + trimmed, UriKind.Absolute, out var uri)) return null;
                if (!string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || uri.AbsolutePath != "/" ||
                    !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                    return null;
                host = uri.IdnHost;
            }

            if (string.IsNullOrEmpty(host) || host.Any(char.IsWhiteSpace)) return null;

            host = host.ToLowerInvariant();
            if (host.EndsWith('.')) host = host[..^1];
            if (host.StartsWith("www.", StringComparison.Ordinal) && host.Length > 4) host = host[4..];

            if (host.Length == 0 || host.Length > 253) return null;

            return host;
        }
        catch
        {
            return null;
        }
    }

    /// The host (or URL) is one of the sites in the list or a subdomain of one. List entries are normalised with Key()
    /// before comparing; invalid entries are ignored. "notexample.com" must NOT match "example.com".
    public static bool Listed(string? hostOrUrl, IEnumerable<string>? list)
    {
        var key = Key(hostOrUrl);
        if (key == null || list == null) return false;

        foreach (var item in list)
        {
            var k = Key(item);
            if (k == null) continue;
            if (key.Equals(k, StringComparison.Ordinal) || key.EndsWith("." + k, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// True when any of the URLs/hosts is listed (nulls are skipped).
    public static bool AnyListed(IEnumerable<string>? list, params string?[] urlsOrHosts)
    {
        if (list == null || urlsOrHosts == null) return false;

        foreach (var u in urlsOrHosts)
        {
            if (u != null && Listed(u, list)) return true;
        }

        return false;
    }

    /// A new list with the site added (on) or removed (off): keys only, distinct, sorted ordinally, at most MaxEntries
    /// (when full, adding does nothing). An invalid host returns a copy of the list unchanged (normalised the same way).
    public static List<string> With(IEnumerable<string>? list, string? hostOrUrl, bool on)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (list != null)
        {
            foreach (var item in list)
            {
                var k = Key(item);
                if (k != null)
                {
                    set.Add(k);
                    if (set.Count == MaxEntries) break;
                }
            }
        }

        var key = Key(hostOrUrl);
        if (key != null)
        {
            if (on)
            {
                if (set.Count < MaxEntries)
                {
                    set.Add(key);
                }
            }
            else
            {
                set.Remove(key);
            }
        }

        var result = set.ToList();
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// Where to send a download that is handed back to the browser: a refused signed link (HTTP 401, 403, 404, 410) is
    /// usually spent, so the page it was started from (the referer, if it is an absolute http/https URL) is reopened
    /// instead; otherwise the download URL itself.
    public static Uri HandOffTarget(Uri url, string? referer, int? lastHttpStatus)
    {
        if (lastHttpStatus is 401 or 403 or 404 or 410 && !string.IsNullOrWhiteSpace(referer))
        {
            try
            {
                if (Uri.TryCreate(referer.Trim(), UriKind.Absolute, out var refUri) &&
                    (string.Equals(refUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(refUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                {
                    return refUri;
                }
            }
            catch
            {
            }
        }

        return url;
    }
}
