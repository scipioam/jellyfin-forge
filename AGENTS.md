# 仓库约定

- 本仓库是独立 Jellyfin 插件 monorepo，不是 Server/Web fork。仅修改本仓库，不触碰相邻项目。
- `src/Jellyfin.Plugin.Danmuku` 负责弹幕；`src/Jellyfin.Plugin.AgentBridge` 负责外部 Agent 适配，不负责 AI 推理。当前仅实现插件骨架、各自配置页与管理员 health API。
- 两插件独立 GUID、版本、配置、发布包；禁止互相引用、代理 API、共享数据库或建立 Core Plugin。版本分别维护在各自 `.csproj`，GUID 保持稳定。
- 当前基线：.NET 10 / `net10.0`，Jellyfin Server 12.1，Controller/Model NuGet 12.1.0。依赖使用固定版本和提交的 `packages.lock.json`，不依赖 Server 源码。
- `./build/build.sh`、`./build/test.sh` 可带 `danmuku` 或 `agentbridge` 参数；`package.sh`、`install-dev.sh`、`uninstall-dev.sh` 必须指定插件。产物位于 `artifacts/`。
- 开发及 smoke 测试访问地址固定为 `http://127.0.0.1:18096`（容器内 8096），不要改端口或遇到占用时自动换端口。Compose 位于 `deploy/docker/dev/compose.yaml`，资源名称使用 `jellyfin-forge` 前缀。
- `./build/smoke-test.sh` 顺序验证两个插件独立加载及共存，使用隔离数据。运行前停止本仓库开发实例以释放 18096，结束后恢复；不得停止其他项目服务。
- 开发 config/cache/media、测试数据、日志、截图、浏览器工具和发布包不提交；媒体只读挂载，不接入生产数据。不要改宿主 Docker 配置。
- CI 分别 build/test/package 两插件并执行 smoke；本地初始化已通过完整构建、8 项单元测试、三组加载测试及配置页保存验证。Synology/SPK 尚未实测。
- 日常说明见 `README.md`，边界见 `docs/architecture.md`，初始化记录见 `docs/bootstrap-report.md`。不将未执行的验证写成通过。
