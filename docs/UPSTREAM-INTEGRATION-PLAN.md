# 上游集成计划（dsh 0.1.7-rc.2 新增能力 → 启动器）

> 状态：**计划（未改任何产品代码）**。高价值 4 项 = **A 定时任务（并入关闭防呆）** / **B 插件版本豁免与启动归因** / **C 忙闲判定接第一手事实** / **D 官方可选 bundle**；其余见 §2「待定」。
> 依据：上游 `deepseek-harness` **`477b4f4205`**（= tag `dsh-v0.1.7-rc.2`，2026-09-24 21:39）相对旧检出 `00102833df`（`dsh-v0.1.7-alpha.2`，09-22）的 **502 提交 / 3771 文件（+149484/−26939）**。本地检出已就地前移到该提交（工作树干净；未装 `node_modules`）。
> 相关：[与上游的能力对照](UPSTREAM-CAPABILITY-REVIEW.md)（启动器 vs 启动器上游，另一件事）、[与 dsh 的契约清单](DSH_CONTRACT_INVENTORY.md)（本计划新增的依赖要在那里挂号 + 配哨兵）。

## 0. 口径与前置

### 0.1 本区间**契约不变**（已核，无需动作）

| 契约 | 证据 |
|---|---|
| C7 CLI 旗标 | `apps/cli/src/args.ts` 本区间**零改动** |
| C8 就绪行 `dsh web: <url>?token=` | `packages/bundle/web-app/src/**` 未改（只改了该 bundle 的 `cordis.patch.yml` / README / `package.json`） |
| C1 会话格式 | 仍 **v4**（无 v5；`snapshots` 里 v4 由 16 → 47 个样本）；`SessionFileNames.KnownMaxFormatVersion = 4` 已覆盖 |
| C14/C15/C16 安全体检键位 / 环境变量 / 遥测域 | `packages/bundle/base/cordis.patch.yml` 中仍在 |
| `llm-deepseek` 设置命名空间 | 该行 `id:` **仍是 `llm-deepseek`**（只把包名换成 `dsh-llm-deepseek-api-key`）⇒ `ModelService` 写 `settings.yaml` 的 `llm-deepseek:` 段仍有效 |

### 0.2 共同前置：Remote/HTTP 通道探针（**已做，2026-09-25；结论：通**）

上游客户端与宿主之间走 `API_PATH = '/api'`（`packages/client/connection/src/api-path.ts`），业务方法以 `@Remote` / `@RemoteScope` 暴露（`docs/api-gateway.md`）。

**探针**：`_verify-p0/func-check/probe-api-remote.ps1`（实现 `func-check/api-remote-probe/`，C#；自带临时沙箱 `DSH_HOME`，只调 list 类方法 + 沙箱内建一个会话；`RESULT: PASS`，日志 `_verify-p0/logs/api-remote-probe.txt`）。

已核实（游源码 + 沙箱实例实测，运行时 `0.1.7-rc.2`）：

| 要核实的 | 结论 |
|---|---|
| 握手（`/api` 上的 RPC 分帧与鉴权头） | `GET /?token=<launchToken>`（就是 `dsh web:` 行里那个 URL）→ **303** + `Set-Cookie: dsh-auth-<authority 哈希>=<签名票据>`；之后 `POST /api/<命名空间>/<方法>`，体为 `{"type":"client-request","rpcId":…,"method":"<ns>/<方法>","payload":{"args":{…}}}`，带该 cookie。响应 = `{"type":"server-response","rpcId":…,"result":{"ok":true,"value":…}}`，失败为 `result.error={code,message,details}`；Content-Type `application/json` |
| 鉴权是否必要 | **必要**：不带 cookie ⇒ **401 unauthorized**（回环地址也不行；启动器得先做 token→cookie 换发）；`Host` 不是本机/未登记 ⇒ **403**（DNS rebinding 防护） |
| 只读事实方法是否对 `web` 实例可用 | **可用**：`session/list`（每条会话直接带 `running` / `agentAvailable`）、`pluginManager/listBundles`、`pluginManager/listVersionExemptions` |
| 有无流式要求 | 普通 POST 调流式方法（`session/control`、`job/list`）⇒ `gateway/signature-invalid`「stream Remote methods must be opened through the stream carrier」⇒ 流式要开 `/api/remote.mux` WebSocket。**C/D 都只用 unary，不需要** |
| 参数形状 | **极严**：`args` 字段名必须与 Remote 描述符一致（`session/list` 要 `_request`、`job/list` 要 `request`），否则 `gateway/arguments-invalid`（业务代码不会被执行） |
| 版本差异（额外发现） | `pluginManager/listVersionExemptions` **rc.2 有、`0.1.7-alpha.2` 上 404**；可选 bundle 也少一个（alpha.2 只有 2 个）⇒ 官方豁免接口是**新版本才有**，启动器不能假设它在 |

