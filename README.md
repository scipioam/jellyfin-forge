# jellyfin-forge

A personal collection of independently deployable Jellyfin plugins.

## Plugins

- **Danmuku** — 弹幕导入、标准化、存储、绑定、时间轴、过滤、管理页面、业务 REST API 和播放器扩展的独立插件。
- **AgentBridge** — Jellyfin 与外部 Agent / Otter 之间的稳定适配层，预留媒体库、受控 metadata/tag 写入、事件及播放状态接口；不负责 AI 推理。

当前仅有可加载的工程骨架、各自的 Dashboard 配置页和管理员 health API，尚未实现上述业务功能。

两个插件分别拥有 GUID、配置、版本和发布包，可以独立安装、升级、卸载和运行；不互相引用或调用，不共享数据库、配置或生命周期，不建立 Core Plugin。未来 Otter 如需弹幕能力，应分别调用两个插件。

Jellyfin Server 不属于本仓库，不维护 Server fork，当前也不维护 jellyfin-web fork。Otter 不属于本仓库；AI、推荐、语义搜索和 HA 编排属于 Otter。

## 技术基线

- Ubuntu 为主要开发环境；Docker 为主要标准化开发、测试和部署模型。
- Jellyfin Server **12.1**；`Jellyfin.Controller` / `Jellyfin.Model` **12.1.0**；`net10.0`。
- .NET SDK **10.0.100+**（`global.json` 允许同主次版本 feature band 向前滚动；初始化实测 10.0.112）。
- Bash、Python 3、Docker 和 Docker Compose。SDK 安装参考 [Microsoft Ubuntu 文档](https://learn.microsoft.com/dotnet/core/install/linux-ubuntu-install)。
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

发布文件：`artifacts/jellyfin-plugin-danmuku-0.1.0.zip`、`artifacts/jellyfin-plugin-agentbridge-0.1.0.zip`，各带 SHA-256 校验文件。ZIP 包含自身 DLL、`meta.json`、LICENSE，不打包 Jellyfin 或 .NET 运行时。新增第三方运行时依赖时须相应更新打包逻辑。

版本分别定义在各插件 `.csproj` 的 `Version` 与 `AssemblyVersion`（例如 `0.1.0` / `0.1.0.0`），独立递增；GUID 发布后保持稳定。共享 MSBuild 文件只包含编译设置，不包含统一插件版本。

## 本地 Jellyfin

```bash
# 首次启动；非 1000 UID/GID 用户也适用
JELLYFIN_FORGE_UID=$(id -u) JELLYFIN_FORGE_GID=$(id -g) \
  docker compose -f deploy/docker/dev/compose.yaml up -d

./build/install-dev.sh danmuku
./build/install-dev.sh agentbridge
./build/restart-dev.sh
./build/uninstall-dev.sh danmuku

# 停止，仅操作本仓库的开发 stack
docker compose -f deploy/docker/dev/compose.yaml down
```

访问 <http://127.0.0.1:18096>，首次运行完成设置向导并创建自己的管理员账号。远程开发可用 SSH 端口转发访问。通过 Dashboard → Plugins → 相应插件进入设置，修改 `Instance label` 并保存。

每个安装/卸载脚本只操作本仓库 dev 实例，更新前停止它、完成后启动它。卸载保留插件 XML 配置。不要混用手动安装的多个插件目录。开发和 smoke 测试的宿主端口统一固定为 `18096`，仅绑定 `127.0.0.1`。

开发配置和缓存位于 `deploy/docker/dev/config/`、`cache/`，均被 Git 忽略。测试媒体放到 `media/`，容器内只读挂载为 `/media`。不挂载生产媒体、不暴露 Docker socket，不修改宿主 Docker 配置。

Health 接口：`GET /Danmuku/Health`、`GET /AgentBridge/Health`。使用 Jellyfin 管理员认证（`X-Emby-Token` 请求头），匿名请求返回 401；响应含 `Plugin`、`Status`、`Version`。不通过 AgentBridge 代理弹幕接口。

```bash
docker compose -f deploy/docker/dev/compose.yaml stop jellyfin
./build/smoke-test.sh
./build/restart-dev.sh
```

该命令分别验证 Danmuku 单独加载、AgentBridge 单独加载、两者共存；检查 API 权限、配置页资源和配置保存/重启持久化。它顺序使用固定端口 `18096`、随机测试密码和独立临时 stack，完成后移除测试容器/网络；日志及结果留在忽略目录 `artifacts/smoke/`，不操作日常开发实例。运行前需先停止占用该端口的开发实例，测试后再启动；端口被占用时测试失败，不自动切换端口。

GitHub Actions 对两个插件分别 build/test/package，并运行 Docker smoke test。CI 上传构建产物，尚不发布 GitHub Release 或 Jellyfin 插件源。

## 部署与边界

Synology DSM 7.4+ 是计划中的主要长期运行平台之一。标准模型是官方 Jellyfin 镜像 + 外部插件产物，可用于 Ubuntu、Synology Container Manager 和其他 Docker 宿主；插件代码不依赖特定 NAS、固定容器名、DSM API 或 Docker 环境变量。

尽量兼容原生 Jellyfin SPK，但当前未经实机验证；必须匹配 Server/.NET 版本。参见 [Synology 部署说明](deploy/synology/README.md)。本项目不构建 SPK、不打包 Server，也不维护自定义 Server 镜像。

目录职责见 [架构说明](docs/architecture.md)，初始化实测结果见 [实施报告](docs/bootstrap-report.md)。
