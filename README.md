# jellyfin-forge

A personal collection of independently deployable Jellyfin plugins.

## Plugins

- **Danmuku** — 弹幕导入、标准化、存储、绑定、有限集合筛选、管理页面、业务 REST API 和播放器扩展的独立插件。
- **AgentBridge** — Jellyfin 与外部 Agent / Otter 之间的稳定适配层，预留媒体库、受控 metadata/tag 写入、事件及播放状态接口；不负责 AI 推理。

Danmuku M1 已实现 XML/JSON 导入、独立 SQLite 存储、文件与媒体绑定管理、有限播放集合和 Web Canvas 展示；阶段验证与人工验收状态见 [M1 验证记录](docs/SpecAndPlan/M1-Danmuku-Validation.md)。M2 已加入合并方案、片段时间映射和滚动速度设置，当前仍在验证中，尚未完成正式验收，见 [M2 验证记录](docs/SpecAndPlan/M2-Danmuku-Validation.md)。AgentBridge 当前仍为独立工程骨架、配置页和管理员 health API。

两个插件分别拥有 GUID、配置、版本和发布包，可以独立安装、升级、卸载和运行；不互相引用或调用，不共享数据库、配置或生命周期，不建立 Core Plugin。未来 Otter 如需弹幕能力，应分别调用两个插件。

Jellyfin Server 不属于本仓库，不维护 Server fork，当前也不维护 jellyfin-web fork。Otter 不属于本仓库；AI、推荐、语义搜索和 HA 编排属于 Otter。

## 技术基线

- 开发脚本使用 Bash；Docker 用于标准化开发和集成测试。正式 Jellyfin 实例可以独立部署在其他主机。
- Jellyfin Server **12.1**；`Jellyfin.Controller` / `Jellyfin.Model` **12.1.0**；`net10.0`。
- .NET SDK **10.0.100+**（`global.json` 允许同主次版本 feature band 向前滚动）。
- 构建和测试工具：.NET SDK、Bash、Python 3；容器集成测试另需 Docker 和 Docker Compose。
- NuGet 使用提交的 lock file；有意升级依赖时执行 `dotnet restore jellyfin-forge.sln --force-evaluate` 并审查 lock diff。
- 仅验证当前目标版本，不承诺 Jellyfin 10.11 / .NET 9 兼容性。版本选择依据见 [初始化报告](docs/bootstrap-report.md)。

## 开发

```bash
./build/build.sh                    # 完整 solution
./build/build.sh danmuku            # 仅 Danmuku
./build/build.sh agentbridge        # 仅 AgentBridge
./build/test.sh                     # 全部测试；也支持单插件参数
./build/package.sh danmuku
./build/package.sh agentbridge
```

发布文件：`artifacts/jellyfin-plugin-danmuku-0.0.2.zip`、`artifacts/jellyfin-plugin-agentbridge-0.1.0.zip`，各带 SHA-256 校验文件。ZIP 包含自身 DLL、`meta.json` 和 LICENSE；Danmuku 另含独立 SQLite 依赖、RID 分层原生资产及依赖清单。不打包 Jellyfin 或 .NET 运行时。

版本分别定义在各插件 `.csproj` 的 `Version` 与 `AssemblyVersion`（例如 `0.1.0` / `0.1.0.0`），独立递增；GUID 发布后保持稳定。共享 MSBuild 文件只包含编译设置，不包含统一插件版本。

## 验证与文档