结论：**通** ⇒ **C** 用 `session/list` 的 `running` 作为"忙"的第一手事实（网络连接启发式降为兜底）；**D** 走 `pluginManager/listBundles` + `setBundleEnabled`（官方接口，而不是自己写 profile `package.json`）；**B** 维持"走上游 CLI + 文件只读"（因为官方豁免接口在旧运行时上没有，见上表末行）。启动器侧新增"仅 HTTP 的 RPC 客户端（token→cookie + unary POST + 超时 + 失败回落）"，按 §0.3⑤ 挂号为契约 **C22**。

### 0.3 落地通则（每项都要走）

① 产品 `dotnet build` 0 警告；② `DshLauncher.SelfTest` 加断言；③ `_verify-p0` 加门禁（`func-check/*.ps1` 或 `Program.cs`）；④ `docs/CHANGESETS.md` / `docs/BEHAVIOR-CHANGES.md` / `DEV_STATE.md`；⑤ 新依赖的上游内部结构在 `docs/DSH_CONTRACT_INVENTORY.md` 挂号并配 `contract:` 哨兵；⑥ 真机在**安装副本**上验收，期间不碰 `launcher-data` 真实数据。

---

## 1. 高价值计划

### A. 定时任务 → 关闭防呆 + 相关只读展示（P0，**不依赖 §0.2**）

> **状态：已在变更集 176 落地，并在变更集 178 修掉布局误判**（work-log/200、[202](../work-log/202-schedule-single-layout.md)）——A1 `ScheduleSnapshotService`、A2 防呆接线（停止前确认 / 空闲自动停跳过 / 退出确认与换版本报告加一句）、A3 卡片只读行已完成；**178 修正**：提醒实际是 `<DSH_HOME>/storages/schedule.json`（**single** 布局，不是 per-record）——176 读错路径 ⇒ 实际读不到任何提醒，现已按真实形状修好并加了改口径后的 C20 哨兵。验证：构建 0/0、SelfTest **215/0**、harness **400 PASS / 0 FAIL / 0 SKIP**。下面保留原计划文字，并标出落地时被事实纠正的两处（A 的存储路径、A3②）。

**上游事实（已核）**

- `web` profile **默认挂载** Schedule（`packages/bundle/web-app/cordis.patch.yml` 插入 `time-context` + `schedule`，`ui-schedule` 保持启用）⇒ 启动器起的每个实例都有定时任务与 `schedule_create/list/update/delete` 工具，并有自动化任务页 / 页头提醒时钟 / 侧栏标记。
- **〔178 纠正〕权威存储：`<DSH_HOME>/storages/schedule.json`（单个文件，`storage-json` 的 single 布局）**：形状 `{"unit":{"name":"schedule","version":1},"global":null,"tables":{"tasks":{"<ScheduleId>":{sessionId,record:{kind,title,scheduledAt,…},status,…}}}}`。推导链：`storage-json` 的 `root` = `dshHomePath('storages')`（`packages/bundle/base/cordis.patch.yml:171`）→ 按域声明的 `layout` 选单元，**默认 single**（`single-unit.ts` 写 `<root>/<name>.json`），只有显式 `layout: 'per-record'` 才写 `<root>/<name>/<table>/<key>.json`（`index.ts`）；**schedule 域没有声明 per-record** ⇒ 落到 `storages/schedule.json`，并已用真实文件核对。~~原计划写的 `<...>/storages/schedule/tasks/<ScheduleId>.json`（per-record）是错的：那条推导把 `session_projcache`（**声明了** per-record）的形状当成了所有域的默认。~~
- 记录形状（`packages/schedule/schedule/src/storage.ts` + `types.ts`）：
  `{ sessionId, record: { id, kind: 'after'|'at'|'every'|'daily'|'weekly'|'cron', title(≤120), prompt, scheduledAt(四位年 RFC3339 UTC = **下一次或最终触发点**), afterSeconds?, everySeconds?, time?, timeZone?, weekdays? }, status: 'active'|'inactive', lastDelivery?, deliveryHistory? }`；domain `name: 'schedule', version: 1`。
