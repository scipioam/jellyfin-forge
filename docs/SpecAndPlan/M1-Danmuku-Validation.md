# M1 弹幕插件验证记录

依据：[设计](M1-Danmuku-Design.md)、[实施计划](M1-Danmuku-ImplementationPlan.md)。本记录当前覆盖 P3/P4，不代表整个 M1 自动化完成或人工验收通过。

## P3/P4 提交前回归（2026-09-22）

基线：.NET 10、Jellyfin Server 12.1、Controller/Model 12.1.0；Danmuku 0.1.0、SQLite schema v3。P3/P4 在 P2 基础上新增流式 XML/JSON 解析、导入生命周期、媒体绑定与原子替换，尚未提供完整管理 HTTP API 和播放集合。

| 验证 | 实际结果 |
| --- | --- |
| `./build/test.sh danmuku`，不设置样本目录 | 147 通过、4 显式跳过、0 失败 |
| `DANMUKU_SAMPLE_DIR=<样本目录> ./build/test.sh danmuku` | 151 通过、0 跳过、0 失败 |
| `./build/test.sh agentbridge` | 4 通过、0 失败 |
| 两插件分别 `./build/package.sh <插件>` | 均构建成功，0 警告、0 错误，独立生成 ZIP 与 SHA-256 |
| Danmuku 单独、AgentBridge 单独、两插件共存 smoke | 三组均通过；独立 GUID/加载、管理员 health、匿名拒绝、配置页资源、配置保存及重启持久化；Danmuku schema v3/WAL |

编译、测试、打包顺序执行，设置 `DOTNET_PROCESSOR_COUNT=1`、`MSBUILDDISABLENODEREUSE=1`、`UseSharedCompilation=false`，限制重度任务到一个可用逻辑 CPU。大输入逐文件流式生成，测试后清理；不在内存中拼接完整大文件。原始日志留在忽略目录 `artifacts/m1/p4-submit/`。

Smoke 调用仓库原有三个 scenario，仅在隔离 Compose 中加入单 CPU、1 GiB 内存和 `DOTNET_PROCESSOR_COUNT=1` 限制。固定端口、只读媒体、独立测试数据及原有检查保持不变；运行后清理对应容器和网络，持久化测试证据不入 Git。

## 覆盖内容

- 两种格式真实 300,000/300,001 源条目、52,428,800/52,428,801 字节、零有效条目、跨缓冲文本及超大 JSON 元素结构校验；真实 300,000 条接收、解析、发布全链路及临时文件清理。
- 1–10 文件批次与第 11 个拒绝、全局 100 个位置、最多两路接收、按序发布、空缺位置取消/到期、断连与响应丢失故障注入。
- 内容复用、同名后缀、主动停用；替换的三种生效状态、B 已绑定及 B=A、版本冲突、保留原目标的 Resume、其他媒体引用不变。
- 单文件/批次异常确认、不授权未来异常；24 小时确认期限、终态七天保留、重启中断、取消竞争、清理失败归属与分页重试。
- v2→v3 迁移备份、关联数据保留、失败回滚；媒体缺失与查询失败分别记录，手动全量检查任务计数。

外部样本分别核对设计记录，不将 XML/JSON 当作同一批数据的等价导出。样本不纳入仓库；未配置目录时明确跳过，内容哈希不匹配时失败。

| 样本 | SHA-256 |
| --- | --- |
| `BV1zCtq61Eoi.xml` | `3827a5efb558ce2fd9398139d6a02d5d556104b2176931268f210faccdc945f6` |
| `BV1zCtq61Eoi.json` | `4f3f73b79f4da149b0bd57b522fc3f28e6c2049cd2ceeff1f271bac827862f7b` |
| `BV1cSec6tEux.xml` | `00937d67b6fed630bcd9ea4f5aeef84e75ef8313df70014e503b1765439b6cbd` |
| `BV1cSec6tEux.json` | `3088b2820de74eadd7701b664f2a7a8f43e3e54a37b05a791889d1a5adacc75c` |

## 未覆盖范围

4 GiB 暂存容量通过持久化预留值触发拒绝，未实写 4 GiB、未测试磁盘耗尽。服务层故障注入不等同于真实 HTTP 上传和权限验证；这些属于 P5。播放集合、完整管理页面、部署入口工具和浏览器性能仍分别属于 P6–P9；本记录不将 P1 的最小叠加层验证当作完整播放器验证。

人工验收尚未开始，待 P9 完成后按实施计划 H01–H10 执行。桌面产品浏览器、全屏、linux-arm64 运行、Synology/SPK 及正式部署均不因上述回归而视为通过。
