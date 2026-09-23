# 插件部署

正式 Jellyfin 实例与开发环境独立，可以位于不同主机。安装以发布包和目标实例配置为准，不依赖开发主机的目录、容器名称或端口。仓库的 `build/*-dev.sh` 和开发 Compose 仅用于隔离测试，不用于管理正式实例。

## 安装与升级

1. 确认目标 Jellyfin Server/.NET 与仓库技术基线匹配，备份目标实例的持久化配置及插件数据。
2. 获取所需插件的 ZIP 发布包及 SHA-256 校验文件，验证完整性；两个插件分别安装，不要求同时存在。
3. 停止目标 Jellyfin 服务，根据实际安装方式确认其插件目录及运行账号。
4. 将发布包解压到插件目录下独立的 `Danmuku/` 或 `AgentBridge/` 子目录，DLL 和 `meta.json` 位于该子目录顶层。升级时替换对应插件文件，避免残留多个版本，保留配置及业务数据。
5. 确保服务账号可读取插件文件，并可写入目标实例的配置和插件数据目录，然后启动服务。
6. 通过服务日志、Dashboard 插件列表、配置页及管理员 health API 验证加载。请求地址使用目标实例的实际入口及 Base URL。

Docker 部署需从实际 volume 配置确定 `/config` 对应的宿主目录；原生安装应通过服务配置或日志确定插件目录，不假定固定宿主路径。媒体挂载与权限按目标环境配置，插件不要求修改媒体文件。

## 卸载

停止目标服务，仅删除所选插件的安装文件后重新启动，保留其他插件。业务数据与配置是否删除应单独决定，不随插件文件一起自动清空。

## 部署方式与覆盖范围

- [标准开发测试环境](docker/dev/README.md) 用于复现构建与集成测试。
- [Synology 部署说明](synology/README.md) 涵盖 Container Manager 和原生 SPK 的注意事项；未经实机验证的方式不视为已支持。
- Danmuku 提供 M1 导入、绑定、管理及 Web 播放能力；实际验证范围见 [M1 验证记录](../docs/SpecAndPlan/M1-Danmuku-Validation.md)。AgentBridge 保持工程骨架。

## Danmuku M1 Web 入口

播放器集成需要额外的只读入口挂载；插件配置开关不修改镜像文件。先在部署目标可访问的 Docker 环境拉取目标镜像，再显式生成入口：

```bash
python3 build/web-entry.py prepare \
  --image '<目标 Jellyfin 镜像>' \
  --web-path '<镜像内 Web 目录>' \
  --base-url '/' \
  --output '<宿主入口产物目录>'
python3 build/web-entry.py verify \
  --image '<目标 Jellyfin 镜像>' --output '<宿主入口产物目录>'
```

Base URL 为非根路径时传入 `/jellyfin` 等与服务器配置一致的路径。工具只生成本地产物，不连接或重启远程服务器；将 `<宿主入口产物目录>/current/index.html` 只读挂载到 `<镜像内 Web 目录>/index.html`，然后由操作者重建目标容器。运行账号保持非 root，媒体保持只读。打开管理页启用 Web 支持，刷新浏览器后验证控件和播放；管理页不会将“设置已保存”声称为“入口已部署”。

镜像升级后必须从新镜像重新运行 prepare，再重建容器。工具记录镜像 ID、原始/生成 SHA-256、Base URL 和 Web 路径；prepare 校验现有产物后才生成新一代，原子切换 `current` 符号链接。发现手工修改或不识别的入口结构时停止，原部署保持不变；不要用旧入口覆盖新版镜像。旧 `generation-*` 目录保留用于回退，回退时程序镜像与入口必须成对匹配，切回对应目录后重建容器。

彻底卸载先撤销入口，再停服删除 Danmuku 程序目录，保留配置和 `<DataPath>/Danmuku/` 业务数据：

```bash
python3 build/web-entry.py remove --output '<宿主入口产物目录>'
```

remove 将入口恢复为记录的对应版本原件；重建容器使挂载更新生效，或者移除入口挂载后重建。即使已经从 Jellyfin 中直接卸载插件，外部工具也可以撤销残留入口。日常关闭 Web 支持只保存配置，不运行 remove；已加载集合的播放可继续，刷新或重新进入后完全停用。
