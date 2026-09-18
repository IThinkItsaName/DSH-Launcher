# DSH Launcher · 本地增强分支

> 基于上游 **[121103qwq/DSH-Launcher](https://github.com/121103qwq/DSH-Launcher) v1.0.7**（`846c44c1`）的本地开发分支。
> 在保留上游全部功能的前提下，把启动器做成一个**可用的实例管理器**：多实例 / 多版本、插件与市场治理、
> 崩溃恢复与安全模式、运行状况与诊断、整合包导入导出。

| 想了解 | 看这里 |
|---|---|
| 每个变更集改了什么 | [`docs/CHANGESETS.md`](docs/CHANGESETS.md)（**159 条**） |
| 用起来和上游有什么不同 | [`docs/BEHAVIOR-CHANGES.md`](docs/BEHAVIOR-CHANGES.md)（**101 条**） |
| 怎么验证、发布前要跑什么 | [`docs/VERIFICATION.md`](docs/VERIFICATION.md) |
| UI 规范（颜色 / 字号 / 圆角 / 按钮 / 图标） | [`docs/UI-DESIGN.md`](docs/UI-DESIGN.md) |

## 主要能力

**实例与版本**

- 多实例管理：导入（扫描本机 dsh 环境 / 源码目录 / 整合包）、重命名、删除；卡片级启动 / 停止 / 重启
- 更换实例运行版本（升级或降级）：预检 Node 引擎、插件核心 peerDeps、会话代际、方向 → 可选快照 → 强制重绑定 → 失败回滚
- 版本控制：准备运行环境（官方 npm 源 / 国内镜像，可设为默认）、新建版本、切换历史与一键回退、DSh 新版本提示
- 会话格式跟随 dsh 版本：v0–v3 读取、导入命名与导出保真；旧版实例拒绝新格式会话并给出明确错误

**启动、守护与恢复**

- 进程内实例守护（5s 探测循环）：识别市场「重启以生效」并接管、孤儿进程检测、空闲自动停止、残留进程清理
- 四层启动健康证据 + 进程提前退出归因；启动失败自动进入安全模式，并在下次正常启动后自动清理
- 崩溃恢复：崩溃策略 / 现场保留 / 冷却期、崩溃原因本地归类、逐插件定位（bisect）
- 诊断：`--diagnose` 导出脱敏诊断包、日志中心（一键清理）、实例生命周期结构化日志

**插件与市场**

- 插件市场（GitHub 社区目录 + 可选中文源）、Skill 市场、Agent Preset、MCP 管理
- 插件 × 实例矩阵（只读对比同一插件在不同实例的安装 / 启用状态）、依赖自检（doctor）
- 安装前校验：确认目标是 DSH 插件（`dsh.bundle.patch` + 可加载入口）、核心依赖兼容性预检
- 安装韧性：pnpm 失败翻译成可操作原因；失败自动回档并附完整诊断报告
- 整合包（`.dspack`）导入 / 导出：文件清单与体积预览、联网下载需显式同意、强制 sha256 + 大小双校验

**界面**

- 统一 UI 规范：语义色 / 圆角 / 间距 / 图标全部走令牌与矢量图标注册表；字号阶梯与键盘可达性有门禁
- 托盘常驻（关闭到托盘或整体退出）、窗口位置尺寸记忆、多屏越界自动回中
- 路径一键复制（悬停看全文）、列表虚拟化 + 滚动位置记忆、提示条按文字长度自适应消失

**安全与隐私**

- 安全体检：凭据、权限档位、审批、遥测、危险配置；诊断物一律脱敏
- 实例环境变量的敏感值落盘用 DPAPI 加密；存储清理只回收可再生缓存（实例数据与会话**只统计、不清理**）

## 构建与运行

前置：**.NET 8 SDK**（本机为全局 `C:\Program Files\dotnet` 8.0.425，**不需要** `DOTNET_ROOT`）。

```powershell
cd src\DshLauncher

# 构建（以 0 警告 0 错误为准）
dotnet build -c Release

# 仓库自测：纯逻辑、不联网、不开窗口、不碰真实数据（发布前必跑）
dotnet run --project ..\..\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Release

# 发布（单文件自包含 → 仓库 dist\）
dotnet publish DshLauncher.csproj -c Release -o ..\..\dist
```

> - ⚠️ **发布前先停掉运行中的 DSH Launcher**，否则 `dist` 里的 exe 被锁 → `MSB4018`。
> - ⚠️ **改动 csproj 资源清单（`<Resource Include>`）后必须 `rm -rf bin/obj` 全量重建**，否则增量构建的 BAML 资源清单不刷新（曾致主窗口 XAML 解析崩溃）。
> - 开发期不要直接 `dotnet "DSH Launcher.dll"`（会带 conhost 黑窗口，关掉黑窗口 = 杀进程）；正式使用一律 `dist\DSH Launcher.exe`。

## 仓库结构

```
dsh-launcher-dev/
├─ src/DshLauncher/                  # C# 源码（WPF + WebView2，.NET 8，自包含单文件）
├─ tests/DshLauncher.SelfTest/       # 仓库自测（纯逻辑、临时目录、不联网）
├─ docs/                             # 变更集清单 / 行为变化 / 验证 / UI 规范 / 契约清单 …
├─ .github/workflows/ci.yml          # CI：构建 + 自测
├─ dist/DSH Launcher.exe             # 发布产物（.gitignore 排除，不随仓库提交）
└─ README.md
```

## 文档索引

| 文档 | 内容 |
|---|---|
| [docs/CHANGESETS.md](docs/CHANGESETS.md) | **159 条**变更集清单（相对上游 v1.0.7）——改了哪些文件、改了什么 |
| [docs/BEHAVIOR-CHANGES.md](docs/BEHAVIOR-CHANGES.md) | **101 条**用户可见行为差异 |
| [docs/VERIFICATION.md](docs/VERIFICATION.md) | 三层验证（构建 / 仓库自测 / 端到端 harness）与发布前清单 |
| [docs/UI-DESIGN.md](docs/UI-DESIGN.md) | UI 规范：颜色令牌、字号阶梯、圆角、按钮分级、图标注册表 |
| [docs/DSH_CONTRACT_INVENTORY.md](docs/DSH_CONTRACT_INVENTORY.md) | 与上游 dsh 的契约清单（会话格式 / 文件名 / CLI / 运行时布局…）及哨兵 |
| [docs/ARCHITECTURE_DECISIONS.md](docs/ARCHITECTURE_DECISIONS.md) | 架构决策记录 |
| [docs/BEHAVIOR_MATRIX.md](docs/BEHAVIOR_MATRIX.md) | 行为矩阵 |
| [docs/UI-UNIFICATION-TODO.md](docs/UI-UNIFICATION-TODO.md) | UI 统一待办（挂起中） |
| [docs/THIRD-PARTY-NOTICES.md](docs/THIRD-PARTY-NOTICES.md) | 第三方组件与许可（Tabler Icons 等） |

## 实现方式说明

本分支相对上游的全部改动（C# 源码、XAML、验证脚本与文档）由 **DeepSeek v4 Flash 系列模型**完成；
每个改动都按「构建 0 警告 0 错误 → 仓库自测 → 端到端验证（含 UI 冒烟与截图）→ 人工验收」的流程提交。

## 上游同步

- 上游仓库：[`121103qwq/DSH-Launcher`](https://github.com/121103qwq/DSH-Launcher)；本分支基线 **v1.0.7**（`846c44c1`）。
- 上游发布新版本时，对照上游 diff，用 [`docs/CHANGESETS.md`](docs/CHANGESETS.md) 当**移植清单**逐项核对。

## 实例守护（进程内）

`dist/DSH Launcher.exe` 是**单文件**：启动器与实例守护在**同一个进程**内。

- **架构**：监控循环 = Launcher 内 5s 定时任务（后台 Task），无子进程、无管道、无额外 exe
- **探测**：识别市场重启（登记 PID 死 + 端口活 + 身份校验）→ 触发接管重启
- **停止判定**：15s 宽限（覆盖 market 重启窗口）+ 5 分钟重生观察窗
- **崩溃兜底**：Launcher 异常退出后的残留实例，由下次启动的台账恢复 + 孤儿检测提示
- **日志**：`%LocalAppData%\DeepSeek\launcher\watchdog.log`（1MB 轮转）；台账 `watchdog-state.json`

## 已知问题与待办

- `MainWindow.OnClosing` 在窗口关闭期间偶发 `InvalidOperationException`（疑为关闭期间仍有异步回调操作窗口可见性），待复现定位。
- 版本下载源切「国内镜像」的真实下载尚未在本机验证（本机已有相关版本，不触发下载）。
- 150% / 200% DPI 未逐屏验收（当前以 125% 为基准）。
- UI 统一待办见 [docs/UI-UNIFICATION-TODO.md](docs/UI-UNIFICATION-TODO.md)（主题色自定义、FAB、壁纸模式等）。
