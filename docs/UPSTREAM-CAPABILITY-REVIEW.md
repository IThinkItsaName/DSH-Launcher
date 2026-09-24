# 与上游的能力对照（评估报告）

> 状态：**只出评估，未改任何上游源码**（用户 2026-09-24 选定「只出评估报告」）。
> 相关：[DIFF-REPORT](DIFF-REPORT.md)（体量与冲突面）｜[FEATURE-INVENTORY](FEATURE-INVENTORY.md)（本分支功能台账）｜
> [DSH_CONTRACT_INVENTORY](DSH_CONTRACT_INVENTORY.md)（与 dsh 的契约）

## 0. 依据、方法与边界

| 项 | 值 |
|---|---|
| 上游对象 | `121103qwq/DSH-Launcher` **`main` = `651f6831b51fa72286497312108f09a84d6578a5`（v1.2.4，2026-09-15）** |
| 取物方式 | `git clone --depth 1 --branch main https://ghfast.top/https://github.com/121103qwq/DSH-Launcher.git`（直连 GitHub 失败：`fetch-pack: unexpected disconnect`；镜像 300 s 内成功） |
| 本地副本 | `_trials/upstream-dsh-launcher-20260924/`（工作区级）；**分析完成后已删除**（141 MB，避免工作区膨胀）——用上一行的命令可随时重建同一 commit |
| 本分支 | 以本仓当前 `main` 为准（`git log -1`），基线仍是上游 **v1.0.7** |
| 规模 | 上游 `src/DshLauncher`: **132** 个 `.cs`/`.xaml`（Services 60 / Models 30 / Controls 1）；本分支 **223** 个（Services 105 / Models 25 / Controls 3） |
| 方法 | ① 文件名级 diff（`comm` 双向）；② 逐条核对上游 `README.md`「主要功能」；③ 读差异文件的关键实现（只读，用于判断"是什么"） |
| **边界（重要）** | 本报告**不做**逐块移植成本评估、**不做**三方合并演练、**不评价**两边代码质量；"我们有/上游没有"只对**同名文件与 README 明述能力**成立 |

## 1. 结论摘要

1. **方向重叠度很高**：实例/版本管理、Plugin·Skill 市场、会话文件管理、任务中心、日志与诊断、实例资源监控、`.dshpack`/`.dspack`、Provider 同步——两边各写一套，**这是 46 个文件双方都改过的根本原因**（见 DIFF-REPORT §2）。
2. **上游多出 8 类能力**（§2）：华丽视觉、Launcher 自更新与回退、下载中心、全局 Provider 页、整合包市场 + ModPack v2、既有 DSH_HOME 关联、CLI/URL 协议/快捷方式、对话级模型继承；另有 3 项工程差异（正式单测工程、MIT 许可、自包含发布口径）。
3. **本分支多出 10 类能力**（§3）：进程内实例守护、崩溃恢复与归因、逐插件二分定位、安全模式四层启动证据、插件×实例矩阵、结构化日志中心、环境扫描导入、安全体检三件套、错误码目录、UI 令牌体系与门禁。
4. 因此**不建议整体 rebase**；建议按"能力块"选边（§5），优先把**上游确实更好/我们没有**的块对齐过来，并把**我们独有的块**做成可单独移植的补丁。

## 2. 上游有、本分支没有（8 类）

