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
/// <param name="UnreadableCount">存在但无法解析的提醒条数（域版本不符 / JSON 坏 / 缺字段）。</param>
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
/// 只读解析某个实例 DSH_HOME 里的定时提醒。
///
/// <para>
/// <b>落盘位置（2026-09-25 用真实文件核对过，变更集 178）</b>：上游 <c>storage-json</c> 按域声明的
/// <c>layout</c> 选布局，**默认是 <c>single</c>**（`single-unit.ts` 写 <c>&lt;root&gt;/&lt;name&gt;.json</c>），
/// 只有显式声明 <c>layout: 'per-record'</c> 的域才用 <c>&lt;root&gt;/&lt;name&gt;/&lt;table&gt;/&lt;key&gt;.json</c>；
/// schedule 域（<c>packages/schedule/schedule/src/storage.ts</c>）**没有声明 per-record** ⇒ 实际文件是
/// <c>&lt;DSH_HOME&gt;/storages/schedule.json</c>，形状：
/// <c>{"unit":{"name":"schedule","version":1},"global":null,"tables":{"tasks":{"&lt;id&gt;":{sessionId,record:{kind,title,scheduledAt,…},status,…}}}}</c>。
/// （变更集 176 曾按 per-record 猜成 <c>storages/schedule/tasks/*.json</c> ⇒ 读不到任何提醒、防呆形同虚设；
/// 那次的自测/harness 夹具也是照错假设造的，属“假绿”。本类现在两种布局都认，single 优先。）
/// </para>
///
/// <para>
/// <b>边界</b>：定时任务是**上游内部存储**、不是公开 API ⇒ ① 只读、绝不写；② 任何异常一律降级为
/// <see cref="ScheduleSnapshot.Unknown"/>，绝不因为读提醒而阻断实例的停止/切换/退出等操作；
/// ③ 不解释记录体（不碰 <c>prompt</c> 等正文）。
/// </para>
/// </summary>
public static class ScheduleSnapshotService
{
    /// <summary>已核对的 schedule 域版本（<c>storage.ts</c>：name 'schedule', version 1）。</summary>
    internal const int SupportedDomainVersion = 1;

    internal const string StoragesDirectoryName = "storages";
    internal const string DomainName = "schedule";

    /// <summary>single 布局（默认）的单元文件名。</summary>
    internal const string UnitFileName = "schedule.json";

    /// <summary>per-record 布局的单元目录名（防御性兼容；schedule 当前不用它）。</summary>
    internal const string DomainDirectoryName = "schedule";

    internal const string TasksTableName = "tasks";

    /// <summary>single 单元文件的体积上限：一个文件装全部提醒（含正文），给足余量。</summary>
    private const long MaximumUnitBytes = 16 * 1024 * 1024;

    /// <summary>per-record 单条记录的体积上限。</summary>
    private const long MaximumRecordBytes = 1024 * 1024;

    /// <summary>默认（single）布局的单元文件：<c>&lt;DSH_HOME&gt;/storages/schedule.json</c>。</summary>
    public static string? ResolveUnitFilePath(string? dshHome) =>
        string.IsNullOrWhiteSpace(dshHome)
            ? null
            : Path.Combine(dshHome, StoragesDirectoryName, UnitFileName);

    /// <summary>per-record 布局的提醒目录：<c>&lt;DSH_HOME&gt;/storages/schedule/tasks</c>（当前上游不用）。</summary>
    public static string? ResolveTasksDirectory(string? dshHome) =>
        string.IsNullOrWhiteSpace(dshHome)
            ? null
            : Path.Combine(dshHome, StoragesDirectoryName, DomainDirectoryName, TasksTableName);

    /// <summary>读取某实例 DSH_HOME 的提醒快照（永不抛异常）。</summary>
    public static ScheduleSnapshot Read(string? dshHome)
    {
        try
        {
            var unitFile = ResolveUnitFilePath(dshHome);
            if (unitFile is null)
            {
                // 连 DSH_HOME 都没有 = 无法判断，返回“未知”。
                return ScheduleSnapshot.Unknown;
            }

            if (File.Exists(unitFile))
            {
                return ReadSingleUnit(unitFile);
            }

            var tasksDirectory = ResolveTasksDirectory(dshHome)!;
            if (Directory.Exists(tasksDirectory))
            {
                return ReadPerRecord(tasksDirectory);
            }

            // 两种布局都不存在 = 这个实例还没有任何提醒（上游首次写入才物化文件/目录）。
            return ScheduleSnapshot.None;
        }
        catch (Exception)
        {
            // 只读诊断信息不值得影响实例操作：任何未预期的失败一律当“未知”。
            return ScheduleSnapshot.Unknown;
        }
    }

