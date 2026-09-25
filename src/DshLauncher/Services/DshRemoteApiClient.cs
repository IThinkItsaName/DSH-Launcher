using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace DshLauncher.Services;

/// <summary>一次远端调用的结果类别（不抛异常，失败也用结果表达）。</summary>
public enum DshRemoteApiStatus
{
    /// <summary>拿到 <c>result.ok = true</c> 与 <c>value</c>。</summary>
    Ok,

    /// <summary>HTTP 401：cookie 缺失/过期（换发一次后仍失败）。</summary>
    Unauthorized,

    /// <summary>连不上（进程已退出、端口没人听、连接被拒）。</summary>
    Unreachable,

    /// <summary>超时。</summary>
    Timeout,

    /// <summary>HTTP 404：该端点在这台运行时的 /api 上不存在（旧版本常见）。</summary>
    EndpointMissing,

    /// <summary>业务/网关返回了 <c>result.ok = false</c>（带 code/message）。</summary>
    Failed
}

/// <summary>一次远端调用的结果：成功时 <see cref="Value"/> 是响应里的 <c>value</c> 元素。</summary>
public sealed record DshRemoteApiResponse(
    DshRemoteApiStatus Status,
    string? ErrorCode,
    string? ErrorMessage,
    JsonElement? Value,
    string Detail)
{
    public bool IsOk => Status == DshRemoteApiStatus.Ok;

    /// <summary>给日志用的一句话（不含 URL/密钥）。</summary>
    public string DescribeForLog() => Status switch
    {
        DshRemoteApiStatus.Ok => "ok",
        DshRemoteApiStatus.Unauthorized => "401（cookie 无效）",
        DshRemoteApiStatus.Unreachable => "连不上",
        DshRemoteApiStatus.Timeout => "超时",
        DshRemoteApiStatus.EndpointMissing => "404（该运行时没有这个接口）",
        _ => $"{ErrorCode ?? "error"}: {ErrorMessage ?? Detail}"
    };
}

/// <summary>
/// dsh `web` 实例的 <c>/api</c> Remote 客户端（契约 C22，见 docs/DSH_CONTRACT_INVENTORY.md）。
/// <para>
/// 握手：用 <c>dsh web:</c> 行里那个带 token 的地址做一次 <c>GET /?token=…</c>，拿 <c>Set-Cookie</c>
/// 换出签名票据；之后的业务调用都是 <c>POST /api/&lt;命名空间&gt;/&lt;方法&gt;</c>，体为
/// <c>{"type":"client-request","rpcId":…,"method":…,"payload":{"args":…}}</c>，响应为
/// <c>{"type":"server-response","rpcId":…,"result":{"ok":true,"value":…}}</c>。
/// </para>
/// <para>
/// 纪律：**只连回环地址**（token 来自本机 dsh 的 stdout，但地址仍按"只信回环"处理）；
/// 绝不抛异常（全部失败都落成 <see cref="DshRemoteApiStatus"/>）；不写 dsh 的任何状态。
/// </para>
/// </summary>
public sealed class DshRemoteApiClient : IDisposable
{
    /// <summary>会话列表端点：`running` 是"实例忙不忙"的第一手事实。</summary>
    public const string SessionListEndpoint = "session/list";

    /// <summary>会话列表的参数名（上游描述符字段名是 <c>_request</c>，不能写别的）。</summary>
    public const string SessionListArgsJson = "{\"_request\":{}}";

    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _handshakeGate = new(1, 1);
    private readonly string _baseUrl;
    private readonly string _authority;
    private readonly Uri _handshakeUrl;

    private string? _cookieHeader;
    private int _rpcSequence;
    private bool _disposed;

    private DshRemoteApiClient(HttpClient http, string authority, Uri handshakeUrl, TimeSpan timeout)
    {
        _http = http;
        _authority = authority;
        _handshakeUrl = handshakeUrl;
        _baseUrl = $"http://{authority}";
        Timeout = timeout;
    }

    public TimeSpan Timeout { get; }

    /// <summary>
    /// 从 <c>dsh web: http://127.0.0.1:&lt;port&gt;/?token=…</c> 建客户端。
    /// 不是回环地址、没有 token、URL 不合法一律返回 null（调用方据此判定"没有可用通道"）。
    /// </summary>
    public static DshRemoteApiClient? TryCreate(string? authenticatedWebUrl, TimeSpan? timeout = null)
    {
        return TryParseAuthenticatedUrl(authenticatedWebUrl, out var authority, out var handshakeUrl, out _)
            ? CreateCore(authority, handshakeUrl, timeout ?? DefaultTimeout)
            : null;
    }

