# 仓库初始化实施报告

报告日期：2026-09-18。本文记录初始化阶段的工程产物和历史验证结果，不描述开发主机的当前状态，也不要求正式部署复用该测试环境。原有 Git 仓库和 MIT LICENSE 保留，不创建 Server/Web fork。

## 实际结构

```text
jellyfin-forge/
├── src/
│   ├── Jellyfin.Plugin.Danmuku/
│   │   ├── Api/ Configuration/ Import/ Model/ Storage/ Timeline/ Filter/ Web/
│   │   ├── Plugin.cs
│   │   ├── Jellyfin.Plugin.Danmuku.csproj
│   │   └── packages.lock.json
│   └── Jellyfin.Plugin.AgentBridge/
│       ├── Api/ Configuration/ Library/ Metadata/ Playback/ Events/
│       ├── Plugin.cs
│       ├── Jellyfin.Plugin.AgentBridge.csproj
│       └── packages.lock.json
├── tests/
│   ├── Jellyfin.Plugin.Danmuku.Tests/
│   └── Jellyfin.Plugin.AgentBridge.Tests/
├── build/                     # build/test/package/install/uninstall/restart/smoke
├── deploy/
│   ├── docker/dev/             # compose.yaml + config/cache/media
│   └── README.md               # 插件部署说明
├── docs/                       # architecture.md + 本报告
├── .github/workflows/ci.yml
├── .editorconfig
├── .gitignore
├── global.json
├── Directory.Build.props
├── jellyfin-forge.sln
├── README.md
└── LICENSE
```

预留业务目录使用 `.gitkeep` 跟踪；运行数据和 `artifacts/` 均忽略。原始初始化指导文档已按要求删除，仓库协作约定记录于根目录 `AGENTS.md`。

## 验证基线与版本选择

| 项目 | 初始化验证基线 |
| --- | --- |
| Target framework | net10.0 |
| Jellyfin.Controller / Jellyfin.Model | 12.1.0 |
| 目标 Server | Jellyfin 12.1（验证时 API 返回 12.1.0） |
| 官方镜像 | jellyfin/jellyfin:12.1 |
| 验证镜像 digest | sha256:78d3ea1207d1322471fcac39a614f004f2ccf7e878f95ab2977d752f07e4dd7e |

构建工具遵循 `global.json`，隔离集成测试使用仓库 Compose 和 smoke 脚本。镜像摘要用于标识该次历史验证对象，不表示其他镜像版本已通过相同验证。

