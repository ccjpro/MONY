# Changelog / 更新记录

## 2.0.0

- 统一应用、安装路径和源码命名为 MONY。<br>
  Standardized the application name, installation paths, and source names as MONY.

## 1.9.9

- 告警触发后暂停检测 5 分钟，性能采样持续运行。<br>
  Alert detection pauses for five minutes after an alert while performance sampling continues.

## 1.6.0

- 网页设置保留 24 小时 Cookie 和手动锁定。<br>
  Web settings retain a 24-hour cookie and support manual locking.
- 新增 Windows 和浏览器告警、阈值与持续时间设置，以及按 localhost 或指定 IP 静音。<br>
  Added Windows and browser alerts, configurable thresholds and durations, and notification muting for localhost or specific IP addresses.

## 1.5.2

- CPU 采集授权跨重启保留，可在桌面撤销。<br>
  CPU monitoring authorization persists across restarts and can be revoked in the desktop app.
- 全屏仪表新增 CPU 频率，修复语言切换后部分文本未立即更新的问题。<br>
  Added CPU frequency to the full-screen dashboard and fixed text that did not immediately update after switching languages.

## 1.5.1

- 安装包集成 PawnIO，首次授权 CPU 采集时自动安装。<br>
  Bundled PawnIO in the installer. It installs automatically the first time CPU monitoring is authorized.
- 支持简体中文与英文，可手动切换。远端连接支持填写 IP、端口和可选密码。<br>
  Added Simplified Chinese and English with manual language switching. Remote connections accept an IP address, port, and optional password.

## 1.5.0

- 支持连接其他实例的 API 并切换目标，网页由本机转发数据。<br>
  Added remote API connections and target switching. The local instance relays data to the web interface.
- 分区容量以 1024 为进制换算。<br>
  Volume capacities use base-1024 units.

## 1.4.5

- 支持通过 UAC 单独授权 CPU 温度和封装功耗采集。<br>
  Added separate UAC authorization for CPU temperature and package power monitoring.

## 1.4.4

- 分区显示已用及剩余容量。<br>
  Volumes show used and free space.

## 1.4.3

- 网页支持安装为 PWA。<br>
  The web dashboard can be installed as a PWA.

## 1.4.2

- 新增共享显存显示开关。<br>
  Added a toggle for shared GPU memory.

## 1.4.1

- 支持从桌面全屏进入设置并返回原状态。<br>
  Settings can be opened from the desktop full-screen dashboard, which is restored when leaving settings.

## 1.4.0

- 新增桌面全屏仪表盘和网页 `/full`。<br>
  Added the desktop full-screen dashboard and the web `/full` page.

## 1.3.7

- GPU 趋势加入专用显存用量，物理磁盘趋势加入读写速率。<br>
  Added dedicated GPU memory usage and physical disk read and write rates to the charts.

## 1.3.6

- 设置与数据 API 返回应用版本。<br>
  The settings page and data API report the application version.

## 1.3.5

- Web 和数据 API 支持独立配置监听端口。<br>
  The web and data APIs can use independently configured listening ports.

## 1.3.4

- 两项服务支持可选密码，登录 Cookie 有效 24 小时。<br>
  Both services support optional passwords. Login cookies are valid for 24 hours.

## 1.3.3

- 增加挂载分区的盘符、卷名及容量，数据 API 同步返回分区数据。<br>
  Added drive letters, volume labels, and capacities for mounted volumes to the app and data API.

## 1.3.2

- 修复 Safari 网页图标兼容性。<br>
  Fixed Safari compatibility for web icons.

## 1.3.1

- 新增登录自启与逐项应用设置。<br>
  Added startup at sign-in and per-setting updates.

## 1.3.0

- 新增可配置的 Web 和数据 API 服务，支持局域网访问和认证。<br>
  Added configurable web and data API services with LAN access and authentication.

## 1.2.1

- 增加设备名称、标识 IP、主题和窗口行为设置。<br>
  Added settings for device name, identifying IP address, theme, and window behavior.

## 1.2.0

- 按设备采集 GPU 和物理磁盘，增加温度、功耗、CPU 频率和显存指标。<br>
  Added per-device GPU and physical disk sampling, plus temperature, power, CPU frequency, and GPU memory metrics.

## 1.1.0

- 新增托盘驻留、单实例恢复和网页监控。<br>
  Added tray operation, single-instance window restoration, and web monitoring.

## 1.0.0

- 首次发布，支持实时性能指标、60 秒趋势和暂停控制。<br>
  Initial release with live metrics, 60 seconds of history, and pause controls.
