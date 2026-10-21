# FanControl.DRGB

为 DeepRGB V5F 控制器提供 FanControl 每路风扇控制与转速反馈。当前构建面向 **FanControl V283 / .NET 10**，使用主程序已有的 `FanControl.Plugins.dll` 与 `HidSharp 2.6.4`，安装包只包含本插件，不替换主程序依赖。

支持表（VID/PID 为十六进制）：

| 控制器 | VID:PID |
|---|---|
| DeepRGB U16 V5F | 2486:3217 |
| DeepRGB C16 V5F | 2486:3229 |
| DeepRGB S16 V5F | 2486:3232 |

U16 V5F 已在本机读取到固件 0.9.212.38、10 个通道。**0.1.3** 对这款经过验证的固件使用风扇专用 `0xCC` 写入零值和切换模式；正值仍用约每秒续发的 `0x99`。修正 99 忽略 0 导致保持先前高速的问题，没有把 0 改成 1。实机零值仍约 890 RPM，**不支持已验证的逐路完全停转**。2026-10-18 反复切换测试期间，用户肉眼确认“灯效连续，没有重播”。详见 [零值原因与证据](docs/zero-percent.md)、[验证记录](VALIDATION.zh-CN.md)。协议来源为 DRGB 2.1.0.3 的 REA 分析、匹配官方固件的离线分析和实机通讯；设备数量由握手读取。

## 安装与使用

1. 解压 `FanControl.DRGB-0.1.3.zip`。
2. 退出 **DRGB** 和 **FanControl**。DRGB 与插件使用同一个 HID 请求/响应通道，同时运行会争抢通讯。也请退出正在访问此控制器的 SignalRGB 或其他工具；插件自动检测的是 `DRGB.exe`。
3. 运行解压目录中的 `install.ps1`，或者把 `FanControl.DRGB.dll` 复制到 `C:\Program Files (x86)\FanControl\Plugins\`。受安装目录权限限制时，在有该目录写权限的终端操作。安装脚本只复制此 DLL；已有版本会先备份为 `.bak`。
4. 启动 FanControl，在插件设置中启用 **DeepRGB V5F**，刷新传感器。
5. 每个通道会出现一项 `DeepRGB ... Fan N` 转速传感器和一项同名控制项，两者自动配对。启用控制项，分配固定百分比或 FanControl 曲线即可。初始化和加载只查询状态，不接管未启用的通道。
6. 禁用某个控制项或正常退出/刷新插件时，恢复该路接管前的存储模式与设置值。原模式为外部曲线时，以原存储值作为退出后的运行目标；它不等于接管前未知的即时曲线输出。

插件使用 `IPlugin3.Update` 集中读取状态。`Set` 先排队，多路正值在下一次更新中合并为一次 `0x99`，恒定正值也约每秒续发，避免临时指令过期回落；实际周期受 FanControl 更新频率影响。校验 ACK 后等待 200ms，EE 回读确认存储配置完整。经过验证的 U16 零值使用 CC 暂设 `S=1,V=0` 并回读，正值恢复外部模式，关闭控制恢复原配置。只剩零目标或完全空闲时不续发 99。控制百分比是已确认命令，RPM 是独立反馈；不把目标值当作电气占空比实测。

如果运行期间重新启动 DRGB，插件会释放 HID 并让出控制，不再用旧快照覆盖 DRGB 的新设置。关闭 DRGB 后会请求 FanControl 刷新并重新接入。拔插设备可自动请求刷新；通讯中断时读数显示不可用，约 5 秒后尝试重连。

## 反馈与恢复的限制

- 厂商程序把返回值 **大于 100** 显示为 RPM，0～100 显示为百分比。本插件不把这些百分比伪装成 RPM，因此这些情况下转速传感器为“不可用”，包括不能区分为 0 RPM 的零值。
- U16 / 固件 0.9.212.38 / 10 路的模式切换和恢复均用 CC，不发 F0。其他固件/型号的零值会明确拒绝；正值仍保留旧协议路径，主板/固定模式的转换和恢复可能触发 F0 灯效重播，尚未实机验证。
- 该 U16 的 `0x99` 中 0 表示忽略该路，而非应用零目标。非接管外部通道仍填其当前存储回退值；非零回退值会覆盖此前未知的即时输出，不能保证保留原动态曲线。存储值为 0 的非接管路填 0，保持该路即时目标。主板/固定模式也填 0。插件只临时修改接管路的存储模式和值。
- 曲线由 FanControl 执行，不读取温度；温度源由 FanControl 自己提供。若此前用厂商软件将某组风扇设为全局停转，应先在 DRGB 中恢复开启，再退出 DRGB 使用 FanControl。
- 正常退出时尽力恢复设置。强制终止、断电、USB 失联等情况不能保证恢复，也没有假定控制器带硬件失联看门狗。
- 如果接管前是 DRGB 的自定义曲线模式，恢复的是存储模式和值及该存储值对应的运行目标；继续执行原曲线仍需要重新运行 DRGB。
- C16/S16 的设备 ID 和协议入口来自同一安装包设备表，尚不能据此宣称它们已做实机测试。

## 构建与离线测试

需要 .NET 10 SDK 以及一份已安装的 FanControl V283。没有第三方 NuGet 包依赖。主程序提供插件 API 与 HidSharp，仅作为编译引用。

```powershell
.\build.ps1 -FanControlDir 'C:\Program Files (x86)\FanControl'
```

也可用 `-DotNet 'X:\path\dotnet.exe'` 指定本地 SDK。脚本编译插件、执行模拟协议测试、编译诊断工具，生成 `dist\FanControl.DRGB-0.1.3.zip`。

43 项测试覆盖报文、坏包拒绝、恢复、断线与生命周期，包含 99 零值忽略、CC 零值及正值转换、混合通道续发、20 秒保持、丢 ACK、原零回退值恢复和固件门限。配对测试使用已安装 FanControl 的实际包装类；离线测试不打开真实 HID。

## 独立诊断

构建后：

```powershell
# 只枚举，不打开 HID 流
dotnet run --project tools/Drgb.Diagnostics -- --list

# 必须先退出 FanControl 与 DRGB；只握手/查询状态
dotnet run --project tools/Drgb.Diagnostics -- --probe

# 真实调速：仅用于明确准备进行硬件测试时；结束后回读确认恢复
dotnet run --project tools/Drgb.Diagnostics -- --exercise --channel 1 --percent 80 --seconds 3
```

如无设备，确认 USB 连接与设备 ID。插件日志在 FanControl 的错误日志中，以 `[FanControl.DRGB]` 开头。日志会解释 DRGB 冲突、握手不兼容、包长不匹配、设备失联与设置未确认。

## 依据与许可

- [FanControl 官方插件接口](https://github.com/Rem0o/FanControl.Releases/wiki/Plugins)
- [本项目使用的协议与证据](docs/protocol.md)
- 本项目新编写的代码采用 MIT；未打包 DRGB 原程序、FanControl API 或 HidSharp DLL。
