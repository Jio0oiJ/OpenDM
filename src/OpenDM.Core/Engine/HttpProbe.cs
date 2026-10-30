using System.Net;
using System.Net.Http.Headers;

namespace OpenDM.Core.Engine;

public sealed record ProbeResult(
    bool SupportsRange,
    long ContentLength,
    string? FileName,
    string? ContentType,
    string FinalUrl);

/// <summary>
/// 探测资源：长度、是否支持 Range、文件名、类型。
/// 先 HEAD，失败或没长度就退回 GET + Range: bytes=0-0 读 Content-Range 的总量。
/// </summary>
public static class HttpProbe
{
    public static async Task<ProbeResult> ProbeAsync(
        HttpClient http, string url,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("只支持 http/https 地址");

        // 1) HEAD
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, uri);
            RequestHeaders.Apply(head, headers);
            using var resp = await http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct)
                                       .ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var len = resp.Content.Headers.ContentLength ?? -1;
                var accept = resp.Headers.AcceptRanges.Any(r => r.Equals("bytes", StringComparison.OrdinalIgnoreCase));
                var name = NameFromHeaders(resp, uri);
                var type = resp.Content.Headers.ContentType?.MediaType;
                if (len > 0 || accept)
                    return new ProbeResult(accept && len > 0, len, name, type, resp.RequestMessage?.RequestUri?.ToString() ?? url);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 落到 GET 探测 */ }

        // 2) GET 探针
        using (var get = new HttpRequestMessage(HttpMethod.Get, uri))
        {
            get.Headers.Range = new RangeHeaderValue(0, 0);
            RequestHeaders.Apply(get, headers);
            using var resp = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct)
                                       .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.PartialContent)
            {
                using var retry = new HttpRequestMessage(HttpMethod.Get, uri);
                RequestHeaders.Apply(retry, headers);
                using var again = await http.SendAsync(retry,
                        HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!again.IsSuccessStatusCode)
                {
                    var hint = (again.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        ? "（该资源可能需要登录 Cookie：在下载地址下方的「Cookie / 请求头」里粘贴浏览器里的 Cookie）"
                        : "";
                    throw new HttpRequestException($"服务器返回 {(int)again.StatusCode} {again.ReasonPhrase}{hint}");
                }
                var len = again.Content.Headers.ContentLength ?? -1;
                return new ProbeResult(false, len, NameFromHeaders(again, uri),
                                       again.Content.Headers.ContentType?.MediaType,
                                       again.RequestMessage?.RequestUri?.ToString() ?? url);
            }

            bool rangeOk = resp.StatusCode == HttpStatusCode.PartialContent;
            long total = -1;
            if (rangeOk && resp.Content.Headers.ContentRange is { } cr && cr.HasLength)
            {
                total = cr.Length!.Value;
                rangeOk = cr.From == 0;
            }
            else if (!rangeOk)
            {
                total = resp.Content.Headers.ContentLength ?? -1;
            }

            return new ProbeResult(rangeOk, total, NameFromHeaders(resp, uri),
                                   resp.Content.Headers.ContentType?.MediaType,
                                   resp.RequestMessage?.RequestUri?.ToString() ?? url);
        }
    }

    private static string? NameFromHeaders(HttpResponseMessage resp, Uri uri)
    {
        var cd = resp.Content.Headers.ContentDisposition;
        var raw = cd?.FileNameStar ?? cd?.FileName;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            raw = raw.Trim().Trim('"');
            if (raw.Contains('%'))
            {
                try { raw = Uri.UnescapeDataString(raw); } catch { }
            }
            var cleaned = Sanitize(raw);
            if (cleaned.Length > 0) return cleaned;
        }

        var last = uri.Segments.LastOrDefault()?.Trim('/');
        if (!string.IsNullOrWhiteSpace(last))
        {
            var decoded = Uri.UnescapeDataString(last);
            if (decoded.Contains('?')) decoded = decoded[..decoded.IndexOf('?')];
            var cleaned = Sanitize(decoded);
            if (cleaned.Length > 0) return cleaned;
        }
        return null;
    }

    /// <summary>去掉 Windows 非法字符，避免写盘炸掉。</summary>
    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.');
        if (s.Length > 180) s = s[..180];
        return s;
    }
}
