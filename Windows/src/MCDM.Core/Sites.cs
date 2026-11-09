using System.Net;
using System.Text.RegularExpressions;

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
    // ---- Google Drive ----

    /// One of Google Drive's own https hosts (the share page, old Docs links, the file server).
    public static bool IsDriveHost(Uri url) => url.Scheme == "https" && IsDriveHostName(url);

    private static bool IsDriveHostName(Uri url) =>
        (url.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase)
        || url.Host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase)
        || url.Host.Equals("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase));

    /// A Google Drive file link (the page a share link opens, "open?id=", "uc?id=", or the file server's own address) as
    /// the address that serves the file; null for any other link, Docs and Sheets documents among them.
    public static Uri? DriveDownloadUrl(Uri url)
    {
        if (!IsDriveHostName(url) || url.Scheme is not ("http" or "https")) return null;
        var path = url.AbsolutePath;
        string? id = null;
        var m = Regex.Match(path, "^/file/(?:u/\\d+/)?d/([A-Za-z0-9_-]+)");
        if (m.Success) id = m.Groups[1].Value;
        else if (path is "/uc" or "/open" or "/download") id = QueryValue(url, "id");
        if (id == null || !Regex.IsMatch(id, "^[A-Za-z0-9_-]+$")) return null;
        // The file server's own address, already carrying the "download anyway" answer: as it is.
        if (url.Host.Equals("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase) && QueryValue(url, "confirm") != null) return url;
        // Files shared before 2021 need their resource key as well.
        var key = QueryValue(url, "resourcekey") is { Length: > 0 } k ? "&resourcekey=" + Uri.EscapeDataString(k) : "";
        return new Uri($"https://drive.usercontent.google.com/download?id={id}&export=download{key}");
    }

    /// Google Drive's "can't scan this file for viruses" page: the address its "Download anyway" button leads to (the
    /// download form with its hidden fields; older pages link it directly). null when the page has neither: the file
    /// is private, deleted or over its download limit.
    public static Uri? DriveConfirmUrl(string html, Uri page)
    {
        var form = Regex.Match(html,
            "<form[^>]*id=\"download-form\"[^>]*>(.*?)</form>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (form.Success)
        {
            var action = Regex.Match(form.Value, "action=\"([^\"]*)\"", RegexOptions.IgnoreCase);
            if (!action.Success || !Uri.TryCreate(page, WebUtility.HtmlDecode(action.Groups[1].Value), out var target)) return null;
            var fields = new List<string>();
            foreach (Match input in Regex.Matches(form.Groups[1].Value, "<input[^>]*>", RegexOptions.IgnoreCase))
            {
                if (!input.Value.Contains("type=\"hidden\"", StringComparison.OrdinalIgnoreCase)) continue;
                var name = Regex.Match(input.Value, "name=\"([^\"]*)\"");
                var value = Regex.Match(input.Value, "value=\"([^\"]*)\"");
                if (!name.Success) continue;
                fields.Add(Uri.EscapeDataString(WebUtility.HtmlDecode(name.Groups[1].Value)) + "="
                    + Uri.EscapeDataString(WebUtility.HtmlDecode(value.Success ? value.Groups[1].Value : "")));
            }
            if (fields.Count == 0) return null;
            var builder = new UriBuilder(target) { Query = string.Join("&", fields) };
            return IsDriveHost(builder.Uri) ? builder.Uri : null;
        }
        var link = Regex.Match(html, "href=\"([^\"]*[?&](?:amp;)?confirm=[^\"]*)\"", RegexOptions.IgnoreCase);
        if (link.Success && Uri.TryCreate(page, WebUtility.HtmlDecode(link.Groups[1].Value), out var direct) && IsDriveHost(direct)) return direct;
        return null;
    }

    private static string? QueryValue(Uri url, string key)
    {
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var k = eq < 0 ? pair : pair[..eq];
            if (k.Equals(key, StringComparison.Ordinal)) return eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }
}
