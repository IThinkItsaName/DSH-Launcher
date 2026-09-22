# 与上游的差异盘点（供评审）

- 日期：**2026-09-23**（修订版；原始 09-13 版随已关闭的 PR #15 一起作废，见文末"本 PR 的来历"）
- 对象：**本 PR**（分支 `launcher-extended-v2`）——基于上游 **v1.0.7**（`846c44c1`），**225 个提交 / 166 文件 / +39,340 / −7,084**，**不含**上游 v1.0.8 ~ v1.2.4 的提交
- 上游现状：`main` = `651f683`（**v1.2.4**，2026-09-15）
- 文中"本分支"指上述分支，"上游"指 `121103qwq/DSH-Launcher` 的 `main`
- 所有数字为 git 实测，复现命令见文末。⚠ **本报告与 `AI-PRODUCT-NOTICE.md` 自身也在本分支新增的文件里**（2 文件），所以 PR 页合计会比 §1 的"代码与其它文档"多 2 个文件与对应行数；精确合计以附录命令为准。
- ⚠️ **来源声明**：本分支的代码与文档**全部由 `DeepSeek Flash 系列模型` 生成**（详见 [`AI-PRODUCT-NOTICE.md`](../AI-PRODUCT-NOTICE.md)）。

## 0. 先看三件事

1. **两条线已分叉**：本分支相对 v1.0.7 是 **166 文件 / +39,340 / −7,084**（新增 113 文件 / 删除 1 文件）；上游自己从 v1.0.7 到 `main` 走了 **148 文件 / +32,596 / −1,291**。**46 个文件双方都改过** —— 这是冲突面的来源，也是 GitHub 显示 *can't automatically merge* 的原因。
2. **硬前提**：本分支基于 v1.0.7，**没有** v1.0.8 ~ v1.2.4 对既有文件的改动。09-13 曾在 v1.1.2 上做过一次三方合并演练：冲突 **35 文件 / 108 块**，最重的是 `MainWindow.xaml.cs`、`DshInstanceRunner.cs`、`MarketplaceService.cs`。**v1.2.x 之后未重新演练**（见 §6）。
3. **刻意保留上游文件，避免无关 diff**：本分支把上游的 13 个文件保持为 v1.0.7 原样 —— `AGENTS.md`、`CLAUDE.md`、`CURRENT_DESIGN.md`、`DEV_STATE.md`、`docs/images/*.png`（7 张）、`VersionOpenTargetService.cs` —— 因为 PR 的 diff 是相对 **v1.0.7（merge base）** 计算的，改动这些文件只会在评审页制造噪音。唯一删除是上游曾提交进仓库的构建产物 `DSH Launcher/DSH Launcher.exe`（约 5.7 MB，建议改为 Release 附件）。

## 1. 体量对比（相对 v1.0.7）

| 领域 | 本分支 | 上游（v1.0.7 → `main`） |
|---|---|---|
| `src/DshLauncher/Services/`（业务逻辑） | 90 文件，+19,429 / −450 | 45 文件，+11,759 / −516 |
| `src/DshLauncher/` 窗口与入口（XAML / 代码后置 / 入口） | 34 文件，+12,727 / −1,908 | 35 文件，+11,128 / −692 |
| `src/DshLauncher/` 其它子目录（Controls / Watchdog / Properties / Assets） | 11 文件，+2,192 / −0 | 2 文件，+114 / −0 |
| `src/DshLauncher/Models/` | 14 文件，+858 / −17 | 19 文件，+1,051 / −7 |
| `tests/` | 2 文件，+2,632 / −4,614 | 38 文件，+7,992 / −44 |
| `docs/` | 10 文件，+1,327 / −0 | 0 |
| 根级与仓库其它（README / .gitignore / CI / 通知文件 / exe 删除） | 5 文件，+158 / −95 | 9 文件，+552 / −32 |
| **合计** | **166 文件（新增 113 / 删除 1），+39,340 / −7,084**（含本报告与 AI 声明 2 文件） | **148 文件，+32,596 / −1,291** |

> `tests/` 的删除行数偏大，是因为本分支把上游 v1.0.7 的 `tests/DshLauncher.SelfTest/Program.cs`（4,694 行）整体替换成了自研自测（实测 **194 PASS / 0 FAIL**）。如果希望保留上游测试，我可以改成在上游文件基础上追加。

## 1b. 09-13 那一轮：UI 统一（变更集 127–153）