    private static DshRemoteApiClient CreateCore(string authority, Uri handshakeUrl, TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        };
        var http = new HttpClient(handler) { Timeout = timeout };
        return new DshRemoteApiClient(http, authority, handshakeUrl, timeout);
    }

    /// <summary>解析带 token 的实例地址（纯函数，便于测试）。</summary>
    internal static bool TryParseAuthenticatedUrl(
        string? url,
        out string authority,
        out Uri handshakeUrl,
        out string? reason)
    {
        authority = string.Empty;
        handshakeUrl = null!;
        reason = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            reason = "没有带 token 的实例地址";
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttp)
        {
            reason = "实例地址不是合法的 http 地址";
            return false;
        }

        if (!parsed.IsLoopback)
        {
            reason = "实例地址不是回环地址（只允许本机实例）";
            return false;
        }

        var token = string.Empty;
        foreach (var pair in parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            if (string.Equals(pair[..separator], "token", StringComparison.Ordinal))
            {
                token = pair[(separator + 1)..];
                break;
            }
        }

        if (token.Length == 0)
        {
            reason = "实例地址里没有 token（无法换取 cookie）";
            return false;
        }

        authority = parsed.Authority;
        handshakeUrl = new Uri($"{parsed.Scheme}://{authority}/?token={token}");
        return true;
    }

    /// <summary>
    /// 握手：<c>GET /?token=…</c> ⇒ 303 + <c>Set-Cookie</c>（一次调用换一次票据）。
    /// </summary>
    private async Task<DshRemoteApiResponse?> HandshakeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _handshakeUrl);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
            {
                return new DshRemoteApiResponse(
                    DshRemoteApiStatus.Unauthorized, null, null, null, $"握手 HTTP {(int)response.StatusCode}");
            }

            var setCookie = response.Headers.TryGetValues("Set-Cookie", out var values)
                ? values.FirstOrDefault()
                : null;
            if (!TryExtractCookieHeader(setCookie, out var cookieHeader))
            {
                return new DshRemoteApiResponse(
                    DshRemoteApiStatus.Unauthorized, null, null, null, $"握手 HTTP {(int)response.StatusCode} 没有 Set-Cookie");
            }

            _cookieHeader = cookieHeader;
            return null;
        }
        catch (TaskCanceledException)
        {
            return new DshRemoteApiResponse(DshRemoteApiStatus.Timeout, null, null, null, "握手超时");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return new DshRemoteApiResponse(DshRemoteApiStatus.Unreachable, null, null, null, $"握手失败：{ex.GetType().Name}");
        }
    }

    /// <summary>从 <c>Set-Cookie</c> 取「名=值」（丢掉属性，纯函数，便于测试）。</summary>
    internal static bool TryExtractCookieHeader(string? setCookie, out string cookieHeader)
    {
        cookieHeader = string.Empty;
        if (string.IsNullOrWhiteSpace(setCookie))
        {
            return false;
        }

        var pair = setCookie.Split(';')[0].Trim();
        if (pair.Length == 0 || !pair.Contains('=', StringComparison.Ordinal))
        {
            return false;
        }

        cookieHeader = pair;
        return true;
    }

    /// <summary>构造请求体（纯函数，便于测试；args 必须是 JSON 对象字面量）。</summary>
    internal static string BuildRequestJson(string endpoint, string rpcId, string argsJson)
    {
        var builder = new StringBuilder();
        builder.Append("{\"type\":\"client-request\",\"rpcId\":");
        builder.Append(JsonSerializer.Serialize(rpcId));
        builder.Append(",\"method\":");
        builder.Append(JsonSerializer.Serialize(endpoint));
        builder.Append(",\"payload\":{\"args\":");
        builder.Append(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        builder.Append("}}");
        return builder.ToString();
    }

    /// <summary>解析响应信封（纯函数，便于测试）。</summary>
    internal static DshRemoteApiResponse ParseResponse(int httpStatus, string? body)
    {
        if (httpStatus == 401)
        {
            return new DshRemoteApiResponse(DshRemoteApiStatus.Unauthorized, null, null, null, "HTTP 401");
        }

        if (httpStatus == 403)
        {
            return new DshRemoteApiResponse(DshRemoteApiStatus.Unauthorized, null, null, null, "HTTP 403（Host 校验未通过）");
        }

        if (httpStatus == 404)
        {
            // 上游对不存在的端点/方法回 404，不套信封。
            return new DshRemoteApiResponse(DshRemoteApiStatus.EndpointMissing, null, null, null, "HTTP 404");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return new DshRemoteApiResponse(DshRemoteApiStatus.Failed, null, null, null, $"HTTP {httpStatus} 空响应");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("result", out var result))
            {
                return new DshRemoteApiResponse(
                    DshRemoteApiStatus.Failed, null, null, null, $"HTTP {httpStatus} 响应没有 result");
            }

            if (result.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                var value = result.TryGetProperty("value", out var raw) ? raw.Clone() : (JsonElement?)null;
                return new DshRemoteApiResponse(DshRemoteApiStatus.Ok, null, null, value, "ok");
            }

            string? code = null;
            string? message = null;
            if (result.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("code", out var codeElement))
                {
                    code = codeElement.GetString();
                }

                if (error.TryGetProperty("message", out var messageElement))
                {
                    message = messageElement.GetString();
                }
            }

            return new DshRemoteApiResponse(
                DshRemoteApiStatus.Failed,
                code,
                message,
                null,
                $"HTTP {httpStatus} {code ?? "error"}");
        }
        catch (JsonException)
        {
            return new DshRemoteApiResponse(DshRemoteApiStatus.Failed, null, null, null, $"HTTP {httpStatus} 响应不是合法 JSON");
        }
    }

    /// <summary>
    /// 调一个 unary Remote 方法。首次调用会懒握手；遇到 401 会重握手一次（票据随 dsh 重启失效）。
    /// 任何失败都以结果返回，不抛异常。
    /// </summary>
    public async Task<DshRemoteApiResponse> CallAsync(
        string endpoint,
        string argsJson,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_cookieHeader is null)
        {
            await _handshakeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_cookieHeader is null && await HandshakeAsync(cancellationToken).ConfigureAwait(false) is { } failure)
                {
                    return failure;
                }
            }
            finally
            {
                _handshakeGate.Release();
            }
        }

        var first = await SendAsync(endpoint, argsJson, cancellationToken).ConfigureAwait(false);
        if (first.Status != DshRemoteApiStatus.Unauthorized)
        {
            return first;
        }

        // 票据过期（dsh 重启过）：丢掉旧 cookie，重握手一次再试一次。
        _cookieHeader = null;
        await _handshakeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cookieHeader is null && await HandshakeAsync(cancellationToken).ConfigureAwait(false) is { } failure)
            {
                return failure;
            }
        }
        finally
        {
            _handshakeGate.Release();
        }

        return await SendAsync(endpoint, argsJson, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DshRemoteApiResponse> SendAsync(
        string endpoint,
        string argsJson,
        CancellationToken cancellationToken)
    {
        var rpcId = $"launcher-{Interlocked.Increment(ref _rpcSequence)}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/{endpoint}")
            {
                Content = new StringContent(
                    BuildRequestJson(endpoint, rpcId, argsJson),
                    Encoding.UTF8,
                    "application/json")
            };
            request.Headers.TryAddWithoutValidation("Cookie", _cookieHeader);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseResponse((int)response.StatusCode, body);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DshRemoteApiResponse(DshRemoteApiStatus.Timeout, null, null, null, $"{endpoint} 超时");
        }
        catch (OperationCanceledException)
        {
            return new DshRemoteApiResponse(DshRemoteApiStatus.Timeout, null, null, null, $"{endpoint} 已取消");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return new DshRemoteApiResponse(
                DshRemoteApiStatus.Unreachable, null, null, null, $"{endpoint} 连不上：{ex.GetType().Name}");
        }
    }

    /// <summary>读一次会话列表（C 的忙闲事实来源）。</summary>
    public Task<DshRemoteApiResponse> GetSessionListAsync(CancellationToken cancellationToken) =>
        CallAsync(SessionListEndpoint, SessionListArgsJson, cancellationToken);

    /// <summary>本客户端说话的 authority（日志用，不含 token）。</summary>
    public string Authority => _authority;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
        _handshakeGate.Dispose();
    }
}
