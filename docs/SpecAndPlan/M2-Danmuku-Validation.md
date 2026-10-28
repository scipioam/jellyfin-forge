# M2 实施与验证记录

## 2026-10-27 媒体识别、普通视频绑定及 12.4 升级（自动化完成，人工待验收）

用户明确授权实施设计第八章及计划第九节，并要求完成后再提高基线到 Jellyfin 12.4。本轮先在 12.1 完成 MB1–MB6，再完成 12.4 升级和兼容验证；MB7 及原 P9 仍待用户人工验收。保留本轮开始前尚未提交的 M2 工作，未提交、推送或正式发布。下方 10 月 15 日及 9 月结果是历史记录，不能作为本轮新包或 12.4 的验证结论。

### 实现与基线

- 媒体绑定列表、搜索、选择器、详情、异常项及方案列表统一显示标题和库内位置。单集显示剧名及已有季集编号（含第 0 季），电影显示已有年份，普通视频按名称／文件名／ID 回退；长位置可通过键盘展开，多根按配置顺序标记，不输出宿主绝对根路径。管理 API 保留原小写开头字段，增量添加展示字段。
- 统一接受电影、剧集单集和媒体库普通独立 Video；MusicVideo、Trailer、带附加类型或归属关系的条目、直播频道和节目等仍被排除。普通 Video 支持导入、绑定、替换、启停、解绑、合并方案及播放。已有不再符合范围的引用仍可显式停用、解绑和删除方案，保留来源文件。
- 当前为 .NET 10／Jellyfin Server 12.4、Controller/Model 12.4.0，两插件及测试项目固定依赖和锁文件、开发／部署镜像、CI 镜像与包 `targetAbi=12.4.0.0` 已同步。开发 Web 入口从 12.4 镜像重新生成并验证加载；升级不只是重建入口。本轮未发现需要额外修改插件业务 API 的 12.4 不兼容点。
- Danmuku `0.0.2`、AgentBridge `0.1.0`、独立 GUID 和配置边界保持；schema v5、资源 `m2-v2`、渲染契约 `m2-speed-v1` 不变。本轮未改动原渲染、抽样、缓存算法；AgentBridge 仅更新依赖并重新构建。
- 资源限制仅适用于本次机器，使用可选 `build/limited-run.py` 串行运行重任务和可选 `FORGE_TEST_CPUS`／`FORGE_TEST_MEMORY` 容器参数；其他开发机器不强制继承。本轮没有因资源压力中止，未修改宿主 Docker 配置或影响其他项目服务。实际资源记录保存在忽略的产物目录。

### 两阶段实际检查

| 检查 | 12.1 实施阶段 | 12.4 升级阶段 |
| --- | --- | --- |
| Danmuku 构建、单元、打包 | 211 项完整回归＋1 项补充播放控制器测试通过，4 项外部样本跳过 | 212 项通过，4 项外部样本跳过；构建打包通过 |
| AgentBridge 构建、单元、独立打包 | 4 项通过，构建打包通过 | 4 项通过，构建打包通过 |
| Node 布局／引导 | 14 项通过 | 14 项通过 |
| 构建／集成工具 | 19／21 项通过 | 19／21 项通过 |
| M1、M2 根路径及 `/jellyfin` | HTTP、隔离部署、Chromium／Firefox 均通过 | HTTP、隔离部署、Chromium／Firefox 均通过 |
| 加载 smoke | Danmuku 独立、AgentBridge 独立、共存三组通过 | 同三组通过 |
| 人工工具生命周期自动化 | 新夹具 prepare／resume／中断恢复／端口冲突／finish 幂等及原状态恢复通过 | 同流程通过 |
| 数据保留部署与隔离恢复 | 已测包安装、前后核对、页面及恢复检查通过 | 12.1→12.4 升级、已测包安装、前后核对、页面及恢复检查通过 |
| 用户人工验收 | 未执行新增人工项 | MB7、原 P9 未通过，不以自动化替代 |

命令、退出码、耗时及消费包摘要分别见 `artifacts/mb/12.1/`、`artifacts/mb/12.4/` 的 `checks.json`、`extra-checks.json` 和对应日志；最终汇总为 `artifacts/mb/completion.json`。每阶段的 M1／M2、smoke、人工工具及部署消费同一已测包，不以相同版本号代替哈希核对。源码清单见各阶段 `source-manifest.json`；12.4 文档收尾前的清单另保留为 `source-manifest-validation.json`。

| 隔离运行 | 12.1 证据目录 | 12.4 证据目录 |
| --- | --- | --- |
| M1 根路径 | `artifacts/m1/integration/jellyfin-forge-m1-3558087cff/` | `artifacts/m1/integration/jellyfin-forge-m1-920556a9a0/` |
| M1 子路径 | `artifacts/m1/integration/jellyfin-forge-m1-796976c594/` | `artifacts/m1/integration/jellyfin-forge-m1-6eeec2881e/` |
| M2 根路径 | `artifacts/m2/integration/jellyfin-forge-m2-740214e53f/` | `artifacts/m2/integration/jellyfin-forge-m2-e1fe090339/` |
| M2 子路径 | `artifacts/m2/integration/jellyfin-forge-m2-8c533deef4/` | `artifacts/m2/integration/jellyfin-forge-m2-58e33d9da7/` |