- **127–139（一个主题一个变更集）**：颜色令牌化（38 处硬编码色 → `App.xaml` 令牌）、字号阶梯（11px 入阶梯 + 归并 6 处越界）、小字字重收窄、图标体系（字体字形 → **Tabler Icons v3.46.0**（MIT）矢量注册表，标题栏字形按**墨迹实测**定尺寸）、菜单统一、表格与侧栏自适应（列宽改代码分配 + 16 个长列表显式虚拟化）、卡片/圆角/按钮三级、空/加载/错误三态分色、窗口与页面框架统一、键盘可达性（15 个无文字按钮补可访问名）、高 DPI 像素对齐（13 个根元素）
- **140–146**：三轮真机验收修复（菜单 `Header` 语义、危险按钮悬停、"内联 `Foreground` 压过样式触发器"、市场源删除按钮、Skill 市场卡片统一与横向溢出）+ 提示条自适应消失 + 滚动位置记忆 + 三态分色共享实现
- **147–153**：图标注册表收口、日志中心（含一键清理）、存储清理卡死修复（独立只读探针量化后再改）、按钮列对齐、单页快照工具
- **行尾治理**：旧尖端里 `ExtensionWindow.xaml.cs` 整份是 CRLF（基线是 LF），在 diff 中表现为 +2,385 / −1,766 的"整文件重写"假象；本轮连同 `MarketplaceService.cs`、`DshLauncher.csproj` 一并统一为 LF，**共消除 2,407 行行尾噪音**（+39,819 / −8,982 → +37,952 / −7,115）

## 1c. 2026-09-23 追加：冻结之后的 9 个提交（变更集 154–160 + 文档）

前一轮在 09-13 冻结（变更集 153）。之后继续做了这些，本次一并带来：

| 变更集 | 内容 | 备注 |
|---|---|---|
| 154 | 实例自动注册不再把「安装目录自身」登记成实例 | 修"幽灵实例"复现路径，自测 `autoreg/*` 4 项 |
| 155 | **版本下载源提成设置项** + 两处弹窗可覆盖 | 官方源 / npmmirror；自测 `download-source/*` 6 项；已真机验证走镜像（npm 日志 610 次 `GET 200` 全指 npmmirror、官方源 0 次） |
| 156 | 插件市场**同名不同包不再被合并**成一张卡 | 根因：条目合并把显示名当包身份（社区目录实测 170 组同名）；改为只用强身份合并；自测 `market-merge/*` 4 项 |
| 157 | 目录下载自动解压（gzip/deflate/br）+ 刷新超时 90→180s | 防 3.9 MB 明文目录拉取超时；自测 `market-http/*` 2 项 |
| 158–159 | 插件页排版两轮（紧凑按钮不继承 `MinHeight`、依赖自检迁到实例设置、卡片去横向滚动） | 真机验收驱动 |
| 160 | **MCP 注入写进 dsh patch 的 `insert:` 列表** | 裸 id 条目会被 dsh 当"改已存在条目"而**静默跳过**；自测 `mcp-patch/*` 2 项 + 已用留痕 MCP server 做正负对照实测 |
| — | README 拆分（变更集表 → `docs/CHANGESETS.md`，行为差异 → `docs/BEHAVIOR-CHANGES.md`） | |
| — | 契约文档更新到上游 `0d1f50007f` + 装机 0.1.5-rc.2 | `docs/DSH_CONTRACT_INVENTORY.md` |

**本轮的验证**：构建 **0 警告 / 0 错误**；仓库自测 **194 PASS / 0 FAIL**；工作区端到端 harness **379 PASS / 1 FAIL**（唯一 FAIL 是既有的"上游会话格式版本超出已核对范围"哨兵，属上游 0.1.7-alpha.2 把会话格式提到 v4、而我们尚未跟进，**与本次改动无关**，见 §6）。

## 2. 冲突面明细（46 个双方都改过的文件，按本分支改动量排序）

