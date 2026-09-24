using System.Diagnostics;
using System.Text;
using System.IO;
using DshLauncher.Models;
using System.Text.RegularExpressions;

namespace DshLauncher.Services;

public sealed class DshInstallService
{
    public const string OfficialRegistry = "https://registry.npmjs.org";
    public const string ChinaRegistry = "https://registry.npmmirror.com";

    /// <summary>下载源 → npm registry URL（work-log/161）。所有版本下载一律走这里，不再各写各的。</summary>
    public static string RegistryFor(DshDownloadSource source) =>
        source == DshDownloadSource.ChinaMirror ? ChinaRegistry : OfficialRegistry;

    /// <summary>下载源的中文显示名（界面与进度文案共用）。</summary>
    public static string DisplayNameFor(DshDownloadSource source) =>
        source == DshDownloadSource.ChinaMirror ? "npmmirror 国内镜像" : "npm 官方源";

    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim VersionInstallGate = new(1, 1);

    /// <summary>
    /// 全局 DSh 只在 Installed 目标（或未指定目标的设置页）缺失时安装；
    /// Source 实例使用项目自带 CLI，不需要全局 @deepseek-ai/dsh。
    /// </summary>
    public static bool ShouldInstallGlobalDSh(bool dshAvailable, InstanceKind? targetKind) =>
        !dshAvailable && (targetKind is null or InstanceKind.Installed);

    public async Task<DshInstallResult> InstallAsync(
        NodeRuntimeInfo nodeRuntime,
        CancellationToken cancellationToken = default)
    {
        return await InstallAsync(nodeRuntime, registry: null, installDirectory: null, cancellationToken);
    }

    public async Task<DshInstallResult> InstallAsync(
        NodeRuntimeInfo nodeRuntime,
        string? registry,
        CancellationToken cancellationToken = default)
    {
        return await InstallAsync(nodeRuntime, registry, installDirectory: null, cancellationToken);
    }

    public async Task<DshInstallResult> InstallAsync(
        NodeRuntimeInfo nodeRuntime,
        string? registry,
        string? installDirectory,
        CancellationToken cancellationToken = default)
    {
        return await InstallVersionAsync(
            nodeRuntime,
            packageVersion: null,
            registry,
            installDirectory,
            cancellationToken);
    }

    public async Task<DshInstallResult> InstallVersionAsync(
        NodeRuntimeInfo nodeRuntime,
        string? packageVersion,
        string? registry,
        string? installDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!nodeRuntime.IsAvailable || string.IsNullOrWhiteSpace(nodeRuntime.ExecutablePath))
        {
            return DshInstallResult.Failure("未找到可用的 Node.js，不能执行 DSh 安装。");
        }

        if (registry is not null
            && !string.Equals(registry, OfficialRegistry, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(registry, ChinaRegistry, StringComparison.OrdinalIgnoreCase))
        {
            return DshInstallResult.Failure("DSh 安装源不受支持，只能使用 npm 官方源或 npmmirror 国内镜像。");
        }

        string? normalizedInstallDirectory;
        try
        {
            normalizedInstallDirectory = NormalizeInstallDirectory(installDirectory);
        }
        catch (ArgumentException ex)
        {
            return DshInstallResult.Failure(ex.Message);
        }

        if (!string.IsNullOrWhiteSpace(packageVersion) && !IsSafePackageVersion(packageVersion))
        {
            return DshInstallResult.Failure("DSh 版本号格式无效。 ");
        }

        var npmPath = FindNpm(nodeRuntime.ExecutablePath);
        if (!string.IsNullOrWhiteSpace(packageVersion)
            && !string.IsNullOrWhiteSpace(normalizedInstallDirectory))
        {
            return await InstallExactVersionAsync(
                npmPath,
                packageVersion,
                registry,
                normalizedInstallDirectory,
                cancellationToken);
        }

        var floatResult = await RunNpmInstallAsync(
            npmPath,
            packageVersion,
            registry,
            normalizedInstallDirectory,
            cancellationToken);
        if (!floatResult.IsSuccess || string.IsNullOrWhiteSpace(normalizedInstallDirectory))
        {
            return floatResult;
        }

        var floatPostCheck = EnsureInstalledRuntimeUsable(normalizedInstallDirectory, floatResult.Output);
        return floatPostCheck ?? floatResult;
    }

