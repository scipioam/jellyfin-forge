# 开发与集成测试

本目录定义可在不同开发主机复用的隔离测试环境，不是正式部署模板。命令均从仓库根目录执行；正式实例的操作见 [部署说明](../../README.md)。

## 启动与停止

需要匹配仓库基线的 .NET SDK、Bash、Python 3、Docker 和 Docker Compose。使用当前开发用户的身份启动：

```bash
JELLYFIN_FORGE_UID=$(id -u) JELLYFIN_FORGE_GID=$(id -g) \
  docker compose -f deploy/docker/dev/compose.yaml up -d

# 下线开发环境，保留宿主数据目录
docker compose -f deploy/docker/dev/compose.yaml down
```

标准开发入口固定为 `http://127.0.0.1:18096`，容器端口为 8096。这是仓库测试契约，不是正式部署端口要求。首次启动后完成 Jellyfin 设置向导，创建开发测试账号；不使用生产账号或数据。端口冲突时先查明占用，不自动切换端口或停止其他项目服务。

## 数据目录

Compose 使用相对于自身文件位置的 bind mount，不依赖开发主机的仓库绝对路径：

| 仓库相对目录 | 容器目录 | 用途 |
| --- | --- | --- |
| `deploy/docker/dev/config/` | `/config` | 配置、数据库、插件；插件目录为其中的 `plugins/` |
| `deploy/docker/dev/cache/` | `/cache` | 缓存 |
| `deploy/docker/dev/media/` | `/media`（只读） | 隔离测试媒体 |

目录由仓库占位文件保留，运行数据被 Git 忽略。Compose 不自动创建缺失目录，避免路径错误；确保执行用户能够读写 config/cache，并读取 media。宿主机可以增删测试媒体，容器只读限制不影响宿主机写入；变更后可触发媒体库扫描。

容器下线或重建不删除这些目录。需要重置测试数据时明确选择目标并处理备份，不把删除容器等同于删除数据；不挂载生产媒体，不暴露 Docker socket，不修改宿主 Docker 配置。

## 插件安装与验证

```bash
./build/install-dev.sh danmuku
./build/install-dev.sh agentbridge
./build/restart-dev.sh
./build/uninstall-dev.sh danmuku
```

脚本只管理本仓库的开发实例，不用于远程正式实例。安装会先打包，再停止实例并替换对应插件文件，随后启动；卸载保留插件配置。手动替换文件同样应先停止实例，避免保留同一插件的多个版本。

通过 Dashboard → Plugins 进入各插件配置页。AgentBridge 的骨架配置项为 `Instance label`；Danmuku 提供媒体、文件、任务、设置四个功能区，保留该配置项。管理员 health 接口分别为 `GET /Danmuku/Health` 和 `GET /AgentBridge/Health`，使用 Jellyfin 管理员认证；匿名返回 401，成功响应包含 `Plugin`、`Status`、`Version`。

## Smoke 测试

脚本会记录并暂停本仓库正在运行的开发实例，以释放固定端口；结束后恢复原先运行的实例，原先停止的实例保持停止：

```bash
./build/smoke-test.sh
```

smoke 顺序验证 Danmuku 单独安装、AgentBridge 单独安装和两者共存，覆盖权限、配置页资源及配置保存/重启持久化。测试使用隔离账号和数据，结束后移除测试容器及网络，结果写入忽略目录 `artifacts/smoke/`。测试前已停止的开发实例无需为测试而启动；如为测试暂停了开发实例，结束后恢复。

日志、截图、浏览器工具、真实样本及发布包不提交。验证报告只记录版本基线、方法、结果和必要的可复现条件；不记录开发主机身份、绝对目录或临时运行状态。

### M1 可选 Web 入口

默认开发环境不要求入口产物存在。需要验证播放器集成时运行：

```bash
python3 build/web-entry.py prepare --image jellyfin/jellyfin:12.1 \
  --web-path /jellyfin/jellyfin-web --base-url / \
  --output deploy/docker/dev/web-entry
./build/restart-dev.sh
```

安装、卸载和 restart 脚本通过 `common.sh` 一致检测入口，并使用 `compose.web-entry.yaml` 加入单文件只读挂载。它们重建容器以应用宿主原子切换后的入口。镜像 ID 不匹配或生成文件被手工修改时停止；重新生成匹配入口后重试。Danmuku 卸载先撤销入口再移除程序目录；AgentBridge 不改 Danmuku 入口。所有配置、缓存、媒体和入口历史目录继续保留在忽略位置。

浏览器与性能验证采用隔离容器，固定 18096，保存原开发实例状态并在结束时恢复。需要 FFmpeg（含 ffprobe）；先执行 `npm ci --prefix tests/browser`，再在 `tests/browser/` 执行 `npx playwright install --with-deps chromium firefox` 安装锁定版本及浏览器系统依赖，然后运行 `./build/test-browser.sh`；非根路径使用 `--base-url /jellyfin`。`./build/test-performance.sh` 执行默认/最大配置及两种视窗的完整测量，各场景关闭/开启弹幕分别连续播放十分钟，耗时较长。两个命令不要并行运行，原始证据位于 `artifacts/m1/integration/`。