| 文件 | 本分支 | 上游 v1.0.7 → `main` |
|---|---|---|
| `src/DshLauncher/MainWindow.xaml.cs` | +5,084 / −926 | +2,396 / −254 |
| `tests/DshLauncher.SelfTest/Program.cs` | +2,619 / −4,612 | +1,298 / −44 |
| `src/DshLauncher/VersionSettingsWindow.xaml.cs` | +1,019 / −216 | +222 / −36 |
| `src/DshLauncher/ExtensionWindow.xaml.cs` | +745 / −139 | +974 / −136 |
| `src/DshLauncher/Services/DshInstanceRunner.cs` | +656 / −54 | +349 / −55 |
| `src/DshLauncher/Services/MarketplaceService.cs` | +587 / −118 | +426 / −38 |
| `src/DshLauncher/VersionControlWindow.xaml.cs` | +493 / −69 | +302 / −21 |
| `src/DshLauncher/App.xaml` | +493 / −34 | +237 / −2 |
| `src/DshLauncher/MainWindow.xaml` | +466 / −149 | +87 / −36 |
| `src/DshLauncher/VersionSettingsWindow.xaml` | +456 / −120 | +117 / −38 |
| `src/DshLauncher/Services/LauncherTaskService.cs` | +395 / −0 | +726 / −0 |
| `src/DshLauncher/Services/ConversationService.cs` | +375 / −16 | +497 / −146 |
| `src/DshLauncher/ExtensionWindow.xaml` | +267 / −131 | +97 / −28 |
| `src/DshLauncher/Services/DshCredentialStoreNormalizer.cs` | +263 / −0 | +90 / −0 |
| `src/DshLauncher/Services/SkillMarketService.cs` | +261 / −78 | +280 / −67 |
| `src/DshLauncher/Services/ExtensionService.cs` | +235 / −44 | +142 / −28 |
| `src/DshLauncher/Services/DshProfileService.cs` | +209 / −0 | +134 / −0 |
| `src/DshLauncher/VersionControlWindow.xaml` | +203 / −45 | +48 / −23 |
| `src/DshLauncher/App.xaml.cs` | +194 / −10 | +85 / −11 |
| `src/DshLauncher/ConversationWindow.xaml.cs` | +183 / −10 | +980 / −73 |
| `src/DshLauncher/Services/VersionSettingsService.cs` | +163 / −6 | +77 / −4 |
| `src/DshLauncher/Models/VersionSettingsModels.cs` | +162 / −5 | +125 / −0 |

**读法**：`MarketplaceService.cs`、`ExtensionWindow.xaml.cs`、`MainWindow.xaml.cs` 这类双方都大改同一文件，逐块合并成本最高；`LauncherTaskService.cs`（双方都是纯新增）这类反而是可拼的。其余 24 个文件的完整清单可用文末命令重放。

## 3. 功能对照（上游有没有对应的东西）

**上游按文件名确实没有、本分支独有的（13 项，2026-09-23 复核仍全部为"上游无此文件"）**：
`SessionFileNames.cs`（会话格式代际）、`CrashCauseClassifier.cs`（崩溃归因）、`InstanceUiPluginScanner.cs`（UI 插件检测）、`PresentationSurfaceService.cs`（呈现面）、`LaunchModePolicy.cs`（启动方式策略）、`TerminalLaunchService.cs`（终端启动/命令）、`SafeProfileService.cs`（隔离 profile）、`SkillMarketQuery.cs`（技能市场筛选排序）、`LogCenterService.cs`（日志中心）、`MarketSourceSettingsService.cs`（市场来源配置）、`AppDialog.cs`（自绘对话框）、`LogCenterWindow.xaml`、`EnvironmentScanWindow.xaml`（环境扫描导入）

> ⚠ **但"没有同名文件"≠"上游没有这个能力"**：实测上游 `main` 已有自己的 **`SessionFormatHelper.cs`**（会话格式）、**`.dspack` 整合包支持（10 个文件命中）与整合包能力（17 个文件命中）**。因此**会话格式代际**与**整合包**这两块属于**与上游的重复实现**，贡献方式应改为**对接上游现有实现**，不建议整块移植（见 §5 方式①）。

**上游有同名文件、但实现与程度不同（抽样）**：`ExtensionService.cs`、`MarketplaceService.cs`、`SkillMarketService.cs`、`ConversationSyncService.cs`、`DshInstanceRunner.cs`、`VersionControlWindow.xaml`

## 4. 上游自己这条线（v1.0.7 → v1.2.4）

| 版本 | 日期 | 内容 |
|---|---|---|
| v1.0.8 | 08-23 | global provider and model management |
| v1.0.9 | 08-25 | profile switching and download center |
| v1.0.10 | 08-26 | harden runtime state + Desktop installer |
| v1.0.11 | 09-04 | harden runtime recovery and package fidelity |
| v1.1.0 | 09-05 | launcher management suite |
| v1.1.1 | 09-05 | harden snapshots, GitHub requests and theme state |
| v1.1.2 | 09-05 | restore self-contained runtime packs in Windows CI |
| v1.2.0 | 09-13 | optional visual effects and align launcher navigation |
| v1.2.1 | 09-13 | polish rounded controls and hover feedback; slim Windows release |
| **v1.2.2** | 09-13 | **session formats、modpack market、MIT license** |
| v1.2.3 | 09-15 | existing HOME management and optimized default visuals |
| v1.2.4 | 09-15 | wait for desktop test helper readiness before publishing |

