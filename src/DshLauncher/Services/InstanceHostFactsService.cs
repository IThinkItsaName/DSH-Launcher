using System.Text.Json;

namespace DshLauncher.Services;

/// <summary>
/// 一次"宿主事实"快照：dsh 自己报的忙闲（C，见 docs/UPSTREAM-INTEGRATION-PLAN.md §C）。
/// <para>
/// 事实来源是 <c>session/list</c> 每条会话的 <c>running</c>（上游 = <c>agent.status === 'running'</c>；
/// agent 的阶段只有 idle/maintenance/running 三种，所以**等审批的回合也是 running**、子代理同样是会话）。
/// </para>
/// </summary>
public sealed record InstanceHostFacts(
    bool HostBusy,
    int SessionCount,
    int RunningCount,
    int LiveAgentCount,
    IReadOnlyList<string> RunningSessionIds,
    string? Reason)
{
    /// <summary>是否真的拿到了事实（false = 拿不到，调用方应回落到连接/CPU 启发式）。</summary>
    public bool IsKnown => Reason is null;

    public static InstanceHostFacts Unknown(string reason) =>
        new(false, 0, 0, 0, Array.Empty<string>(), reason);

    /// <summary>卡片/日志用的一句话（不含路径与凭据）。</summary>
    public string SummaryText => IsKnown
        ? (HostBusy
            ? $"宿主报告 {RunningCount} 个会话正在运行"
            : $"宿主报告无会话运行（共 {SessionCount} 个会话）")
        : $"宿主事实不可用（{Reason}）";
}

/// <summary>把 <c>session/list</c> 的响应解析成忙闲事实（纯函数，形状不符即"拿不到"）。</summary>
public static class InstanceHostFactsParser
{
    /// <summary>解析 <c>value</c>（即 <c>{"items":[…]}</c>）。</summary>
    public static InstanceHostFacts ParseSessionList(JsonElement? value)
    {
        if (value is not { } root || root.ValueKind != JsonValueKind.Object)
        {
            return InstanceHostFacts.Unknown("响应没有 value");
        }

        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return InstanceHostFacts.Unknown("响应没有 items");
        }

        var running = new List<string>();
        var sessionCount = 0;
        var liveAgents = 0;
        foreach (var item in items.EnumerateArray())
        {
            sessionCount++;
            if (!item.TryGetProperty("sessionId", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                // 单条形状不认识：整份事实视为不可信（宁可回落启发式，也不要用半个列表下判断）。
                return InstanceHostFacts.Unknown("会话条目缺少 sessionId");
            }

            var runningNow = item.TryGetProperty("running", out var runningElement)
                && runningElement.ValueKind == JsonValueKind.True;
            if (item.TryGetProperty("agentAvailable", out var agentElement)
                && agentElement.ValueKind == JsonValueKind.True)
            {
                liveAgents++;
            }

            if (runningNow)
            {
                running.Add(idElement.GetString() ?? string.Empty);
            }
        }

        return new InstanceHostFacts(
            HostBusy: running.Count > 0,
            SessionCount: sessionCount,
            RunningCount: running.Count,
            LiveAgentCount: liveAgents,
            RunningSessionIds: running,
            Reason: null);
    }
}

/// <summary>
/// 每个实例的宿主事实缓存 + 刷新（C 的接入点）。
/// <para>
/// 只做只读查询（<c>session/list</c>）：**不写 dsh 的任何状态**；拿不到就是拿不到，由调用方回落启发式。
/// 刷新节流与"够不够新"分两个常量：前者控制请求频率，后者控制事实的有效期。
/// </para>
/// </summary>
public sealed class InstanceHostFactsService : IDisposable
{
    /// <summary>同一实例两次刷新的最小间隔。</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);

    /// <summary>事实的保鲜期：超过就当作"拿不到"（避免用过期事实下判断）。</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(45);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DshRemoteApiClient> _clients = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    private sealed record Entry(InstanceHostFacts Facts, DateTimeOffset CapturedAt);

    /// <summary>缓存里的事实（过期/不存在返回 null）。纯读，不改状态。</summary>
    public InstanceHostFacts? GetKnown(string? instanceId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return null;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(instanceId, out var entry))
            {
                return null;
            }