- **只有宿主在跑才会投递**：官方桌面端为此专门弹退出确认（"应用关闭期间定时任务不会运行"）。

**启动器现状**：全仓 `schedule` 命中 **0**。`InstanceIdleTracker.ShouldAutoStop`（`MainWindow.xaml.cs:748`）在"空闲 N 分钟"后**静默停实例** ⇒ 现在会直接掐掉用户设好的提醒；停止/换版本路径也没有任何相关提示。

**范围**

| # | 做 | 落点 |
|---|---|---|
| A1 | 新增只读 `ScheduleSnapshotService`：解析某实例 `DshHome` 下 `storages/schedule/tasks/*.json` → `{ activeCount, nextAtUtc, titles[] }`；**失败静默降级**（目录不存在 / JSON 坏 / `version ≠ 1` → 返回"未知"，绝不让解析问题影响实例操作） | `Services/`（新文件）+ `Models/` |
| A2 | 防呆接线（"关闭防呆"这一族）：手动停止（`MainWindow.StopInstanceAsync:6313` 及其余停止入口 1747/6262/6350/6419/6759）、**空闲自动停**（`MainWindow:748` 一带）、换版本（`InstanceVersionSwitchService`）、关启动器 —— 有 active 任务时：手动停给一次确认；**空闲自动停直接跳过并记录原因**；换版本/退出给提示（文案与官方桌面端的两种事实口径对齐） | `MainWindow.xaml.cs`、`InstanceIdleTracker` 调用点、`InstanceVersionSwitchService` |
| A3 | 相关内容：① 实例卡片**只读**显示「提醒 N · 下次 …」（启动页 + 版本控制页两处卡片，空则不占行）——**已做**；② **〔原计划有误，已改〕** 曾以为 `DshHomeImportService` 不含 `storages/schedule` ⇒ “导入会丢提醒”。落地时通读 `CopyMissingDirectory` 后确认：它**拷贝除 `.dsh-launcher` / `.credentials.yaml` / `webview2` / `node_modules` / `storages/workspace.json` 之外的全部 missing 内容**（`Services/DshHomeImportService.cs:342-380`），而 170/172/376 那几处只是**特例合并**（sessions / workspace / 凭据）⇒ `storages/schedule/**` 本来就会被带过去，**无需改动** | 卡片渲染处（已完成）；导入服务**不动** |

**不做**：新建/编辑/删除提醒（dsh 自己的 UI 负责）；**不写** `schedule` 存储（避免跟随上游 domain 版本演进而做迁移）；`VersionSnapshotService` **不动**（它只覆盖配置文件——`settings.yaml`/`.credentials.yaml`/profile 清单与锁/`launcher.patch.yml`，与 `sessions/` 同级语义，加 `storages/` 会改变快照语义与上限）。

**验证**：SelfTest（假 `storages/schedule/tasks/*.json` 矩阵：全 active / 含 inactive / 坏 JSON / 无目录 / `version:2` / 字段缺失）+ harness 门禁（"有 active 提醒的实例拒绝空闲自动停"、"坏 JSON 不崩且降级为未知"、"导入保留 schedule"）；真机：沙箱实例里建一条 1 分钟后的提醒，看卡片与停止确认。

