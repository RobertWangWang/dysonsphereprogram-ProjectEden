# 原生代码与 GPU 代码

这一层补充托管 C# 中的外部接口与 GPU 调度实现。入口：[原生文件清单](native-inventory.md)、[原生进度](native-progress.md)、[Shader 目录](shader-catalog.md)。原始游戏文件不修改、不加载执行。

## 原生代码

使用 [Ghidra 12.1.4 官方发行版](https://github.com/NationalSecurityAgency/ghidra/releases/tag/Ghidra_12.1.4_build)，下载压缩包 SHA-256 已核对为 `ddac49f903da9d5bac833e5cc79395098b9c33cfd3279be5f31bd00387d2d4db`。便携工具位于当前任务工作目录，未替换系统 Java 或安装游戏插件。

`tools/dsp_native_inventory.py` 解析 PE 头，排除含 CLR 描述符的托管文件，再调用本机 Visual Studio dumpbin 输出 headers/imports/exports；加 `--disassembly` 导出原生反汇编。目录为 `generated/native/<相对路径以双下划线连接>/`。

`tools/dsp_native_decompile.py` 使用 Ghidra headless 分析并调用 [DspExportNative.java](../../tools/DspExportNative.java)，逐函数保存 C 伪代码、地址、签名、长度、thunk 标志和失败信息。每个函数超时 60 秒，自动分析阶段上限 1800 秒；超时或失败不能当作全部代码已完成。`decompile-manifest.json` 只标记本次导出结束且哈希匹配，完整质量需读取 functions.json 和 headless.log。

```powershell
python -X utf8 tools/dsp_native_inventory.py --disassembly
python -X utf8 tools/dsp_native_decompile.py --ghidra 'Ghidra解压目录' --java-home '包含bin/javac.exe的JDK目录'
python -X utf8 tools/dsp_native_report.py
```

可加 `--module DSPGAME.exe` 只处理一个模块；有效结果按文件与导出脚本哈希复用。这里的 C 是工具重建的伪代码，类型常为 undefined，不是项目可直接编译的源码。

较大模块可用 `--heap 32G` 调整 Java 堆上限，须结合本机可用内存。本次 Mono 分析在 4G 和 32G 上限均出现 OutOfMemoryError，旧日志保留为 headless-4g-oom.log 和 headless-32g-failed.log。随后从 [Unity 官方符号服务器](https://docs.unity3d.com/cn/2020.3/Manual/WindowsDebugging.html) 获取 mono-2.0-bdwgc.pdb，用 `dsp_pdb_identity.py` 核对 PE CodeView 与 PDB 的 GUID/age 后加载；GUID 为 544D4B45-8595-478B-B78C-03782729A807、age 为 1。加载符号后的分析仍发生内存溢出，留下 18,608 个函数伪代码、69 个失败项的阶段输出，没有成功标记。

当前 12/12 个模块的基础导出已完成哈希核验，共 261,368 个函数伪代码、70 个函数级失败；补充恢复 68 个后，Mono 和 UnityPlayer 各 1 个尚未生成完整 C；其中 UnityPlayer 的 VP8 函数现已逐字节匹配上游汇编源码，单独计为源码匹配。该数字表示已导出函数，不表示模块语义全部恢复。`DspRetryNative.java` 可在保存的 Ghidra 项目上以 `-process -noanalysis -readOnly` 单独重试失败函数，增加至 300 秒并记录选项变体；结果保存在 retry 子目录，不覆盖原始失败记录。

Mono 的独立 `--attempt pdb-96g-serial --heap 96G --max-cpu 1 --analysis-timeout 3600` 分析已结束，没有 OOM 或分析超时，保存旧项目供对照。新项目的 69 个失败项中，67 个通过关闭可选语法树转换恢复 C 输出；`ves_icall_RuntimeMethodInfo_get_name` 通过临时等宽未定义参数类型恢复，修改在导出后回滚。补充恢复在新项目上重新执行，剩余 `mono_method_to_ir` 超时；见 [失败函数补充恢复](native-recovery.md)。这次自动分析正常结束，但函数恢复仍未全部完成。

使用 `python -X utf8 tools/dsp_native_select.py --module mono-2.0-bdwgc.dll --attempt pdb-96g-serial` 核验并选择独立尝试。选择记录保存为 generated/native/selected-exports.json，旧目录不覆盖。报告和 `dsp_native_query.py` 自动使用该选择；查询示例：`python -X utf8 tools/dsp_native_query.py mono_method_to_ir --module mono-2.0-bdwgc.dll`。查询保留失败状态，已有补充 C 则显示恢复方式及原始签名。

进一步覆盖审计发现 `mono_method_to_ir` 有两张遗漏的跳转表；正常结束的分析仍只识别了部分方法体。已依据原指令补齐 328 项和 28 项表，暂存分析中的方法体从 34,156 字节增加到 379,619 字节，剩余 computed jump 为 0。这次 C 恢复在 900 秒后超时，未加入完成数。另一个引擎生成的候选文件因控制流或 SIMD 占位问题未计入完成数，详见 [剩余问题与覆盖证据](native-edge-cases.md)。

已核对 DSPGAME.exe 的导入表包含 `UnityPlayer.dll!UnityMain2`。Ghidra 的 `140001000` 函数输出也调用该入口，同时带间接跳转恢复警告。因此可以确认原生入口转交 Unity，但不能拿该伪代码证明完整的启动参数语义。

## GPU 资源与字节码

当前七个序列化资产文件（包括 Resources 内置资产）已扫描：共 540 个图形 Shader、25 个 ComputeShader、10,929 段 DXBC。工具保留 Unity 原始对象、解析元数据、解压程序块、DXBC 和 Windows D3DDisassemble 输出。每段 DXBC 的头部长度已核对；全部 DXBC compute 的线程组在资源元数据与反汇编声明之间一致。基础导出中的 12 个对象含非 DXBC 程序块，阶段目录明确列出，不能声称全部平台已完成。

补充工具 `dsp_compute_platforms.py` 已解析 56 段 GLSL 和 38 段 SPIR-V 计算程序，使用 Khronos spirv-dis 生成 Vulkan 指令并核对线程组。查询见 [非 DXBC compute](compute-platforms.md)。`dsp_graphics_platforms.py` 另外恢复 89 段 GLSL 与 58 段 Metal 图形程序，按元数据引用核验程序类型与条目边界；33 段 Vulkan 压缩载荷已解码为 66 个 SPIR-V 阶段程序，并通过重新编码、解码后的字节一致性检查，见 [图形程序目录](graphics-platforms.md)。基础目录的非 DXBC 提示是其单独运行的结果，补充导出保留在独立清单中。

`tools/dsp_shader_export.py` 依赖 UnityPy 1.25.3，可在专用虚拟环境安装。`tools/dsp_shader_catalog.py` 校验源资产/所有输出哈希、DXBC 长度和 compute 线程组，再生成目录。二进制 TextAsset 另存 payload.bin，不能从已解码字符串重建其原始字节。

104 个 SPIR-V 程序均通过 Khronos spirv-val 校验（38 个计算程序＋66 个图形阶段程序）。可运行 `python -X utf8 tools/dsp_spirv_verify.py --spirv-val 路径/spirv-val.exe` 重验当前游戏资产、补充输出哈希与 SPIR-V；结果记录于 generated/spirv-validation.json。通过该检查表示符合指令格式和校验约束，不等于 GPU 上运行结果已测试。

### 戴森云核对实例

[DysonSwarm C#](generated/full/Assembly-CSharp/source/DysonSwarm.cs) 从 `Configs.GPGPU.DysonSwarmShader` 获取计算 shader，再找到 UpdatePos、UpdateVel、BlitBuffer、AppendNear 四个 kernel。资源 Path ID 10240 对应 DysonSwarm，线程组分别为 256、256、512、128（其余两维均为 1）。C# 使用 GetKernelThreadGroupSizes 读取线程数，以向上取整公式 dispatch；修改线程组时不能只改 C# 一侧的猜测常数。

[UpdatePos 指令](generated/shaders/resources.assets/10240/v0-k0-u0-0.asm) 声明两个 UAV，stride 分别为 32 和 24 字节；资源绑定元数据对应 _SwarmBuffer 和 _SwarmInfoBuffer。C# 的 [DysonSail](generated/full/Assembly-CSharp/source/DysonSail.cs) 刚好包含八个 float，dataLen 为 32。分支内的 mad 以约 0.016667 倍速度推进位置并写回 byte offset 4。这里是 GPU 执行的更新，普通 C# 方法列表无法展示其计算细节。

上述实例仅核验绑定、布局、线程组与位置更新指令；UpdateVel 的吸收轨迹和引力计算还需结合常量缓冲、DysonSailInfo 与节点结构完整分析。字节码成功导出不等于逐段公式均已人工解释完毕。