            return now - entry.CapturedAt > MaxAge ? null : entry.Facts;
        }
    }

    /// <summary>缓存里的原样条目（含"拿不到"的原因），用于日志与展示。</summary>
    public InstanceHostFacts? GetLast(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return null;
        }

        lock (_gate)
        {
            return _entries.TryGetValue(instanceId, out var entry) ? entry.Facts : null;
        }
    }

    /// <summary>是否到了该刷新的时间（无记录 = 该刷；正在查询中不算）。</summary>
    public bool ShouldRefresh(string? instanceId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return false;
        }

        lock (_gate)
        {
            if (_inFlight.Contains(instanceId))
            {
                return false;
            }

            return !_entries.TryGetValue(instanceId, out var entry)
                || now - entry.CapturedAt >= RefreshInterval;
        }
    }

    /// <summary>
    /// 试着开始一次刷新（原子）：没到点、已有查询在路上、实例为空 ⇒ false。
    /// 成功返回 true 的一方必须配对调 <see cref="EndRefresh"/>。
    /// <para>
    /// 占位式“查询中”已被废弃（变更集 182 现场发现）：它会与“事实不可用”混在一起，
    /// 导致刷新还在飞的时候就被当成“拿不到”记一条回落日志。
    /// </para>
    /// </summary>
    public bool TryBeginRefresh(string? instanceId, DateTimeOffset now)
    {
        if (!ShouldRefresh(instanceId, now))
        {
            return false;
        }

        lock (_gate)
        {
            _inFlight.Add(instanceId!);
            return true;
        }
    }

    /// <summary>结束一次刷新（无论成败都要调，否则该实例再也不会刷新）。</summary>
    public void EndRefresh(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        lock (_gate)
        {
            _inFlight.Remove(instanceId);
        }
    }

    /// <summary>是否有一次刷新正在飞（测试/日志用）。</summary>
    public bool IsInFlight(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return false;
        }

        lock (_gate)
        {
            return _inFlight.Contains(instanceId);
        }
    }

    /// <summary>
    /// 刷新一次宿主事实。任何失败都返回 <see cref="InstanceHostFacts.Unknown"/>（绝不抛）。
    /// </summary>
    public async Task<InstanceHostFacts> RefreshAsync(
        string instanceId,
        string? authenticatedWebUrl,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var client = GetOrCreateClient(instanceId, authenticatedWebUrl, out var createReason);
        if (client is null)
        {
            var unknown = InstanceHostFacts.Unknown(createReason ?? "没有可用的实例地址");
            Store(instanceId, unknown, now);
            return unknown;
        }

        var response = await client.GetSessionListAsync(cancellationToken).ConfigureAwait(false);
        var facts = response.IsOk
            ? InstanceHostFactsParser.ParseSessionList(response.Value)
            : InstanceHostFacts.Unknown($"session/list {response.DescribeForLog()}");

        Store(instanceId, facts, now);
        return facts;
    }

    private DshRemoteApiClient? GetOrCreateClient(
        string instanceId,
        string? authenticatedWebUrl,
        out string? reason)
    {
        reason = null;
        lock (_gate)
        {
            if (_clients.TryGetValue(instanceId, out var existing))
            {
                return existing;
            }

            var created = DshRemoteApiClient.TryCreate(authenticatedWebUrl);
            if (created is null)
            {
                DshRemoteApiClient.TryParseAuthenticatedUrl(authenticatedWebUrl, out _, out _, out reason);
                return null;
            }

            _clients[instanceId] = created;
            return created;
        }
    }

    /// <summary>写入一条事实（供刷新与测试使用）。</summary>
    public void Store(string? instanceId, InstanceHostFacts facts, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        lock (_gate)
        {
            _entries[instanceId] = new Entry(facts, now);
        }
    }

    /// <summary>实例停止/删除时调用：丢掉事实与客户端（下次重新握手）。</summary>
    public void Forget(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        DshRemoteApiClient? client = null;
        lock (_gate)
        {
            _entries.Remove(instanceId);
            _inFlight.Remove(instanceId);
            if (_clients.Remove(instanceId, out var existing))
            {
                client = existing;
            }
        }

        client?.Dispose();
    }

    /// <summary>把缓存里的事实用在空闲判定上：<c>true/false</c> = 有事实，<c>null</c> = 拿不到（回落启发式）。</summary>
    public bool? ResolveHostBusy(string? instanceId, DateTimeOffset now)
    {
        var facts = GetKnown(instanceId, now);
        return facts is { IsKnown: true } known ? known.HostBusy : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<DshRemoteApiClient> clients;
        lock (_gate)
        {
            clients = _clients.Values.ToList();
            _clients.Clear();
            _entries.Clear();
            _inFlight.Clear();
        }

        foreach (var client in clients)
        {
            client.Dispose();
        }
    }
}
