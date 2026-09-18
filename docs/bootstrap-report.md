# 仓库初始化实施报告

实施日期：2026-09-18。原有 Git 仓库、origin 和 MIT LICENSE 保留；不修改其他项目，不创建 Server/Web fork。

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
│   └── synology/README.md
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

## 环境及版本选择

| 项目 | 实际值 |
| --- | --- |
| Ubuntu | 24.04.4 LTS / amd64 |
| .NET SDK | 10.0.112 |
| .NET / ASP.NET Runtime | 10.0.12 |
| Target framework | net10.0 |
| Jellyfin.Controller | 12.1.0 |
| Jellyfin.Model | 12.1.0 |
| 目标 Server | Jellyfin 12.1（运行时 API 返回 12.1.0） |
| Docker / Compose | 29.8.0 / v5.5.1，沿用现有安装 |
| Git | 2.43.0 |
| 官方镜像 | jellyfin/jellyfin:12.1 |
| 本次拉取 digest | sha256:78d3ea1207d1322471fcac39a614f004f2ccf7e878f95ab2977d752f07e4dd7e |

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

## 命令

```bash
./build/build.sh
./build/build.sh danmuku
./build/build.sh agentbridge
./build/test.sh
./build/install-dev.sh danmuku
./build/install-dev.sh agentbridge
./build/package.sh danmuku
./build/package.sh agentbridge
./build/smoke-test.sh

JELLYFIN_FORGE_UID=$(id -u) JELLYFIN_FORGE_GID=$(id -g) \
  docker compose -f deploy/docker/dev/compose.yaml up -d
```

开发入口：<http://127.0.0.1:18096>。两个插件已安装，首次向导尚未完成，未代用户设置日常开发账号。测试账号仅存在隔离 smoke 实例，测试后容器与网络移除。

## 验证结果

- 完整 solution build：通过，0 warnings / 0 errors。
- Danmuku / AgentBridge standalone build：分别通过。
- 单元测试：8/8 通过，包括嵌入配置页身份、XML 配置往返、health 权限契约和程序集无兄弟插件引用。
- 两个 release ZIP：分别生成；开发安装脚本实际执行通过。
- 开发实例：启动成功，日志确认两个插件均 loaded，API 返回 Server 12.1.0。
- 卸载/重新安装 Danmuku：脚本实际执行通过，AgentBridge 安装目录保留。
- CI：已创建独立 build/test/package matrix 和三场景 smoke job；尚未推送，未声称 GitHub 托管运行已通过。

容器 smoke 与浏览器结果见下文。

## 系统修改

依照任务授权及 [Microsoft Ubuntu 安装文档](https://learn.microsoft.com/dotnet/core/install/linux-ubuntu-install)，执行：

```bash
sudo -n apt-get update
sudo -n apt-get install -y dotnet-sdk-10.0
```

直接使用 Ubuntu 24.04 已有仓库，无需新增 PPA 或 Microsoft 源。共新增 13 个包：`dotnet-sdk-10.0`、`dotnet-host-10.0`、`dotnet-hostfxr-10.0`、`dotnet-runtime-10.0`、`dotnet-targeting-pack-10.0`、`dotnet-apphost-pack-10.0`、`dotnet-templates-10.0`、`dotnet-sdk-aot-10.0`、`aspnetcore-runtime-10.0`、`aspnetcore-targeting-pack-10.0`、`liblttng-ust-common1t64`、`liblttng-ust-ctl5t64`、`liblttng-ust1t64`。AOT 包由发行版 SDK 依赖自动带入，未额外请求工作负载。0 个已有包升级或删除。

安装后系统 `needrestart` 自动重启 `tat_agent.service`，报告不需要重启容器。首次运行 .NET 自动创建用户级 HTTPS 开发证书，未执行 trust。NuGet 使用常规用户缓存。未安装 IDE、Mono 或全局 NuGet 工具；未重装/升级/重配 Docker。

浏览器验证工具仅安装于忽略目录 `artifacts/browser/`，使用已有 Chromium，无全局 npm 或系统浏览器安装。除上述授权的 SDK bootstrap 和工具常规缓存外，项目文件及测试数据均在当前仓库内。

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

使用 `build-web-apps:frontend-testing-debugging` 工作流。Browser plugin not available，因此采用仓库忽略目录中的 Playwright Core 1.58.2 与已有 Chromium。仅对隔离测试实例执行登录，未初始化日常 dev 实例。

流程：管理员登录 → `/web/index.html#/configurationpage?name=Danmuku` / `AgentBridge` → 修改 Instance label → 点击 Save → 配置 POST 返回 204 → 刷新后字段保留新值。

| 检查 | 结果 |
| --- | --- |
| 页面身份 | 两个 URL、页面标题区和各自表单正确；浏览器 document title 使用 Jellyfin 实例名 |
| 非空及错误覆盖层 | 正常显示表单，无空白页或框架错误覆盖层 |
| 桌面与窄屏 | 1440×1000 / 390×844，字段与按钮可见，无明显裁剪或重叠 |
| 保存/重新加载 | 两插件分别保存中文标签，刷新后读取到更新值 |
| 页面脚本 | 无 pageerror；配置页加载、提交均成功 |
| 浏览器控制台 | 登录阶段出现 2 次 Jellyfin 自身 `/socket` 的 WebSocket 403；服务端明确记录 `Token is required`。此为未携 token 的宿主连接，配置页不创建 WebSocket；保留记录，不宣称控制台完全无错误 |

截图证据（本地忽略文件，不提交运行截图）：

- `artifacts/browser/Danmuku-desktop.png`
- `artifacts/browser/Danmuku-mobile.png`
- `artifacts/browser/AgentBridge-desktop.png`
- `artifacts/browser/AgentBridge-mobile.png`
- `artifacts/browser/result.json`（含控制台原始错误）

使用 `node artifacts/browser/check.cjs`，由临时 UI smoke runner 注入隔离实例 URL/随机密码并在完成后回收实例。浏览器仅验证 Chromium；未验证其他浏览器、实际播放器或 WebSocket 会话功能。

## 提交前整理

开发实例和隔离 smoke 实例的宿主端口统一固定为 `127.0.0.1:18096`，容器内仍为 8096。历史浏览器验证使用临时端口；后续测试不再随机分配宿主端口。两种实例不能同时占用该端口，运行 smoke 前停止开发实例，测试结束后恢复。

固定端口后的回归：三组 smoke 均在 `18096` 上通过（含重启后的配置持久化），测试容器/网络已清理，日常开发实例已恢复。
