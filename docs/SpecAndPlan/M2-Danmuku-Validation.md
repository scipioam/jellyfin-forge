# M2 实施与验证记录

## 状态与范围

用户于 2026-09-23 明确授权按 [实施计划](M2-Danmuku-ImplementationPlan.md) 开始实施。P1–P6 主体代码已落地，P6/P7 验收仍未全部完成。**M2 尚未完成，P7/P8/P9 不标记通过。** 用户于 2026-09-24 调整执行顺序：补充 README 部署说明并整体提交后，复用现有开发 Jellyfin 容器准备人工试用。该准备使用已通过功能回归的包并保留升级前备份，不替代完整性能验证、隔离恢复演练或正式人工验收；本文记录截至该提交前的自动化结果。未推送或正式发布。

设计依据为已批准 [M2 设计](M2-Danmuku-Design.md)，批准版本及状态同步后的 SHA-256 见实施计划。保留 .NET 10 / Jellyfin 12.1、固定依赖及锁文件；Danmuku 版本 0.0.2，schema v5，资源 `m2-v1`，渲染契约 `m2-speed-v1`。AgentBridge 业务代码、GUID、版本及配置未修改。

## 已实现内容

- schema v5：方案、片段、媒体生效方案和播放版本快照；迁移前一致性备份、互斥选择、媒体归属和来源外键。
- 方案生命周期：名称、范围、统计和规模限制；保存／启用／删除事务；方案及媒体版本检查；失效媒体可发现并显式清理；文件引用保护。
- 绑定与发布：新 preserve/set 意图，旧格式不能覆盖生效方案；首次追加及替换保留方案，导入继续使用媒体版本冲突恢复。
- 管理接口：管理员方案 API、Selection、References，128 KiB 方案请求体限制及 413 错误。
- 合并播放：两次数值流式扫描、目标分钟统一配额、选中后读取文本、片段实例 ID、固定播放缓存及方案版本。扫描次数随响应记录，不为每个分钟重新扫描全表。
- 滚动速度：0.5–2.0、0.1 步长、默认 1.0，参与布局寿命、位置、碰撞、回看和重建；固定弹幕保持 4 秒。保留旧偏好存储键。
- 管理页面：方案编辑、范围同步／撤销、预览防抖及取消、保存快照、未保存确认、来源搜索分页、引用显示、局部时间表滚动及速度控件窄屏调整。
- 新增 M2 HTTP／浏览器驱动；`build/test-browser.sh --milestone m2` 可运行已实现的 M2 功能子集，默认仍运行 M1。它目前不代表实施计划要求的完整 M2 验收矩阵。

## 已执行检查

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

## 证据位置

全部路径相对仓库根目录，均位于 Git 忽略目录：

- P3 首次修复后 HTTP：`artifacts/m2/integration/jellyfin-forge-m2-07202b1eeb/http-results.json`。
- P6 前根路径 M1：`artifacts/m1/integration/jellyfin-forge-m1-7a2447c1aa/` 下的结果 JSON、日志及截图。
- P6 基础页面根路径：`artifacts/m2/integration/jellyfin-forge-m2-8dc523c71f/`。
- P6 基础页面 `/jellyfin`：`artifacts/m2/integration/jellyfin-forge-m2-e52da64200/`。
- 双视频根路径：`artifacts/m2/integration/jellyfin-forge-m2-8fd435c36d/`；当前包复核：`artifacts/m2/integration/jellyfin-forge-m2-17f6350cf7/`。
- 双视频 `/jellyfin`：`artifacts/m2/integration/jellyfin-forge-m2-d3786621bc/`；当前包复核：`artifacts/m2/integration/jellyfin-forge-m2-68dba5ca62/`。
- P6 后 `/jellyfin` M1：`artifacts/m1/integration/jellyfin-forge-m1-5383c75229/`。

`http-results.json` 记录其实际使用的 ZIP 哈希；`*-m2-browser.json` 记录浏览器结果；`*-m2-editor-*.png`、`*-m2-speed-*.png` 为截图。当前包两路径、两引擎和 smoke 复核通过；P6 前后 M1 历史包结果仍按各自记录区分。凭据及数据库仅在忽略的隔离目录，不提交也不作为可上传证据。

## 失败及修复记录

- 首轮单元失败：旧迁移测试写死 v4、新增迁移失败注入位置无效、集合记录误用引用相等断言。修正后回归通过。
- P3 超大请求最初被统一异常处理转成 500；保留请求体上限并映射 413 后 HTTP 通过。
- 速度控件窄屏标签最初竖排；调整标签不换行及滑块伸缩后，两引擎两视窗通过并生成截图。
- M2 专用 Firefox 测试缺少 locale，原生页面出现 `invalid language tag`；对齐已有测试的 `en-US` 后通过。
- 缩放视窗与调速同时发生导致操作观察器正确拒绝被取消代次；等待缩放提交后分别测量，未将该失败样本计作成功。

- 当前包第一次浏览器复核曾在等待字体加载后截图时超时（`artifacts/m2/integration/jellyfin-forge-m2-1c97e0a900/`）。加入非本站字体阻断及记录后重跑通过；四次成功运行均记录 0 个被阻断字体，因此不能认定外部字体是根因。超时未稳定复现，保留为测试不稳定记录，不计该失败为通过。插件未引入字体或 CDN。

## 尚未完成的验收

以下不能根据目前子集成绩标记通过：

- A01 的容器中断恢复、完整迁移保留矩阵；A02–A09 中尚未补齐的竞争、取消、故障和 HTTP 边界组合。
- A10–A14 的完整偏好、明暗主题、键盘焦点、未保存离开、迟到保存响应、统计失败重试、跨媒体实际保存归属及全部混合缓存组合。双视频迟到**预览**已验证，不能代替迟到**保存**验收。
- A15：24 组 M2 性能、两组 M1 基线、百万稀疏分钟峰值、20 次加载／设置／seek、关闭／开启各 10 分钟掉帧、负向测量及汇总。未开始性能长测，不报告任何性能阈值达标。
- A17：CI 已追加 M2 两路径常规调用、120 分钟超时及独立证据白名单；性能分组、缺组汇总和故意失败传播验证尚未实施，本地 YAML、两路径调用、包复用、上传白名单及 Release 依赖静态检查已通过；远端 CI 未运行。
- A18：`m2-manual.py` 持久人工夹具 prepare/status/resume/finish 尚未实施，也未进行生命周期演练。
- P8：开发数据一致性备份、已测包部署、升级前后摘要和隔离回退演练未执行。
- P9：人工隔离环境尚未准备，H01–H11 未开始。正式 Chrome／Edge／Firefox 的体验、Synology/SPK、ARM64 均未验证。

当前不能将 schema v5 包安装到保留真实开发数据的环境并宣称升级完成。v5 不能仅替换回旧 DLL；后续 P8 必须使用配套旧程序、配置、迁移前一致性数据库及原文件完成隔离恢复演练，且不得覆盖未经核对的升级后新写入。
