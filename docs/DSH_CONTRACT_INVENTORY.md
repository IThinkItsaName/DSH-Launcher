# 与 dsh 的契约清单（DSH Contract Inventory）

> 相关：[架构决策记录](ARCHITECTURE_DECISIONS.md)（为什么这么做）、[行为×验证矩阵](BEHAVIOR_MATRIX.md)（哪些行为有自动化保护）。

> 目的：把启动器对 dsh 的**全部依赖点**写成一张表，并为每条配一个**哨兵测试**。
> 背景：本仓已被 dsh 升级打断过多次（`--no-open`、token 401、会话格式 0→3），
> 靠"升完再发现"代价很高；这张表是止血工具（借鉴清单 #23）。
>
> - 上游权威来源优先级：**已安装运行时**（安装目录由 `VersionSettingsService.ResolveDshInstallDirectory()` 决定；本机 2026-09-14 起重装为
>   `%ProgramFiles(x86)%\dsh_launcher\run_time\versions\<版本>\node_modules\@deepseek-ai\dsh`；
>   2026-09-24 实测本机的载荷直接落在 `run_time\node_modules\@deepseek-ai\dsh`（`versions\` 存在但为空），
>   探针两种布局都能解析，用户实际在跑的东西以探针输出为准）
> ＞ 本地上游克隆 `deepseek-harness/`（最新源码，用于提前发现漂移）。
> - 哨兵实现在 `_verify-p0/Program.cs` 的「契约哨兵」段，命名前缀 `contract:`。
> - 已安装运行时的定位**不再写死版本号**（2026-09-15，work-log/156）：探针遍历 `versions/` 按版本降序取包根，
>   并先跑 `contract: 已安装运行时包根可解析（C1/C2/C6 探针前置）`；该前置 FAIL 时先修路径/重装，不要当作契约破坏。
> - 最近核对：上游 `477b4f42`（2026-09-24，`dsh-v0.1.7-rc.2`；本区间新增 **C20**、**C21** 与 **C22**（/api Remote 通道，2026-09-25 §0.2 探针））、安装运行时 `0.1.7-rc.1`（2026-09-24 核对；上一次记录 0.1.5-rc.2 已过时）。
> - 编号 **C18** 为历史保留号（对应能力已并入 C13/C17），表中不单列。

## 一、契约表

| # | 契约点 | dsh 侧权威定义 | 启动器落点 | 破坏时的症状 | 哨兵 |
|---|---|---|---|---|---|
| **C1** | 会话格式版本号 | `dsh-session/lib/index.js` `SESSION_FORMAT_VERSION = 4`（2026-09-17 起；v3→v4 **只把头部 version 3 改成 4**，「all other logical header fields remain unchanged」，见上游 `packages/session/session-format-v3-to-v4/README.md`；变动主要在记录体：工具角色结果 / producer 来源 / **父目录补全** / 引用重映射。0.1.2-rc.1 无此包；标签 0.1.2 为 `0`） | `SessionFileNames.KnownMaxFormatVersion`（**变更集 167：3 → 4**）、`ConversationService`（**宽容接受任意版本**：不读记录体，只按头部 version 命名文件） | 高版本会话读不出（对话页「已读取 0/0」） | `contract: 会话格式版本未超出已核对范围`（**语义 = 已核对范围**：上游与已装运行时都必须 **≤** 该常量；已装运行时低于它是正常的——旧运行时会写旧版本日志） |
| **C2** | 会话文件名 canonical 规则 | `dsh-session-format` `CANONICAL_LOG_FILENAME = /^session(?:\.v([1-9][0-9]*))?\.jsonl$/u`；v0 不带标签，v1+ 为 `session.vN.jsonl`（小写 v、无前导零） | `SessionFileNames.TryParse/Build` | 导入/同步写出 dsh 不认识的文件名 → 会话"消失" | `contract: canonical 文件名规则与上游一致` |
| **C3** | 会话头字段 | `dsh-session-persistence-jsonl` `parseHeader`/`toHeaderLine`：必需 `type/version/id/createdAt/isSeeded/delegationDepth`，可选 `cwd/parentSession/origin/agentPreset`，白名单拒绝 `sandboxMode`/`approvalPolicy` | `ConversationService` 头部读取（**故意比 dsh 宽松**：`isSeeded` 缺失也接受） | 导入被判"corrupt session log"；或写出 dsh 拒收的头 | `contract: 会话头必需字段仍被 dsh 要求`、`contract: 真实样本头部可被启动器解析` |
| **C4** | 会话/项目目录名编码 | `projectKey`（`/ \ :`→单 `-`；不安全码元 `~XXXX` 大写四位十六进制；去前导 `-`；空→`root`；截断 251；包 `--…--`）与 `encodeSegment`（安全集 `[A-Za-z0-9._-]`，`~` 自身转义，`.`/`..` 特例） | `ConversationService.ProjectKey/EncodeSegment`（同源重实现） | 导入落到陌生项目目录、会话在 UI 里"丢了" | `contract: 目录编码向量与上游测试一致` |
| **C5** | 会话根布局 | `<DSH_HOME>/sessions/<projectKey>/<encodeSegment(sessionId)>/session.vN.jsonl[.zstd]` | `ConversationService.GetSessionsRoot` 等 | 全部会话不可见 | `contract: 会话根仍位于 DSH_HOME/sessions`（源码级） |
| **C6** | 压缩编码 | `DEFAULT_COMPRESSION = "zstd"`；**同一 sessions 根混用两种编码 → `encodingMismatch` 硬报错** | `SessionFileNames.ResolveCompression`、`ConversationService.ResolveTargetSessionCompression` | dsh 启动即报错 | `contract: canonical 文件名规则与上游一致`（含编码后缀矩阵） |
| **C7** | CLI 启动参数 | `apps/cli/src/args.ts`：`--profile`/`--from-default-profile`/`--patch`（启动器自己的旗标，第一个不认识的 token 之后全部交给 app）；`packages/bundle/web-app/src/startup.ts`：`--host`/`--port`/`--no-open`/`--trusted-host` | `DshInstanceRunner.BuildArguments` | 启动失败，或参数被 app 吞掉当成自己的参数 | `contract: CLI 旗标仍在上游参数解析里` |
| **C8** | 就绪输出行（含 launch token） | `packages/bundle/web-app/src/index.ts`：`console.log(\`dsh web: ${authenticatedUrl}…\`)`，URL 带 `?token=…`，可能追加 `(LAN: …)` | `DshInstanceRunner.AuthenticatedUrlPattern = @"dsh\s+web:\s*(https?://\S+)"` | Chat 窗口 401 白屏；Web 重开拿不到 token | `contract: dsh web: 输出行仍匹配启动器正则` |
| **C9** | Node 引擎范围 | 上游根 `package.json` `engines.node = "^22.19.0 \|\| >=24.0.0"` | `PortableNodeService.MinimumVersion = "22.19.0"` | 启动早停；zstd / AbortSignal.timeout 缺失 | `contract: Node 引擎下限与上游一致` |
| **C10** | profile 结构与 bundles | `profiles/<name>/package.json` 的 `dsh.profile.bundles`（上游 `profile_kind` 同口径） | `DshEnvironmentScanner`、`ExtensionService`、`SafeProfileService`、`PluginBisectService` | 扫描分类错；安全模式剥错包 | `contract: dsh.profile.bundles 仍在上游使用` |
| **C11** | 插件 peerDependencies | 如 `@deepseek-ai/dsh-settings`（核心包随 dsh 版本走） | `PluginCompatibility`（变更集 70） | 插件装上即崩（工具调用全失败） | 既有插件兼容性用例 |
| **C12** | `--no-open` 引入版本 | 历史事实：0.1.0-rc.8 起；**无法从代码验证**，只能靠实测 | `DshInstanceRunner.SupportsNoOpen` | 旧版启动弹浏览器（有浏览器守卫兜底） | 文档记录（不设哨兵） |
| **C13** | DSH_HOME 内固定文件名 | `settings.yaml`、`.credentials.yaml`、`profiles/`、`sessions/`、`storages/` | 快照/导出/同步/设置写入 | 快照缺内容、导出不完整 | 部分由真实端到端覆盖 |

| **C14** | 权限模型键位：`sandbox-policy.config.mode` / `workspaceRoot`、`approval.config.policy`、`permission.config.presets`（三档含 `danger-full-access` + `approval: never`） | 已安装运行时出厂层：`<version>/node_modules/@deepseek-ai/dsh/node_modules/@deepseek-ai/dsh-base/cordis.patch.yml` | `DangerousConfigAuditService`（#20 增量 5） | **静默失效**：危险配置检查永远"未发现"，用户误以为体检通过 | `contract: 权限模型键位仍在出厂层（C14）` |
| **C15** | 安全体检依赖的环境变量名：`DSH_PERMISSION_MODE`、`DSH_TELEMETRY_MODE`、`DSH_TELEMETRY_OTLP_URL` | 同上（出厂层里的 `!!js process.env.…` 表达式） | `DangerousConfigAuditService` | 环境变量类风险不再被检出（同样是静默失效） | `contract: 安全体检依赖的环境变量名仍在出厂层（C15）` |
| **C16** | 遥测默认导出口域名 `harness-telemetry.deepseeksvc.com`（官方域判据） | 同上 | `DangerousConfigAuditService`（非官方域 → 危险） | **假报警**：上游换域后官方域会被当成"第三方外发" | `contract: 遥测默认域仍是 *.deepseeksvc.com（C16）` |
| **C17** | 凭据文件名 `.credentials.yaml`（含 `.yml` 变体）与"只报元数据、不回显值"策略 | C13 的固定文件名 + 本仓安全契约（work-log/73） | `CredentialAuditService`、`DangerousConfigAuditService`（凭据可写检查） | 清单为空 → 看起来"本机没有凭据文件" | 由 SelfTest 断言覆盖（凭据文件清单 + 4 组安全反证；未单独设 harness 哨兵） |

| **C19** | 社区探针审计文件格式：`<DSH_HOME>/audit/<DSH_AUDIT_PROFILE\|session>.jsonl`，每行 `{t,sid,seq,type,actor,h[,flags,sev,key,raw]}`（`@marcog-h/dsh-audit` 0.1.5，MIT，第三方可选） | 探针源码 `lib/index.js`（`_repro/probe-audit/` 留有 tarball 备查） | `AuditProbeTimelineService`（#20 增量 3） | 字段改名 → 时间轴少字段或空态；**因我们只读白名单字段，最坏是显示变少，不会崩** | 无 harness 哨兵（第三方可选数据源、非我方依赖；解析容错 + 字段白名单 + 反证 F/G 覆盖） |

| **C20** | 定时提醒存储：**`<DSH_HOME>/storages/schedule.json`**（`storage-json` 的 **single** 布局——schedule 域没声明 `layout: 'per-record'`，默认就是 single；形状 `{"unit":{"name":"schedule","version":1},"global":null,"tables":{"tasks":{"<id>":{sessionId,record:{kind,title,scheduledAt,…},status,…}}}}`，单条 `status` 缺省 ⇒ active）。**启动器只读、不写**；per-record 写法只作兼容 | 上游 `packages/schedule/schedule/src/storage.ts`（域/表/布局）+ `packages/storage/storage-json/src/index.ts`（按 `descriptor.layout === 'per-record'` 选单元）+ `single-unit.ts`（写 `<root>/<name>.json`）+ `packages/bundle/base/cordis.patch.yml`（`dshHomePath('storages')`）；`web` profile 自 0.1.7-rc.1 起默认挂载 Schedule | `ScheduleSnapshotService`（**变更集 176，布局口径 178 修正**）、`MainWindow.EvaluateIdleAutoStop` / `ConfirmStopWithPendingSchedules`、`ManagerInstance.ScheduleSummaryText` | **温和**：读不懂就降级为「未知」（不提示、不拦操作；绝不会让实例操作失败）。最坏=防呆漏报或卡片少一行；**176 曾猜成 per-record ⇒ 真实文件读不到任何提醒（已用真实文件核对并在 178 修正）** | `contract: 定时提醒仍是 single 布局（storages/schedule.json）+ 域版本 1（C20）`（源码级；额外断言启动器读的就是 `schedule.json`） |

| **C21** | 插件**精确版本豁免**：文件 `<DSH_HOME>/profiles/<profile>/compatibility.json`，形状 `{ "包名@精确版本": ["精确 DSH 版本", …] }`；键必须是**小写包名 + 规范写法 SemVer**（拒 `v` 前缀/区间/前导零/空白），值是精确 DSH 版本列表；坏记录只警告并忽略、且此时文件**不可重写**；写入入口 `dsh plugin allow-version <pkg@ver> --dsh-version <exact> --accept-risk`（上游做精确校验/当前版本校验/文件锁/原子写/0600，并拒绝改写已有坏记录的文件）。插件 peerDependencies 不满足时，**启动期预检会禁用该行**（stderr: `dsh: disabling profile plugin <id>: …`）。**启动器只读，写入一律走上游 CLI** | 上游 `packages/boot/app-boot/src/profile-compatibility.ts`（文件名/校验/写入）、`plugin-compatibility.ts`（判定与文案）、`compatibility-preflight.ts`（启动拒绝）、`apps/cli/src/plugin.ts`（`allow-version`/`--accept-risk`） | `PluginVersionExemptionService`（**变更集 180**）、`ExtensionService.AllowPluginVersionAsync`（**变更集 180**）、`ExtensionWindow.ConfirmIncompatiblePluginAsync`、`CrashCauseClassifier`（`PluginVersionIncompatible`） | **温和但看得见**：读不出/看不懂 ⇒ 按“没有放行”处理并提示（不会崩）；上游改文件名或收紧入口时，放行按钮会失败但会附上上游原输出；最坏是“文案还不准”而非误操作 | `contract: 插件精确版本豁免仍是 compatibility.json + allow-version --accept-risk（C21）`（源码级，读本地上游克隆） |

| **C22** | `dsh web` 的 **`/api` Remote 通道**：握手 `GET /?token=<launchToken>` → **303** + `Set-Cookie dsh-auth-<authority 哈希>`，业务调用 `POST /api/<命名空间>/<方法>`，体 `{type:'client-request',rpcId,method,payload:{args}}`；响应 `{type:'server-response',rpcId,result:{ok,value\|error:{code,message,details}}}`。**必需鉴权**（无 cookie ⇒ 401，回环也不行）；`Host` 非本机/未登记 ⇒ 403；`args` 字段名必须与描述符完全一致；**流式方法不能用 unary POST**（须 `/api/remote.mux`）。只读事实：`session/list`（带 `running`/`agentAvailable`）、`pluginManager/listBundles`、`pluginManager/listVersionExemptions`（**rc.2 才有，`0.1.7-alpha.2` 上 404**） | 上游 `packages/client/connection/src/api-path.ts`（`/api`）、`rpc-host.ts`（`requestRejection` = 403 fence + `browserAuth.isAuthenticated` 401）、`browser-auth.ts`（`TOKEN_QUERY='token'` / 303 + `sessionCookie` / 401）、`rpc-schema.ts`（`client-request` / `server-response` 信封）、`packages/api/gateway/src/index.ts`（流式须走 stream carrier）；实测探针 `_verify-p0/func-check/probe-api-remote.ps1`（+ `api-remote-probe/`） | `DshRemoteApiClient`（**变更集 182**，仅 HTTP、只连回环）、`InstanceHostFactsService`（**变更集 182**，忙闲事实）、`InstanceIdleTracker.EvaluateAutoStop`（宿主事实为主、启发式兜底） | **温和**：通道不可用 ⇒ 空闲自动停止回落连接/CPU 启发式（`E1020`）且绝不因此崩溃/改数据；**事实说忙则绝不停**（`E1021`）。早期版本缺方法（如豁免接口 404）是真实存在的 ⇒ 调用方必须按“方法不存在 = 降级”处理 | `contract: /api Remote 通道仍是 token→cookie + unary POST 信封（C22）`（源码级，读本地上游克隆 + 计划文档指向探针）；另有 `p1/host-facts` 盯启动器侧落地物与**真起 dsh** 的端到端 |

## 二、降级方向（0.1.5 → 0.1.2）为什么是单向的

**两个已安装运行时的实物证据（2026-09-11 核对）**：

| 能力 | 0.1.2-rc.1 | 0.1.5-rc.1 |
|---|---|---|
| 会话文件名构造 | `logPath()` 恒为 `session.jsonl[.zstd]`（`dsh-session-persistence-jsonl/lib/index.js:165`），写出的头文件名固定 `session.jsonl`（同文件 `:948`） | `sessionFormatLogFilename(v)` → v0 = `session.jsonl`，v1+ = `session.vN.jsonl`（`dsh-session-format/lib/index.js:474`） |
| 格式目录/迁移链 | **不存在**（无 `dsh-session-format`、`-format-catalog`、`-format-v0-to-v1/-v1-to-v2/-v2-to-v3`） | 五个包齐全（`SESSION_FORMAT_VERSION = 3`） |
| 读 v3 会话 | 按 `session.jsonl.zstd` 找文件 → **找不到**（文件叫 `session.v3.jsonl.zstd`），会话表现为缺失/空 | 正常读取 |

推论（已用真实文件核对，见 work-log/53）：
1. **0.1.5 下新建的会话在 0.1.2 里读不出来**（不是报错，而是"文件不存在"式的静默缺失，用户观感最差）。
2. **已迁移会话保留历史代际**（上游：`migrated historical generation retained until an explicit write open publishes it`）→ 若目录里同时存在 `session.jsonl`（v0）与 `session.v3.jsonl`，降级后 0.1.2 会读到 **v0 那份**（内容止于迁移点），0.1.5 读 v3 → **同会话两个分叉**。
3. 因此启动器对降级的策略是**警告 + 一键导出会话备份**，**不做格式转换**（迁移交给 dsh 官方链，见 work-log/51）。

## 二之二、已知分歧与未决项

| 项 | 现状 | 影响 |
|---|---|---|
| 多代际会话目录在**对话页**列多行 | `ConversationService.List` 按文件列（同目录 v2+v3 → 2 行）；跨实例同步服务已按"会话目录归并、取最高代际" | 从 0.1.2 升级上来的实例会看到重复会话行；纯 v3 实例不受影响。见 work-log/53 F1（待决定） |
| 启动器对会话头的校验比 dsh **宽松** | dsh 在 v3 要求 `isSeeded`；启动器缺失也接受（保证旧格式与半成品文件可读） | 有意为之：宽松读、按头部版本命名写，宁可多读不误判"损坏" |

### 二之三、已确认的上游行为事实（实现选择的依据）

- **`dsh --dump-config` 会写盘**：在空 `DSH_HOME` 里执行 `dsh --profile web --dump-config`（"不启动"），
  仍会生成 `profiles/web/{cordis.yml, cordis.patch.yml, package.json, pnpm-workspace.yaml}`（work-log/74 实测）。
  → 因此**安全体检一律直接读文件**（profile 层 + `settings.yaml` + 运行时出厂层），
  绝不调用 CLI 取配置；若上游哪天改成不写盘，可再评估是否简化为调 CLI。

## 三、哨兵怎么跑、失败了怎么办

```powershell
# 在 _verify-p0 下（先停 Launcher，避免共用 launcher.log 干扰 diagnose 用例）
dotnet bin\Release\net8.0-windows\win-x64\VerifyP0.dll --ui
```

- 输出里 `contract:` 开头的 PASS/FAIL 即哨兵；上游克隆缺失时整段 SKIP（不会误报失败）。
- **先看前置**：若 `contract: 已安装运行时包根可解析（C1/C2/C6 探针前置）` FAIL，说明探针没找到装机运行时（路径变了/未安装）→
  先修路径或重装；此时 C1/C2/C6 的 FAIL **不代表契约破坏**（2026-09-13 ~ 09-15 就因写死 `0.1.5-rc.1` 而假红，见 work-log/156）。
- **FAIL 的处理流程**：先看该条对应的 C# 编号 → 在上游找到新定义 → 判断是"启动器要跟改"还是"记录新版本" → 改代码/常量 → 更新本表"最近核对" → 重跑。
- 哨兵只覆盖**可从源码静态核对**的契约；`C12`/`C13` 与真实启动行为仍靠端到端用例（`--ui` 的 UI 段 + `func-check/` 探针）。

---

## 整合包格式（#24，work-log/70 §E；规范来源 `repo-review-dsh-plugins/docs/PACK_MANIFEST.md`）

### 容器

| 容器 | 结构 | 我方 |
|---|---|---|
| `.dspack`（pack-structure **v2**） | ZIP：根 `dspack.json` = `{"format":"dspack","version":2}` + `manifest.json` + 可选 `package.json`/`pnpm-workspace.yaml`/`pnpm-lock.yaml` + `overrides/` | **读写**（导出用 v2） |
| `.dspack` **v3** | 同上，`dspack.json.version = 3`；**manifestVersion 5 必须配 v3** | **读**（配对校验） |
| 旧 `.tgz`（pack-structure v1） | gzip+tar，扁平 `manifest.json` / `package.json` / `cordis.patch.yml` / 可选 lock/workspace | **读** |

### manifest 兼容矩阵

| 版本 | 关键差异 | 我方 |
|---|---|---|
| **v2** | `displayName`/`description` 仅字符串；`dshVersion` 是**范围**（导入取**下限**）；`dependencies` 为 pnpm 原始 spec **原样透传** | 读 |
| **v3** | 依赖坐标钉死（npm 精确版本 / git commit sha）；`dshVersion` 精确；内联 `patch` | 读 |
| **v4** | 新增 `type`（`profile`；`collection` 报"暂未支持"）与 `files[]`（`{path,sha256,size,urls[]}`，与 `overrides/` 同路径时 **files[] 胜**） | **读 + 写（导出用 v4）** |
| **v5** | `type: profile`（可带 `home/` 覆盖 `$DSH_HOME/xxx`）与 `type: dshhome`（整个 HOME 快照：`defaultProfile` + `profiles`（≥1，**不得含 web/headless**）+ 可选 `presets`/`skills`/`instructions`）；仅支持**新建实例** | 读 |

### 依赖坐标三条转换规则

| manifest 坐标 | package.json 条目 |
|---|---|
| `dsh-pet: "0.2.0"` | `"dsh-pet": "0.2.0"` |
| `github:owner/repo: "<sha>"` | `"repo": "github:owner/repo#<sha>"` |
| `github:owner/repo#path:/pkg: "<sha>"` | `"pkg": "github:owner/repo#<sha>&path:pkg"` |

### 我方实现与安全边界

| 关注点 | 实现 |
|---|---|
| 读取 | `Services/DshPackArchive.cs`：条目数 4096 / 单条 32 MB / 总 256 MB；**任一条目路径非法（zip-slip）即整体拒载** |
| 解析 | `Services/DshPackFormat.cs`：容器标记、manifest v2–v5、`files[]` 校验（64 位小写 sha256、正整数 size、http(s) urls）、路径安全、配对校验 |
| 导入 | `Services/DshPackImportService.cs`：**一律新建实例**（HOME 强制在 Launcher 的 `instances/` 下）；`package.json` 由 manifest **权威重建**；失败**整体回滚**（注销实例 + 删 HOME + 清空目录） |
| `files[]` 下载 | `Services/DshPackFileDownloader.cs`：**默认拒绝**（须显式同意）；按 `urls[]` 试镜像；**sha256 + size 双校验通过才落位**；单文件上限 64 MB |
| 导出 | `Services/DshPackWriter.cs`（规范写盘）+ `Services/PackExportService.cs`（从实例 profile 组装）；自家 v1 导出保留为旧格式 |
| 未做 | `pnpm install` 与按 `dshVersion` 自动安装（版本不符时**明确拒绝**，见 work-log/70 §G）；真实第三方样本包验证（仍缺） |
