# 传送带、货物与增产字段

证据：[Cargo.cs](generated/Cargo.cs)、[CargoContainer.cs](generated/CargoContainer.cs)、[CargoPath.cs](generated/CargoPath.cs)、[CargoTraffic.cs](generated/CargoTraffic.cs)。版本见 [baseline.json](baseline.json)。

## 原版数据与路径

| 类型 | 已核验职责 |
|---|---|
| `Cargo` | 单份货物：`byte stack`、`byte inc`、`short item`、位置和旋转 |
| `CargoContainer` | 货物池、回收 ID、添加/删除、GPU 缓冲和序列化 |
| `CargoPath` | 路径上的插入、挤压空间、取出及末端交接 |
| `CargoTraffic` | 传送带实体到路径的转接；分拣、喷涂、集装等 tick 入口 |

`CargoPath.TryInsertItem` 和多组 `TryPickItem` 原版签名使用 `byte` 的 stack/inc，取出还有 `out byte`。只把 Cargo 字段改为较宽类型，不会自动修复调用参数、局部变量、间接读写和强制转换。

原版 Cargo 数据长度是 32，`CargoContainer` 也用 stride 32 创建 `ComputeBuffer`。字段布局变化时，CPU 内存与渲染上传的数据布局必须一起检查，不能仅以「编译通过」认定兼容。

`Cargo.accTableMilli` 和 `incTableMilli` 均有 11 项（索引 0..10）。最高项分别为加速增量 2.5 和额外产出 0.4；最高档加速是基础速度乘 3.5，而非乘 2.5。原版常用喷涂档位与数组能够表示的最大档位是两回事。

## 原版存档与 mod 改写

`CargoContainer.Export` 写出块版本 2，然后按字段写货物；`Import` 的对应路径用 `ReadByte` 读 stack/inc。导出值的类型决定占用字节，内存加宽后必须处理序列化版本与旧档读取。

ProjectEden 的 [preloader](../../ProjectEden.Preloader) 将货物通道加宽，并由 `SerializationFixer` 处理货物块版本及兼容读取；运行时还有 [Cargo 补丁](../../ProjectEden/src/Patches/Cargo) 与品质通道。

因此本库显示 `byte` 是**原版磁盘代码**，不代表正在运行的 mod 没加宽。要查最终类型，应读 preloader 检查报告或运行时诊断。不要据反编译原文重新把 Harmony 参数写回 byte。

## 排错与搜索

```powershell
python -X utf8 tools/dsp_knowledge.py search CargoPath::TryPickItem
python -X utf8 tools/dsp_knowledge.py search CargoContainer::AddCargo --callers
powershell -ExecutionPolicy Bypass -File tools/verify_preloader.ps1 -Config Release
```

增产溢出或集装数量截断，要沿「库存 → 分拣器 → 路径 → Cargo → 路径 → 库存」检查全链路。分拣器、集装机、喷涂机和分流器现已导出，重点机制见 [传送带配套设备](belt-devices.md)；完整索引仍不能替代对每条改写路径的验证。