    private static ScheduleSnapshot ReadSingleUnit(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaximumUnitBytes)
        {
            return ScheduleSnapshot.Unknown;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !HasExpectedUnitIdentity(root))
        {
            return ScheduleSnapshot.Unknown;
        }

        if (!root.TryGetProperty("tables", out var tables) || tables.ValueKind != JsonValueKind.Object)
        {
            return ScheduleSnapshot.Unknown;
        }

        if (!tables.TryGetProperty(TasksTableName, out var tasks))
        {
            // 表还没建 = 空域（没有提醒）。
            return ScheduleSnapshot.None;
        }

        if (tasks.ValueKind != JsonValueKind.Object)
        {
            return ScheduleSnapshot.Unknown;
        }

        var collected = new List<TaskEntry>();
        var unreadable = 0;
        foreach (var task in tasks.EnumerateObject())
        {
            var parsed = TryReadTask(task.Value);
            if (parsed is null)
            {
                unreadable++;
                continue;
            }

            collected.Add(parsed);
        }

        return Build(collected, unreadable);
    }

    private static ScheduleSnapshot ReadPerRecord(string tasksDirectory)
    {
        var collected = new List<TaskEntry>();
        var unreadable = 0;
        foreach (var file in Directory.EnumerateFiles(tasksDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var record = TryReadPerRecordFile(file);
            if (record is null)
            {
                unreadable++;
                continue;
            }

            var parsed = TryReadTask(record.Value);
            if (parsed is null)
            {
                unreadable++;
                continue;
            }

            collected.Add(parsed);
        }

        return Build(collected, unreadable);
    }

    private static ScheduleSnapshot Build(List<TaskEntry> collected, int unreadable)
    {
        var active = collected.Where(entry => entry.Active).ToList();
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
            TotalCount: collected.Count + unreadable,
            UnreadableCount: unreadable,
            NextDueUtc: active.Count > 0 ? active[0].Due : null,
            ActiveTitles: active.Select(entry => entry.Title).ToArray());
    }

    /// <summary>single 单元的 <c>unit</c> 身份必须是 name='schedule' 且 version=1。</summary>
    private static bool HasExpectedUnitIdentity(JsonElement root)
    {
        if (!root.TryGetProperty("unit", out var unit) || unit.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!unit.TryGetProperty("name", out var name)
            || name.ValueKind != JsonValueKind.String
            || !string.Equals(name.GetString(), DomainName, StringComparison.Ordinal))
        {
            return false;
        }

        return unit.TryGetProperty("version", out var version)
            && version.ValueKind == JsonValueKind.Number
            && version.TryGetInt32(out var value)
            && value == SupportedDomainVersion;
    }

    /// <summary>
    /// per-record 布局的单条文件：外层是 <c>{ "version": &lt;域版本&gt;, "record": &lt;任务&gt; }</c>。
    /// 返回 <c>null</c> 表示“这条读不懂”；返回 <c>Some(null)</c> 语义上不存在，故用 <c>JsonElement?</c> 表示。
    /// </summary>
    private static JsonElement? TryReadPerRecordFile(string path)
    {
        try
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

            if (!root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var value)
                || value != SupportedDomainVersion)
            {
                return null;
            }

            if (!root.TryGetProperty("record", out var record) || record.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return record.Clone();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed record TaskEntry(bool Active, DateTimeOffset? Due, string Title);

    /// <summary>
    /// 读一条任务：只解析判定与展示所需字段（status / title / scheduledAt），不碰正文。
    /// 返回 null 表示“这条读不懂”（由调用方计成 unreadable，并把整体判为未知）。
    /// </summary>
    private static TaskEntry? TryReadTask(JsonElement task)
    {
        if (task.ValueKind != JsonValueKind.Object)
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

        return new TaskEntry(active, due, title);
    }
}
