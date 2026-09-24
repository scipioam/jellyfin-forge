# 插件部署

本页是正式部署的操作入口。Danmuku 和 AgentBridge 可分别安装、升级和卸载；正式实例与仓库开发环境独立。`build/*-dev.sh` 和 [开发 Compose](docker/dev/README.md) 只用于隔离测试，不能用于管理正式实例。

当前插件基线为 **Jellyfin Server 12.1 / .NET 10**，不要直接套用其他版本或 `latest` 镜像。Danmuku 0.0.2 包含 M2 合并方案和滚动速度功能，完整性能及人工验收尚未完成，详见 [M2 验证记录](../docs/SpecAndPlan/M2-Danmuku-Validation.md)。AgentBridge 当前提供插件骨架、配置页及管理员 health API。

## Docker 生产部署

### 1. 准备文件与目录

目标 Linux 主机需要 Docker Engine、Docker Compose v2 或更新版、Python 3、`unzip` 和 `sha256sum`。从本仓库同一版本复制以下文件到自行选择的部署工作目录；不需要在目标主机构建插件或克隆整个仓库：

- [compose.yaml](docker/prod/compose.yaml)：Jellyfin 服务、持久挂载、只读媒体、日志轮转和自动重启。
- [compose.web-entry.yaml](docker/prod/compose.web-entry.yaml)：可选的 Danmuku Web 入口只读挂载。
- [.env.example](docker/prod/.env.example)：复制为 `.env` 后修改。
- [web-entry.py](../build/web-entry.py)：放在同一工作目录，仅在需要 Web 播放器时使用。

在该工作目录执行后续命令。先创建 `.env`：

```bash
cp .env.example .env
```

编辑 `.env`，将所有占位符替换为目标环境的实际值：

| 配置 | 用途 |
| --- | --- |
| `JELLYFIN_IMAGE` | 与插件匹配的镜像，默认 `jellyfin/jellyfin:12.1`；部署前拉取并记录镜像摘要，建议改为经过验证的 `jellyfin/jellyfin@sha256:…` |
| `JELLYFIN_UID` / `JELLYFIN_GID` | 目标非 root 服务账号的数字 UID/GID，可用 `id <服务账号>` 查询 |
| `JELLYFIN_CONFIG_DIR` | 独立持久配置目录，含服务器数据库、账号、插件程序、插件配置和弹幕业务数据 |
| `JELLYFIN_CACHE_DIR` | 独立缓存目录，可写 |
| `JELLYFIN_MEDIA_DIR` | 已存在的媒体目录，挂载到 `/media`，只读 |
| `JELLYFIN_WEB_ENTRY_DIR` | Web 入口生成目录，保留所有版本及 `current` 符号链接 |
| `JELLYFIN_BIND_ADDRESS` / `JELLYFIN_HTTP_PORT` | 宿主监听地址和端口，默认 `127.0.0.1:8096`；局域网直接访问时改为宿主的局域网地址 |
| `JELLYFIN_BASE_URL` | 默认 `/`；使用 `/jellyfin` 等子路径时，必须同时配置服务器 Base URL 和反向代理 |
| `COMPOSE_FILE` | 默认加载基础文件及 Web 覆盖文件；仅 AgentBridge 或只用 Danmuku API 时改为 `compose.yaml` |

所有宿主目录使用绝对路径，路径不要互相包含，也不要指向开发或临时测试目录。以下命令以当前工作目录的 `.env` 同时供 Bash 与 Compose 使用；值含空格或特殊字符时用单引号包裹。每次开启新终端或修改 `.env` 后重新加载：

```bash
set -a
. ./.env
set +a
mkdir -p "$JELLYFIN_CONFIG_DIR" "$JELLYFIN_CACHE_DIR"
test -d "$JELLYFIN_MEDIA_DIR"
```