浏览器使用锁定 Playwright 1.58.2（Browser plugin 不可用）、Chromium 145.0.7632.6／Firefox 146.0.1 headless，覆盖 1440×900、390×844、原生明暗主题和键盘操作。两路径两引擎新增媒体流程及原 M1／M2 回归通过；普通 Video 的单文件和方案均实际加载并渲染弹幕。截图包括 M2 目录下的 `*-mb-episodes.png`、`*-mb-roots.png` 和 `*-mb-light-390.png`。

### MB 覆盖与修正

| 计划项 | 本轮证据及覆盖边界 |
| --- | --- |
| MB-A01／02／03 | `MediaPresentationTests.cs` 覆盖精确类型、标题回退、路径边界、多根多库、查询失败和请求内缓存；真实扫描核对 Movie／Episode／Video／MusicVideo／附加项。直播实体和难扫描的排除类型由 SDK 实体与实际控制器测试覆盖，未启动直播服务 |
| MB-A04／08 | 真实 HTTP 保留小写开头 JSON 字段、搜索与分页总数；54 个分页样本、同名 A/B、HTML 字符、长位置、窄屏按钮及迟到响应由两路径两引擎验证 |
| MB-A05／06／07 | `m2_media.py`、`m2-media.cjs` 与契约测试覆盖普通 Video 生命周期、直接请求拒绝、权限、失效清理及来源保护；原事务／竞争回归仍运行 |
| MB-A09／10 | M1／M2 完整功能入口、三组 smoke、两插件独立包、人工工具演练、数据保留及隔离恢复；旧人工会话只读核对与恢复保护不等于用户人工验收 |

实际核对发现 Jellyfin 的 `VirtualFolderInfo.Locations` 返回排序后的根目录，展示改读 `LibraryOptions.PathInfos` 的配置顺序，并以逆序双根真实样本验证。展示 DTO 显式保持原 JSON 字段大小写；窄屏表格修正列宽并验证操作按钮完整可见。测试来源选择显式筛选既有样本，Firefox 流式响应改取同一固定播放快照验证内容，未降低服务端断言。

12.4 首轮 M2 子路径在原生登录后的通用 `networkidle` 等待超时，改为等待已认证 ApiClient 和插件就绪后完整重跑通过；M1 首轮控件尺寸检查碰到原生 OSD 自动隐藏，改在鼠标唤出并确认可见后测量，尺寸阈值不变，两路径完整重跑通过。失败日志保留在 `artifacts/mb/12.4/before-login-wait-fix-*`、`before-osd-wait-fix-*`，相应失败隔离目录保留截图；未据此改动播放器算法或跳过检查。

夹具版本 `mb-v1`，生成媒体及样本摘要在每次 M2 目录的 `mb-media/manifest.json`。12.4 根路径代表样本：MKV SHA-256 为 `050536133e9da58b28630b0b5ba3ae72410708d494667cf52bf13ad534cab6d6`，浏览器 MP4 为 `d58c810573b78cd95a562507af902502a322074681a098a20f83cc36192e853f`；均为短 H.264 无音轨合成视频，不代表完整格式兼容矩阵。

另以 Firefox 对原生 MKV 播放做独立复核：视频就绪、单文件弹幕实际渲染通过，但出现 blob worker 的 `ReferenceError: e is not defined`；禁用插件脚本后仍可播放并复现相同错误。记录为原生路径的非阻断控制台异常，不宣称控制台无错误或长时播放达标，证据在 `artifacts/m2/integration/jellyfin-forge-m2-49c404dc82/native-mkv.json` 及两张对照截图。常规 MP4 功能矩阵通过不能代替所有 MKV 编码和转码场景。

### 包、数据保留与恢复

| 基线／插件 | 已测 ZIP SHA-256 |
| --- | --- |
| 12.1 Danmuku 0.0.2 | `8c89cd314625eeab97084c70222af453a5ef772b9f049330ee3381b1d4eab594` |
| 12.1 AgentBridge 0.1.0 | `bc337c1082a496044056cdf21a1a8d633a6d415f549b37c10cabd884847cddc6` |
| 12.4 Danmuku 0.0.2 | `1590ddfa7d97311fd10968cb5feff457e671931dbcee85d05cedf55d1835c1f9` |
| 12.4 AgentBridge 0.1.0 | `9a580ee9e66587d0648460b33763ea2f5fc742958a52eb58906c787136e80c9c` |

两阶段包独立保存在 `artifacts/mb/<基线>/`；默认 `artifacts/` 的两份 ZIP 和校验文件为上述 12.4 已测包的原样副本。12.4 实际服务版本为 `12.4.0`，镜像摘要及本地镜像信息保存在 `artifacts/mb/12.4/image.json`。

