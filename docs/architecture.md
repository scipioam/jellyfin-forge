# 架构边界

`src/Jellyfin.Plugin.Danmuku/` 与 `src/Jellyfin.Plugin.AgentBridge/` 是两个独立插件；对应的测试项目各自仅引用被测插件。`Directory.Build.props`、脚本和 CI 属于工程工具，不是公共运行时。

Danmuku 的 `Import`、`Model`、`Storage`、`Timeline`、`Filter`、`Web` 目录预留 XML 导入、标准化模型、弹幕存储、时间轴操作、过滤和播放器显示扩展。`Api` 自行提供业务接口，`Configuration` 自行管理 Dashboard 与配置。播放器未来可评估 JS/CSS 注入，初始化不实施注入或 web patch。

AgentBridge 的 `Library`、`Metadata`、`Playback`、`Events` 预留媒体上下文 DTO、受控元数据写入、播放/会话状态及 change feed；`Api` 定义外部 Agent 的稳定契约。AI 推理及决策在外部 Otter 中进行。

当前唯一配置 `InstanceLabel` 用于验证独立配置读写，唯一 API 是需要管理员权限的只读 health 接口；不暗示业务能力已可用。

不创建共享 Core、Service Locator、跨插件业务服务、数据库、事件总线或生命周期协调器。未来确有重复基础逻辑时才考虑极薄的编译时 helper。任何插件的发布与安装不要求另一个插件存在。
