using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DshLauncher.Bootstrapper;

/// <summary>
/// 自检引导器（变更集 165）：主程序是框架依赖小包，需要 .NET 8 Desktop Runtime。
/// 缺运行时时主程序无法自检（起不来），所以提醒必须由本程序提供。
/// 目标框架是 .NET Framework 4.8 —— Windows 10 1903+ / Windows 11 自带，本体只有几十 KB。
/// </summary>
internal static class Program
{
    /// <summary>需要的运行时主版本（.NET 8 的 x64 Desktop Runtime）。</summary>
    private const string RequiredMajorVersion = "8";

    /// <summary>同目录里的主程序文件名（发布形态：引导器 DSH Launcher.exe + 主程序 DSH Launcher.App.exe）。</summary>
    private const string AppFileName = "DSH Launcher.App.exe";

    private const string DownloadUrl = "https://dotnet.microsoft.com/download/dotnet/8.0";
    private const string WingetCommand = "winget install Microsoft.DotNet.DesktopRuntime.8";
    private const string SharedFrameworkKeyPath =
        @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App";

    [STAThread]
    private static int Main(string[] args)
    {
        var checkOnly = HasFlag(args, "--check-only");
        var simulateMissing = HasFlag(args, "--simulate-missing");
        var runtimeVersion = simulateMissing ? null : DetectDesktopRuntime();

        if (checkOnly)
        {
            // 供自动化核对用：把检测结果打到 stdout（WinExe 在重定向时同样可写）。
            Console.WriteLine("desktop-runtime=" + (runtimeVersion ?? "missing"));
            Console.WriteLine("main-app=" + (File.Exists(ResolveAppPath()) ? "found" : "missing"));
            return runtimeVersion is null ? 3 : 0;
        }

        if (runtimeVersion is not null)
        {
            return LaunchMainApp(args);
        }

        using (var dialog = new MissingRuntimeDialog(runtimeVersion))
        {
            var result = dialog.ShowDialog();
            // 用户在对话框里装好后点了「重试」：主程序已由对话框拉起。
            return result == DialogResult.OK ? 0 : 3;
        }
    }

    /// <summary>对话框「我已安装，重试」用：重新检测一次。</summary>
    internal static string DetectDesktopRuntimeForRetry() => DetectDesktopRuntime();

    private static bool HasFlag(string[] args, string flag) =>
        args is not null && args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 检测 x64 的 Microsoft.WindowsDesktop.App 8.x：
    /// ① 注册表（dotnet 安装器写的清单，权威）；② %ProgramFiles%\dotnet\shared 下的目录（兜底）。
    /// </summary>
    private static string DetectDesktopRuntime()
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