通过目录所有权或 ACL，赋予配置和缓存目录对应 UID/GID 的读写权限，以及媒体目录的读取和目录遍历权限；例如由有权限的管理员执行 `chown "$JELLYFIN_UID:$JELLYFIN_GID" "$JELLYFIN_CONFIG_DIR" "$JELLYFIN_CACHE_DIR"`。不要递归修改媒体库权限。Compose 禁止自动创建挂载源，路径错误会在启动时报错。

模板使用独立项目名 `jellyfin-forge-prod`，不设置固定容器名。它不接管已有 Jellyfin：迁移已有实例时应先停服备份、确认原挂载及容器内路径，再沿用原数据；不可让两个实例同时写同一个配置目录。模板不默认配置硬件转码、自动发现或反向代理；有需要时根据目标设备另行配置。

### 2. 获取并安装插件

从所选发布版本获取插件 ZIP 及同名 `.sha256` 文件，存放到工作目录的 `packages/`。也可在构建机器执行 `./build/package.sh danmuku` 或 `./build/package.sh agentbridge`，将 `artifacts/` 中的包及校验文件传到目标主机。

以下以 Danmuku 0.0.2 为例，选择其他版本时同步替换文件名：

```bash
(cd packages && sha256sum -c jellyfin-plugin-danmuku-0.0.2.zip.sha256)
mkdir -p "$JELLYFIN_CONFIG_DIR/plugins/Danmuku"
unzip packages/jellyfin-plugin-danmuku-0.0.2.zip -d "$JELLYFIN_CONFIG_DIR/plugins/Danmuku"
```

这组解压命令用于首次安装到空目录。**升级已有插件时先按后文停服备份并移走旧程序目录，不要直接覆盖解压。** ZIP 全部内容都需要保留，包括 Danmuku 的 SQLite 依赖及原生资产；DLL 和 `meta.json` 应直接位于 `plugins/Danmuku/` 下，不能多嵌套一层。

AgentBridge 使用自己的 ZIP 和 `plugins/AgentBridge/` 目录，同样独立校验和解压。确保解压后的文件可被服务账号读取，插件配置和业务数据目录可写；无需同时安装两个插件。

### 3. 生成 Danmuku Web 入口

仅使用 AgentBridge 或 Danmuku API 时跳过本节，并确认 `COMPOSE_FILE=compose.yaml`。需要 Web 弹幕播放器时保留默认的两个 Compose 文件：

```bash
docker compose pull jellyfin
python3 web-entry.py prepare \
  --image "$JELLYFIN_IMAGE" \
  --web-path "$JELLYFIN_WEB_PATH" \
  --base-url "$JELLYFIN_BASE_URL" \
  --output "$JELLYFIN_WEB_ENTRY_DIR"
python3 web-entry.py verify \
  --image "$JELLYFIN_IMAGE" --output "$JELLYFIN_WEB_ENTRY_DIR"
```

工具从镜像提取 Web 入口，在独立目录中生成产物，不修改镜像或运行中的服务器。覆盖文件将 `current/index.html` 只读挂载到镜像 Web 目录。整个入口目录须允许服务账号遍历及读取。`JELLYFIN_BASE_URL` 仅用于入口生成，不会替你修改 Jellyfin 网络设置；子路径部署应先以基础 Compose 启动并在 Jellyfin 中设置相同 Base URL，再加载 Web 覆盖文件。

### 4. 启动与检查

```bash
docker compose config --quiet
docker compose up -d
docker compose ps
docker compose logs --tail 100 jellyfin
```

首次访问 `http://<宿主地址>:<所选端口>/`（有 Base URL 时加前缀），完成 Jellyfin 管理员和媒体库向导，媒体目录选择 `/media`。默认只绑定回环地址，需在宿主访问或通过同主机反向代理访问；远程浏览器不能使用自己的 `127.0.0.1` 访问服务器。

在控制台插件列表与日志确认版本，打开配置页。Danmuku Web 播放器还需在配置页启用 Web 支持并强制刷新浏览器；配置保存成功不等于入口挂载已生效。检查：