方向上有重叠（实例与运行时管理、profile 切换、provider / 模型管理、下载中心、**会话格式**、**整合包与市场**），只是各写各的 —— 这解释了冲突为何集中在 `MainWindow.xaml.cs` / `DshInstanceRunner.cs` / `LauncherTaskService.cs` / `DshProfileService.cs` 这些文件上。

## 5. 我们能配合的三种方式

| 方式 | 我们要做的 | 说明 |
|---|---|---|
| ① 你指定一块，我移植到 `main` | 在 v1.2.4 基线上重做该块，并用**你的**自测验证 | 建议优先选**你还没有**的块（演示面/终端启动/崩溃归因/日志中心/环境扫描）；**会话格式**与**整合包**请让我对接你已有的 `SessionFormatHelper.cs` 与 `.dspack` 实现，而不是搬我的 |
| ② 我整体 rebase 到 `main` | 解 §2 的冲突（09-13 在 v1.1.2 上实测 108 块，v1.2.x 需重测），解完重跑双方自测 | 需要你给取舍标准（哪些文件以哪边为准） |
| ③ 只当参考实现 | 不做改动 | 需要哪块告诉我，我再单独整理 |

没有"必须接受"的意思；都不合适也可以直接说。

## 6. 诚实边界

- 本报告只做**体量、结构与文件级**盘点；每块代码的移植成本需要具体尝试才能确定。例如 `CrashCauseClassifier` 依赖本分支自己的 `StartupEvidence` / `InstanceLogLine` 模型，移植要连模型一起搬。
- §3 的"上游没有"只对**同名文件**成立（已按 §3 的警告修正结论：会话格式与整合包两块上游有**不同名的等价实现**）。
- **未做** v1.2.4 的三方合并演练；§2 的 46 文件是**静态统计**（按 v1.0.7 为基线），不等于实测冲突块数。
- harness 现有 **1 项 FAIL**：`contract: 会话格式版本未超出已核对范围（C1）`（上游 0.1.7-alpha.2 把 `SESSION_FORMAT_VERSION` 提到 4，本启动器哨兵仍为 3）。这是**已知未跟进项**，不影响 v1.0.7 ~ v1.2.x 的运行时兼容结论，但应在合入前对齐。
- 本分支把上游 v1.0.7 的自测文件整体替换为自研自测（见 §1 注）；这不是无意删除，但确实改变了该文件的形态。

## 附：本 PR 的来历（避免误会）

- 上一轮同类 PR 是 **#15**（分支 `launcher-extended`，163 文件 / +38,609 / −7,065），**#18** 是其中 5 处最小修复的单独 PR。
- 这两个 PR 都是**我（`IThinkItsaName`）自己关闭的**——起因是我在 2026-09-22 删除了自己的 fork 仓库（GitHub 记录显示 `closed_at` 同一秒、actor 为我本人）。**不是你拒绝的**，也没有任何你的回复被丢失。
- 本次是把**冻结之后**的 9 个提交补上后重发。如果你完全不希望收到这类 PR，回一句就行，我不会再发。

## 附录：复现命令

```bash
# 本分支相对 v1.0.7（846c44c1 = v1.0.7，651f683 = main）
git diff --shortstat 846c44c1 launcher-extended-v2
git rev-list --count 846c44c1..launcher-extended-v2
git diff --diff-filter=A --name-only 846c44c1 launcher-extended-v2 | wc -l
git diff --diff-filter=D --name-only 846c44c1 launcher-extended-v2 | wc -l

# 上游自己这条线
git diff --shortstat 846c44c1 651f683

# 双方都改过的文件（冲突面）
comm -12 <(git diff --name-only 846c44c1 651f683 | sort) \
         <(git diff --name-only 846c44c1 launcher-extended-v2 | sort)

# 某个文件两边各改了多少
git diff --numstat 846c44c1 launcher-extended-v2 -- src/DshLauncher/MainWindow.xaml.cs
git diff --numstat 846c44c1 651f683            -- src/DshLauncher/MainWindow.xaml.cs

# 本分支的验证（都不联网）
dotnet build src/DshLauncher/DshLauncher.csproj -c Release     # 期望 0 警告 0 错误
dotnet run --project tests/DshLauncher.SelfTest/DshLauncher.SelfTest.csproj -c Release   # 期望 194 PASS / 0 FAIL
```
