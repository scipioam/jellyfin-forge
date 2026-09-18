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

## 设计、计划与实施流程

- 插件具体功能按 Milestone 推进，设计文档和实施计划文档统一存放在 `docs/SpecAndPlan/`。文件名以 `M1`、`M2` 等为前缀，同一 Milestone 的两份文档使用相同前缀，例如 `M1-Danmuku-Design.md` 与 `M1-Danmuku-ImplementationPlan.md`；编号和范围在开始讨论时确定。
- 设计阶段先与用户确定本 Milestone 的目标、范围和章节目录，再逐章节讨论并记录结论；每章经用户确认后再推进下一章。未确认的内容标记为待讨论，不将建议或默认假设写成已确定决策。
- 全部章节确定后，整理完整设计文档交由用户整体审阅。章节确认不等于整份设计文档审阅通过；设计文档须经用户明确通过后，才能编写实施计划。
- 设计文档审阅通过后，可直接编写对应实施计划，无需再次询问是否开始编写。计划应覆盖任务拆分、实施顺序与依赖、涉及的插件和代码范围、验收标准、自动化测试及人工测试步骤，并关联设计文档。
- 实施计划完成后交由用户审阅；用户明确通过后即可按计划推进实施，无需再次询问是否开始实施。未通过前不提前实施功能。实施中如需改变已批准的设计或计划范围，先更新相关文档并就变更取得用户确认，再实施受影响的部分。
- 实施完成后，先运行并确保相关自动化测试及必要的构建、打包和 smoke 检查通过，再进入人工测试阶段。自动化验证失败时先修复并重新验证，不提前进入人工测试，也不将自动化测试通过等同于人工验收完成。
- 文档记录当前阶段、章节确认情况、整体审阅结论和实际验证结果；未执行、失败或受阻的检查须如实标明。人工测试阶段提供可复现步骤和预期结果，记录实际结果及遗留问题；修复人工测试发现的问题后，先通过相关自动化回归，再进行人工复测。
