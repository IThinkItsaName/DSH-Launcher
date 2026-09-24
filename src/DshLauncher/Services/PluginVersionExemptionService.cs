using System.Globalization;
using System.IO;using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshLauncher.Services;

/// <summary>
/// 某个 profile 的「精确版本豁免」现状（读 <c>&lt;DSH_HOME&gt;/profiles/&lt;profile&gt;/compatibility.json</c>）。
/// </summary>
/// <param name="Entries">已接受的记录：<c>"包名@精确版本"</c> → 允许的 DSH 精确版本列表。</param>
/// <param name="Warnings">被拒绝的记录 / 文件级问题的可读说明；空表示全部记录都被接受。</param>
/// <param name="Rewritable">文件里没有本读者拒绝的内容 ⇒ 允许改写（false = 必须用户手工修，改写会丢内容）。</param>
/// <param name="FilePath">被读取的文件路径（界面提示用）。</param>
/// <param name="FileExists">文件是否存在（不存在＝没有任何放行，且可安全写入）。</param>
public sealed record PluginVersionExemptions(
    IReadOnlyDictionary<string, IReadOnlyList<string>> Entries,
    IReadOnlyList<string> Warnings,
    bool Rewritable,
    string FilePath,
    bool FileExists)
{
    /// <summary>空状态（文件不存在，或读不出且按“无放行”处理）。</summary>
    public static PluginVersionExemptions Empty(string filePath, bool fileExists) =>
        new(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            Array.Empty<string>(),
            Rewritable: true,
            filePath,
            fileExists);

    /// <summary>给定「包名@版本」是否已对某 DSH 版本放行（比较精确字符串）。</summary>
    public bool IsExempted(string? packageVersion, string? runtimeVersion)
    {
        if (string.IsNullOrWhiteSpace(packageVersion) || string.IsNullOrWhiteSpace(runtimeVersion))
        {
            return false;
        }

        return Entries.TryGetValue(packageVersion.Trim(), out var versions)
            && versions.Contains(runtimeVersion.Trim(), StringComparer.Ordinal);
    }

    /// <summary>该包名在文件里有没有任何记录（用于提示“已放行的是别的版本”）。</summary>
    public IReadOnlyList<string> VersionsFor(string? packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();
        foreach (var (key, versions) in Entries)
        {
            if (key.StartsWith(packageName.Trim() + "@", StringComparison.Ordinal))
            {
                values.AddRange(versions);
            }
        }

        return values;
    }
}

/// <summary>
/// 一次「精确版本放行」的结果。
/// </summary>
/// <param name="Ok">上游 CLI 是否成功写入（退出码 0）。</param>
/// <param name="Message">给用户看的中文说明（失败时是可操作的原因，不是英文堆栈）。</param>
/// <param name="Output">上游原输出（含英文原因与诊断路径），折叠展示 / 便于粘贴。</param>
public sealed record PluginVersionExemptionResult(bool Ok, string Message, string Output);

/// <summary>
/// 只读解析 profile 的 <c>compatibility.json</c>（上游 <c>PROFILE_COMPATIBILITY_FILENAME</c>）。
///
/// <para>
/// <b>为什么要有这个服务（docs/UPSTREAM-INTEGRATION-PLAN.md §B）</b>：dsh 0.1.7-rc.1 起，插件声明的
/// <c>@deepseek-ai/dsh*</c> peerDependencies 与运行时不匹配时，**启动期的兼容性预检会直接禁用该行**
/// （stderr: <c>dsh: disabling profile plugin &lt;id&gt;: Plugin pkg@ver is incompatible with dsh X …</c>）；
/// 唯一的“确实要用”通道就是这条**精确版本豁免**（CLI：<c>dsh plugin allow-version</c>）。启动器原来的兼容性
/// 检查（<see cref="PluginCompatibility"/>）只给建议性警告、且不看豁免 ⇒ 会出现“已经放行了，启动器还在吓唬你”
/// 与“放行了却不知道为什么还在崩”两种情况。
/// </para>
///
/// <para>
/// <b>语义严格镜像上游</b>（<c>packages/boot/app-boot/src/profile-compatibility.ts</c>）：
/// 键必须是**精确**的 <c>包名@版本</c>（小写包名 + 规范写法 SemVer，不接受 <c>v</c> 前缀/区间/前导零/空白），
/// 值是**精确 DSH 版本**的列表；坏记录只**警告并忽略**、且此时 <see cref="PluginVersionExemptions.Rewritable"/>
/// 为 false（改写会丢内容）；文件不存在＝没有任何放行。**本服务只读**——写入一律交给上游 CLI
/// （<see cref="ExtensionService.AllowPluginVersionAsync"/>），避免复刻文件格式/锁/权限语义。
/// </para>
/// </summary>
public static class PluginVersionExemptionService
{
    /// <summary>与上游 <c>PROFILE_COMPATIBILITY_FILENAME</c> 同源。</summary>
    internal const string FileName = "compatibility.json";

