# MONY

Windows 11 x64 实时性能监控。安装发布页的 `MONY-*-x64.msi`。

## 功能

- 每秒采样 CPU、内存、各 GPU、物理磁盘、网络和挂载分区，保留最近 60 秒趋势。CPU 温度与封装功耗可在桌面授权 PawnIO 后读取。
- 支持托盘驻留、登录自启、全屏仪表盘和简体中文／英文。可连接其他 MONY 实例，网页通过本机转发所选目标的数据。
- 网页支持 PWA、Safari 添加到主屏幕和同机 Cloudflare Tunnel。概览、全屏、切换目标和暂停可直接使用。网页设置可选密码、24 小时 Cookie 和手动锁定。
- Windows 和浏览器告警可独立启用，支持 CPU／GPU 温度、分区空间、远端断线及 5 分钟冷却。浏览器可按 localhost 或指定 IP 静音。

## API

| 默认端口 | 路径 | 返回 |
| --- | --- | --- |
| 2727 | `GET /api/data`（或 `GET /`） | 本机原始采样 |
| 2727 | `GET /api/dashboard` | 本机卡片、趋势和分区 |
| 2417 | `GET /api/dashboard` | 当前选中目标的卡片、趋势和分区 |

两项服务默认仅监听本机，可独立调整端口和开启局域网访问。数据 API 配置密码后使用 HTTP Basic，用户名为 `pulse`。网页设置密码独立于数据 API。

```powershell
curl.exe http://127.0.0.1:2727/api/data
curl.exe --user pulse http://127.0.0.1:2727/api/data
```

两类响应均含 `version`。`/api/data` 还返回 `computerName`、`computerIp`、`paused`、`cpuDetails` 和 `sample`。采样包括 `time`、CPU 和内存读数、`gpus`、`disks`、`volumes`、`download` 与 `upload`。首次采样前 `sample` 为 `null`，不可用的读数也为 `null`。原始数值使用 MHz、°C、W、B、B/s 和 0–100 百分比。

## 构建

在 Windows 安装 .NET 10 SDK 与 WiX 5 后运行 `.\tools\build-msi.ps1`，MSI 输出到 `dist/`。脚本会校验 [PawnIO 2.2.0 官方安装器](https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0) 的 SHA-256。缺少 `installer/vendor/PawnIO_setup.exe` 时自动下载。

[更新记录](CHANGELOG.md) · [第三方许可](installer/licenses)