| # | 能力 | 上游落点（文件级） | 本分支现状 | 建议 |
|---|---|---|---|---|
| U1 | **华丽视觉**（毛玻璃 / 液态玻璃、流体背景、粒子、鼠标光晕、拖尾、点击波纹、层次视差；设置里可关；窗口隐藏时暂停） | `VisualEffectsController.cs`、`UiMotion.cs`、`UiHover.cs`、`MainWindow.Appearance.cs`、`Controls/VisualSurface.cs` | **完全没有**（UI-TODO §1.12 记为 P2 未做；用户 2026-09-24 对"主题色 5 选"说"先别搞"，但本项未单独表态） | 与 B2c 壁纸方案一起评估（两者都改 `MainWindow` 背景层，**宜一次设计**） |
| U2 | **Launcher 自更新与回退**（后台查 Release → 用户确认 → 只下载固定名 `DSH.Launcher.exe` → 校验大小/文件版本/SHA-256 → 免管理员替换；可回退历史稳定版） | `LauncherUpdateService.cs`、`Models/LauncherUpdateModels.cs` | **完全没有**（我们的"更新提示"只覆盖 **DSh** 版本，不覆盖启动器自身） | 优先级建议**高**：这是"发给别人用"的必要件；但需先定更新源（本仓无 Release 渠道，见 README「上游同步」） |
| U3 | **下载中心页**（顶栏"下载"：Launcher 更新 + 官方 DSh 版本选择集中一处） | `MainWindow.Header.cs` + U2/DSh 版本选择 | 版本选择分散在「新建干净版本」「更换运行版本」「设置→运行环境→版本下载源」三处 ★ | 与我们 S1 的"入口去重"同题；可考虑先做 S1 的收敛，再看是否值得单独页面 |
| U4 | **全局 Provider 页**（汇总全部 Coding 版本 + 运行中 DSh 的模型目录，不显示具体实例；统一设置新对话默认模型；打开期间每 15 s 读 `llm.providers` 状态） | `ProviderManagementWindow.xaml(.cs)`、`Services/CodingModelPolicyService.cs`、`Models/CodingModelModels.cs`、`Services/DshApiClient.cs`、`Services/ProviderDiagnosticService.cs` | **有意移除**：启动页 Provider 卡片与诊断已删（`CURRENT_DESIGN.md`），只留"跨版本同步" | **不建议**直接对齐（我们的口径是"启动器不读 Provider"，属产品取舍）；若要，需先改 `CURRENT_DESIGN.md` 口径 |
| U5 | **整合包市场**（搜 DSH-PackForge 社区市场 → 大小+SHA-256 校验 → 预览 → 安装到新版本；缺校验信息的条目不可安装） | `ModPackMarketView.xaml(.cs)`、`Services/ModPackMarketService.cs`、`Models/ModPackMarketModels.cs` | **只有本地导入/导出**（`.dshpack` / `.dspack` 读 v2/v3、写 v4） | 优先级**中**：市场依赖第三方站 + 校验信任链，需先确认来源与安全口径（本仓已有"目录即信任边界"的先例） |
| U6 | **DSH-PackForge ModPack v2 `.tgz` 与双向转换** | `Services/VersionPackageService.ModPack.cs`、`VersionPackageService.Dspack.cs` | 只读旧 `.tgz`（pack-structure v1），**不做转换** | 优先级**低**（等市场议题一起定） |
| U7 | **既有 DSH_HOME 关联**（直接"关联"其他桌面端已有 HOME + `ExternalDshHomeGuard` 防并发写） | `LinkExistingHomeWindow.xaml(.cs)`、`Services/ExistingDshHomeDiscoveryService.cs`、`ProcessDshHomeReader.cs`、`ExternalDshHomeGuard.cs`、`Models/ExistingDshHomeCandidate.cs` | 我们只有"**复制**导入"（`DshHomeImportService` + `DshEnvironmentScanner`），并**拒绝**共用同一 HOME | 优先级**中**：两套语义（关联 vs 复制）各有理由；真要支持"关联"必须先解决"同一 HOME 并发写"（上游用 Guard，我们用"拒绝"） |
| U8 | **CLI + URL 协议 + 桌面快捷方式**（`dsh-launcher://` 注册；`open/start/stop/restart/chat/version-settings/plugins/conversations` 转发给已运行实例） | `Services/LauncherCommandParser.cs`、`Models/LauncherCommand.cs`、`Services/LauncherIntegrationService.cs` | **没有**（我们只有 `--diagnose` 一个开关） | 优先级**中**：与我们的"单实例 + 命名管道唤醒"天然可拼；但需定"参数稳定性"承诺（对外接口） |
| U9 | **对话级模型继承 + `session.selectModel`**（单独对话 → DSh 真实工作目录 → 全局默认） | 上游 README「Provider、对话与同步」段 | 我们只编辑 `llm-*` 配置与同步，**没有** per-conversation 模型 | 优先级**低**（需 dsh 侧接口，且与"启动器不读 Provider"口径相冲） |
| U10 | **GitHub 条件缓存与配额**（ETag / Last-Modified；设置页显示剩余配额与限流恢复时间；可选 DPAPI 存 Token） | `Services/GitHubApiService.cs`、`GitHubCredentialService.cs`、`Models/GitHubApiModels.cs` | 市场是 **TTL 缓存 + 自动解压**（变更集 157），**无**条件请求、**无**配额显示、**无** Token | 优先级**中**：配额显示对"刷新目录失败"的排障价值高（我们已踩过限流/超时） |

> 另外两处"上游有、我们刻意没有"：**扩展页的 Profile 切换**（我们变更集 114 删掉入口，改为实例设置里自动指向 TUI profile）、**Chat 主题联动走 `ui-theme.preference` 探测**（我们改走 dsh-market loopback）。属设计取舍，不是缺能力。

## 3. 本分支有、上游没有（10 类）

