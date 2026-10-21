# 协议依据

针对 DRGB.dll 2.1.0.3，SHA256 `2a5ed7d67d1c3b1b288950fe4b9a142c3d9b0523af157e87d1dd2c93c8c3201f`，MVID `cc77d880-33af-4c5c-a78f-5260242061ba`，通过 REA 检查原始元数据与 CIL。表内方法为主机软件的静态证据；另在下文注明固件 0.9.212.38 的实际通讯结果，未取得固件源码。

| 用途 | 请求/返回布局 | 方法证据 |
|---|---|---|
| 握手 | 请求 `00 02 00`；返回 `[2]==0 && [3]==9`，`[7]` 为通道数 | InitDevice `0600008D` |
| 查询 | 请求 `00 30 33 EE` | ReadFansData `060000BA` |
| 查询响应 | `[2..4]=30 33 EE`，`[5]=全局模式`，`[6]=通道数`；从 `[7]` 起每路 5 字节 | ReadFansData `060000BA`，回调 `06000507` |
| 每路响应 | `未解释字节 / 模式 / 设置值 / 读数高字节 / 读数低字节` | 同上 |
| 设置 | `00 30 33 F0 M 00`，随后每路 `00 S V` | WriteFansData `060000BB` |
| 即时调速 | `00 30 33 99 00 00`，随后每路 `00 00 P` | ControllerAsyncFanUpdater.UpdateDevice `06000065` |
| 读数合并 | `(high << 8) | low` | HexHelper.MergeByte `0600035D` |
| 传输 | 同一锁内 HidStream.Write 后 HidStream.Read | WriteDataForRead `060000B7` |

模式 S=0 为主板模式，S=1 为固定值，S=2..5 为厂商主机曲线/外部运行时控制。0.1.1 对已处于 S=2..5 的通道直接用 0x99；只在 S=0/1 首次接管及恢复这些模式时用 F0。曲线由 FanControl 驱动。

0x99 需要持续续发。原厂 `MonitorService::<Run>b__6_0 06000042` 每轮发送更新消息后 `Thread.Sleep(1000)` 并无条件循环，Updater 每次处理均发送 99，没有相同百分比去重。0.1.1 只在目标变化时发送的策略已被实机证明不能持续保持目标；0.1.2 改为有接管通道时约每秒续发（在下一个 FanControl Update 执行），并保留目标变化立即排入下次 Update 的行为。见 [转速保持验证](runtime-hold.md)。

F0 的 M 是动作选择器：0/1 为内置灯效开启/关闭，2/3 为同步，8 为恢复出厂，9/10 为风扇组启停，11/12 为低于 25% 停转策略开关。将 EE 返回的 M=0 每次写回，会重复发出灯效启动动作。M=9 不能作为安全的逐路设置替代：实机没有应用附带的调速参数。详见 [灯效重播排查](lighting-reset.md)。

原厂 UpdateDevice 只对 S=2..5 计算 P，S=0/1 填 0。后续匹配 U16 0.9.212.38 固件分析及实测确认：99 的 P=0 跳过该路目标，P=1..100 才应用。本插件给非接管的外部模式通道填其当前存储值，非零回退值会替换未知的即时输出，存储值0则保持原即时目标。

0.1.3 对已验证的 U16 / 0.9.212.38 / 10 路使用固件中的 `00 30 33 CC 00 00 + [00,S,V]×10`，绕过 F0 全局灯效配置。零目标暂设 S1/V0，正值恢复外部模式并续发99，Reset/Close用CC恢复原配置。原程序DLL没有使用CC；这是匹配官方固件的普通工具离线分析和实机通讯证据，不属于REA原生引擎输出。其他型号/版本不启用CC，拒绝零值。详见 [零值排查及固件地址](zero-percent.md)。

厂商显示分支 `ReadFansData IL_00BC..0117`：读数 >100 为 RPM，否则为百分比。本插件只在 >100 时给 FanControl 提供 RPM。

厂商只消费设置 ACK，不校验内容。本插件的 F0 设置额外 EE 回读，核对修改通道的模式和值；CC 回读核对所有通道模式、值及 global。运行时 0x99 校验 ACK 的 `[2..4]=30 33 99`、通道数和包长，再 EE 回读确认全部存储配置及全局模式未变。实机 0.9.212.38 在目标 80% 生效后仍回报存储值 40，所以控制百分比表示已确认命令，不能假装为即时占空比实测值。零值确认来自CC后S1/V0回读，不把99零ACK当成功。RPM 单独反馈。ACK 丢失时可能已完成写入，所以写前保存恢复快照，不能以超时推断设备没有改变。

HID 输出按设备描述的报告长度补零；不写死 64/65 字节。握手数、状态数和完整包长必须一致，不完整响应不会用来生成写包。

关键 REA Evidence：

- `ev_960f0125cd68da6fc45ae3e727051618cf926ba64d792df8a7a0e058f2483d0b`：ControllerDRGBDevice、Updater、DeviceInfo 等重点类型。
- `ev_ff027590007db0576178873f8d64c3a581583578c8c05fde8496ebd45161ba1a`：响应验证回调。
- `ev_ac6188d628ccffefb34ddd662011101004af836db2ba74f1f2d93cd65d5955a4`：高低字节合并。
- `ev_4975d5c11b5345dcc0da4c33e17ad60df6513c94fae31bb834676ef0a2bbe5b2`：型号设备表、每路模式和全局模式。

原始分析包包含本机路径和设备记录，保留在本地；公开源码提供方法锚点、目标哈希及协议摘要。未从 PC DLL 推断 PWM 频率、电平、每转脉冲数、转速测量机制或固件持久化机制。

## FanControl V283 的配对 ID

实机界面验证发现，API 的原始 `sensor.Id` 不能直接作为配对 ID。REA 检查 FanControl.Library.dll：`PluginSensor.get_Identifier`（token `060004D3`）返回 `origin + "/" + sensor.Id`，origin 来自插件 Name；`PluginControlSensor::.ctor`（`060004CE`）将 `PairedFanSensorId` 原样赋给 ISensorId.Identifier，不再补前缀。因此本插件返回 `DeepRGB V5F/` 加上原始 RPM sensor.Id。

REA Evidence：`ev_ad242c670e74aa978063962ada514710248146a687ddea5899af6810b5c2bd5e`。同时通过反射构造已安装 V283 的实际 PluginSensor/PluginControlSensor 包装类，验证二者标识相等，不用模拟包装规则代替此项集成测试。
