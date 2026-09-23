# CI 策略

配置入口为 `.github/workflows/ci.yml`。两插件仍独立构建、测试、打包；仅 Danmuku 支持按标签自动生成 Release 草稿，不自动正式发布或部署。

## 触发与覆盖

| 事件 | 验证内容 |
| --- | --- |
| 纯文档分支 push／PR | 不触发 workflow，不执行测试 |
| 开发分支代码 push | 两插件构建、单元测试、打包 |
| 代码 PR（含文档与代码混合改动） | 两插件构建、单测、打包；三组加载 smoke；HTTP／部署与 Chromium、Firefox 的根路径及 `/jellyfin` 回归 |
| `main` 代码 push／普通标签 push | 与代码 PR 相同 |
| `danmuku-vX.Y.Z` 标签 push | 先校验标签和项目版本，再执行完整常规验证；成功后上传 Danmuku Release 草稿 |
| 手动 `regular`（默认） | 完整常规验证，允许用于纯文档提交 |
| 手动 `performance` | 两插件构建、单测、打包及 Danmuku 性能长测；性能入口自身仍包含 HTTP／部署准备验证 |
| 手动 `all` | 完整常规验证与性能长测 |

纯文档仅包括任意目录的 `.md`，以及 `docs/` 内的 `.png`、`.jpg`、`.svg`。任何其他文件变更都会触发检查，包括构建、依赖、部署、工作流和 LICENSE；不将整个 `docs/` 目录无条件忽略。PR 判断的是相对目标分支的累计改动：含代码的 PR 即使最新一次只改文档，也仍需完整验证。push 判断本次推送的改动。标签 push 保留完整常规验证；GitHub 的路径过滤不作用于标签，不将其视为纯文档分支 push。

同一事件类型内，同分支／同 PR 的新运行取消旧运行；push、PR、手动任务分组隔离，避免轻量 push 取消完整 PR。同一提交可能同时有轻量 push 和完整 PR 两次运行。纯文档提交被过滤后不会创建新运行，因此不会取消此前正在运行的代码验证。

本策略使用 workflow 级路径过滤。GitHub 对被路径过滤跳过的必需检查可能保持 Pending；因此不能直接将这些可整体跳过的检查设为所有 PR 无条件必需。本轮不修改远端分支保护。如果未来需要强制合并门禁，应先改为始终运行的轻量改动分类／汇总任务，文档 PR 只报告无需测试，代码 PR 必须等待完整验证通过，再将汇总任务设为必需。参见 [GitHub 官方说明](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax)。

## 构建与资源

轻量 `release-checks` 先运行发布工具测试；Danmuku 发布标签还须匹配项目版本，非法标签在构建前失败。`standalone` 矩阵分别恢复对应测试工程的锁定依赖，构建测试工程及其插件引用，再以 `--no-build --no-restore` 运行单测。通过后调用 `build/package.py` 打包刚测试的二进制，不再调用会重新构建的 `package.sh`。

smoke、集成和性能任务依赖 `standalone` 成功，下载同次 workflow 的 ZIP 与校验文件，通过 SHA-256 检查后直接调用 Python 测试入口；两个浏览器路径使用同一包。下游无需 .NET SDK，也不会重新编译或打包。开发者本地 `build/*.sh` 仍保留原先自动构建的便利行为。

NuGet 缓存由提交的 `packages.lock.json` 标识，npm 缓存由浏览器 `package-lock.json` 标识；缓存缺失时正常恢复，不缓存发布包作为测试输入。两个插件各在独立 runner 构建；各重度任务内部仍限制 .NET 并发、禁用节点复用和共享编译，浏览器按现有测试入口串行。容器保持现有资源限制和固定测试端口。

## 证据与限制

- 上传两插件 ZIP／SHA-256；单测 TRX 包含成功、失败及跳过记录。
- smoke 上传结果和服务日志；集成上传结果、失败记录、截图和服务日志；性能上传性能记录、进度、失败记录和服务日志。测试账号凭据、数据库及完整测试数据目录不上传。
- 外部私有样本不进入仓库或 CI。未设置 `DANMUKU_SAMPLE_DIR` 时，四项外部样本测试明确跳过；CI 绿色不代表这些样本验证通过。本地授权样本测试仍通过 `build/test-danmuku-samples.sh` 执行。
- 性能长测按需运行，不列入 PR 的完整常规验证。人工浏览器验收、Synology/SPK 及生产部署也不由 CI 绿色代替。
- 此配置的本地语法、路由和命令验证不等于 GitHub 远端执行通过；artifact 传递、缓存及实际触发结果须由推送后的运行确认。

## M2 接入进度

M2 实施中，常规 `danmuku-integration` 已追加两路径 `tests/integration/m2.py --browser`，内部串行执行 Chromium／Firefox，超时调整为 120 分钟。继续直接使用同次构建下载的包，不重新打包；M2 HTTP 结果记录包哈希。独立的 `danmuku-m2-integration-results` artifact 在失败时仍上传明确白名单内的结果、日志及截图，不递归上传配置、凭据或数据库。