**成本/风险**：改动都在启动器侧，量级小。风险=上游 `domain.version` 或字段改名 ⇒ 靠"读不到就未知"降级 + `contract:` 哨兵盯 `version` 与 `tasks/status/scheduledAt` 字段名。

### B. 插件版本豁免 + 启动失败归因（P1）

> **状态：已在变更集 180 落地（181 补上界面收口）**（work-log/204、[205](../work-log/205-exemption-list-and-revoke.md)）——B1 只读 `PluginVersionExemptionService`、B2 `ExtensionService.AllowPluginVersionAsync`（**写走上游 CLI**）+ 界面“已放行识别 / 一键放行”、B3 归因新增 `CrashCauseKind.PluginVersionIncompatible`；**181**：插件页常驻「已放行：N 条」+ 浮层列表 + 逐条**撤销**（走上游 `revoke-version`）。验证：构建 0/0、SelfTest **226/0**、harness **404/0/0**（+2 门禁 + 哨兵 **C21**）+ 真机端到端（放行/滞后重试/区间被拒/坏文件拒绝改写/撤销幂等）+ 真机截图。

**上游事实（已核）**

- CLI：`dsh plugin [--profile P] allow-version <pkg@ver> --dsh-version <exact> --accept-risk` / `revoke-version` / `version-exemptions`（`apps/cli/src/plugin.ts`）。
- 豁免文件：`<profileDir>/compatibility.json`，形状 `{ "@scope/pkg@1.2.3": ["0.1.7-rc.2"] }`；key/value **都必须是精确版本**，坏记录只警告并忽略，且此时文件 `rewritable=false`（`packages/boot/app-boot/src/profile-compatibility.ts`）。
- 启动期 `compatibility-preflight.ts` 对不兼容行**直接拒绝**；插件管理接口对"不兼容且未豁免"的 bundle 抛 `incompatible-version`（`packages/boot/plugin-manager/src/index.ts` `listBundles`）。
- 每次 `dsh plugin` 都会把这些警告打到 stderr，失败时给出 `allow-version … --accept-risk` 的提示行。

**启动器现状**：`PluginCompatibility.Check` 只按 manifest 的 semver 范围给**建议性**警告；不读豁免、不提供放行入口。`ExtensionService` 失败判定是 `ExitCode != 0`（`ExtensionService.cs:857`），新警告文案既不解析也不展示。

**范围**：① 读 `compatibility.json` 展示"已放行：`pkg@ver` → DSH 版本列表"；② 安装/更新时把"不兼容 + 未豁免"讲成可操作提示，并提供**一键写豁免**（语义与 `allow-version` 一致：仅精确版本、必须显式确认风险，且 `rewritable=false` 时拒绝写并要求用户手工修）；③ "启动失败"归因新增一类：插件兼容性 preflight 拒绝（并入 `CrashCauseClassifier` / 启动证据链）。

**验证**：SelfTest（豁免解析、坏 key/value 忽略、拒绝写不可重写文件）+ harness 门禁（写豁免不触碰 `dsh.profile.bundles`；拒绝态归因文案）；真机：造一个不兼容插件，看启动失败提示是否指向"兼容性/豁免"。

**风险**：写 `compatibility.json` 属对上游内部文件的写入 ⇒ 必须严格按上游校验语义（不可重写时拒绝），并配哨兵盯文件名与结构。

### C. 忙闲判定接第一手事实（P1，**先做 §0.2 探针**）—— **已落地（变更集 182）**

**上游事实**：宿主知道得很细——运行中的 agent（含子代理、等待审批的回合）、排队消息、运行中/停止中的后台任务（`packages/jobs`）、已挂定时器（`schedule`）；官方桌面端的退出确认用的就是这套事实（`apps/desktop/README.zh.md`，走私有 IPC）。对 `web` 实例的官方通道：**已核实**（§0.2）——`session/list` 是**普通 unary 方法**，每条会话直接带 `running` 与 `agentAvailable`，无需流式。
**语义已核实**（上游 `packages/core/agent-loop/src/agent.ts`）：agent 阶段只有 `idle`/`maintenance`/`running` 三种，审批等待发生在 step 内部 ⇒ **等审批时 `running` 仍为真**；子代理也是会话 ⇒ 也会计入。