    /// <summary>
    /// 安装完成后的收尾自检（变更集 166）：补根级入口 shim，并验证依赖树从 dsh 安装锚点
    /// 完全可达。返回 null 表示一切正常；否则返回带原因的失败结果。
    /// </summary>
    private static DshInstallResult? EnsureInstalledRuntimeUsable(string installDirectory, string? output)
    {
        try
        {
            WriteRuntimeCommandShims(installDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DshInstallResult.Failure($"写入 DSh 运行时入口失败：{ex.Message}", output: output);
        }

        var unreachable = FindUnreachableLayerPackages(installDirectory);
        if (unreachable.Count > 0)
        {
            return DshInstallResult.Failure(
                $"安装出的依赖树不可用：{unreachable.Count} 个插件包从 DSh 安装目录不可达"
                + $"（例如 {string.Join("、", unreachable.Take(3))}）。"
                + "这会让实例启动时报 “plugin(s) failed to load”；请删除该运行时目录后重试安装。",
                output: output);
        }

        return null;
    }

    private static async Task<DshInstallResult> InstallExactVersionAsync(
        string npmPath,
        string packageVersion,
        string? registry,
        string installDirectory,
        CancellationToken cancellationToken)
    {
        await VersionInstallGate.WaitAsync(cancellationToken);
        string? stagingDirectory = null;
        try
        {
            if (InstalledVersionMatches(installDirectory, packageVersion))
            {
                return DshInstallResult.Success($"DSh {packageVersion} 已安装。");
            }

            stagingDirectory = CreateVersionStagingDirectory(installDirectory);
            // 变更集 175：暂存目录必须先成为 npm 认可的项目根。npm 的项目根不是 cwd，
            // 而是就近向上找的第一个含 package.json 或 node_modules 的祖先；非 global
            // 安装会在 DSh 安装根留下 package.json，于是这里的 npm 会把包装进共享运行根。
            PrepareStagingProject(stagingDirectory);
            var displacedProjectRoot = FindAncestorProjectRoot(stagingDirectory);
            var displacedBefore = displacedProjectRoot is null
                ? null
                : FingerprintProjectRoot(displacedProjectRoot);
            var result = await RunNpmInstallAsync(
                npmPath,
                packageVersion,
                registry,
                stagingDirectory,
                cancellationToken);
            if (!result.IsSuccess)
            {
                return result;
            }

            if (displacedProjectRoot is not null
                && displacedBefore is not null
                && !string.Equals(
                    FingerprintProjectRoot(displacedProjectRoot),
                    displacedBefore,
                    StringComparison.Ordinal))
            {
                return DshInstallResult.Failure(
                    $"npm 把包装进了安装根「{displacedProjectRoot}」而不是版本目录「{installDirectory}」："
                    + "共享运行根已被改动，该版本未落位。请核对 npm 行为后重试（并按需重装运行环境）。",
                    output: result.Output);
            }

            if (!InstalledVersionMatches(stagingDirectory, packageVersion))
            {
                return DshInstallResult.Failure(
                    $"npm 安装完成，但没有找到 DSh {packageVersion} 的有效运行目录。",
                    output: result.Output);
            }

            PromoteVersionDirectory(stagingDirectory, installDirectory);
            stagingDirectory = null;
            // 变更集 166：装完补入口 shim + 依赖树可达性自检（不可用则报失败而不是留着坑）。
            var postCheck = EnsureInstalledRuntimeUsable(installDirectory, result.Output);
            return postCheck ?? result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DshInstallResult.Failure($"保存 DSh {packageVersion} 失败：{ex.Message}");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(stagingDirectory))
            {
                TryDeleteDirectory(stagingDirectory);
            }

            VersionInstallGate.Release();
        }
    }