部署证据分别为 `artifacts/mb/12.1/deployment-20261027-202756/` 与 `artifacts/mb/12.4/deployment-20261027-212847/`。每次均保留程序、配置、一致性数据库、原文件和 Web 入口备份，安装阶段内已测包，验证两插件 health、实际原生 Web 配置页、媒体标题／位置与资源加载链，然后恢复开发实例原运行状态。Danmuku 1 个文件、1,065 条 Comments、1 个绑定、2 个媒体状态、配置及原文件摘要前后完全一致；该开发样本没有方案／片段或未结束导入任务，非空状态另由隔离集成及 SQLite 契约测试覆盖。schema 仍为 v5，完整性及外键检查通过。

原生账号、权限、播放进度、已有偏好、媒体身份及库／服务配置按已定义字段核对；排除登录／活动时间和行版本，不声称原生整个数据库字节不变。12.4 升级保留全部 18 条已有显示偏好，并新增 1 条当前管理员的默认 `usersettings` 偏好。首次核对仍要求空 ID 而失败；Jellyfin 12.2 已修正新偏好错误使用空 ID 的行为，12.4 使用请求键的 UTF-16LE MD5 Guid。按该精确键、管理员身份及全部默认字段重新核对原升级前备份，未放宽其他记录检查。[官方修复 #18259](https://github.com/jellyfin/jellyfin/pull/18259)、[12.4 控制器](https://raw.githubusercontent.com/jellyfin/jellyfin/v12.4/Jellyfin.Api/Controllers/DisplayPreferencesController.cs)、[键值生成实现](https://raw.githubusercontent.com/jellyfin/jellyfin/v12.4/MediaBrowser.Common/Extensions/BaseExtensions.cs) 支持此核对规则。

原始失败结果保留为 12.4 部署目录中的 `deployment-results-initial.json`；完成只读重新核对及剩余恢复检查后，`deployment-results.json` 记录通过和修订原因，没有重新部署以替换原 12.1→12.4 比较基线。两次恢复检查均使用独立备份副本验证完整性、外键和弹幕数量，从未覆盖活动数据库。回退仍须配套程序、配置、一致性数据库及原文件，不能只换 DLL。

### 未完成项与交接边界

本轮按批准计划未重跑 26 组长时性能矩阵，未运行远端 CI，4 项外部私有样本未启用；ARM64、Synology/SPK、正式 Chrome／Edge 及主观体验未验证。原性能成绩仅保留为历史证据，不计作 12.4 性能通过。

MB7 和原 P9／H01–H11 保持待验收。旧人工会话尚未交回，保留其状态、夹具及已安装程序；新夹具只完成独立目录下的自动化生命周期演练。旧默认 ZIP 在本轮早期打包时已被覆盖，无法恢复原 ZIP；旧会话恢复改为继续使用会话内已安装程序，禁止静默换包或用新包补缺失 DLL。新会话保存独立 ZIP 副本并核对哈希，旧会话恢复保护已做工具回归。按计划 §9.7，收到旧会话交回后再结束旧会话并准备本轮持久人工环境，不将演练结果记为用户验收通过。

## 2026年10月完成至人工验收前

记录日期：2026-10-15。用户已授权继续完成后续工作，止于人工测试开始前。P1–P8 的实现、对应测试、完整本地自动化及数据保留部署已完成；持久人工隔离环境已准备，H01–H11 均未执行，P9 待用户验收。未提交、推送或正式发布。9 月历史结果在文末独立保留，不作为本轮包的通过证明。

保留 .NET 10／Jellyfin Server 12.1、Controller/Model 12.1.0、固定依赖及锁文件。Danmuku 0.0.2、schema v5、渲染契约 `m2-speed-v1`、合并算法 `m2-combine-v1`；Web 缓存标识因性能修复递增为 `m2-v2`。AgentBridge 0.1.0 的业务代码、GUID、配置及部署程序摘要不变。

### 实现与执行范围

- 合并选择对重复来源复用数值扫描，仍按 50 个片段实例展开、统一分配分钟配额并保留重复实例；仅对选中项读取文本，不缓存百万条全文。
- 布局热路径优化通过与原实现 48 组模式／速度／视窗组合的决定、位置和快照差分回归；256 事件／4 ms 分批、2 MiB 索引预算、加载上限、600 同屏及验收阈值未改变。
- 补齐迁移中断、竞争原子性、固定缓存、真实 HTTP 边界、原生主题、迟到保存、失效媒体、混合资源缓存等测试；增加严格性能分组及汇总、CI 路由检查、持久人工工具与生命周期演练。
- 所有重任务使用 `build/limited-run.py` 串行运行：单 CPU 亲和性、降低进程优先级、限制构建并发；可用内存低于 600 MiB 或磁盘低于 2 GiB 时拒绝或中止。容器限制 1 CPU／1 GiB，固定 18096，媒体只读。各轮资源记录位于 `artifacts/m2/completion/*resources*.json`，未修改宿主 Docker 配置或停止其他项目。

### 构建与自动化结果

| 检查 | 本轮实际结果 | 证据（相对仓库根目录） |
| --- | --- | --- |
| Danmuku 构建、测试、打包 | 203 核心测试及 4 外部样本测试通过，共 207 项；最终构建及打包通过 | `artifacts/m2/completion/unit-final/`、`artifacts/m2/completion/private-samples/`、`artifacts/m2/completion/package-final.log` |
| AgentBridge | 构建、4 项测试、独立打包通过 | `artifacts/m2/completion/agentbridge-*.log` |
| Node 布局／引导 | 14 项通过，包含 48 组差分比较 | `artifacts/m2/completion/layout-final.log` |
| 构建／集成工具 | 19 项构建工具、16 项集成工具通过 | `artifacts/m2/completion/build-tools-final.log`、`artifacts/m2/completion/integration-tools-final.log` |
| M1 两路径回归 | HTTP、隔离部署、Chromium／Firefox 全通过 | `artifacts/m1/integration/jellyfin-forge-m1-2221def76e/`、`artifacts/m1/integration/jellyfin-forge-m1-e2707282e5/` |
| M2 两路径功能 | HTTP、Chromium／Firefox 全通过；包括原生明暗主题、51 来源分页、失效媒体 UI、全部混合缓存组合 | `artifacts/m2/integration/jellyfin-forge-m2-1efc9911ac/`、`artifacts/m2/integration/jellyfin-forge-m2-745350ec06/` |
| 三组 smoke | Danmuku 独立、AgentBridge 独立、共存全部通过 | `artifacts/m2/completion/smoke.log` |
| 迁移、中断续迁与配套回退 | 真实旧程序及旧数据隔离演练通过 | `artifacts/m2/migration/jellyfin-forge-m2-79d9418d11/migration-results.json` |
| 人工工具生命周期 | 完整演练通过；媒体扫描修复后再次完整回归通过 | `artifacts/m2/completion/manual-lifecycle-results.json`、`artifacts/m2/completion/manual-lifecycle-final.log` |
| 完整性能 | 26/26 组、32 个配置全部通过，无缺组或混包 | `artifacts/m2/completion/performance-summary.json`、`artifacts/m2/completion/performance-final/` |
| CI | Actionlint 1.7.7 及本地工作流／驱动检查通过；远端未执行 | 构建／集成工具日志与 `.github/workflows/ci.yml` |
| P8 数据保留部署 | 最终已测包部署及前后摘要、页面加载链、数据库一致性通过 | `artifacts/m2/deployment/20261015-091913/deployment-results.json` |
| P9 人工验收 | H01–H11 未执行；夹具准备不计作人工通过 | 按实施计划 §7 执行并记录实际结果 |

浏览器自动化因 Browser plugin 不可用，使用仓库锁定 Playwright 1.58.2 的 Chromium／Firefox headless；功能覆盖根路径及 `/jellyfin`、1440×900 和 390×844。性能只在 Chromium 测量，不将 Firefox 功能通过解释为其性能达标。

### A01–A18 追溯

以下均对应实施计划 §5.1；测试文件列出断言位置，上表中的运行产物才是本轮执行证据。

| 编号 | 实际覆盖与结果 | 主要断言／证据 |
| --- | --- | --- |
| A01 | 新库、v4 任务及生效矩阵、失败回滚、真实中断续迁和旧程序拒绝 v5 通过 | `CombineMigrationContractTests.cs`；迁移结果 JSON |
| A02 | 方案／行／候选数量上下界、Unicode／裁剪／重名／并发占名、空范围通过 | `CombinePlanContractTests.cs`、`CombineAcceptanceContractTests.cs`、`m2_http.py` |
| A03 | 毫秒区间、到末尾、映射、稳定排序、安全整数及溢出边界通过 | 方案及播放契约测试、M2 HTTP |
| A04 | 重复实例保留、完全重复行拒绝、全局额度、已知／未知／超时长通过 | 方案及播放契约测试、M2 HTTP |
| A05 | 保存／启用／切换／停用、preserve/set、文件引用保护、旧请求冲突及旁路数据保留通过 | 方案／竞争契约测试、M2 HTTP 与管理页浏览器 |
| A06 | 仅方案的失效媒体发现、Missing/CheckFailed、显式清理保留文件通过 | 方案契约测试；M2 HTTP；两引擎实际失效媒体 UI |
| A07 | 方案／媒体版本竞争、取消排队保存、删除来源竞争、冲突不部分提交通过 | `CombineAcceptanceContractTests.cs`、方案契约测试、M2 HTTP |
| A08 | 实际待发布追加／替换冲突、显式 Resume、选择及来源保留通过 | 方案契约测试、`m2_http.py`，M1 发布回归 |
| A09 | 首次失败可重试、成功集合固定、修改删除隔离、过期／重启／缺缓存及身份检查通过 | `CombinePlaybackContractTests.cs`、M1/M2 HTTP |
| A10 | 四种速度、碰撞／回看、seek、代次取消、原生播放操作、偏好非法回退通过 | Node 14 项、M2 速度浏览器模块 |
| A11 | 四区、名称、行操作、51 来源分页、撤销失效、未保存／保存失败及主题焦点通过 | `m2-admin*.cjs`；两路径两引擎 JSON 与截图 |
| A12 | 两真实媒体迟到预览和已提交保存响应、归属保持、统计失败重试通过 | `m2-admin-acceptance.cjs`；M2 浏览器证据 |
| A13 | 管理权限、媒体访问、跨媒体 ID、伪造版本、413 与来源私有字段保护通过 | M1/M2 真实 HTTP |
| A14 | 两路径新旧资源六类混合、普通刷新恢复及单次初始化，字幕／退出／禁用回归通过 | `m2-cache.cjs`、M1/M2 两引擎 |
| A15 | 26 完整组、百万稀疏分钟、清理及负向测量校验通过 | 性能汇总、`sparse.json`、Node 与集成工具日志 |
| A16 | 两插件构建测试打包、三组加载、开发数据保留和隔离恢复通过 | 包／smoke 日志、P8 及迁移 JSON |
| A17 | 本地事件路由、同包复用、故意失败传播、证据白名单、缺组／失败／混包拒绝通过 | 构建／集成工具日志；远端运行与上传未验证 |
| A18 | prepare 持久化、重复调用、中断后 resume、异项目端口拒绝、finish 幂等及原状态恢复通过 | 人工工具完整生命周期 JSON；前后开发数据摘要一致 |

测试文件位于 `tests/Jellyfin.Plugin.Danmuku.Tests/`、`tests/integration/` 或 `tests/browser/`。迁移演练仅在隔离旧数据副本将历史终态任务保留时间调整后建立基线，避免七天清理影响比对；源备份未修改。保留原 1,065 条弹幕、文件、绑定、任务及原文件哈希；未终结任务的完整状态矩阵另由真实 SQLite 契约测试覆盖。

### 性能证据与输入

26 组包含 24 个 M2 组（两路径×两视窗×默认／最大×速度 0.5／1／2）与两个 M1 单文件组，每个 M1 组含四个配置，共 32 个配置。每配置 20 个新播放 ID 的热加载、20 次设置、20 次 seek、20 次进出清理，以及关闭／开启弹幕各连续十分钟；成功成绩合计至少 640 分钟掉帧采样。P95 使用 20 个值排序后的第 19 个。

| 指标 | 全部配置的最差结果 | 阈值 |
| --- | ---: | ---: |
| 热加载 P95 | 1679.1 ms | ≤2000 ms |
| 设置 P95 | 86.9 ms | ≤200 ms |
| seek P95 | 108.9 ms | ≤200 ms |
| 开启相对关闭掉帧增量 | 0.284 个百分点 | ≤1 个百分点 |

最大档均达到 600 活跃且 ≥570 活跃连续不少于 30 秒。每组保留完整采样、峰值内存、索引规模、清理计数及后端选择诊断。M2 每次集合为 1,000,000 候选、两次共 2,000,000 逻辑扫描；输入为同一 20,000 条文件的 50 个片段实例，不声称测试了 50 个不同来源文件的性能。

默认档在十分钟内持续到达；最大档最终统一为 `m2-pressure-v3`：19,920 条源条目按 4 秒间隔分成 15 批，另有 80 条长视频尾段条目；展开后分别抽样 16,000／4,000 条。逐批确认选中数 ≥600，总密集量为 16,000。旧两版最大档成绩均不纳入最终汇总；输入未变的 6 个默认档复用同包完整证据。

| 性能合成源样本 | SHA-256 |
| --- | --- |
| m2-default-v1 | `096257d8ef195ef4c8910443b6c00dedaea7edf7f91964347efa121d24b9eb48` |
| m2-pressure-v3 | `7154b804c1cebd5e12e9b16afc5f18527d45d9a304c856ff6ca7d140be00de61` |

独立百万稀疏分钟样本选中 20,000 条、扫描 2,000,000 条，耗时 1117.7 ms，托管峰值 162.0 MiB，取消响应 31.3 ms；不等同于容器总 RSS 或浏览器峰值。完整组证据索引为 `artifacts/m2/completion/performance-final/<group>/evidence-path.txt`，失败及预检目录不混入成功汇总。

### 已测包与 P8

- ZIP SHA-256：`b557914e2c9c014bf9c9dfcf04c0db05d42760f4f54fa9324601e6ee8812e5eb`。
- DLL SHA-256：`472941526ca8891fb93162c50cee7237fc54ec9fcc86232cc7f938146e459b38`。
- 71 个运行时源文件清单摘要：`2103d6877a46319f71f3ed02fc2478f5f2c81080b84eefd79bbf8f43cd7f0c7e`，见 `artifacts/m2/completion/source-manifest.json`。性能及后续部署未重建或替换包。
- P8 通过证据与该次备份：`artifacts/m2/deployment/20261015-091913/`。本轮首次安装前的原程序、配置、数据及入口备份位于 `artifacts/m2/deployment/20261015-091616/` 的 `program-before/`、`config-before/`、`entry-before/`；后一次备份已包含本轮已测程序。Danmuku 原文件、1,065 条弹幕、单文件绑定、媒体状态与配置前后相同；原生账号、已有偏好、媒体目录和 AgentBridge 程序保留，health、schema v5、外键和原生 Web 加载链检查通过。
- 摘要排除可过期播放请求缓存、原生登录／活动时间及行版本；原生媒体目录比较 ID、路径、名称、类型与父节点。备份仍保留完整配置与一致性数据库。
- 首轮 P8 因原生显示偏好表总摘要增加一行而失败。逐行确认全部旧记录未变，新增仅为当前管理员、空媒体 ID、默认排序的全局显示记录；校验改为保留所有旧行，并仅接受这一明确默认形态，重新部署通过。第二次在浏览器打开前后分别取样，确认新增发生在原生页面核验期间。两次共新增 2 条默认记录，原有 15 条逐条保留；首轮失败证据保留在首次备份目录及 `artifacts/m2/completion/deploy-attempt1.log`。
- 实际开发数据在本轮之前已是 schema v5；本次安装最终包及核对保留，不能描述成该实例本轮重新由 v4 升级。v4→v5 与配套旧程序恢复在隔离副本实测。
- 回退须协调旧程序、配置、一致性数据库和原文件；不能只换旧 DLL。若已有升级后新写入，先保留并核对差异，不能直接覆盖。

### 失败、修正与覆盖限制

完整性能第 9 组在创建媒体库后重复刷新，导致扫描取消并初始化超时；测试夹具改为关闭添加时自动扫描、等待空闲后仅刷新一次，补充两项时序回归且对应真实窄屏速度 2 短预检通过，原始失败日志保留为 `full-performance-attempt1.log`。续跑窄屏最大档速度 1 加载／设置／seek 达标，但 ≥570 活跃只连续约 24 秒；离线重放同样出现缺口。旧样本两分钟各抽到 10,000 条，密集段不足以可靠持续饱和。最大档改为密集 19,920 条来源及尾段 80 条，经 50 片段展开后抽样为密集 16,000、尾段 4,000，仍为百万候选、20,000 加载。旧样本 4 个已通过最大档组保留到 `performance-before-pressure-fix/`，从最终汇总移除并全数重跑；默认档不变。新最大档短预检延长至开／关各 75 秒，也严格检查持续 30 秒，完整组仍各 10 分钟。新样本窄屏速度 1／2 短预检已通过（各 75 秒），不代替完整组。第二轮失败日志保留为 `full-performance-attempt2.log`。第二版样本后续桌面速度 1 的连续压力仅约 21 秒，延迟及掉帧达标；已通过的 5 个第二版最大档组归档到 `performance-pressure-v2/`，失败日志为 `full-performance-attempt3.log`。第三版仍为密集 19,920／尾段 80 条来源，将密集段分为 15 批、间隔 4 秒，覆盖被测速度的 4／8／16 秒寿命；先验证抽样后每批 ≥600、共 16,000 条，再做完整长测。压力区间结束后可提前判失败，但不提前判通过或减少成功组时长。第四轮完成全部 M2 与 M1 根路径后，M1 子路径窄屏设置测量前遇到控件挂载时序，已在计时之外补等待；先预检，再完整重跑该组，其他 25 组保留。原始失败日志为 `full-performance-attempt4.log`。百万合并首次加载超时及 seek 超时经优化后短预检通过；人工夹具中断恢复的启动就绪和首次账号初始化时序经修正后完整演练通过；以外部 SQLite 写锁制造迁移中断会先阻塞连接初始化，改为真实备份创建事件中断后通过。窄屏性能驱动曾在设置控件挂载前操作，已补就绪等待并重跑通过；等待位于计时之外，未改阈值。旧的直接加载浅色 CSS 未切换原生主题变量，不能计作浅色验证；已改用 Jellyfin 原生显示设置切换并核对实际颜色。

上述失败均保留原始证据；最后 M1 子路径完整重跑通过，完整性能汇总为 26/26。扫描与控件等待调整仅影响夹具时序，未改变测量起止、成功时长或阈值。人工工具扫描修复后再次完成全生命周期回归。

P9 的 H01–H11 均未执行。正式 Chrome／Edge／Firefox 的实际体验、主观可读性与速度舒适度、ARM64、Synology/SPK 及远端 GitHub Actions 尚未实测。人工步骤见实施计划 §7；测试失败须先修复并通过相关自动化，再人工复测。仓库文档不包含本机账号、密码、token 或绝对开发目录。

## 2026年9月历史验证记录

以下保留当时的范围、结果与限制，包哈希和“当前”均指该历史阶段，不作为本轮新包的通过证明。

### 状态与范围

用户于 2026-09-23 明确授权按 [实施计划](M2-Danmuku-ImplementationPlan.md) 开始实施。P1–P6 主体代码已落地，P6/P7 验收仍未全部完成。**M2 尚未完成，P7/P8/P9 不标记通过。** 用户于 2026-09-24 调整执行顺序：补充 README 部署说明并整体提交后，复用现有开发 Jellyfin 容器准备人工试用。该准备使用已通过功能回归的包并保留升级前备份，不替代完整性能验证、隔离恢复演练或正式人工验收；本文记录截至该提交前的自动化结果。未推送或正式发布。

设计依据为已批准 [M2 设计](M2-Danmuku-Design.md)，批准版本及状态同步后的 SHA-256 见实施计划。保留 .NET 10 / Jellyfin 12.1、固定依赖及锁文件；Danmuku 版本 0.0.2，schema v5，资源 `m2-v1`，渲染契约 `m2-speed-v1`。AgentBridge 业务代码、GUID、版本及配置未修改。

### 已实现内容

- schema v5：方案、片段、媒体生效方案和播放版本快照；迁移前一致性备份、互斥选择、媒体归属和来源外键。
- 方案生命周期：名称、范围、统计和规模限制；保存／启用／删除事务；方案及媒体版本检查；失效媒体可发现并显式清理；文件引用保护。
- 绑定与发布：新 preserve/set 意图，旧格式不能覆盖生效方案；首次追加及替换保留方案，导入继续使用媒体版本冲突恢复。
- 管理接口：管理员方案 API、Selection、References，128 KiB 方案请求体限制及 413 错误。
- 合并播放：两次数值流式扫描、目标分钟统一配额、选中后读取文本、片段实例 ID、固定播放缓存及方案版本。扫描次数随响应记录，不为每个分钟重新扫描全表。
- 滚动速度：0.5–2.0、0.1 步长、默认 1.0，参与布局寿命、位置、碰撞、回看和重建；固定弹幕保持 4 秒。保留旧偏好存储键。
- 管理页面：方案编辑、范围同步／撤销、预览防抖及取消、保存快照、未保存确认、来源搜索分页、引用显示、局部时间表滚动及速度控件窄屏调整。
- 新增 M2 HTTP／浏览器驱动；`build/test-browser.sh --milestone m2` 可运行已实现的 M2 功能子集，默认仍运行 M1。它目前不代表实施计划要求的完整 M2 验收矩阵。

### 已执行检查

| 检查 | 实际结果 | 覆盖限制 |
| --- | --- | --- |
| P1 迁移定向测试 | 17 项通过 | 临时真实 SQLite；容器迁移中断、真实开发数据升级未验证 |
| P2 完整单元回归 | 193 项通过、4 项跳过 | 当时的中间实现 |
| P4 播放定向回归 | 21 项通过 | 含 3 项合并播放测试，非性能测试 |
| 当前后端 `./build/test.sh danmuku` | 196 项通过、4 项跳过、0 失败 | 外部样本未提供／未启用，不计为通过；之后补充的原文件哈希保留与精确 1,000,001 条边界断言经 14 项 Combine 定向测试通过 |
| `./build/build.sh danmuku` | 通过，0 警告、0 错误 | 不代表浏览器或性能通过 |
| `./build/build.sh agentbridge`、`./build/test.sh agentbridge` | 构建通过、4 项测试通过 | AgentBridge 未增加业务功能 |
| 两插件 `./build/package.sh` | 独立构建及打包通过 | 中间包不用于开发数据部署 |
| `node --test tests/browser/danmuku-layout.test.cjs` | 13 项通过 | 四种速度、寿命、回看、碰撞、seek 一致性及原布局回归 |
| 根路径 M1 HTTP、隔离部署、Chromium／Firefox | 通过 | 在 P6 页面改动前运行；不能代替改动后的完整回归 |
| 根路径及 `/jellyfin` M2 HTTP＋Chromium／Firefox | 通过 | 新建／编辑／保存／启用、范围同步撤销、合并播放、固定重试、权限、引用、超大请求、速度控件及双视频迟到预览子集 |
| 三组 smoke | Danmuku、AgentBridge、共存全部通过 | 当前包经 `python3 build/smoke-test.py` 三组复核通过；不等于 P8 部署或数据保留验收 |
| `/jellyfin` 的 P6 后 M1 回归 | HTTP、隔离部署、Chromium／Firefox 全部通过 | 证据见对应目录；最后一处导入恢复文案随后另包回归 |

浏览器路径：Browser plugin not available；使用仓库锁定的 Playwright 1.58.2，Chromium 与 Firefox headless，视窗 1440×900 和 390×844。实际引擎版本、操作完成事件及截图见各浏览器 JSON。测试观察器等待有效提交；视窗缩放稳定后才开始独立速度操作，不把被取消代次计为成功。

当前源码清单摘要（按相对路径及 SHA-256 排序生成）：`dee0eb70390d571f5b9ace6ab72f7fe8b79ef71b44a6a6e1a88afbb6e82f416b`；清单位于 `artifacts/m2/source-manifest.json`。

当前 Danmuku 包 SHA-256：`a42477ed3c4787a20fa0fefcf53c7ac7a5dbd53d74040992356c4d27e97f4dc9`；DLL SHA-256：`6e5c5a9dd01423debd8bfb6c1df99bef3d9a0bfa27e14e5e78909a905dcdd5f7`。这些是功能回归包，不标记为通过全部 P7 的最终交付包。

### 证据位置

全部路径相对仓库根目录，均位于 Git 忽略目录：

- P3 首次修复后 HTTP：`artifacts/m2/integration/jellyfin-forge-m2-07202b1eeb/http-results.json`。
- P6 前根路径 M1：`artifacts/m1/integration/jellyfin-forge-m1-7a2447c1aa/` 下的结果 JSON、日志及截图。
- P6 基础页面根路径：`artifacts/m2/integration/jellyfin-forge-m2-8dc523c71f/`。
- P6 基础页面 `/jellyfin`：`artifacts/m2/integration/jellyfin-forge-m2-e52da64200/`。
- 双视频根路径：`artifacts/m2/integration/jellyfin-forge-m2-8fd435c36d/`；当前包复核：`artifacts/m2/integration/jellyfin-forge-m2-17f6350cf7/`。
- 双视频 `/jellyfin`：`artifacts/m2/integration/jellyfin-forge-m2-d3786621bc/`；当前包复核：`artifacts/m2/integration/jellyfin-forge-m2-68dba5ca62/`。
- P6 后 `/jellyfin` M1：`artifacts/m1/integration/jellyfin-forge-m1-5383c75229/`。

`http-results.json` 记录其实际使用的 ZIP 哈希；`*-m2-browser.json` 记录浏览器结果；`*-m2-editor-*.png`、`*-m2-speed-*.png` 为截图。当前包两路径、两引擎和 smoke 复核通过；P6 前后 M1 历史包结果仍按各自记录区分。凭据及数据库仅在忽略的隔离目录，不提交也不作为可上传证据。

### 失败及修复记录

- 首轮单元失败：旧迁移测试写死 v4、新增迁移失败注入位置无效、集合记录误用引用相等断言。修正后回归通过。
- P3 超大请求最初被统一异常处理转成 500；保留请求体上限并映射 413 后 HTTP 通过。
- 速度控件窄屏标签最初竖排；调整标签不换行及滑块伸缩后，两引擎两视窗通过并生成截图。
- M2 专用 Firefox 测试缺少 locale，原生页面出现 `invalid language tag`；对齐已有测试的 `en-US` 后通过。
- 缩放视窗与调速同时发生导致操作观察器正确拒绝被取消代次；等待缩放提交后分别测量，未将该失败样本计作成功。

- 当前包第一次浏览器复核曾在等待字体加载后截图时超时（`artifacts/m2/integration/jellyfin-forge-m2-1c97e0a900/`）。加入非本站字体阻断及记录后重跑通过；四次成功运行均记录 0 个被阻断字体，因此不能认定外部字体是根因。超时未稳定复现，保留为测试不稳定记录，不计该失败为通过。插件未引入字体或 CDN。

### 尚未完成的验收

以下不能根据目前子集成绩标记通过：

- A01 的容器中断恢复、完整迁移保留矩阵；A02–A09 中尚未补齐的竞争、取消、故障和 HTTP 边界组合。
- A10–A14 的完整偏好、明暗主题、键盘焦点、未保存离开、迟到保存响应、统计失败重试、跨媒体实际保存归属及全部混合缓存组合。双视频迟到**预览**已验证，不能代替迟到**保存**验收。
- A15：24 组 M2 性能、两组 M1 基线、百万稀疏分钟峰值、20 次加载／设置／seek、关闭／开启各 10 分钟掉帧、负向测量及汇总。未开始性能长测，不报告任何性能阈值达标。
- A17：CI 已追加 M2 两路径常规调用、120 分钟超时及独立证据白名单；性能分组、缺组汇总和故意失败传播验证尚未实施，本地 YAML、两路径调用、包复用、上传白名单及 Release 依赖静态检查已通过；远端 CI 未运行。
- A18：`m2-manual.py` 持久人工夹具 prepare/status/resume/finish 尚未实施，也未进行生命周期演练。
- P8：按用户调整的顺序，已完成现有开发实例的停机配置备份、已测 0.0.2 包部署及 schema v4 → v5 迁移核对；原有 1,065 条弹幕、文件、绑定、任务及配置保留，AgentBridge 未变更。证据见 Git 忽略目录 `artifacts/m2/dev-preparation/`。隔离回退演练未执行，P8 不标记全部通过。
- P9：用户确认现有开发实例的人工测试正在进行；尚未收到逐项结果或整体验收通过结论。正式隔离夹具尚未准备，H01–H11 不标记通过，ARM64 运行未验证。

本次开发实例部署及数据核对不等于完整升级验收通过。v5 不能仅替换回旧 DLL；后续 P8 必须使用配套旧程序、配置、迁移前一致性数据库及原文件完成隔离恢复演练，且不得覆盖未经核对的升级后新写入。
