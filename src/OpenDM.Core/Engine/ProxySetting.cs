using System.Net;

namespace OpenDM.Core.Engine;

/// <summary>
/// 代理设置的解析结果。三种状态要区分清楚：
///   空 / 未填  → 跟随系统代理（Proxy = null，交由 SocketsHttpHandler 用系统默认）
///   none/direct → 强制直连（UseProxy = false）
///   http://… / socks5://… → 走指定代理
/// </summary>
public sealed class ProxySetting
{
    public bool UseProxy { get; init; } = true;
    public IWebProxy? Proxy { get; init; }
    public string? Error { get; init; }
    public string Display { get; init; } = "跟随系统";

    public bool IsDirect => !UseProxy;

    public static readonly ProxySetting System = new() { UseProxy = true, Proxy = null, Display = "跟随系统" };
    public static readonly ProxySetting Direct = new() { UseProxy = false, Proxy = null, Display = "直连" };

    /// <summary>
    /// 解析用户填的代理串。允许省略协议（默认 http）；支持 http / https / socks4 / socks4a / socks5 与 user:pass@。
    /// 解析失败不抛异常，回落到跟随系统并在 Error 里说明。
    /// </summary>
    public static ProxySetting Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return System;

        var s = spec.Trim();

        if (s.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("direct", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            s == "直连")
            return Direct;

        if (!s.Contains("://", StringComparison.Ordinal)) s = "http://" + s;

        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return new ProxySetting { UseProxy = true, Proxy = null, Error = $"代理地址无效：{spec}", Display = spec.Trim() };

        var scheme = uri.Scheme.ToLowerInvariant();
        bool ok = scheme is "http" or "https" or "socks4" or "socks4a" or "socks5";
        if (!ok)
            return new ProxySetting
            {
                UseProxy = true, Proxy = null,
                Error = $"不支持的代理协议：{scheme}（可用 http / socks5）",
                Display = spec.Trim()
            };

        var proxy = new WebProxy(new Uri($"{scheme}://{uri.Host}:{uri.Port}"));

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            proxy.Credentials = new NetworkCredential(
                Uri.UnescapeDataString(parts[0]),
                parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }

        return new ProxySetting { UseProxy = true, Proxy = proxy, Display = $"{scheme}://{uri.Host}:{uri.Port}" };
    }
}