GitHub Actions 对纯文档 push／PR 跳过测试；开发分支代码 push 执行两插件构建、单测和打包，代码 PR 与 `main` 代码 push 另执行三组加载 smoke、Danmuku HTTP/部署及双浏览器双路径回归。下游任务复用同次运行的构建包；同一事件下的同分支／PR 新运行取消旧运行。手动入口可选择常规验证、性能长测或全部。具体触发规则、产物和覆盖限制见 [CI 策略](docs/ci.md)。推送 `danmuku-vX.Y.Z` 标签时，完整常规验证通过后自动生成 Danmuku Release 草稿，由维护者确认后手动发布；AgentBridge 暂不发布。不会自动更新 Jellyfin 插件源或部署服务，操作步骤见 [CI 策略](docs/ci.md#danmuku-release-草稿)。

- [开发与集成测试](deploy/docker/dev/README.md)：仓库标准测试环境、脚本用法和隔离数据约定。
- [部署说明](deploy/README.md)：向独立 Jellyfin 实例安装、升级和卸载插件。
- [架构边界](docs/architecture.md)：插件职责和独立性约束。
- [初始化验证报告](docs/bootstrap-report.md)：已执行验证及覆盖范围。
- [M1 弹幕验证记录](docs/SpecAndPlan/M1-Danmuku-Validation.md)：各实施阶段实际结果与未覆盖范围。
- 设计与实施计划位于 `docs/SpecAndPlan/`，按 Milestone 管理。

## 部署与边界

插件发布包安装到匹配技术基线的 Jellyfin 实例，开发环境与正式运行环境相互独立。部署路径、服务账号、媒体位置和网络入口由目标环境决定，不要求在开发主机运行 Jellyfin，也不要求目标主机克隆本仓库。

Docker 与 Synology Container Manager 属于计划支持的部署方式；Synology 原生 SPK 尚未实机验证，详见 [Synology 部署说明](deploy/synology/README.md)。本项目不构建 SPK、不打包 Jellyfin Server，也不维护自定义 Server 镜像。插件代码不依赖固定宿主路径、容器名或特定 NAS API。


### 安装与升级

1. 确认目标 Jellyfin 符合上述技术基线，备份其持久配置、插件配置及 Danmuku 业务数据。获取对应插件的 ZIP 和 `.sha256` 文件，在产物目录执行 `sha256sum -c <校验文件名>`。
2. 停止目标 Jellyfin 服务或容器，确认它实际使用的插件目录。Docker 部署根据 `/config` 的持久挂载定位宿主目录；其他部署根据 Jellyfin 配置与日志确认，不能直接套用开发路径。
3. 将 ZIP **全部内容**解压到插件目录下独立的 `Danmuku/` 或 `AgentBridge/`，使 DLL 与 `meta.json` 位于该子目录顶层。升级时替换该插件的整个程序子目录，避免遗留旧依赖；保留插件配置和 `<DataPath>/Danmuku/` 业务数据，勿覆盖另一个插件。
4. 确认服务账号具有所需读写权限，启动 Jellyfin，在日志及控制台插件页确认加载。使用管理员身份检查 `/Danmuku/Health` 或 `/AgentBridge/Health`；实例使用 Base URL 时加上相应前缀。

Danmuku 0.0.2 使用 schema v5。升级前保留配套程序与数据备份；回退需停止服务并恢复匹配的旧程序、配置和数据库，不能只降级 DLL。

### 启用 Danmuku Web 播放器

安装插件后，管理 API 和配置页即可加载；Web 播放器还需配置入口。针对目标 Jellyfin 镜像生成并校验入口（以下占位符须替换）：

```bash
python3 build/web-entry.py prepare --image '<目标镜像>' --web-path '<镜像内 Web 目录>' --base-url '/' --output '<入口产物目录>'
python3 build/web-entry.py verify --image '<目标镜像>' --output '<入口产物目录>'
```

将 `<入口产物目录>/current/index.html` 只读挂载到 `<镜像内 Web 目录>/index.html`，按目标部署方式重建容器，再在 Danmuku 配置页启用 Web 功能并刷新浏览器。使用子路径时，将 `--base-url` 改为实际值。镜像升级后重新生成与校验入口，回退时同步恢复镜像及对应入口；不要直接修改容器内文件。完整挂载、移除和其他部署注意事项见 [部署说明](deploy/README.md)。

### 卸载与开发环境

卸载时先停止服务，只移除所选插件的程序子目录，再启动服务；默认保留配置与业务数据。卸载 Danmuku 时还需按部署说明恢复 Web 入口或移除对应挂载。

本仓库开发实例使用 `http://127.0.0.1:18096`，只接入隔离测试数据，媒体只读挂载。开发安装入口为 `./build/install-dev.sh danmuku` 或 `./build/install-dev.sh agentbridge`，会构建、安装并重建开发容器；这些脚本不能用于正式实例。持久目录、Web 入口和测试操作见 [开发环境说明](deploy/docker/dev/README.md)。