初始化时 [官方 Plugin Template](https://github.com/jellyfin/jellyfin-plugin-template/tree/726279e026ff82e3bebea1bcd8f106412a718952) 仍使用 `net9.0` 和 NuGet `10.11.5`；但 [官方最新稳定版 v12.1](https://github.com/jellyfin/jellyfin/releases/tag/v12.1) 的 [global.json](https://github.com/jellyfin/jellyfin/blob/v12.1/global.json) 要求 .NET 10，[Controller 项目](https://github.com/jellyfin/jellyfin/blob/v12.1/MediaBrowser.Controller/MediaBrowser.Controller.csproj) 目标为 `net10.0`、版本 `12.1.0`。因此跟随当前稳定 Server 的框架/包版本，保留模板的 `BasePlugin<T>`、`IHasWebPages`、嵌入 HTML 配置页及 `ExcludeAssets=runtime` 方式。

未照搬模板面向贡献者的全部第三方代码风格 analyzer；使用 .NET 自带检查、nullable 和 warnings-as-errors，避免为最小骨架增加无关依赖。不引用 Server 源码，官方文件只用于只读核对。

## 插件身份及独立发布

| 插件 | GUID | 初始版本 |
| --- | --- | --- |
| Danmuku | 6f79690c-c1d0-4738-b241-09aaa2c570e7 | 0.1.0 / assembly 0.1.0.0 |
| AgentBridge | 8c8f013d-21b4-467c-8dac-c6446b97c0ea | 0.1.0 / assembly 0.1.0.0 |

两个初始版本恰好相同，但分别定义在各自 `.csproj`，无统一版本属性。配置各自序列化；无插件间 ProjectReference、AssemblyReference、API 调用、数据库或运行时依赖。

产物：

- `artifacts/jellyfin-plugin-danmuku-0.1.0.zip`
- `artifacts/jellyfin-plugin-agentbridge-0.1.0.zip`
- 对应 `.zip.sha256`

ZIP 仅包含对应 DLL、插件清单 `meta.json` 和 LICENSE。未发布 Release 或插件 repository manifest。

## 复现入口

构建、单元测试和打包命令见 [项目说明](../README.md)，隔离实例及 smoke 操作见 [开发与集成测试](../deploy/docker/dev/README.md)。各命令使用仓库相对路径；工具安装和目标服务地址由执行环境提供，不依赖原验证主机。

## 验证结果

- 完整 solution build：通过，0 warnings / 0 errors。
- Danmuku / AgentBridge standalone build：分别通过。
- 单元测试：8/8 通过，包括嵌入配置页身份、XML 配置往返、health 权限契约和程序集无兄弟插件引用。
- 两个 release ZIP：分别生成；开发安装脚本实际执行通过。
- 开发实例：启动成功，日志确认两个插件均 loaded，API 返回 Server 12.1.0。
- 卸载/重新安装 Danmuku：脚本实际执行通过，AgentBridge 安装目录保留。
- CI：已创建独立 build/test/package matrix 和三场景 smoke job；本报告未验证 GitHub 托管运行结果。

容器 smoke 与浏览器结果见下文。

## 验证记录的可迁移性

运行工具、缓存、日志和截图均位于 Git 忽略目录，不作为仓库依赖提交。复现验证时根据执行环境准备匹配基线的 SDK、Docker 和浏览器；宿主包管理操作、系统服务变化和开发账号信息不属于项目设计或部署要求。

以下结果是历史记录，不表示当前开发实例正在运行。`artifacts/` 中的证据路径相对于仓库，仅用于说明生成位置；新检出仓库不包含这些运行产物，应通过重新执行验证生成。

## 尚未覆盖

Synology DSM / 原生 SPK 实机、其他 Jellyfin 版本、真实媒体播放、完整弹幕/Agent 业务功能均不在本次已验证范围。此基线没有播放器注入、XML parser、AI 或 Server/Web patch。

## 容器集成测试

| 场景 | 加载/独立性 | Health | Dashboard 资源 | 配置持久化 |
| --- | --- | --- | --- | --- |
| Danmuku 单独安装 | Active；AgentBridge 不在列表且其路由 404 | 管理员 200，匿名 401 | 注册及 HTML/GUID 正确 | 保存、重启、读取通过 |
| AgentBridge 单独安装 | Active；Danmuku 不在列表且其路由 404 | 管理员 200，匿名 401 | 注册及 HTML/GUID 正确 | 保存、重启、读取通过 |
| 两者共存 | 两个 GUID 均 Active | 两个接口均通过 | 两个独立页面均通过 | 各自标签保存后重启保留 |

证据：`artifacts/smoke/results.json` 及每个测试子目录中的 `result.json` / `server.log`。测试脚本会等待实际 API 就绪，并在重启后重新查询端口映射；不以 `/health` 单独作为应用已完全就绪的依据。

Shell 脚本语法、Python 编译检查、Compose 配置验证均通过。build/test/package/install/uninstall 对非法插件名返回非零（2）。ZIP 内容检查确认不含另一个插件或 Jellyfin 运行时 DLL。

## Dashboard 浏览器验证

初始化阶段采用 Playwright Core 1.58.2 与 Chromium 对隔离测试实例执行浏览器验证。其他浏览器不在该次验证范围内。

流程：管理员登录 → `/web/index.html#/configurationpage?name=Danmuku` / `AgentBridge` → 修改 Instance label → 点击 Save → 配置 POST 返回 204 → 刷新后字段保留新值。

| 检查 | 结果 |
| --- | --- |
| 页面身份 | 两个 URL、页面标题区和各自表单正确；浏览器 document title 使用 Jellyfin 实例名 |
| 非空及错误覆盖层 | 正常显示表单，无空白页或框架错误覆盖层 |
| 桌面与窄屏 | 1440×1000 / 390×844，字段与按钮可见，无明显裁剪或重叠 |
| 保存/重新加载 | 两插件分别保存中文标签，刷新后读取到更新值 |
| 页面脚本 | 无 pageerror；配置页加载、提交均成功 |
| 浏览器控制台 | 登录阶段出现 2 次 Jellyfin 自身 `/socket` 的 WebSocket 403；服务端明确记录 `Token is required`。此为未携 token 的宿主连接，配置页不创建 WebSocket；保留记录，不宣称控制台完全无错误 |

截图及结果的生成位置（Git 忽略，不提交运行产物）：

- `artifacts/browser/Danmuku-desktop.png`
- `artifacts/browser/Danmuku-mobile.png`
- `artifacts/browser/AgentBridge-desktop.png`
- `artifacts/browser/AgentBridge-mobile.png`
- `artifacts/browser/result.json`（含控制台原始错误）

使用 `node artifacts/browser/check.cjs`，由临时 UI smoke runner 注入隔离实例 URL/随机密码并在完成后回收实例。浏览器仅验证 Chromium；未验证其他浏览器、实际播放器或 WebSocket 会话功能。

## 固定端口回归

仓库约定开发与隔离 smoke 使用固定测试入口，操作步骤见 [开发与集成测试](../deploy/docker/dev/README.md)。端口约定只适用于仓库测试环境，不限制正式实例的网络入口。

初始化阶段在固定端口配置下重新执行三组 smoke，均通过，包括重启后的配置持久化。该结论仅覆盖报告中的测试基线和功能，不代表后续变更已经回归。