    /// <summary>上游 <c>PACKAGE_NAME</c>：npm 允许发布/作用域的包名（小写）。</summary>
    private static readonly Regex PackageNamePattern = new(
        @"^(?:@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>规范写法 SemVer（数值段不允许前导零；预发布的数值标识符另行检查）。</summary>
    private static readonly Regex ExactVersionPattern = new(
        @"^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+(?<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>豁免文件路径：<c>&lt;DSH_HOME&gt;/profiles/&lt;profile&gt;/compatibility.json</c>。</summary>
    public static string ResolveFilePath(string? dshHome, string? profileName)
    {
        var home = string.IsNullOrWhiteSpace(dshHome) ? "." : dshHome;
        var profile = string.IsNullOrWhiteSpace(profileName) ? DshProfileService.DefaultProfileName : profileName;
        return Path.Combine(home, DshProfileService.ProfilesDirectoryName, profile, FileName);
    }

    /// <summary>读取某个实例某个 profile 的豁免现状（永不抛异常；读不到按“没有放行”处理并给出警告）。</summary>
    public static PluginVersionExemptions Read(string? dshHome, string? profileName)
    {
        var path = ResolveFilePath(dshHome, profileName);
        try
        {
            if (!File.Exists(path))
            {
                return PluginVersionExemptions.Empty(path, fileExists: false);
            }

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return Unreadable(path, $"cannot be read ({ex.Message})");
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Unreadable(path, "must map exact package@version keys to DSH version lists");
            }

            var entries = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var warnings = new List<string>();
            foreach (var property in root.EnumerateObject())
            {
                if (!IsPackageVersionKey(property.Name))
                {
                    warnings.Add($"{path}: \"{property.Name}\" is not an exact package-name@version key; the record is ignored");
                    continue;
                }

                if (!TryReadVersionList(property.Value, out var versions))
                {
                    warnings.Add($"{path}: {property.Name} must contain a list of exact DSH versions; the record is ignored");
                    continue;
                }

                entries[property.Name] = versions;
            }

            return new PluginVersionExemptions(entries, warnings, Rewritable: warnings.Count == 0, path, FileExists: true);
        }
        catch (Exception)
        {
            // 只读诊断信息不值得影响实例操作：任何未预期的失败一律当“没有放行 + 未知”。
            return Unreadable(path, "cannot be parsed");
        }
    }

    /// <summary>键必须是 <c>包名@精确版本</c>（按<b>最后一个</b> @ 切分，与上游一致）。</summary>
    internal static bool IsPackageVersionKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var separator = key.LastIndexOf('@');
        if (separator <= 0)
        {
            return false;
        }

        return PackageNamePattern.IsMatch(key[..separator]) && IsExactVersion(key[(separator + 1)..]);
    }

    /// <summary>
    /// 上游 <c>isExactPluginVersion</c> 的等价判定：字符串必须**就是**规范写法（<c>semver.parse</c> 后拼回等于原串）
    /// ⇒ 拒绝 <c>v1.2.3</c>、<c>=1.2.3</c>、<c>^1.2.3</c>、<c>1.2</c>、<c>01.2.3</c>、<c>1.2.3-01</c>、带首尾空白等。
    /// </summary>
    internal static bool IsExactVersion(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var match = ExactVersionPattern.Match(value);
        if (!match.Success)
        {
            return false;
        }

        foreach (var part in match.Groups["pre"].Value.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            // 预发布里的「纯数字标识符」不得有前导零（semver 规范；node-semver 同样拒绝）。
            if (part.Length > 1 && part[0] == '0' && part.All(char.IsAsciiDigit))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadVersionList(JsonElement value, out IReadOnlyList<string> versions)
    {
        versions = Array.Empty<string>();
        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var values = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !IsExactVersion(item.GetString()))
            {
                return false;
            }

            values.Add(item.GetString()!);
        }

        versions = values;
        return true;
    }

    private static PluginVersionExemptions Unreadable(string path, string reason) =>
        new(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            new[] { $"{path} {reason}; treating the profile as having no exemptions" },
            Rewritable: false,
            path,
            FileExists: true);

    /// <summary>界面/日志用：把放行记录描述成一行（<c>pkg@1.2.3 → DSH 0.1.7-rc.2</c>，多条用 <c>；</c> 连接）。</summary>
    public static string Describe(PluginVersionExemptions exemptions)
    {
        if (exemptions.Entries.Count == 0)
        {
            return "（没有放行记录）";
        }

        var parts = exemptions.Entries
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key} → DSH {string.Join(" / ", pair.Value)}");
        return string.Join("；", parts);
    }

    /// <summary>
    /// 从上游 CLI 的报错里解析出它真正要求的 DSH 版本（<c>Use --dsh-version &lt;v&gt;.</c>）。
    /// 用途：实例登记的版本可能滞后（例如刚换过运行版本），这时用真实的那个版本重试一次即可，
    /// 而不是把上游的英文报错原样甩给用户。
    /// </summary>
    internal static string? TryReadRequiredDshVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var match = Regex.Match(output, @"Use --dsh-version\s+(?<version>[0-9A-Za-z.+-]+)", RegexOptions.CultureInvariant);
        // 上游那句以句号结尾（“…Use --dsh-version 0.1.7-rc.2.”）⇒ 去掉尾部标点。
        return match.Success ? match.Groups["version"].Value.TrimEnd('.', ',', ';') : null;
    }
}