当前 M2 驱动仅覆盖已实现功能子集，详见 [M2 验证记录](SpecAndPlan/M2-Danmuku-Validation.md)。计划中的 24 组 M2 性能＋2 组 M1 基线、缺组／失败汇总及故意失败证据验证尚未实施；现有手动性能 job 仍是 M1 入口，不能称为 M2 性能验收。此段不改变下面的历史验证事实，也不表示远端 CI 已执行。

## CI 分层调整的历史验证记录

- Actionlint 1.7.7、各内联 Bash 语法及 `git diff --check` 通过；21 组任务路由判断、10 组纯文档／代码／混合路径案例通过。
- 按新 workflow 的构建命令串行执行：Danmuku 182 通过、4 外部样本跳过；AgentBridge 4/4，构建无错误或警告。TRX 已生成，两插件直接打包及 SHA-256 校验通过。
- 直接使用上述包执行 Python smoke 入口，三组加载／配置持久化场景通过；未在 smoke 前重复编译。入口工具测试 3/3，布局／引导测试 10/10。
- 本轮没有重跑完整双浏览器矩阵或性能长测，没有执行远端 GitHub Actions；产物跨 job 下载、缓存命中和 GitHub 实际事件过滤尚待远端确认。插件功能代码及本地构建入口未修改。


## Danmuku Release 草稿

当前仅发布已实施的 Danmuku。AgentBridge 继续参与构建和共存检查，但没有 Release 发布任务，`agentbridge-v…` 等其他标签只执行常规 CI。发布入口是 `push` 的 `danmuku-vX.Y.Z` 标签；分支 push、PR 和手动运行（包括在标签上手动运行）均不会写入 Release。

标签必须使用三段非负整数版本（如 `danmuku-v0.0.1`），与该提交的 Danmuku `.csproj` 中 `Version` 完全一致，`AssemblyVersion` 必须为该版本加 `.0`。当前未定义预发布后缀标签。Release 草稿不是 prerelease，是否标记预发布可在人工确认时决定。

`danmuku-release-draft` 等待两插件单测／打包、三组 smoke、HTTP／部署及双浏览器双路径集成全部成功；失败、取消或跳过必需任务都不能进入发布。性能长测仍按需运行，不是发布门禁。该任务只下载同次运行的 `jellyfin-plugin-danmuku` artifact，核对 ZIP 完整性、SHA-256、包名、版本和 GUID，再上传以下两项，不重新构建，不附带 AgentBridge：

- `jellyfin-plugin-danmuku-X.Y.Z.zip`
- `jellyfin-plugin-danmuku-X.Y.Z.zip.sha256`

只有草稿任务获得 `contents: write`，其余任务保持 `contents: read`。使用 Actions 提供的 `GITHUB_TOKEN`，无需增加个人访问令牌；仓库或组织策略须允许该任务申请写权限。创建时使用 `--verify-tag --draft`，不会自动创建标签或正式发布。已存在草稿时，只替换上述两个生成附件以恢复中断上传，保留人工编辑的说明和其他附件；若同名 Release 已正式发布，则失败退出，不覆盖正式版本。操作使用 [GitHub CLI 的草稿创建](https://cli.github.com/manual/gh_release_create)及[附件上传](https://cli.github.com/manual/gh_release_upload)能力。

### 发布步骤

1. 先将此工作流及待发布功能通过 PR 合并到 `main`；待打标签的提交必须包含此发布配置。
2. 确认 `.csproj` 版本正确，更新本地 `main`，再对确定的提交打标签。首次版本示例：

   ```bash
   git switch main
   git pull --ff-only origin main
   git tag -a danmuku-v0.0.1 -m "Danmuku 0.0.1" HEAD
   git push origin danmuku-v0.0.1
   ```

3. 在 Actions 等待该标签的完整常规 CI 和 `danmuku-release-draft` 成功，再进入仓库 Releases 查看 **Danmuku 0.0.1** 草稿。
4. 核对版本、说明及两个附件，完成必要人工验收后点击 **Publish release**。等待上传完成后再发布，避免与草稿补传同时操作。ZIP 用于安装，GitHub 自动提供的源码归档不代替插件安装包。

上传中断时可在 Actions 重跑失败任务，恢复同一草稿。不要通过移动已发布标签或覆盖正式附件修正版本；正式发布后的改动应提升插件版本，再按新标签发布。草稿删除及标签清理由维护者明确操作，此工具不自动执行。

### 草稿流程本地验证

发布工具的 16 项自动化测试通过，覆盖标签／程序集／包身份不一致、错误校验和、缺包、混入另一插件、创建草稿、补传、权限失败及拒绝修改正式 Release；GitHub 写入由测试替身模拟，没有创建远端测试 Release。Actionlint、脚本语法、7 组发布事件路由、必需依赖和权限范围检查通过，现有 0.0.1 实际包通过本地身份与校验和核对。另以只读请求核对 GitHub 分页查询传输格式；草稿恢复查找覆盖后续页，不依赖仅面向已发布版本的按标签查询。

本轮未改变插件代码、构建和业务测试入口，未重新编译或运行重度浏览器／性能验证，未启动开发容器。尚未推送发布标签，真实的标签触发、授权及 Release 上传需在配置合并后的首次发布中验证；不将模拟测试视为远端发布成功。
