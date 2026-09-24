using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Minimal helper that rebinds a stale Installed instance to a freshly
/// detected DSh runtime without creating a new instance or touching a Source
/// instance. Pure logic so the behavior can be regression-tested.
/// </summary>
public static class InstanceRuntimeRebinder
{
    public static ManagerInstance? RebindInstalledInstance(ManagerInstance instance, DshRuntimeInfo detected)
        => RebindInstalledInstance(instance, detected, force: false);

    /// <summary>
    /// 把 Installed 实例重绑定到指定运行时。<paramref name="force"/> = false 时只在当前绑定已
    /// 失效时重绑（历史行为，用于“修复”）；true 时用于显式“更换运行版本”（升级/降级）。
    /// </summary>
    public static ManagerInstance? RebindInstalledInstance(
        ManagerInstance instance,
        DshRuntimeInfo detected,
        bool force)
    {
        if (DescribeBlocker(instance, detected) is not null)
        {
            return null;
        }

        if (!force)
        {
            var rootValid = DshRuntimeDetector.TryResolvePackageRoot(instance.RootPath) is not null;
            var exeValid = DshRuntimeCommandFactory.IsUsable(instance.EffectiveDshLaunchSpec);
            if (rootValid && exeValid)
            {
                return null;
            }
        }

        return instance with
        {
            RootPath = detected.PackageRoot!,   // DescribeBlocker 已排除空值
            DshExecutablePath = detected.ExecutablePath,
            DshLaunchSpec = detected.EffectiveLaunchSpec,
            DetectedVersion = detected.Version,
            PackageManager = "npm",
            RuntimeStatus = InstanceRuntimeStatus.Ready,
            RuntimeOwnership = InstanceRuntimeOwnership.None,
            LastError = null,
            ProcessId = null,
            Port = null,
            WebUrl = null,
            AuthenticatedWebUrl = null
        };
    }

    /// <summary>
    /// 重绑前的守卫清单。返回 null 表示可以重绑，否则返回**具体哪一条**不过（变更集 175）：
    /// 调用方原先只拿到一个 null，只能报“目标运行时不完整”，排查时无法区分
    /// “实例状态仍是运行中”和“启动入口文件不在了”。两条判定共用这一处，不再各写一份。
    /// </summary>
    internal static string? DescribeBlocker(ManagerInstance instance, DshRuntimeInfo detected)
    {
        if (instance.Kind != InstanceKind.Installed)
        {
            return "只有 Installed 实例可以更换运行版本。";
        }

        if (instance.RuntimeStatus == InstanceRuntimeStatus.Running)
        {
            return "实例对象的状态仍是「运行中」（Launcher 侧的运行判断却认为已停止，属状态不同步）："
                + "请先停止（或重启 Launcher 重算状态）后再换版本。";
        }

        if (instance.RuntimeOwnership == InstanceRuntimeOwnership.Attached)
        {
            return "实例当前是外部连接（Attached），只读。";
        }

        if (!detected.IsAvailable)
        {
            return "目标运行环境不可用。";
        }

        if (string.IsNullOrWhiteSpace(detected.PackageRoot))
        {
            return "目标运行环境没有包目录（PackageRoot 为空）。";
        }

        var spec = detected.EffectiveLaunchSpec;
        if (!DshRuntimeCommandFactory.IsUsable(spec))
        {
            return $"目标启动入口不可用：{spec?.HostPath ?? "(未提供入口)"}";
        }

        return null;
    }
}