| # | 能力 | 我们的落点 |
|---|---|---|
| O1 | 进程内实例守护（5 s 循环、幽灵实例转正、接管重启、残留清理、台账落盘） | `Watchdog/*`（6 文件） |
| O2 | 崩溃恢复策略 + 崩溃原因归类（规则表 + 主/次因 + 置信度） | `CrashRecoveryService`、`CrashCauseClassifier`、`Models/CrashCauseModels` |
| O3 | 逐插件二分定位 → 一键禁用并启动 | `PluginBisectService` + 隔离 profile |
| O4 | 安全模式（`.dsh-safe` Tier1/Tier2）+ 四层启动健康证据 + 证据落盘 | `SafeProfileService`、`StartupHealthEvidence`、`StartupEvidenceStore`、`HttpHealthMonitor` |
| O5 | 插件 × 实例矩阵（只读三态 + 复制 TSV） | `PluginMatrixWindow`、`PluginMatrixService`、`Models/PluginMatrixModels` |
| O6 | 结构化日志 + 日志中心（按天分组/级别/实例/关键字过滤/清理） | `LauncherLog`、`LogCenterService`、`LogCenterWindow` |
| O7 | 环境扫描导入（`%USERPROFILE%\.dsh*` + `DSH_HOME`，按 web/tui/other 分类） | `DshEnvironmentScanner`、`EnvironmentScanWindow` |
| O8 | 安全体检三件套（凭据只报位置 / 危险配置 / 社区探针时间轴） | `CredentialAuditService`、`DangerousConfigAuditService`、`AuditProbeTimelineService` |
| O9 | 错误码目录（`E1xxx`–`E9xxx`）+ 结构化日志共用 | `Services/ErrorCodes.cs` |
| O10 | UI 令牌体系 + 门禁（颜色/字号/圆角/图标注册表/虚拟化/危险按钮/三态…） | `App.xaml`、`Controls/UiIcon`、`docs/UI-DESIGN.md`、harness 门禁 |

（另：便携数据根、终端 TUI 命令卡片、实例环境变量 DPAPI、托盘"运行中的实例"菜单、UI 状态/滚动记忆等，见 `docs/FEATURE-INVENTORY.md`。）

## 4. 同能力、不同实现（对齐成本最高的一批）

| 能力 | 上游 | 本分支 | 说明 |
|---|---|---|---|
| 会话格式 | `Services/SessionFormatHelper.cs` | `Services/SessionFileNames.cs` + `ConversationService` | 我们已核对到 **v4**（契约哨兵 C1 全绿） |
| 整合包 | `VersionPackageService.Dspack.cs` / `.ModPack.cs` | `DshPackFormat/Archive/Writer/ImportService` + `PackExportService` | 我们**写 v4 / 读 v2–v5**；上游另有 ModPack v2 与双向转换 |
| 任务中心 | `TaskCenterView.cs` | `LauncherTaskWindow` + `LauncherTaskService` | 语义相近（台账 + 取消 + 50 条） |
| 诊断包 | `DiagnosticBundleService.cs` | `DiagnoseExportService.cs` | 都是"脱敏 zip"，字段不同 |
| 实例资源监控 | `InstanceResourceMonitor.cs` | `Watchdog/InstanceResourceSampler.cs` | 我们挂在守护轮上（无独立定时器） |
| 存储管理 | `StorageManagementWindow.cs` + `InstanceStorageService.cs` + 密码快照 | `LauncherStorageService` + `VersionSnapshotService`（DPAPI） | 上游**密码保护快照**，我们**DPAPI + 只统计不清理实例数据** |
| 日志 | `LauncherLogService.cs` | `LauncherLog.cs` + 日志中心 | 我们是 JSONL + 按天分组 UI |
| 市场 | `MarketplaceService` 一族 + GitHub 条件缓存 | `MarketplaceService`（2418 行）+ 2 个中文源适配器 | 上游多"配额/Token/条件缓存"，我们多"中文源/强身份合并/gzip 解压" |
| 主题 | `ThemeIntegrationController.cs` + `DshMarketThemeService.cs` | `DshMarketThemeService.cs` | 上游走 dsh `ui-theme.preference` 探测 |

## 5. 建议路线（三条，按需选）

| 路线 | 内容 | 适用 | 粗判 |
|---|---|---|---|
| **R1 逐块移植上游 → 本分支**（推荐先做 U1/U2/U10） | 在**本分支**上重做该块（不是搬上游代码，避免二次冲突），用**我们的**验证链（SelfTest + harness 门禁 + 截图）验收 | 上游块明显更好、且与我们现有模块耦合低时 | U2（自更新）需先有 Release 渠道；U10（GitHub 条件缓存/配额）与我们市场服务同层，属可局部替换 |
| **R2 把我们的独有块整理成补丁给上游**（O1–O5 最有价值） | 按 DIFF-REPORT §5 的"方式①：指定一块移植到 main" | 上游明确欢迎时 | 我们这些块大多**依赖本仓自己的模型**（如崩溃归因依赖四层证据），移植要连模型一起搬 |
| **R3 只做参考实现，不动代码** | 需要哪块再单独整理 | 当前默认 | 本报告即属 R3 的产出 |

**明确不建议**：整体 rebase 到 v1.2.4（09-13 在 v1.1.2 上实测过 **35 文件 / 108 冲突块**，v1.2.x 未重测；且两边 46 个文件同改，重做成本远高于逐块）。

## 6. 诚实边界

- 本报告是**文件级 + 上游 README 级**核对：某能力"上游没有"只对**同名文件**成立（我们已用 DIFF-REPORT §3 的教训修正过一次同类结论）。
- **未做**：三方合并演练、每块移植工时估算、上游测试是否可复用（上游有 `tests/DshLauncher.UnitTests` 34 个文件 + `SelfTest` + `windows-ci.yml`，**未逐个读**）。
- **未评价**代码质量/安全性，只判断"有什么、在哪里"。
- 上游 `main` 会继续前进；本报告固定在上面的 commit，后续核对请重放 §0 的克隆命令。
