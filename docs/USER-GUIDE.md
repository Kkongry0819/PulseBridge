# PulseBridge · 心率工作台
Windows 10/11 x64 · v1.1.0

## v1.1 界面与品牌
- 全新「心跳连接桥」矢量 LOGO，同时用于侧栏、窗口和应用图标；Source/Assets 提供 SVG、PNG、多尺寸 ICO。
- 深色玫瑰配色、薄荷绿状态点、统一线条图标、深色下拉菜单与滚动条、圆角输入框及键盘焦点反馈。
- 卡片分层入场、悬停微移、数值更新过渡、数据源面板切换过渡，以及随有效 BPM 变化的心跳与涟漪。
- 趋势图新增渐变面积和移动端点；折线仍表示实际采样，断流超过 15 秒时不跨段连接。
- 侧栏「界面动效」开关会保存到 settings.json；同时遵循 Windows 动画设置。最小化、无有效 BPM 或断开后停止心跳动画。
- Windows 11 使用与界面一致的深色原生标题栏；保留系统拖动、缩放、最小化与最大化操作。
- Source/New-BrandAssets.ps1 可由 Theme.xaml 中的矢量图形重新生成 PNG 与 ICO。
## 使用
解压整个文件夹，双击 PulseBridge.exe。请保留 DLL、BleHelper.exe 等同目录文件。
先选「演示模式」→「连接设备」检查界面；所有模拟值均标记 [演示]。
支持实时数字、最近 60 秒趋势、会话平均/最低/最高、CSV 导出、自定义文本、本地 HTTP、VRChat OSC。
软件自身不通过电脑测量心率，必须从设备或已有采集程序取得数据。

## 设备接入
| 设备 | 推荐路线 | 条件 |
| --- | --- | --- |
| Apple Watch | 官方 Pulsoid 手表/手机 App → 云端 | 登录同一账号，开始测量；电脑填写读取 Token |
| 小米 Smart Band 10 | Share HR → BLE；或手机 Pulsoid → 云端 | 开启设置中的 Share HR，直连时需开放标准 180D/2A37 |
| 小米 Smart Band 7/8/9 | Android Notify → Pulsoid → 云端 | Notify for Xiaomi & Mi Fitness 的 Pulsoid 集成和连续心率，Notify 高级功能可能收费 |
| 小米 Mi Band 3/4/5/6 | Android Pulsoid → 云端；开放标准广播时也可 BLE | 先按官方指南完成手机配对和测量 |
| 华为 Band 8/9/10、Watch Fit 3 | HR data broadcast → BLE 或手机 Pulsoid | 先开启心率广播；不保证其他 GT/Fit 型号 |
| Wear OS / Samsung / Pixel Watch | 官方 Pulsoid 配套 App → 云端 | 需要受支持的型号、系统及 App |
| Polar / Wahoo / Garmin 等 | 标准 BLE 心率广播 → 本地直连 | 仅 ANT+ 广播不可用 |

按型号和固件区分，不能承诺整个品牌的所有产品都支持。普通蓝牙配对成功、健康 App 有历史数据，不等于开放实时心率广播。
上述兼容路线依据官方资料核实，未逐款实机测试。

### BLE
开启 Windows 蓝牙及设备「心率广播/心率共享」，选择 BLE → 扫描 → 选设备 → 连接。
「心率广播」表示广告包含 Heart Rate Service；「待确认」仍需连接后验证服务。
标准服务 UUID：0000180d-0000-1000-8000-00805f9b34fb。
测量特征 UUID：00002a37-0000-1000-8000-00805f9b34fb。
支持 8/16 位数据及接触标志，断线按 2–30 秒退避重试。
某些设备仅运动时广播，或只能连接一个客户端；请解除手机 App 占用后重试。
Apple Watch 通常不开放该通用直连方式，请走云端。
蓝牙辅助组件使用 Windows WinRT 和 .NET Framework 4.8；Windows 11 通常已内置，较旧 Windows 10 需要安装 4.8。

### Pulsoid
先确认官方网页中已收到手表实时心率，再打开 https://pulsoid.net/ui/keys 创建有 data:heart_rate:read 权限的 Token，填写并连接。
官方文档注明：手动创建 Token 需要 BRO 付费方案或试用，本软件不附带第三方订阅。
Token 仅在本次运行内存中，不写入配置、导出或日志；重启需重新填写。
使用官方 WebSocket 接口，按 measured_at 验证时效，断线 1–30 秒退避重连。
电脑和手机均需能访问 Pulsoid；支持广播的设备可以选择不依赖云端的 BLE。

## 状态同步 / NekoTrak
PowerShell/CMD 先测试：
    curl.exe -fsS --max-time 2 http://127.0.0.1:8976/text
输出示例：
    ♥ 78 BPM

