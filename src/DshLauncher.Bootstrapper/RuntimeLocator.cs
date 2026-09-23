using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace DshLauncher.Bootstrapper;

/// <summary>运行时来源（变更集 168）。</summary>
internal sealed class RuntimeLocation
{
    internal RuntimeLocation(string? version, string? dotnetRoot, string source, string? detail)
    {
        Version = version;
        DotnetRoot = dotnetRoot;
        Source = source;
        Detail = detail;
    }

    /// <summary>检测到的 WindowsDesktop.App 版本；null 表示"没找到可用运行时"。</summary>
    internal string? Version { get; }

    /// <summary>需要注入 <c>DOTNET_ROOT</c>/<c>DOTNET_ROOT_X64</c> 的目录；系统级安装时为 null（不需要注入）。</summary>
    internal string? DotnetRoot { get; }

    /// <summary>portable / system / user / missing。</summary>
    internal string Source { get; }

    /// <summary>给对话框用的补充说明（例如"便携目录存在但不可用"）。</summary>
    internal string? Detail { get; }

    internal bool Found => Version is not null;
}

/// <summary>
/// 找 .NET 8 Desktop Runtime 的三条路（变更集 168）：
/// ① <c>&lt;exe 目录&gt;\runtime\dotnet</c>（**便携运行时**：放在这儿就是要用它，可用则优先）
/// ② 系统级（注册表 → <c>%ProgramFiles%\dotnet\shared</c>）
/// ③ <c>%USERPROFILE%\.dotnet</c>（用户级安装，例如官方 dotnet-install.ps1 的默认位置）
/// ②与③都以 "8.x 的 Microsoft.WindowsDesktop.App + 宿主（host/fxr）" 为可用判据。
/// </summary>
internal static class RuntimeLocator
{
    internal const string RequiredMajorVersion = "8";
    internal const string PortableRuntimeFolder = "runtime";
    internal const string PortableRuntimeLeaf = "dotnet";
    private const string DesktopFrameworkName = "Microsoft.WindowsDesktop.App";
    private const string SharedFrameworkKeyPath =
        @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App";

    /// <summary>便携运行时目录：<c>&lt;exe 目录&gt;\runtime\dotnet</c>。</summary>
    internal static string PortableRuntimeDirectory(string executableDirectory) =>
        Path.Combine(executableDirectory, PortableRuntimeFolder, PortableRuntimeLeaf);

    /// <summary>用户级运行时目录：<c>%USERPROFILE%\.dotnet</c>。</summary>
    internal static string UserRuntimeDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? string.Empty,
            ".dotnet");

    internal static RuntimeLocation Probe(string executableDirectory)
    {
        // ① 便携运行时（放这儿就是要用它）。
        var portableDirectory = PortableRuntimeDirectory(executableDirectory);
        if (Directory.Exists(portableDirectory))
        {
            var portableVersion = FindDesktopFramework(portableDirectory);
            if (portableVersion is not null && HasHost(portableDirectory))
            {
                return new RuntimeLocation(portableVersion, portableDirectory, "portable", portableDirectory);
            }

            var reason = portableVersion is null
                ? "目录里没有 8.x 的 Microsoft.WindowsDesktop.App"
                : "目录里没有宿主 host/fxr";
            return ProbeSystemOrUser($"便携运行时不可用（{reason}）：{portableDirectory}");
        }

        return ProbeSystemOrUser(null);
    }

    private static RuntimeLocation ProbeSystemOrUser(string? detail)
    {
        // ② 系统级。
        var systemVersion = DetectSystemDesktopRuntime();
        if (systemVersion is not null)
        {
            return new RuntimeLocation(systemVersion, null, "system", detail);
        }

        // ③ 用户级。
        var userDirectory = UserRuntimeDirectory();
        if (Directory.Exists(userDirectory))
        {
            var userVersion = FindDesktopFramework(userDirectory);
            if (userVersion is not null && HasHost(userDirectory))
            {
                return new RuntimeLocation(userVersion, userDirectory, "user", detail);
            }
        }

        return new RuntimeLocation(null, null, "missing", detail);
    }

    /// <summary>在某个运行时根下找 8.x 的 Microsoft.WindowsDesktop.App（取最高补丁）。</summary>
    internal static string? FindDesktopFramework(string runtimeRoot)
    {
        try
        {
            var shared = Path.Combine(runtimeRoot, "shared", DesktopFrameworkName);
            if (!Directory.Exists(shared))
            {
                return null;
            }

            return Directory.GetDirectories(shared)
                .Select(Path.GetFileName)
                .Where(name => name is not null && name.StartsWith(RequiredMajorVersion + ".", StringComparison.Ordinal))
                .OrderByDescending(ParseVersion)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>宿主是否在位（缺 host/fxr 的话 apphost 解析不了框架 —— 实验里踩过）。</summary>
    internal static bool HasHost(string runtimeRoot)
    {
        try
        {
            var fxr = Path.Combine(runtimeRoot, "host", "fxr");
            if (!Directory.Exists(fxr))
            {
                return false;
            }

            return Directory.GetDirectories(fxr)
                .Any(directory => File.Exists(Path.Combine(directory, "hostfxr.dll")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 系统级检测：① 注册表（dotnet 安装器写的清单，权威）；② %ProgramFiles%\dotnet\shared 目录（兜底）。
    /// </summary>
    internal static string? DetectSystemDesktopRuntime()
    {
        try
        {
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = baseKey.OpenSubKey(SharedFrameworkKeyPath))
            {
                if (key is not null)
                {
                    var match = key.GetValueNames()
                        .Where(name => name.StartsWith(RequiredMajorVersion + ".", StringComparison.Ordinal))
                        .OrderByDescending(ParseVersion)
                        .FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(match))
                    {
                        return match;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // 读不到注册表时继续走目录探测。
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return string.IsNullOrWhiteSpace(programFiles) ? null : FindDesktopFramework(programFiles + Path.DirectorySeparatorChar + "dotnet");
    }

    private static Version ParseVersion(string value) =>
        Version.TryParse(value, out var parsed) ? parsed : new Version(0, 0);
}
