using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DshLauncher.Bootstrapper;

/// <summary>
/// 自检引导器（变更集 165 引入，变更集 168 扩成"三条路"）：
/// 主程序是框架依赖小包，需要 .NET 8 Desktop Runtime。缺运行时时主程序无法自检（起不来），
/// 所以提醒与"就地补齐"必须由本程序提供。目标框架是 .NET Framework 4.8 ——
/// Windows 10 1903+ / Windows 11 自带，本体只有几十 KB。
///
/// 三条路（见 <see cref="RuntimeLocator"/>）：
/// ① <c>&lt;exe 目录&gt;\runtime\dotnet</c>（便携运行时，可用则优先，注入 DOTNET_ROOT 后拉起主程序）
/// ② 系统级 .NET 8 Desktop Runtime（直接拉起）
/// ③ <c>%USERPROFILE%\.dotnet</c>（用户级安装，注入 DOTNET_ROOT 后拉起）
/// 都没有 ⇒ 弹中文对话框：官方下载页 / winget 命令 / 「我已安装，重试」/「下载便携运行时（免管理员）」。
/// </summary>
internal static class Program
{
    /// <summary>同目录里的主程序文件名（发布形态：引导器 DSH Launcher.exe + 主程序 DSH Launcher.App.exe）。</summary>
    private const string AppFileName = "DSH Launcher.App.exe";

    private const string DownloadUrl = "https://dotnet.microsoft.com/download/dotnet/8.0";
    private const string WingetCommand = "winget install Microsoft.DotNet.DesktopRuntime.8";

    [STAThread]
    private static int Main(string[] args)
    {
        var executableDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var checkOnly = HasFlag(args, "--check-only");
        var simulateMissing = HasFlag(args, "--simulate-missing");
        var installPortableOnly = HasFlag(args, "--install-portable-runtime");

        // 无界面：只装便携运行时（便于脚本化验证）。
        if (installPortableOnly)
        {
            try
            {
                var installed = PortableRuntimeInstaller.Install(
                    executableDirectory,
                    line => Console.WriteLine("  " + line),
                    out var downloadedBytes);
                Console.WriteLine("portable-runtime=" + installed);
                Console.WriteLine("target=" + RuntimeLocator.PortableRuntimeDirectory(executableDirectory));
                Console.WriteLine("downloaded-bytes=" + downloadedBytes);
                return 0;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Net.WebException)
            {
                Console.WriteLine("install-failed=" + ex.Message);
                return 4;
            }
        }

        // 无界面：只解析官方版本索引，打印两份 zip 的地址（不下载）。
        if (HasFlag(args, "--print-release-urls"))
        {
            try
            {
                foreach (var (label, url) in PortableRuntimeInstaller.ResolveLatestRuntimeZipUrls())
                {
                    Console.WriteLine(label + "=" + url);
                }

                return 0;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Net.WebException)
            {
                Console.WriteLine("resolve-failed=" + ex.Message);
                return 5;
            }
        }

        var probe = simulateMissing
            ? new RuntimeLocation(null, null, "missing", "（--simulate-missing 强制按「缺失」处理）")
            : RuntimeLocator.Probe(executableDirectory);

        if (checkOnly)
        {
            // 供自动化核对用：把检测结果打到 stdout（WinExe 在重定向时同样可写）。
            Console.WriteLine("desktop-runtime=" + (probe.Version ?? "missing"));
            Console.WriteLine("source=" + probe.Source);
            Console.WriteLine("dotnet-root=" + (probe.DotnetRoot ?? "none"));
            Console.WriteLine("portable-runtime-dir=" + RuntimeLocator.PortableRuntimeDirectory(executableDirectory));
            Console.WriteLine("main-app=" + (File.Exists(ResolveAppPath(executableDirectory)) ? "found" : "missing"));
            return probe.Found ? 0 : 3;
        }

        if (probe.Found)
        {
            return LaunchMainApp(executableDirectory, args, probe.DotnetRoot);
        }

        DialogResult result;
        using (var dialog = new MissingRuntimeDialog(executableDirectory, probe))
        {
            result = dialog.ShowDialog();
        }

        if (result == DialogResult.OK)
        {
            // 用户在对话框里装好了（可能是便携运行时，也可能是系统运行时）⇒ 重探一次再拉起。
            var after = RuntimeLocator.Probe(executableDirectory);
            if (after.Found)
            {
                return LaunchMainApp(executableDirectory, args, after.DotnetRoot);
            }
        }

        return 3;
    }