        try
        {
            var sharedRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet", "shared", "Microsoft.WindowsDesktop.App");
            if (Directory.Exists(sharedRoot))
            {
                var match = Directory.GetDirectories(sharedRoot)
                    .Select(Path.GetFileName)
                    .Where(name => name is not null && name.StartsWith(RequiredMajorVersion + ".", StringComparison.Ordinal))
                    .OrderByDescending(name => ParseVersion(name))
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(match))
                {
                    return match;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static Version ParseVersion(string value) =>
        Version.TryParse(value, out var parsed) ? parsed : new Version(0, 0);

    private static string ResolveAppPath() =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppFileName);

    /// <summary>拉起主程序（把命令行原样透传；不等待，本身立刻退出）。</summary>
    private static int LaunchMainApp(string[] args)
    {
        var appPath = ResolveAppPath();
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
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = true
            };
            if (args is not null && args.Length > 0)
            {
                // UseShellExecute + 手工拼参数：逐项加引号（路径可能带空格）。
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
}

/// <summary>
/// 缺运行时的中文对话框：给出官方下载页与 winget 命令，并允许装完后原地重试。
/// </summary>
internal sealed class MissingRuntimeDialog : Form
{
    private readonly Label _status;
    private readonly TextBox _command;
    private readonly Button _retry;

    internal MissingRuntimeDialog(string detectedVersion)
    {
        Text = "DSH Launcher 需要 .NET 8 桌面运行时";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(580, 300);
        Font = new Font("Microsoft YaHei UI", 9F);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        var title = new Label
        {
            Text = "缺少 .NET 8 Desktop Runtime（x64）",
            Font = new Font(Font.FontFamily, 12F, FontStyle.Bold),
            Location = new Point(20, 18),
            Size = new Size(540, 30)
        };

        var body = new Label
        {
            Text = "启动器的主程序是框架依赖包（约 3.6 MB），需要机器上装有 .NET 8 Desktop Runtime (x64)。"
                 + Environment.NewLine
                 + "任选一种方式安装，回来后点「我已安装，重试」即可。",
            Location = new Point(22, 56),
            Size = new Size(536, 56)
        };

        _status = new Label
        {
            Text = "检测结果：" + (string.IsNullOrWhiteSpace(detectedVersion)
                ? "未检测到 Microsoft.WindowsDesktop.App 8.x"
                : "已检测到 " + detectedVersion),
            ForeColor = string.IsNullOrWhiteSpace(detectedVersion) ? Color.Firebrick : Color.DarkGreen,
            Location = new Point(22, 116),
            Size = new Size(536, 22)
        };

        var commandLabel = new Label
        {
            Text = "方式二：命令行（复制后在「终端 / PowerShell」里执行）",
            Location = new Point(22, 146),
            Size = new Size(536, 22)
        };

        _command = new TextBox
        {
            Text = "winget install Microsoft.DotNet.DesktopRuntime.8",
            ReadOnly = true,
            Location = new Point(22, 170),
            Size = new Size(420, 26),
            BackColor = Color.White
        };

        var copyButton = new Button
        {
            Text = "复制命令",
            Location = new Point(452, 169),
            Size = new Size(106, 28)
        };
        copyButton.Click += (_, __) =>
        {
            try
            {
                Clipboard.SetText(_command.Text);
                _status.Text = "已复制 winget 命令，可在终端里执行。";
                _status.ForeColor = Color.DarkGreen;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
            {
                _status.Text = "复制失败，请手动选中命令复制。";
                _status.ForeColor = Color.Firebrick;
            }
        };

        var downloadButton = new Button
        {
            Text = "打开官方下载页",
            Location = new Point(22, 216),
            Size = new Size(150, 34)
        };
        downloadButton.Click += (_, __) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://dotnet.microsoft.com/download/dotnet/8.0",
                    UseShellExecute = true
                });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _status.Text = "无法打开浏览器，请手动访问：https://dotnet.microsoft.com/download/dotnet/8.0";
                _status.ForeColor = Color.Firebrick;
            }
        };

        _retry = new Button
        {
            Text = "我已安装，重试",
            Location = new Point(182, 216),
            Size = new Size(150, 34)
        };
        _retry.Click += (_, __) =>
        {
            var version = Program.DetectDesktopRuntimeForRetry();
            if (version is null)
            {
                _status.Text = "仍未检测到 .NET 8 Desktop Runtime（x64），请确认安装完成。";
                _status.ForeColor = Color.Firebrick;
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        };

        var closeButton = new Button
        {
            Text = "关闭",
            Location = new Point(342, 216),
            Size = new Size(110, 34)
        };
        closeButton.Click += (_, __) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        Controls.AddRange(new Control[]
        {
            title, body, _status, commandLabel, _command, copyButton, downloadButton, _retry, closeButton
        });
        AcceptButton = _retry;
        CancelButton = closeButton;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);

        // 用户在对话框里装好运行时后点了「重试」⇒ 直接接着拉起主程序，不让用户再点一次。
        if (DialogResult == DialogResult.OK)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DSH Launcher.App.exe"),
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = true
                });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                MessageBox.Show("启动主程序失败：" + ex.Message, "DSH Launcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