若外部软件当前版本支持终端替换，可用「复制状态命令」生成：
    ${curl.exe -fsS --max-time 2 http://127.0.0.1:8976/text}

${...} 是外部软件语法，未在 NekoTrak 中实测；如显示字面文本，请确认该版本终端替换功能与文档。
嵌套调用 PowerShell 本身不是命令必然失败的原因。

附带 status.ps1 提供接口离线回退：
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "完整路径\status.ps1"
程序关闭时输出「♥ -- BPM · 离线」。

模板支持 {bpm} 和 {source}，例如「心率 {bpm} BPM | {source}」。
默认文件位置：%LOCALAPPDATA%\PulseBridge，可点「输出文件夹」打开。
- heart-rate.txt：UTF-8 状态文本。
- heart-rate.json：当前值、来源、演示标志、采样/更新时间。
- settings.json：普通设置，不包含 Token。
- session.lock：避免同时运行多个实例。

15 秒没有新样本时，BPM 变为 --，并显示「数据已过期」；正常断开/退出清空当前值。
异常退出可能留下旧文件，优先使用 HTTP/status.ps1；自行读 JSON 时检查 measured_at 和 updated_at。

## VRChat OSC
在 VRChat 中开启 OSC，然后勾选本程序「同步 VRChat Chatbox」。
发送到本机 UDP 9000（可改端口），地址 /chatbox/input，立即发送、关闭通知音，每 5 秒一次、最多 144 个 Unicode 字符。
演示输出仍带 [演示]；过期/断开输出 --。每次启动默认关闭该发送选项。
已有状态聚合工具时建议由其读取本程序文本，避免多个工具覆盖 Chatbox。
UDP 发送不代表 VRChat 一定已接收，需实机确认。

## 本地 HTTP 桥接
这是给已有采集能力的手机 App/自写脚本的接口，不附带通用手机采集 App。
选择「本地 HTTP 桥接」并连接，点击「复制本地推送示例」在 PowerShell 中测试。
默认仅监听 127.0.0.1:8976：
- GET /health：服务状态。
- GET /text：状态文本。
- GET /heart-rate：当前 JSON。
- POST /heart-rate：推送；必须携带 Authorization: Bearer <本次密钥>。
JSON：{"bpm":78,"measured_at":<Unix毫秒，可省略>}
bpm 为 1–350 整数，拒绝过期、明显未来和乱序样本。省略时间表示发送端声明它是当前样本。
只有 HTTP 模式已连接时允许写入，避免覆盖其他来源。

从手机推送时勾选「允许局域网设备推送」，在指南窗口查看本机 IPv4 与密钥。
LAN 模式的读写均需验证；切换监听设置/重启会更换密钥。只在可信局域网使用，HTTP 没有加密。
必要时自行允许防火墙 TCP 8976，勿映射到公网。程序不自动修改防火墙。
接口限制请求头/请求体大小与超时，不接受分块请求。

## 会话记录
趋势是真实测量值折线，不是 ECG，不生成虚构心电图。
每次连接开始新会话；断开后可导出，重新连接清空之前的样本，不自动保存长期历史。
最多保留 100,000 样本（每秒一个约 27.8 小时），超出后移除最早样本；统计和 CSV 针对保留样本。
CSV 字段：timestamp_utc、bpm、source、demo；时间统一 UTC。
用于个人状态展示，不用于医疗诊断。

## 源码、构建与验证
Source 目录包含完整源码；需要 .NET 10 SDK、.NET Framework 4.8 Developer Pack：
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Source\build.ps1
默认发布自包含 win-x64 程序；离线构建需预先缓存对应运行时和 apphost。
附带已构建的程序包含 .NET 桌面运行时。

运行检查：
    PulseBridge.exe --self-test --report "完整路径\test-report.txt"
指定数据目录：
    PulseBridge.exe --data-dir "D:\PulseBridgeData"

实际验证范围见 TEST-REPORT.txt：
- WPF 编译及演示界面渲染检查。
- BLE 数据解析、时效/乱序处理、状态切换、CSV/JSON/文本。
- HTTP 生产处理器内存流测试：鉴权、推送、读取、异常数据和大小限制。
- OSC 编码检查。
- 本次执行环境禁止本地网络连接，未完成真实 HTTP/UDP 回环收发。
- 原生蓝牙组件成功调用 Windows 扫描接口；系统返回 DisabledByUser（蓝牙关闭），尚未连真实手表。
- 未提供云端 Token，未实测账号链路；NekoTrak 与 VRChat 接收也需要设备验证。

## 官方参考资料（2026-10-15 核实）
Bluetooth：https://www.bluetooth.com/specifications/specs/heart-rate-service-1-0/
Pulsoid WebSocket：https://docs.pulsoid.net/read-heart-rate/read-heart-rate-via-websocket
Token 条件：https://docs.pulsoid.net/access-token-management/manual-token-issuing
Apple Watch：https://blog.pulsoid.net/post/pulsoid-supports-apple-watch-natively
小米：https://blog.pulsoid.net/post/how-to-use-xiaomi-mi-band-3-with-android-pulsoid-app
华为：https://blog.pulsoid.net/post/show-your-heart-rate-on-stream-with-huawei-devices
Wear OS：https://blog.pulsoid.net/post/wearos-devices-are-now-natively-supported-by-pulsoid
VRChat：https://docs.vrchat.com/docs/osc-as-input-controller