    private static bool HasFlag(string[] args, string flag) =>
        args is not null && args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    internal static string ResolveAppPath(string executableDirectory) =>
        Path.Combine(executableDirectory, AppFileName);

    /// <summary>
    /// 拉起主程序（命令行原样透传；不等待，本身立刻退出）。
    /// <paramref name="dotnetRoot"/> 非空时注入 DOTNET_ROOT/DOTNET_ROOT_X64 —— 便携/用户级运行时就靠这个生效
    /// （实测：进程序加载的 coreclr 会从该系统安装切到该目录，见 work-log/192）。
    /// </summary>
    private static int LaunchMainApp(string executableDirectory, string[] args, string? dotnetRoot)
    {
        var appPath = ResolveAppPath(executableDirectory);
        if (!File.Exists(appPath))
        {
            MessageBox.Show(
                "未找到主程序：" + AppFileName + Environment.NewLine + Environment.NewLine
                + "请确认引导器与主程序在同一个文件夹里（发布物应为两个文件）。" + Environment.NewLine
                + appPath,
                "DSH Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = appPath,
                WorkingDirectory = executableDirectory,
                UseShellExecute = false
            };
            if (!string.IsNullOrWhiteSpace(dotnetRoot))
            {
                startInfo.Environment["DOTNET_ROOT"] = dotnetRoot;
                startInfo.Environment["DOTNET_ROOT_X64"] = dotnetRoot;
                startInfo.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
            }

            if (args is not null && args.Length > 0)
            {
                // 逐项加引号（路径可能带空格）。
                startInfo.Arguments = string.Join(" ", args.Select(QuoteArgument));
            }

            Process.Start(startInfo);
            return 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            MessageBox.Show(
                "启动主程序失败：" + ex.Message + Environment.NewLine + Environment.NewLine + appPath,
                "DSH Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
    }

    private static string QuoteArgument(string value) =>
        string.IsNullOrEmpty(value) ? "\"\"" : "\"" + value.Replace("\"", "\\\"") + "\"";

    // ---- 供对话框复用的常量 -------------------------------------------------
    internal const string DownloadPageUrl = DownloadUrl;
    internal const string WingetInstallCommand = WingetCommand;
}

/// <summary>
/// 缺运行时的中文对话框（变更集 165/168）：给出三条路 ——
/// 官方下载页、winget 命令、**下载便携运行时（免管理员，约 67 MB）**，并允许装完后原地重试。
/// </summary>
internal sealed class MissingRuntimeDialog : Form
{
    private readonly string _executableDirectory;
    private readonly Label _status;
    private readonly TextBox _command;
    private readonly Button _installPortable;
    private readonly Button _retry;
    private readonly Button _downloadPage;
    private readonly Button _close;

    internal MissingRuntimeDialog(string executableDirectory, RuntimeLocation probe)
    {
        _executableDirectory = executableDirectory;
        Text = "DSH Launcher 需要 .NET 8 桌面运行时";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(620, 372);
        Font = new Font("Microsoft YaHei UI", 9F);
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException)
        {
            // 图标取不到不影响功能。
        }

        var title = new Label
        {
            Text = "缺少 .NET 8 Desktop Runtime（x64）",
            Font = new Font(Font.FontFamily, 12F, FontStyle.Bold),
            Location = new Point(20, 18),
            Size = new Size(580, 30)
        };

        var body = new Label
        {
            Text = "启动器的主程序是框架依赖包（约 3.6 MB），需要 .NET 8 Desktop Runtime (x64)。"
                 + Environment.NewLine
                 + "三种办法任选：① 官方下载页安装；② winget 命令；③ 让启动器**下载便携运行时**（免管理员、不写注册表，约 67 MB，解压到程序旁的 runtime 目录）。",
            Location = new Point(22, 56),
            Size = new Size(576, 64)
        };

        _status = new Label
        {
            Text = "检测结果：" + DescribeProbe(probe),
            ForeColor = probe.Found ? Color.DarkGreen : Color.Firebrick,
            Location = new Point(22, 124),
            Size = new Size(576, 40)
        };

        var commandLabel = new Label
        {
            Text = "方式二：命令行（复制后在「终端 / PowerShell」里执行）",
            Location = new Point(22, 170),
            Size = new Size(576, 22)
        };

        _command = new TextBox
        {
            Text = Program.WingetInstallCommand,
            ReadOnly = true,
            Location = new Point(22, 194),
            Size = new Size(460, 26),
            BackColor = Color.White
        };

        var copyButton = new Button
        {
            Text = "复制命令",
            Location = new Point(492, 193),
            Size = new Size(106, 28)
        };
        copyButton.Click += (_, __) =>
        {
            try
            {
                Clipboard.SetText(_command.Text);
                SetStatus("已复制 winget 命令，可在终端里执行。", error: false);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
            {
                SetStatus("复制失败，请手动选中命令复制。", error: true);
            }
        };

        _downloadPage = new Button
        {
            Text = "打开官方下载页",
            Location = new Point(22, 236),
            Size = new Size(150, 34)
        };
        _downloadPage.Click += (_, __) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = Program.DownloadPageUrl, UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                SetStatus("无法打开浏览器，请手动访问：" + Program.DownloadPageUrl, error: true);
            }
        };

        _installPortable = new Button
        {
            Text = "下载便携运行时（免管理员）",
            Location = new Point(182, 236),
            Size = new Size(230, 34)
        };
        _installPortable.Click += async (_, __) => await InstallPortableAsync();

        _retry = new Button
        {
            Text = "我已安装，重试",
            Location = new Point(422, 236),
            Size = new Size(150, 34)
        };
        _retry.Click += (_, __) =>
        {
            var after = RuntimeLocator.Probe(_executableDirectory);
            if (!after.Found)
            {
                SetStatus("仍未检测到 .NET 8 Desktop Runtime（x64）：" + DescribeProbe(after), error: true);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        };

        _close = new Button
        {
            Text = "关闭",
            Location = new Point(422, 286),
            Size = new Size(150, 34)
        };
        _close.Click += (_, __) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        var hint = new Label
        {
            Text = "提示：便携运行时放在 <程序目录>\\runtime\\dotnet；放好之后启动器会优先用它，并把 DOTNET_ROOT 指过去。",
            ForeColor = Color.DimGray,
            Location = new Point(22, 292),
            Size = new Size(390, 60)
        };

        Controls.AddRange(new Control[]
        {
            title, body, _status, commandLabel, _command, copyButton,
            _downloadPage, _installPortable, _retry, _close, hint
        });
        AcceptButton = _installPortable;
        CancelButton = _close;
    }

    private async Task InstallPortableAsync()
    {
        SetBusy(true);
        try
        {
            var progress = new Progress<string>(line => SetStatus(line, error: false));
            var version = await Task.Run(() => PortableRuntimeInstaller.Install(
                _executableDirectory,
                line => ((IProgress<string>)progress).Report(line),
                out var downloadedBytes));

            SetStatus(
                $"便携运行时已就绪（{version}）并已校验；即将启动主程序。" + Environment.NewLine
                + "目录：" + RuntimeLocator.PortableRuntimeDirectory(_executableDirectory),
                error: false);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Net.WebException)
        {
            SetStatus("下载便携运行时失败：" + ex.Message + "（可改用官方下载页或 winget 命令）", error: true);
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _installPortable.Enabled = !busy;
        _retry.Enabled = !busy;
        _downloadPage.Enabled = !busy;
        _close.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.ForeColor = error ? Color.Firebrick : Color.DarkGreen;
    }

    private static string DescribeProbe(RuntimeLocation probe)
    {
        if (probe.Found)
        {
            return $"已检测到 {probe.Version}（来源：{DescribeSource(probe.Source)}）";
        }

        return string.IsNullOrWhiteSpace(probe.Detail)
            ? "未检测到 Microsoft.WindowsDesktop.App 8.x（系统级 / 便携 / 用户级都没有）"
            : probe.Detail!;
    }

    private static string DescribeSource(string source) => source switch
    {
        "portable" => "便携运行时",
        "user" => "用户级安装（%USERPROFILE%\\.dotnet）",
        "system" => "系统安装",
        _ => source
    };
}