**启动器现状**：`InstanceIdleTracker.ShouldAutoStop` + `ProcessQuery`（"进程树有到非回环地址的已建立连接"=忙）——启发式，会漏：等审批、消息排队、只有定时任务的实例都可能判成空闲。

**范围**：把"忙"的判据换成宿主事实（`session/list` ⇒ 任一会话 `running`；连接/CPU 启发式只在**拿不到事实时**兜底），探针/客户端跑不通就整体不做。**范围外**：不做远程控制（不代发消息、不代批准）。

**成本/风险**：引入了仅 HTTP 的 RPC 客户端（握手换 cookie + unary POST + 超时 + 失败回落）。风险点（把忙实例当空闲）的三道措施：① 事实说忙 ⇒ 绝不停；② 只有拿不到事实才看连接/CPU 启发式；③ 事实保鲜期 45 秒，过期即当拿不到（不用过期事实下判断）。

**落地物（变更集 182）**：`Services/DshRemoteApiClient.cs`（契约 C22 的客户端）、`Services/InstanceHostFactsService.cs`（事实解析/缓存/节流/原子刷新）、`InstanceIdleTracker.EvaluateAutoStop`（判定阶梯 + `AutoStopDecision`）、`MainWindow`（每轮探测按实例节流刷新；停止理由写明"凭什么说空闲"）、`ErrorCodes.E1020/E1021`。验证：SelfTest **241/0**（含本机假服务跑真实握手/信封/401 后重握手重试）、harness **408/0/0**（含一条**真起 dsh web** 的端到端门禁）+ 沙箱实例现场。

### D. 官方可选 bundle 开关（P2）

**上游事实（已核）**：`OPTIONAL_BUNDLES`（`packages/boot/app-boot/src/profile.ts:213`）= `@deepseek-ai/dsh-experimental-agent-team-profile`、`@deepseek-ai/dsh-experimental-voice-input-bundle`、`@deepseek-ai/dsh-experimental-auto-review`，各带 icon 与本地化 title/description，**默认关**；插件管理器 `@Remote listBundles()` 返回 `{ name, version, description, meta, enabled, installed, optional, removable, readOnlyReason, error, rows, overrides }`；"启用"= 写 profile `package.json` 的 `dsh.profile.bundles`（并做 patch 调和）。

**启动器现状**：无认知（只有市场分类字符串 `"voice"`）；但 `DshProfileService` / `ExtensionService` 已在读写 `dsh.profile.bundles` ⇒ 有现成落点。

**范围**：在插件/扩展页把这三个列成"官方可选能力（默认关）"，显示本地化名称与说明、可开关；读写**走上游官方接口**（`pluginManager/listBundles` + `setBundleEnabled`，§0.2 已实测可用）——而不是自己写 profile `package.json`；**不**把非官方 provider 运行时（语音/浏览器操作/电脑操作）自动塞进安装。

**验证**：SelfTest（列表读写 + 开关后 profile JSON 断言）+ 真机开关一次，与 dsh 侧 `listBundles` 的 `enabled` 对齐。

---

## 2. 待定（本轮不排期）

