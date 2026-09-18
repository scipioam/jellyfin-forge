# Synology 部署说明

计划目标是 DSM 7.4+；目前未在 Synology 实机或原生 SPK 上验证。已验证的标准路径是 Ubuntu Docker + 官方 Jellyfin 12.1 镜像。不要将宿主系统版本等同于 Jellyfin Server 兼容性。

## Docker / Container Manager

1. 确认 Jellyfin Server 是 12.1，并备份持久化 config。
2. 停止自己的 Jellyfin 服务。
3. 确认容器 `/config` 对应的宿主目录，将选定插件 ZIP 解压到其 `plugins/Danmuku/` 或 `plugins/AgentBridge/`；DLL 和 `meta.json` 位于该目录顶层。
4. 赋予 Jellyfin 运行用户读取插件目录的权限及写入配置目录的权限，避免使用 777。
5. 启动服务，在日志、Dashboard 插件列表和 health API 中验证加载。

升级时先停止服务，再替换对应插件目录，避免保留多个版本 DLL；保留 config 下的插件配置。卸载时只删除选定插件的安装目录后重启，可按需另行备份/移除它自己的配置。不要移除另一个插件。

不要将 `build/install-dev.sh` 用于 NAS；该脚本只操作本仓库隔离开发实例。生产路径、UID/GID 和容器名称由自己的部署决定，没有固定 Synology 路径要求。

## 原生 Jellyfin SPK

必须先确认套件内 Jellyfin Server/.NET 与本仓库目标版本一致。从服务日志或套件文档确认实际 config/plugin directory 与服务用户，不假定它是 `/config` 或某个固定 `/volume1` 路径。停止套件服务，将插件安装到其 plugins 下独立目录，设置服务用户权限并重启验证。

本仓库不构建 SPK、不维护 DSM 套件、不调用 DSM API。SPK 的升级与运行时由套件维护者负责。
