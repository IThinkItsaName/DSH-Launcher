using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Web.Script.Serialization;

namespace DshLauncher.Bootstrapper;

/// <summary>
/// 「下载便携运行时」按钮的实现（变更集 168）：免管理员、不写注册表，只往
/// <c>&lt;exe 目录&gt;\runtime\dotnet</c> 放两份官方 zip 解出来的文件。
/// <list type="number">
/// <item>取官方版本索引 <c>releases.json</c>（8.0 渠道）→ 最新 8.0.x 的两份 win-x64 zip 地址；</item>
/// <item>下载 <c>dotnet-runtime-*</c>（含 dotnet.exe + host/fxr + NETCore.App）与
///       <c>windowsdesktop-runtime-*</c>（含 WindowsDesktop.App，WPF 要用）；</item>
/// <item>解压到 staging 目录 → 校验（8.x 桌面框架 + 宿主）→ 原子替换到目标目录。</item>
/// </list>
/// 实测（work-log/192）：两份 zip 解压在一起就是一份可用的运行时，<c>DOTNET_ROOT</c> 指过去即可；
/// 宿主必须来自 runtime zip（只解 windowsdesktop 那份会缺 host/fxr，起不来）。
/// </summary>
internal static class PortableRuntimeInstaller
{
    internal const string ReleasesIndexUrl =
        "https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json";

    /// <summary>读索引的超时（秒）——WebClient 默认无超时，不加就会无限等（真机上撞到过）。</summary>
    private const int IndexTimeoutSeconds = 60;

    /// <summary>单份 zip 的下载超时（分钟）。</summary>
    private const int DownloadTimeoutMinutes = 30;

    /// <summary>下载体积经验值（用于对话框文案；取自 8.0.31：31.8 MB + 35.1 MB）。</summary>
    internal const string ApproximateDownloadSizeText = "约 67 MB（两份官方 zip）";

    internal static string Install(string executableDirectory, Action<string> progress, out long downloadedBytes)
    {
        downloadedBytes = 0;
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

        var target = RuntimeLocator.PortableRuntimeDirectory(executableDirectory);
        var runtimeRoot = Path.Combine(executableDirectory, RuntimeLocator.PortableRuntimeFolder);
        Directory.CreateDirectory(runtimeRoot);

        progress("正在读取官方版本索引…");
        var urls = ResolveLatestRuntimeZipUrls();

        var staging = Path.Combine(runtimeRoot, ".dotnet-staging-" + Guid.NewGuid().ToString("N"));
        var tempZips = Path.Combine(Path.GetTempPath(), "dsh-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(tempZips);

            var index = 0;
            foreach (var (label, url) in urls)
            {
                index++;
                var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
                var zipPath = Path.Combine(tempZips, fileName);
                progress($"正在下载 {index}/{urls.Count}：{fileName}…");
                Download(url, zipPath, (done, total) =>
                {
                    var percent = total > 0 ? (int)(done * 100 / total) : 0;
                    progress($"正在下载 {index}/{urls.Count}：{fileName}（{percent}%）");
                });
                downloadedBytes += new FileInfo(zipPath).Length;

                progress($"正在解压 {fileName}…");
                ExtractZip(zipPath, staging);
            }

            var version = RuntimeLocator.FindDesktopFramework(staging);
            if (version is null || !RuntimeLocator.HasHost(staging))
            {
                throw new InvalidOperationException(
                    "解压后校验失败：缺少 8.x 的 Microsoft.WindowsDesktop.App 或宿主 host/fxr。");
            }

            progress("正在就位…");
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.Move(staging, target);
            return version;
        }
        finally
        {
            TryDelete(staging);
            TryDelete(tempZips);
        }
    }