    private static async Task<DshInstallResult> RunNpmInstallAsync(
        string npmPath,
        string? packageVersion,
        string? registry,
        string? installDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(npmPath, registry, installDirectory, packageVersion);
        DshRuntimeCommandFactory.ApplyProxyFallback(startInfo);
        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return DshInstallResult.Failure("npm 进程无法启动。");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var exitTask = process.WaitForExitAsync();
            var completionTask = Task.WhenAll(exitTask, outputTask, errorTask);
            var timeoutTask = Task.Delay(InstallTimeout, cancellationToken);
            var completedTask = await Task.WhenAny(completionTask, timeoutTask);

            if (completedTask != completionTask)
            {
                TryKill(process);
                await WaitForExitSafelyAsync(completionTask);
                cancellationToken.ThrowIfCancellationRequested();
                return DshInstallResult.Failure("DSh 安装超过 10 分钟，已终止 npm 进程。");
            }

            await completionTask;
            var output = outputTask.Result.Trim();
            var error = errorTask.Result.Trim();
            if (process.ExitCode != 0)
            {
                return DshInstallResult.Failure(
                    string.IsNullOrWhiteSpace(error)
                        ? $"npm 安装失败，退出码 {process.ExitCode}。"
                        : error,
                    process.ExitCode,
                    output);
            }

            return DshInstallResult.Success(output);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await WaitForExitSafelyAsync(process);
            throw;
        }
        catch (Exception ex)
        {
            TryKill(process);
            await WaitForExitSafelyAsync(process);
            return DshInstallResult.Failure($"执行 DSh 安装失败：{ex.Message}");
        }
    }

    internal static string CreateVersionStagingDirectory(string installDirectory)
    {
        var normalized = NormalizeInstallDirectory(installDirectory)
            ?? throw new ArgumentException("DSh 安装位置不能为空。", nameof(installDirectory));
        var parent = Path.GetDirectoryName(normalized)
            ?? throw new ArgumentException("DSh 安装位置必须有父目录。", nameof(installDirectory));
        Directory.CreateDirectory(parent);
        return Path.Combine(parent, $".{Path.GetFileName(normalized)}.install-{Guid.NewGuid():N}");
    }

    /// <summary>
    /// 变更集 175：让暂存目录成为 npm 认可的项目根（自带一个最小 <c>package.json</c>）。
    /// </summary>
    internal static void PrepareStagingProject(string stagingDirectory)
    {
        Directory.CreateDirectory(stagingDirectory);
        var manifestPath = Path.Combine(stagingDirectory, "package.json");
        if (!File.Exists(manifestPath))
        {
            File.WriteAllText(
                manifestPath,
                "{\"private\":true}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    /// <summary>npm 在暂存目录不是项目根时会挑中的祖先目录（跳过暂存目录本身）。</summary>
    internal static string? FindAncestorProjectRoot(string stagingDirectory)
    {
        var current = Directory.GetParent(Path.GetFullPath(stagingDirectory))?.FullName;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "package.json"))
                || Directory.Exists(Path.Combine(current, "node_modules")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        return null;
    }

    /// <summary>项目根指纹（package.json + package-lock.json）：安装后据此判断有没有被 npm 改动。</summary>
    internal static string FingerprintProjectRoot(string projectRoot) =>
        FingerprintFile(Path.Combine(projectRoot, "package.json"))
        + "|"
        + FingerprintFile(Path.Combine(projectRoot, "package-lock.json"));

    private static string FingerprintFile(string path)
    {
        if (!File.Exists(path))
        {
            return "(不存在)";
        }

        var info = new FileInfo(path);
        return $"{info.Length}@{info.LastWriteTimeUtc.Ticks}";
    }

    internal static void PromoteVersionDirectory(string stagingDirectory, string installDirectory)
    {
        var staging = Path.GetFullPath(stagingDirectory);
        var target = Path.GetFullPath(installDirectory);
        var parent = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException("DSh 版本目录必须有父目录。");
        if (!string.Equals(Path.GetDirectoryName(staging), parent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(staging).StartsWith($".{Path.GetFileName(target)}.install-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("DSh 临时安装目录不属于目标版本目录。");
        }

        var backup = Path.Combine(parent, $".{Path.GetFileName(target)}.backup-{Guid.NewGuid():N}");
        var movedExisting = false;
        try
        {
            if (Directory.Exists(target))
            {
                Directory.Move(target, backup);
                movedExisting = true;
            }

            Directory.Move(staging, target);
        }
        catch
        {
            if (movedExisting && !Directory.Exists(target) && Directory.Exists(backup))
            {
                Directory.Move(backup, target);
            }

            throw;
        }

        if (movedExisting)
        {
            TryDeleteDirectory(backup);
        }
    }

    /// <summary>
    /// 在安装目录根补 dsh 入口 shim（变更集 166）。npm 的普通安装把 shim 放在
    /// <c>node_modules/.bin/dsh.cmd</c>，而启动器与运行时探测都期望
    /// <c>&lt;安装目录&gt;\dsh.cmd</c>（旧实现靠 npm --global 才建在那里）。
    /// </summary>
    internal static void WriteRuntimeCommandShims(string installDirectory)
    {
        Directory.CreateDirectory(installDirectory);
        var binShimPath = Path.Combine(installDirectory, "node_modules", ".bin", "dsh.cmd");
        var rootShimPath = Path.Combine(installDirectory, "dsh.cmd");
        var content = File.Exists(binShimPath)
            ? RewriteShimForRoot(File.ReadAllText(binShimPath))
            : BuildFallbackShim();
        File.WriteAllText(rootShimPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// 把 <c>node_modules\.bin\dsh.cmd</c> 的目标从“上一级”改成“node_modules 内”，
    /// 使同一个 shim 放到安装目录根也能用；找不到目标模式时退回自建骨架。
    /// </summary>
    internal static string RewriteShimForRoot(string shimText)
    {
        const string from = "\\..\\@deepseek-ai\\dsh\\lib\\bin.js";
        const string to = "\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js";
        if (string.IsNullOrEmpty(shimText) || !shimText.Contains(from, StringComparison.Ordinal))
        {
            return BuildFallbackShim();
        }

        return shimText.Replace(from, to, StringComparison.Ordinal);
    }

    /// <summary>自建最小 shim（npm 换 shim 写法时的兜底；ASCII + CRLF）。</summary>
    internal static string BuildFallbackShim() =>
        string.Join("\r\n", new[]
        {
            "@ECHO off",
            "GOTO start",
            ":find_dp0",
            "SET dp0=%~dp0",
            "EXIT /b",
            ":start",
            "SETLOCAL",
            "CALL :find_dp0",
            string.Empty,
            "IF EXIST \"%dp0%\\node.exe\" (",
            "  SET \"_prog=%dp0%\\node.exe\"",
            ") ELSE (",
            "  SET \"_prog=node\"",
            "  SET PATHEXT=%PATHEXT:;.JS;=;%",
            ")",
            string.Empty,
            "endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & \"%_prog%\"  \"%dp0%\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js\" %*",
            string.Empty
        });

    /// <summary>
    /// 依赖树可达性自检（变更集 166 的回归门）：树里每个 <c>@deepseek-ai/*</c> 包都必须能
    /// 从 dsh 安装锚点按 Node 的向上查找规则找到。npm --global 的深层嵌套会让大量包不可达，
    /// dsh 的插件解析随之失败（实测 120/240）。
    /// </summary>
    internal static IReadOnlyList<string> FindUnreachableLayerPackages(string installDirectory)
    {
        var anchor = Path.Combine(installDirectory, "node_modules", "@deepseek-ai", "dsh");
        if (!Directory.Exists(anchor))
        {
            return Array.Empty<string>();
        }

        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scopeDirectory in EnumerateScopeDirectories(installDirectory))
        {
            foreach (var packageDirectory in Directory.EnumerateDirectories(scopeDirectory))
            {
                if (File.Exists(Path.Combine(packageDirectory, "package.json")))
                {
                    names.Add("@deepseek-ai/" + Path.GetFileName(packageDirectory));
                }
            }
        }

        var unreachable = new List<string>();
        foreach (var name in names)
        {
            if (!IsResolvableFromAnchor(anchor, name))
            {
                unreachable.Add(name);
            }
        }

        return unreachable;
    }

    private static IEnumerable<string> EnumerateScopeDirectories(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (string.Equals(Path.GetFileName(child), "@deepseek-ai", StringComparison.OrdinalIgnoreCase))
                {
                    yield return child;
                }

                pending.Push(child);
            }
        }
    }

    private static bool IsResolvableFromAnchor(string anchorDirectory, string packageName)
    {
        var current = anchorDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var candidate = Path.Combine(
                current, "node_modules", packageName.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(Path.Combine(candidate, "package.json")))
            {
                return true;
            }

            current = Path.GetDirectoryName(current);
        }

        return false;
    }

    private static bool InstalledVersionMatches(string installDirectory, string packageVersion)
    {
        var packageRoot = DshRuntimeDetector.TryResolvePackageRoot(installDirectory);
        var installedVersion = packageRoot is null
            ? null
            : DshRuntimeDetector.TryReadPackageVersion(packageRoot);
        return string.Equals(installedVersion, packageVersion, StringComparison.OrdinalIgnoreCase)
            && DshRuntimeCommandFactory.IsUsable(
                packageRoot is null ? null : DshRuntimeDetector.CreateLaunchSpecForPackageRoot(packageRoot));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                FileSystemCleanup.DeleteDirectoryRecursive(path);
            }
        }
        catch
        {
            // A stale temp/backup directory is safer than deleting an uncertain target.
        }
    }

    private static string FindNpm(string nodeExecutablePath)
    {
        var nodeDirectory = Path.GetDirectoryName(Path.GetFullPath(nodeExecutablePath));
        if (!string.IsNullOrWhiteSpace(nodeDirectory))
        {
            foreach (var fileName in new[] { "npm.cmd", "npm.exe", "npm" })
            {
                var candidate = Path.Combine(nodeDirectory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return "npm.cmd";
    }

    internal static string? NormalizeInstallDirectory(string? installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            return null;
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(installDirectory.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"DSh 安装位置无效：{ex.Message}", nameof(installDirectory));
        }

        var root = Path.GetPathRoot(normalized)?.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized)
            || string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("DSh 安装位置不能是磁盘根目录。", nameof(installDirectory));
        }

        if (File.Exists(normalized))
        {
            throw new ArgumentException("DSh 安装位置必须是文件夹，不能是现有文件。", nameof(installDirectory));
        }

        return normalized;
    }

    internal static bool IsSafePackageVersion(string? packageVersion) =>
        !string.IsNullOrWhiteSpace(packageVersion)
        && Regex.IsMatch(packageVersion, "^[0-9A-Za-z][0-9A-Za-z.+-]{0,79}$", RegexOptions.CultureInvariant);

    internal static ProcessStartInfo CreateStartInfo(
        string npmPath,
        string? registry,
        string? installDirectory,
        string? packageVersion = null)
    {
        var packageSpec = string.IsNullOrWhiteSpace(packageVersion)
            ? "@deepseek-ai/dsh"
            : $"@deepseek-ai/dsh@{packageVersion}";
        // 安装方式：**非 global**（变更集 166）。
         // 原实现是 `npm install --global` + NPM_CONFIG_PREFIX=<安装目录>：好处是 npm 会把
         // dsh.cmd 直接建到该 prefix 下，坏处是**全局模式的依赖树会深度嵌套** —— 实测那份
         // 运行时 240 个 @deepseek-ai/* 包里有 **120 个从 dsh 安装锚点不可达**，而 dsh 解析
         // 层文件里的插件名时够不到它们 ⇒ 实例启动直接崩在
         // “plugin(s) failed to load: @deepseek-ai/dsh-sandbox-local”。
         // 改成在安装目录里做普通安装（WorkingDirectory = 安装目录）：依赖树天然扁平、
         // 可达性 100%；入口 shim 由 WriteRuntimeCommandShims 补到安装目录根。
        // 变更集 175：显式用 --prefix 钉住项目根。只设 WorkingDirectory 时，只要安装根
        // 已经有 package.json（非 global 安装的必然产物），npm 就会把包装进安装根。
        var commandArguments = $"install {packageSpec}"
            + (string.IsNullOrWhiteSpace(installDirectory) ? string.Empty : $" --prefix \"{installDirectory}\"")
            + (string.IsNullOrWhiteSpace(registry) ? string.Empty : $" --registry={registry}");
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(installDirectory))
        {
            // 普通（非 global）安装以工作目录为项目根：npm 把依赖装到
            // <安装目录>/node_modules，并就地写 package.json / package-lock.json。
            Directory.CreateDirectory(installDirectory);
            startInfo.WorkingDirectory = installDirectory;
        }

        if (Path.GetExtension(npmPath).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(npmPath).Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.Arguments = $"/d /c \"\"{npmPath}\" {commandArguments}\"";
        }
        else
        {
            startInfo.FileName = npmPath;
            startInfo.Arguments = commandArguments;
        }

        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Installation cleanup must not mask the original error.
        }
    }

    private static async Task WaitForExitSafelyAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch
        {
            // The process may have exited while it was being terminated.
        }
    }

    private static async Task WaitForExitSafelyAsync(Task completionTask)
    {
        try
        {
            await completionTask;
        }
        catch
        {
            // A terminated npm process may fail while its output streams close.
        }
    }
}
