# 架构边界

`src/Jellyfin.Plugin.Danmuku/` 与 `src/Jellyfin.Plugin.AgentBridge/` 是两个独立插件；对应的测试项目各自仅引用被测插件。`Directory.Build.props`、脚本和 CI 属于工程工具，不是公共运行时。

Danmuku 的 `Import`、`Model`、`Storage` 实现流式 XML/JSON 解析、导入生命周期及自有 SQLite 数据库；`Playback` 生成确定性的有限集合与磁盘重试缓存。`Api` 提供管理员业务接口及重新校验媒体权限的播放接口，`Configuration` 管理 Dashboard 与配置，`Web` 提供固定白名单引导资源和原生 Canvas 适配。外部入口工具从指定镜像生成只读挂载，不修改 Server/Web 源码，也不由插件写入镜像入口。`Timeline`、`Filter` 中未实施的扩展不表示 M1 支持时间轴编辑、切分或合并。

AgentBridge 的 `Library`、`Metadata`、`Playback`、`Events` 预留媒体上下文 DTO、受控元数据写入、播放/会话状态及 change feed；`Api` 定义外部 Agent 的稳定契约。AI 推理及决策在外部 Otter 中进行。

两插件均保留独立 `InstanceLabel` 与管理员 health API。Danmuku 的 Web 开关、时长加载上限和密度档位通过自身 Jellyfin 插件配置保存；导入、绑定及播放请求元信息存入独立数据库，不与 AgentBridge 共享。公开端点限启用标志、资源版本和固定静态资源，文件及来源详情仅管理员可读。

不创建共享 Core、Service Locator、跨插件业务服务、数据库、事件总线或生命周期协调器。未来确有重复基础逻辑时才考虑极薄的编译时 helper。任何插件的发布与安装不要求另一个插件存在。