| # | 能力 | 上游落点 | 为何待定 | 重启条件 |
|---|---|---|---|---|
| E | 账号态与额度（双 provider、`ACCOUNT_QUOTA`、退出登录、内嵌充值页） | `packages/llm/llm-deepseek-account`、`llm-deepseek-api-key`、`packages/credentials/deepseek-account-platform` | 与启动器"只读 API key 查余额"（`BalanceService`）口径冲突；账号态涉及凭据与计费 | **已定（2026-09-25，用户口径）：不做**——账号相关能力不往启动器里做，见 §3.5；`BalanceService` 的只读 API key 查余额不受影响 |
| F | SDK / ACP 自动化实例 | `packages/sdk/**`、`packages/acp/**`、`dsh --profile sdk\|acp` | 启动器现**明确拒绝**启动无界面 profile；要新增实例类型与协议实现 | 出现"无界面跑任务并回收结果/通知"的需求 |
| G | 官方桌面端协同/共存 | `apps/desktop`、`apps/desktop-host`（端口 19387、独占 `profiles/desktop`、内置 Node/Python/pnpm） | 上游明确"CLI 不能启动或修改 `profiles/desktop`"；启动器 `DeepSeekDesktopDetector` 已能探测其安装根 | 用户同时用官方桌面端与启动器，需要互斥检测或共用运行时 |
| H | pnpm/npm 有界运行口径（静默上界、杀进程树、被终止的运行不启用半成品 manifest） | `bounded-pnpm-runs` 决策 + `plugin-manager` 实现 | 属启动器自建流程（版本安装/插件安装/源码构建），与本次 175 同源但不是同一处 | 再出现"安装卡住/锁不放"的真实现场 |
| I | Windows 细节：经 shell 解析打开、junction 正确列目录 | `windows-open-through-shell-resolution`、`windows-directory-junction-listing` | 影响面小，宜攒着一起做 | 出现 junction / 打开方式相关报障 |
| J | 崩溃/反馈上传与诊断包对接 | `packages/runtime-diagnostics`、`packages/feedback`、`bounded-session-log-upload`（8 MiB/请求 + 水位续传） | 需先定"诊断包含哪些 dsh 侧记录" | 下一次诊断能力迭代 |

### 2.1 兼容性观察项（不是新能力，但要盯）

| # | 观察项 | 现状 / 动作 |
|---|---|---|
| W1 | provider 分家：出厂层新增 `id: llm-deepseek-account`（显示名 "DeepSeek Account"） | `llm-deepseek` 命名空间不变 ✅；`ModelService` / `ProviderStateService` / `ModelProviderSyncService` 需认得第二个 provider |
| W2 | npm dist-tags：`latest` = **0.1.5-rc.3**、`next` = **0.1.7-rc.2**、`alpha` = 0.1.7-alpha.2 | 核 `DshVersionCatalogService` 用的是哪个 tag（若只认 `latest`，用户就选不到 rc.2） |
| W3 | `dsh plugin` 现在会往 stderr 打兼容警告与放行提示 | 核 `ExtensionService` 的判成败不会把警告当失败（当前只按退出码） |
| W4 | 启动时报告被跳过的 bundle（`reportSkippedBundles`） | 可接进日志归因（低优先） |
| W5 | `web` 每个会话多 4 个工具 schema + 每步一条 `time-context` 持久 user 消息 | 用户可感知的 token 成本；被问到要能解释 |
| W6 | 每个实例的 DSH_HOME 多出 `storages/schedule/**` | 导入会自然带过去（见 A3②，无需改）；换版本快照按设计只含配置（不含 `sessions/`、`storages/`） |

---

## 3. 显式不做

1. 驱动或改写 `profiles/desktop`（上游明确禁止；Electron 有进程级单实例锁与独占 profile）。
2. 把 dsh 的 Web UI / 插件页当 API 抓屏（Remote 接口是另一条正规通道，见 §0.2）。
3. 写 `schedule` 存储，或自建提醒引擎（只读 + 防呆即可覆盖需求）。
4. 依赖未文档化的内部文件却不降级：`storages/*`、`compatibility.json` 一律按"读不到 = 未知"处理。
5. **账号相关能力（账号态、额度点数、退出登录、内嵌充值页）**。用户口径（2026-09-25）：不往启动器里做；`BalanceService` 现有的"只用 API key 只读查余额"不属此列，保持现状。

## 4. 建议顺序

**A**（独立、价值最高、无需新通道）→ **§0.2 探针**（一次性，决定 B/C/D 形态）→ **B、D**（同一通道 + 同一族文件）→ **C**（以探针结论为准）。
