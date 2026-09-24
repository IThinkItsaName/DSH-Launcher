using System.Globalization;
using System.IO;
using System.Text.Json;

namespace DshLauncher.Services;

/// <summary>
/// 某实例的「定时提醒」只读快照（dsh 0.1.7-rc.1 起 <c>web</c> profile 默认挂载 Schedule）。
/// </summary>
/// <param name="Known">能否确认读取结果；<c>false</c> = 未知（读不到/看不懂），调用方按“不知道”处理。</param>
/// <param name="ActiveCount">状态为 active 的提醒条数（缺失 <c>status</c> 按上游语义视为 active）。</param>
/// <param name="TotalCount">能解析出的提醒总条数（含 inactive）。</param>
/// <param name="UnreadableCount">存在但无法解析的记录文件数（域版本不符 / JSON 坏 / 缺 record）。</param>
/// <param name="NextDueUtc">活跃提醒里最近的触发时刻（UTC）；没有可解析的时刻则为 null。</param>
/// <param name="ActiveTitles">活跃提醒的标题（按触发时刻升序，缺时刻的排在最后）。</param>
public sealed record ScheduleSnapshot(
    bool Known,
    int ActiveCount,
    int TotalCount,
    int UnreadableCount,
    DateTimeOffset? NextDueUtc,
    IReadOnlyList<string> ActiveTitles)
{
    /// <summary>读不到或看不懂（文件异常、域版本不符）。</summary>
    public static readonly ScheduleSnapshot Unknown = new(false, 0, 0, 0, null, Array.Empty<string>());

    /// <summary>确认读到了、且没有任何提醒。</summary>
    public static readonly ScheduleSnapshot None = new(true, 0, 0, 0, null, Array.Empty<string>());

    /// <summary>能确认「有到点的提醒」时才为 true（未知不算，避免误报）。</summary>
    public bool HasActive => Known && ActiveCount > 0;

    /// <summary>实例卡片/详情用的短摘要；不值得显示时返回空串。</summary>
    public string SummaryText => HasActive
        ? NextDueUtc is { } next
            ? $"提醒 {ActiveCount} · 下次 {next.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}"
            : $"提醒 {ActiveCount}"
        : string.Empty;
}

/// <summary>
/// 只读解析 <c>&lt;DSH_HOME&gt;/storages/schedule/tasks/*.json</c>。
///
/// <para>
/// 由来与边界（见 <c>docs/UPSTREAM-INTEGRATION-PLAN.md</c> §A、<c>docs/DSH_CONTRACT_INVENTORY.md</c>）：
/// 定时任务是 <b>上游内部存储</b>（storage-json 的 per-record 单元：单元目录 <c>&lt;root&gt;/&lt;domain.name&gt;/</c>，
/// 每个 table 一个子目录、逐记录一个 <c>&lt;key&gt;.json</c>；文件形如
/// <c>{"version":1,"record":{"sessionId":…,"record":{"kind":…,"title":…,"scheduledAt":…},"status":"active"}}</c>），
/// <b>不是公开 API</b>。因此本服务：① 只读、绝不写；② 任何异常一律降级为 <see cref="ScheduleSnapshot.Unknown"/>，
/// 绝不因为读提醒而阻断实例的停止/切换/退出等操作；③ 不解释记录体（不碰 <c>prompt</c> 等正文）。
/// </para>
/// </summary>
public static class ScheduleSnapshotService
{
    /// <summary>已核对的 schedule 域版本（<c>packages/schedule/schedule/src/storage.ts</c>：name 'schedule', version 1）。</summary>
    internal const int SupportedDomainVersion = 1;

    internal const string StoragesDirectoryName = "storages";
    internal const string DomainDirectoryName = "schedule";
    internal const string TasksDirectoryName = "tasks";

    /// <summary>单文件大小上限：提醒记录只有几百字节，超过这个尺寸的必然是别的东西（不读）。</summary>
    private const long MaximumRecordBytes = 1024 * 1024;