- 管理员身份请求 `/Danmuku/Health` 或 `/AgentBridge/Health`，预期返回 `ok` 和对应插件版本；未登录不能据此判断插件损坏。
- `/Danmuku/Web/Status` 返回 `enabled: true`，0.0.2 的 `resourceVersion` 为 `m2-v1`。
- 导入测试弹幕、绑定媒体并开始新播放，确认控件与画面；检查字幕、全屏及播放退出后行为。

API 均需带实例实际 Base URL。生产模板目前仅经过 Compose 配置解析和静态检查，未作为新的生产实例运行；开发实例的加载及功能验证不能代替你的目标环境检查。

## 升级、备份与回退

1. 在工作目录加载 `.env`，执行 `docker compose stop jellyfin`。停服后备份**整个配置目录**，以及 `.env`、Compose 文件和整个 Web 入口目录；备份放在数据目录之外，保留权限。记录插件包校验和、镜像 ID/摘要及入口 `current/manifest.json`。这些备份包含账号等敏感数据，应限制访问。
2. 校验新插件包。将 `$JELLYFIN_CONFIG_DIR/plugins/Danmuku` 或 `AgentBridge` 旧程序子目录移到插件目录之外保存，再新建同名空目录并完整解压新包。不要移动 `plugins/configurations/` 或 `data/Danmuku/`，不要影响另一个插件。
3. 只升级插件、镜像和 Base URL 未变时，校验原入口即可。更换镜像时先加载新的 `.env`、拉取镜像，再对新镜像重新执行 `prepare` 和 `verify`。不要让容器在后台自动换镜像而继续挂载旧入口。
4. 执行 `docker compose up -d --force-recreate jellyfin`，让新入口挂载生效；单纯 `restart` 不会更新已绑定的旧文件。重复启动检查，并核对旧文件、弹幕、绑定、方案及配置。

Danmuku 0.0.2 将数据库升级至 schema v5。迁移自带的一致性数据库备份不能替代部署前完整备份。回退时先停服并另存当前数据，再恢复配套旧程序、配置和数据库；不能只降级 DLL。回退镜像时也恢复对应入口目录和 `.env`，运行 `verify` 后重建容器。恢复旧数据会放弃备份后的写入，应先确认需要保留的新增内容。

入口工具保存不可变的 `generation-*` 目录，并通过 `current` 符号链接选择版本。请保留目录结构；检测到手工改写或未知入口结构时工具会停止，不要强行用旧入口覆盖新镜像。

## 停止与卸载

日常停止使用 `docker compose stop jellyfin`，启动使用 `docker compose up -d`；移除容器使用 `docker compose down`，持久数据位于宿主绑定目录，不随容器删除。

卸载单个插件时先停服，只删除该插件的程序子目录，再启动。默认保留插件配置与业务数据；永久删除数据应作为单独操作。卸载 Danmuku 还需撤销入口，可任选一种方式：

- 将 `.env` 的 `COMPOSE_FILE` 改为 `compose.yaml`，重新加载环境后执行 `docker compose up -d --force-recreate jellyfin`，移除入口挂载。
- 执行 `python3 web-entry.py remove --output "$JELLYFIN_WEB_ENTRY_DIR"`，再重建容器，使挂载内容恢复为该镜像原件。

日常暂时关闭 Web 支持只需修改插件配置，不必卸载入口；已加载集合的播放可继续，刷新或重新进入后完全停用。

## 已有实例或非 Docker 安装

已有 Docker 服务可以在原 Compose 中加入独立插件目录内容及可选 Web 挂载，无需换成此模板；安装路径以它实际映射的 `/config` 为准。原生安装则通过 Jellyfin 服务配置和日志确认插件目录、数据目录及运行账号。两者都遵循停服备份、整目录替换程序、保留业务数据、启动验证的流程。

Docker 配置字段参考 [Jellyfin 官方容器说明](https://jellyfin.org/docs/general/installation/container/) 和 [Docker Compose 服务参考](https://docs.docker.com/reference/compose-file/services/)。本仓库固定的插件技术基线与已执行验证范围以仓库记录为准。