    /// <summary>解析 releases.json，取最新 8.0.x 的 runtime 与 windowsdesktop（win-x64 zip）。</summary>
    internal static List<(string Label, string Url)> ResolveLatestRuntimeZipUrls()
    {
        string json;
        using (var client = new WebClient())
        {
            client.Encoding = System.Text.Encoding.UTF8;
            var task = client.DownloadStringTaskAsync(new Uri(ReleasesIndexUrl));
            if (!task.Wait(TimeSpan.FromSeconds(IndexTimeoutSeconds)))
            {
                client.CancelAsync();
                throw new InvalidOperationException($"读取官方版本索引超时（{IndexTimeoutSeconds} 秒）。");
            }

            if (task.Exception is not null)
            {
                throw new InvalidOperationException(
                    "读取官方版本索引失败：" + (task.Exception.InnerException?.Message ?? task.Exception.Message),
                    task.Exception);
            }

            json = task.Result;
        }

        var serializer = new JavaScriptSerializer();
        var root = serializer.Deserialize<Dictionary<string, object>>(json);
        var releases = AsList(root is not null && root.TryGetValue("releases", out var releasesObject) ? releasesObject : null);
        if (releases.Count == 0)
        {
            var preview = json.Length > 200 ? json.Substring(0, 200) : json;
            var keys = root is null ? "null" : string.Join(",", root.Keys);
            throw new InvalidOperationException(
                $"官方版本索引里没有 releases（收到 {json.Length} 字符；顶层键={keys}；开头={preview.Replace('\n', ' ').Replace('\r', ' ')}）");
        }

        var latest = AsObject(releases[0])
            ?? throw new InvalidOperationException("官方版本索引里的最新条目格式不符合预期。");

        var result = new List<(string, string)>();
        foreach (var (label, key) in new[] { ("运行时", "runtime"), ("桌面运行时", "windowsdesktop") })
        {
            var url = FindWinX64ZipUrl(latest, key);
            if (url is null)
            {
                throw new InvalidOperationException($"官方版本索引里找不到 {key} 的 win-x64 zip。");
            }

            result.Add((label, url));
        }

        return result;
    }

    private static string? FindWinX64ZipUrl(Dictionary<string, object> release, string key)
    {
        var block = release.TryGetValue(key, out var blockObject) ? AsObject(blockObject) : null;
        if (block is null || !block.TryGetValue("files", out var filesObject))
        {
            return null;
        }

        foreach (var item in AsList(filesObject))
        {
            var file = AsObject(item);
            if (file is null)
            {
                continue;
            }

            var rid = file.TryGetValue("rid", out var ridValue) ? ridValue as string : null;
            var name = file.TryGetValue("name", out var nameValue) ? nameValue as string : null;
            var url = file.TryGetValue("url", out var urlValue) ? urlValue as string : null;
            if (string.Equals(rid, "win-x64", StringComparison.OrdinalIgnoreCase)
                && name is not null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        return null;
    }

    /// <summary>
    /// JavaScriptSerializer 把 JSON 数组反序列化成 <see cref="System.Collections.ArrayList"/>（不是 <c>object[]</c>）——
    /// 首版按 <c>object[]</c> 匹配，于是误报"索引里没有 releases"（真机上撞到）。这里两种都收。
    /// </summary>
    private static List<object?> AsList(object? value) => value switch
    {
        System.Collections.ArrayList list => list.Cast<object?>().ToList(),
        object?[] array => array.ToList(),
        _ => new List<object?>()
    };

    private static Dictionary<string, object>? AsObject(object? value) => value as Dictionary<string, object>;

    private static void Download(string url, string destinationPath, Action<long, long> onProgress)
    {
        using (var client = new WebClient())
        {
            client.Headers.Add("User-Agent", "DshLauncher.Bootstrapper");
            client.DownloadProgressChanged += (_, e) => onProgress(e.BytesReceived, e.TotalBytesToReceive);
            var task = client.DownloadFileTaskAsync(new Uri(url), destinationPath);
            if (!task.Wait(TimeSpan.FromMinutes(DownloadTimeoutMinutes)))
            {
                client.CancelAsync();
                throw new InvalidOperationException($"下载超时（{DownloadTimeoutMinutes} 分钟）：{Path.GetFileName(destinationPath)}");
            }

            if (task.Exception is not null)
            {
                throw new InvalidOperationException(
                    "下载失败：" + (task.Exception.InnerException?.Message ?? task.Exception.Message),
                    task.Exception);
            }
        }
    }

    /// <summary>逐条解压（net48 没有 ExtractToDirectory 的 overwrite 重载）。</summary>
    private static void ExtractZip(string zipPath, string destinationDirectory)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue; // 目录条目：解压文件时会自动建
            }

            var targetPath = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
            if (!targetPath.StartsWith(Path.GetFullPath(destinationDirectory), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("zip 条目越出了目标目录：" + entry.FullName);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响主流程（staging 名字带 guid，不会覆盖正式目录）。
        }
    }
}
