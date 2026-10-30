using System.Text;
using System.Text.Json;

namespace OpenDM.Core.Engine;

/// <summary>
/// 自定义请求头的解析与下发。
///
/// 输入允许四种粘法，混着写也行：
///   1. 裸 Cookie 串      a=1; b=2
///   2. 请求头行          Cookie: a=1; b=2   /   Referer: https://x/
///   3. cURL 命令         curl 'https://x/f' -H 'Cookie: a=1' -b 'b=2' -A 'UA'
///   4. Netscape cookies.txt（浏览器插件导出，7 列 tab 分隔）
/// 另外还认 JSON 对象 { "a": "1" }。
///
/// 解析完统一合并成一个 Cookie 头 + 若干普通头，Range/Host 这类由引擎自己控制的
/// 一律忽略 —— 让用户塞进来的头覆盖 Range 会直接下坏文件。
/// </summary>
public static class RequestHeaders
{
    /// <summary>由引擎自己管理、不允许用户覆盖的头。</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Content-Type", "Content-Encoding",
        "Connection", "Transfer-Encoding", "Range", "If-Range", "Expect", "Accept-Encoding"
    };

    private static readonly Dictionary<string, string> Canonical = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cookie"] = "Cookie",
        ["referer"] = "Referer",
        ["referrer"] = "Referer",
        ["user-agent"] = "User-Agent",
        ["origin"] = "Origin",
        ["authorization"] = "Authorization",
        ["accept"] = "Accept",
        ["accept-language"] = "Accept-Language",
        ["x-requested-with"] = "X-Requested-With",
        ["x-csrf-token"] = "X-CSRF-Token",
        ["x-xsrf-token"] = "X-XSRF-Token"
    };

    // ------------------------------------------------------------------ 解析

    public static Dictionary<string, string> Parse(string? text)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cookies = new List<string>();

        if (string.IsNullOrWhiteSpace(text)) return headers;

        var raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim().TrimStart('\uFEFF');

        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // Netscape cookies.txt：domain, flag, path, secure, expiry, name, value
            if (line.Contains('\t') && !line.StartsWith('#'))
            {
                var f = line.Split('\t');
                if (f.Length >= 7) AddCookieString(cookies, f[5] + "=" + f[6]);
                continue;
            }

            if (line.StartsWith('#')) continue;

            // 单行 cURL 参数片段：-H '...' / -b '...'
            if (line.StartsWith('-') && !line.StartsWith("--\t"))
            {
                foreach (var (kind, value) in CurlArgs(line))
                {
                    if (kind == "cookie") AddCookieString(cookies, value);
                    else AddHeaderLine(headers, cookies, value);
                }
                continue;
            }

            if (LooksLikeCurl(line))
            {
                foreach (var (kind, value) in CurlArgs(line))
                {
                    if (kind == "cookie") AddCookieString(cookies, value);
                    else AddHeaderLine(headers, cookies, value);
                }
                continue;
            }

            // 整段 JSON 对象
            if (line.StartsWith('{') && TryJsonCookies(line, cookies)) continue;

            AddHeaderLine(headers, cookies, line);
        }

        if (cookies.Count > 0)
            headers["Cookie"] = JoinCookies(headers, cookies);

        return headers;
    }

    private static string JoinCookies(Dictionary<string, string> headers, List<string> cookies)
    {
        var seen = new List<string>();
        foreach (var c in cookies)
        {
            var kv = c.Trim().TrimEnd(';').Trim();
            if (kv.Length == 0) continue;
            var key = kv.Contains('=') ? kv[..kv.IndexOf('=')] : kv;
            seen.RemoveAll(x => x.StartsWith(key + "=", StringComparison.Ordinal));
            seen.Add(kv);
        }

        var joined = string.Join("; ", seen);
        if (headers.TryGetValue("Cookie", out var existing) && !string.IsNullOrWhiteSpace(existing))
        {
            // 已有 Cookie 头（比如用户写了多行 Cookie:），拼在前面
            var head = existing.Trim().TrimEnd(';').Trim();
            return head.Length == 0 ? joined : head + "; " + joined;
        }
        return joined;
    }

    private static void AddHeaderLine(Dictionary<string, string> headers, List<string> cookies, string line)
    {
        var text = Unquote(line.Trim());

        int colon = text.IndexOf(':');
        if (colon > 0)
        {
            var name = text[..colon].Trim();
            var value = text[(colon + 1)..].Trim();
            if (IsHeaderName(name))
            {
                if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    AddCookieString(cookies, value);
                }
                else
                {
                    AddHeader(headers, name, value);
                }
                return;
            }
        }

        // 没有冒号但有等号 → 当成 Cookie 串
        if (text.Contains('=')) AddCookieString(cookies, text);
    }

    private static void AddHeader(Dictionary<string, string> headers, string name, string value)
    {
        if (Ignored.Contains(name)) return;

        var key = Canonical.TryGetValue(name, out var canon) ? canon : name;
        var val = Clean(value);
        if (val.Length == 0)
        {
            headers.Remove(key);
            return;
        }
        headers[key] = val;
    }

    private static void AddCookieString(List<string> cookies, string value)
    {
        var v = Clean(value).Trim('"', '\'');
        if (v.Length == 0) return;
        foreach (var part in v.Split(';'))
        {
            var item = part.Trim();
            if (item.Length > 0) cookies.Add(item);
        }
    }

    private static bool TryJsonCookies(string line, List<string> cookies)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            bool any = false;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String || p.Value.ValueKind == JsonValueKind.Number)
                {
                    cookies.Add($"{p.Name}={p.Value.ToString()}");
                    any = true;
                }
            }
            return any;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ cURL

    private static bool LooksLikeCurl(string line) =>
        line.StartsWith("curl ", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("curl.exe ", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("curl\r", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(string Kind, string Value)> CurlArgs(string line)
    {
        var toks = Tokenize(line);

        for (int i = 0; i < toks.Count; i++)
        {
            var tok = toks[i];

            string name = tok;
            string? inline = null;

            // --header='...' 这种等号写法
            if (tok.StartsWith("--", StringComparison.Ordinal))
            {
                int eq = tok.IndexOf('=');
                if (eq > 2)
                {
                    name = tok[..eq];
                    inline = tok[(eq + 1)..];
                }
            }

            string? Take()
            {
                if (inline is not null) return inline;
                return i + 1 < toks.Count ? toks[++i] : null;
            }

            switch (name)
            {
                case "-H":
                case "--header":
                {
                    var v = Take();
                    if (!string.IsNullOrWhiteSpace(v)) yield return ("header", v);
                    break;
                }
                case "-b":
                case "--cookie":
                {
                    var v = Take();
                    if (!string.IsNullOrWhiteSpace(v) && !v.StartsWith('@')) yield return ("cookie", v);
                    break;
                }
                case "-A":
                case "--user-agent":
                {
                    var v = Take();
                    if (!string.IsNullOrWhiteSpace(v)) yield return ("header", "User-Agent: " + v);
                    break;
                }
                case "-e":
                case "--referer":
                {
                    var v = Take();
                    if (!string.IsNullOrWhiteSpace(v)) yield return ("header", "Referer: " + v);
                    break;
                }
            }
        }
    }

    private static List<string> Tokenize(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool inSingle = false, inDouble = false;

        foreach (var c in s)
        {
            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                if (!inSingle) { list.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
                if (!inDouble) { list.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            if (char.IsWhiteSpace(c) && !inSingle && !inDouble)
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    // ------------------------------------------------------------------ 工具

    private static bool IsHeaderName(string name)
    {
        if (name.Length is 0 or > 64) return false;
        foreach (var c in name)
        {
            bool ok = char.IsLetterOrDigit(c) || c is '-' or '_' or '!' or '#' or '$' or '%' or
                      '&' or '\'' or '*' or '+' or '.' or '^' or '`' or '|' or '~';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>去掉换行，避免头部注入把请求拆坏。</summary>
    private static string Clean(string v) =>
        v.Replace("\r", " ").Replace("\n", " ").Trim();

    private static string Unquote(string s)
    {
        if (s.Length >= 2 &&
            ((s[0] == '\'' && s[^1] == '\'') || (s[0] == '"' && s[^1] == '"')))
            return s[1..^1];
        return s;
    }

    /// <summary>把请求头渲染回可编辑文本（Cookie 排最前）。</summary>
    public static string ToDisplayText(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0) return "";

        var lines = new List<string>();
        if (headers.TryGetValue("Cookie", out var cookie) && !string.IsNullOrWhiteSpace(cookie))
            lines.Add("Cookie: " + cookie);

        foreach (var kv in headers)
        {
            if (kv.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) continue;
            lines.Add($"{kv.Key}: {kv.Value}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>这个头是否由引擎自己控制（外部传进来时应丢弃）。</summary>
    public static bool IsForbidden(string name) => Ignored.Contains(name);

    /// <summary>把请求头挂到请求上；Range 等引擎自管的头不会被打乱。</summary>
    public static void Apply(HttpRequestMessage req, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0) return;

        foreach (var kv in headers)
        {
            if (Ignored.Contains(kv.Key)) continue;
            if (string.IsNullOrWhiteSpace(kv.Value)) continue;
            try { req.Headers.TryAddWithoutValidation(kv.Key, kv.Value); }
            catch { /* 非法头直接丢弃，不阻塞下载 */ }
        }
    }
}