    /// <summary>提醒记录目录；<paramref name="dshHome"/> 为空时返回 null。</summary>
    public static string? ResolveTasksDirectory(string? dshHome) =>
        string.IsNullOrWhiteSpace(dshHome)
            ? null
            : Path.Combine(dshHome, StoragesDirectoryName, DomainDirectoryName, TasksDirectoryName);

    /// <summary>读取某实例 DSH_HOME 的提醒快照（永不抛异常）。</summary>
    public static ScheduleSnapshot Read(string? dshHome)
    {
        try
        {
            var tasksDirectory = ResolveTasksDirectory(dshHome);
            if (tasksDirectory is null)
            {
                // 连 DSH_HOME 都没有 = 无法判断，返回“未知”。
                return ScheduleSnapshot.Unknown;
            }

            if (!Directory.Exists(tasksDirectory))
            {
                // 没有该目录 = 这个实例还没有任何提醒（上游首次写入才物化目录）。
                return ScheduleSnapshot.None;
            }

            var active = new List<(DateTimeOffset? Due, string Title)>();
            var total = 0;
            var unreadable = 0;
            foreach (var file in Directory.EnumerateFiles(tasksDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                var task = TryReadTask(file);
                if (task is null)
                {
                    unreadable++;
                    continue;
                }

                total++;
                if (task.Active)
                {
                    active.Add((task.Due, task.Title));
                }
            }

            active.Sort(static (left, right) =>
            {
                if (left.Due is { } l && right.Due is { } r)
                {
                    return DateTimeOffset.Compare(l, r);
                }

                if (left.Due is null && right.Due is null)
                {
                    return string.CompareOrdinal(left.Title, right.Title);
                }

                return left.Due is null ? 1 : -1;
            });

            return new ScheduleSnapshot(
                Known: unreadable == 0,
                ActiveCount: active.Count,
                TotalCount: total,
                UnreadableCount: unreadable,
                NextDueUtc: active.Count > 0 ? active[0].Due : null,
                ActiveTitles: active.Select(entry => entry.Title).ToArray());
        }
        catch (Exception)
        {
            // 只读诊断信息不值得影响实例操作：任何未预期的失败一律当“未知”。
            return ScheduleSnapshot.Unknown;
        }
    }

    private sealed record ParsedTask(bool Active, DateTimeOffset? Due, string Title);

    /// <summary>
    /// 读一条记录：只解析判定与展示所需字段（域版本 / status / 标题 / 触发时刻），不碰正文。
    /// 返回 null 表示“这条读不懂”（由调用方计成 unreadable，并把整体判为未知）。
    /// </summary>
    private static ParsedTask? TryReadTask(string path)
    {
        try
        {
            return ReadTaskCore(path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 单条记录读坏了不能连累其余记录（也不能让整体变成“未知”而丢掉可解析的条数）。
            return null;
        }
    }

    private static ParsedTask? ReadTaskCore(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaximumRecordBytes)
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // 域版本（storage-json per-record 文件的外层 stamp）。不符 = 我们看不懂这一代记录。
        if (!root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var versionValue)
            || versionValue != SupportedDomainVersion)
        {
            return null;
        }

        if (!root.TryGetProperty("record", out var task) || task.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // status 缺失按上游 schema 默认值 active；非法取值算读不懂。
        var active = true;
        if (task.TryGetProperty("status", out var status) && status.ValueKind != JsonValueKind.Null)
        {
            if (status.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            switch (status.GetString())
            {
                case "active":
                    active = true;
                    break;
                case "inactive":
                    active = false;
                    break;
                default:
                    return null;
            }
        }

        if (!task.TryGetProperty("record", out var rule) || rule.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var title = "(未命名)";
        if (rule.TryGetProperty("title", out var titleElement)
            && titleElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(titleElement.GetString()))
        {
            title = titleElement.GetString()!;
        }

        DateTimeOffset? due = null;
        if (rule.TryGetProperty("scheduledAt", out var dueElement)
            && dueElement.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                dueElement.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            due = parsed;
        }

        return new ParsedTask(active, due, title);
    }
}
