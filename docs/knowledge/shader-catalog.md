# Shader 与 compute 全量目录

当前 7 个 Unity 序列化文件（含 Resources 内置资产）中提取 540 个 Shader、25 个 ComputeShader、2798 个 MonoScript 记录及 10 个 TextAsset，成功反汇编 **10,929 个 DXBC 程序**。核验资产/输出哈希、DXBC 长度、全部 compute 线程组与字节码声明一致。

来源版本为本机资产，哈希在 generated/shaders/manifest.json。工具为 UnityPy 1.25.3 与 Windows 系统 D3DDisassemble。asm 是 GPU 指令，metadata.json 是资源结构；均不声称恢复了原始 HLSL。TextAsset 的原始 payload.bin 单独保存，避免二进制数据经过文本解码失真。

**基础导出范围：12 个对象包含非 DXBC 程序块，此目录只统计 DXBC。** 其他平台的补充解析见 [非 DXBC compute](compute-platforms.md) 和 [图形程序](graphics-platforms.md)；下表保留基础导出发现的全部提示，是否已解析以补充清单为准。

| 对象 | 基础导出待处理项 |
|---|---|
| Resources/unity default resources / 17 Hidden/InternalErrorShader | blob1: no DXBC found; raw blob retained; blob2: no DXBC found; raw blob retained; blob3: no DXBC found; raw blob retained; blob4: no DXBC found; raw blob retained; blob5: no DXBC found; raw blob retained |
| Resources/unity default resources / 68 Hidden/InternalClear | blob1: no DXBC found; raw blob retained; blob2: no DXBC found; raw blob retained; blob3: no DXBC found; raw blob retained; blob4: no DXBC found; raw blob retained; blob5: no DXBC found; raw blob retained |
| Resources/unity default resources / 69 Hidden/Internal-Colored | blob1: no DXBC found; raw blob retained; blob2: no DXBC found; raw blob retained; blob3: no DXBC found; raw blob retained; blob4: no DXBC found; raw blob retained; blob5: no DXBC found; raw blob retained |
| Resources/unity default resources / 70 Hidden/Internal-Loading | blob1: no DXBC found; raw blob retained; blob2: no DXBC found; raw blob retained; blob3: no DXBC found; raw blob retained; blob4: no DXBC found; raw blob retained; blob5: no DXBC found; raw blob retained |
| Resources/unity default resources / 300 Internal-Skinning | v1-k0-u0: no DXBC found; raw blob retained; v1-k1-u0: no DXBC found; raw blob retained; v1-k2-u0: no DXBC found; raw blob retained; v1-k3-u0: no DXBC found; raw blob retained; v1-k4-u0: no DXBC found; raw blob retained; v1-k5-u0: no DXBC found; raw blob retained; v1-k6-u0: no DXBC found; raw blob retained; v1-k7-u0: no DXBC found; raw blob retained; v1-k8-u0: no DXBC found; raw blob retained; v1-k9-u0: no DXBC found; raw blob retained; v1-k10-u0: no DXBC found; raw blob retained; v1-k11-u0: no DXBC found; raw blob retained; v1-k12-u0: no DXBC found; raw blob retained; v1-k13-u0: no DXBC found; raw blob retained; v1-k14-u0: no DXBC found; raw blob retained; v1-k15-u0: no DXBC found; raw blob retained; v2-k0-u0: no DXBC found; raw blob retained; v2-k1-u0: no DXBC found; raw blob retained; v2-k2-u0: no DXBC found; raw blob retained; v2-k3-u0: no DXBC found; raw blob retained; v2-k4-u0: no DXBC found; raw blob retained; v2-k5-u0: no DXBC found; raw blob retained; v2-k6-u0: no DXBC found; raw blob retained; v2-k7-u0: no DXBC found; raw blob retained; v2-k8-u0: no DXBC found; raw blob retained; v2-k9-u0: no DXBC found; raw blob retained; v2-k10-u0: no DXBC found; raw blob retained; v2-k11-u0: no DXBC found; raw blob retained; v2-k12-u0: no DXBC found; raw blob retained; v2-k13-u0: no DXBC found; raw blob retained; v2-k14-u0: no DXBC found; raw blob retained; v2-k15-u0: no DXBC found; raw blob retained; v4-k0-u0: no DXBC found; raw blob retained; v4-k1-u0: no DXBC found; raw blob retained; v4-k2-u0: no DXBC found; raw blob retained; v4-k3-u0: no DXBC found; raw blob retained; v4-k4-u0: no DXBC found; raw blob retained; v4-k5-u0: no DXBC found; raw blob retained; v4-k6-u0: no DXBC found; raw blob retained; v4-k7-u0: no DXBC found; raw blob retained; v4-k8-u0: no DXBC found; raw blob retained; v4-k9-u0: no DXBC found; raw blob retained; v4-k10-u0: no DXBC found; raw blob retained; v4-k11-u0: no DXBC found; raw blob retained; v4-k12-u0: no DXBC found; raw blob retained; v4-k13-u0: no DXBC found; raw blob retained; v4-k14-u0: no DXBC found; raw blob retained; v4-k15-u0: no DXBC found; raw blob retained |
| Resources/unity default resources / 301 Internal-BlendShape | v1-k0-u0: no DXBC found; raw blob retained; v1-k1-u0: no DXBC found; raw blob retained; v1-k2-u0: no DXBC found; raw blob retained; v2-k0-u0: no DXBC found; raw blob retained; v2-k1-u0: no DXBC found; raw blob retained; v2-k2-u0: no DXBC found; raw blob retained; v4-k0-u0: no DXBC found; raw blob retained; v4-k1-u0: no DXBC found; raw blob retained; v4-k2-u0: no DXBC found; raw blob retained |
| Resources/unity default resources / 400 Internal-VT-TranslationTableReplace | v1-k0-u0: no DXBC found; raw blob retained; v2-k0-u0: no DXBC found; raw blob retained; v4-k0-u0: no DXBC found; raw blob retained |
| Resources/unity default resources / 401 Internal-VT-TranslationTableUpsample | v1-k0-u0: no DXBC found; raw blob retained; v1-k1-u0: no DXBC found; raw blob retained; v1-k2-u0: no DXBC found; raw blob retained; v1-k3-u0: no DXBC found; raw blob retained; v1-k4-u0: no DXBC found; raw blob retained; v1-k5-u0: no DXBC found; raw blob retained; v1-k6-u0: no DXBC found; raw blob retained; v1-k7-u0: no DXBC found; raw blob retained; v1-k8-u0: no DXBC found; raw blob retained; v1-k9-u0: no DXBC found; raw blob retained; v1-k10-u0: no DXBC found; raw blob retained; v1-k11-u0: no DXBC found; raw blob retained; v1-k12-u0: no DXBC found; raw blob retained; v1-k13-u0: no DXBC found; raw blob retained; v1-k14-u0: no DXBC found; raw blob retained; v1-k15-u0: no DXBC found; raw blob retained; v3-k0-u0: no DXBC found; raw blob retained; v3-k1-u0: no DXBC found; raw blob retained; v3-k2-u0: no DXBC found; raw blob retained; v3-k3-u0: no DXBC found; raw blob retained; v3-k4-u0: no DXBC found; raw blob retained; v3-k5-u0: no DXBC found; raw blob retained; v3-k6-u0: no DXBC found; raw blob retained; v3-k7-u0: no DXBC found; raw blob retained; v3-k8-u0: no DXBC found; raw blob retained; v3-k9-u0: no DXBC found; raw blob retained; v3-k10-u0: no DXBC found; raw blob retained; v3-k11-u0: no DXBC found; raw blob retained; v3-k12-u0: no DXBC found; raw blob retained; v3-k13-u0: no DXBC found; raw blob retained; v3-k14-u0: no DXBC found; raw blob retained; v3-k15-u0: no DXBC found; raw blob retained |
| Resources/unity default resources / 600 Internal-CreateFoveatedShadingRateTextureArray | v0-k0-u0: no DXBC found; raw blob retained |
| Resources/unity default resources / 601 Internal-CreateFoveatedShadingRateTextureNoArray | v2-k0-u0: no DXBC found; raw blob retained |
| Resources/unity default resources / 10101 GUI/Text Shader | blob1: no DXBC found; raw blob retained; blob2: no DXBC found; raw blob retained; blob3: no DXBC found; raw blob retained; blob4: no DXBC found; raw blob retained; blob5: no DXBC found; raw blob retained |
| Resources/unity default resources / 10755 Hidden/FrameDebuggerRenderTargetDisplay | blob1: no DXBC found; raw blob retained; blob2: no DXBC found; raw blob retained; blob3: no DXBC found; raw blob retained; blob4: no DXBC found; raw blob retained; blob5: no DXBC found; raw blob retained |

重建：用安装 UnityPy 的 Python 执行 `tools/dsp_shader_export.py`，再执行 `tools/dsp_shader_catalog.py`。原始资源、字节码、指令和详细元数据均保留在被 Git 忽略的 generated/shaders。

## Compute kernels

| ComputeShader | Kernel | 线程组 | 指令 |
|---|---|---|---|
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v0-k0-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v0-k1-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v0-k2-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v0-k3-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v0-k4-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v0-k5-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v0-k6-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v0-k7-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v0-k8-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v0-k9-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v0-k10-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v0-k11-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v0-k12-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v0-k13-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v0-k14-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v0-k15-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v3-k0-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v3-k1-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v3-k2-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v3-k3-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v3-k4-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v3-k5-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v3-k6-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v3-k7-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v3-k8-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v3-k9-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v3-k10-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v3-k11-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v3-k12-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v3-k13-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v3-k14-u0-0.asm) |
| Internal-Skinning | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/300/v3-k15-u0-0.asm) |
| Internal-BlendShape | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/301/v0-k0-u0-0.asm) |
| Internal-BlendShape | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/301/v0-k1-u0-0.asm) |
| Internal-BlendShape | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/301/v0-k2-u0-0.asm) |
| Internal-BlendShape | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/301/v3-k0-u0-0.asm) |
| Internal-BlendShape | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/301/v3-k1-u0-0.asm) |
| Internal-BlendShape | main | 64 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/301/v3-k2-u0-0.asm) |
| Internal-VT-TranslationTableReplace | ReplaceTranslationTable | 256 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/400/v0-k0-u0-0.asm) |
| Internal-VT-TranslationTableReplace | ReplaceTranslationTable | 256 × 1 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/400/v3-k0-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v0-k0-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v0-k1-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v0-k2-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v0-k3-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v0-k4-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v0-k5-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v0-k6-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v0-k7-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v0-k8-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v0-k9-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v0-k10-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v0-k11-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v0-k12-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v0-k13-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v0-k14-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v0-k15-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v2-k0-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v2-k1-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v2-k2-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v2-k3-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v2-k4-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v2-k5-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v2-k6-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v2-k7-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v2-k8-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v2-k9-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v2-k10-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v2-k11-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v2-k12-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v2-k13-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v2-k14-u0-0.asm) |
| Internal-VT-TranslationTableUpsample | Main | 32 × 32 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/401/v2-k15-u0-0.asm) |
| Internal-CreateFoveatedShadingRateTextureNoArray | CreateFoveatedShadingRateTexture | 8 × 8 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/601/v0-k0-u0-0.asm) |
| Internal-CreateFoveatedShadingRateTextureNoArray | CreateFoveatedShadingRateTexture | 8 × 8 × 1 | [asm](generated/shaders/Resources/unity%20default%20resources/601/v1-k0-u0-0.asm) |
| WarningTransform | TransformData | 128 × 1 × 1 | [asm](generated/shaders/resources.assets/10223/v0-k0-u0-0.asm) |
| DFSLancerCulling | _1LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10224/v0-k0-u0-0.asm) |
| DFSLancerCulling | _2LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10224/v0-k1-u0-0.asm) |
| DFSLancerCulling | _3LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10224/v0-k2-u0-0.asm) |
| DFSLancerCulling | _4LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10224/v0-k3-u0-0.asm) |
| DFSLancerCulling | _StarmapCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10224/v0-k4-u0-0.asm) |
| CombatStatProcedure | CombatStatProcedure | 64 × 1 × 1 | [asm](generated/shaders/resources.assets/10225/v0-k0-u0-0.asm) |
| CloudCulling | AppendMain | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10226/v0-k0-u0-0.asm) |
| CloudCulling | AppendExpensive | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10226/v0-k1-u0-0.asm) |
| CloudCulling | AppendCheap | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10226/v0-k2-u0-0.asm) |
| DFSAntCulling | _AntCulling | 64 × 1 × 1 | [asm](generated/shaders/resources.assets/10227/v0-k0-u0-0.asm) |
| DFSFortressCulling | _1LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10228/v0-k0-u0-0.asm) |
| DFSFortressCulling | _2LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10228/v0-k1-u0-0.asm) |
| DFSFortressCulling | _3LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10228/v0-k2-u0-0.asm) |
| DFSFortressCulling | _4LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10228/v0-k3-u0-0.asm) |
| DFSFortressCulling | _StarmapCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10228/v0-k4-u0-0.asm) |
| PlanetATFieldGenerate | FieldGenerate | 256 × 1 × 1 | [asm](generated/shaders/resources.assets/10229/v0-k0-u0-0.asm) |
| EntitySignFilter | Filter | 128 × 1 × 1 | [asm](generated/shaders/resources.assets/10230/v0-k0-u0-0.asm) |
| GetAreaColliders | CSMain | 8 × 1 × 1 | [asm](generated/shaders/resources.assets/10231/v0-k0-u0-0.asm) |
| PlanetATField2Graph | RenderTriangles | 32 × 32 × 1 | [asm](generated/shaders/resources.assets/10232/v0-k0-u0-0.asm) |
| SpaceLODCulling | _NOLODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10233/v0-k0-u0-0.asm) |
| SpaceLODCulling | _1LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10233/v0-k1-u0-0.asm) |
| SpaceLODCulling | _2LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10233/v0-k2-u0-0.asm) |
| SpaceLODCulling | _3LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10233/v0-k3-u0-0.asm) |
| SpaceLODCulling | _4LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10233/v0-k4-u0-0.asm) |
| SpaceLODCulling | _StarmapCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10233/v0-k5-u0-0.asm) |
| SpaceLODCulling | _ForceLOD1 | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10233/v0-k6-u0-0.asm) |
| DFGRangerCulling | _1LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10234/v0-k0-u0-0.asm) |
| DFGRangerCulling | _2LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10234/v0-k1-u0-0.asm) |
| DFGRangerCulling | _3LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10234/v0-k2-u0-0.asm) |
| DFGRangerCulling | _4LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10234/v0-k3-u0-0.asm) |
| DFSHumpbackCulling | _1LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10235/v0-k0-u0-0.asm) |
| DFSHumpbackCulling | _2LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10235/v0-k1-u0-0.asm) |
| DFSHumpbackCulling | _3LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10235/v0-k2-u0-0.asm) |
| DFSHumpbackCulling | _4LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10235/v0-k3-u0-0.asm) |
| DFSHumpbackCulling | _StarmapCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10235/v0-k4-u0-0.asm) |
| LODCulling | _1LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10236/v0-k0-u0-0.asm) |
| LODCulling | _2LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10236/v0-k1-u0-0.asm) |
| LODCulling | _3LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10236/v0-k2-u0-0.asm) |
| LODCulling | _4LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10236/v0-k3-u0-0.asm) |
| EyeHistogram | KEyeHistogram | 16 × 16 × 1 | [asm](generated/shaders/resources.assets/10237/v0-k0-u0-0.asm) |
| DFGGuardianCulling | _1LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10238/v0-k0-u0-0.asm) |
| DFGGuardianCulling | _2LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10238/v0-k1-u0-0.asm) |
| DFGGuardianCulling | _3LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10238/v0-k2-u0-0.asm) |
| DFGGuardianCulling | _4LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10238/v0-k3-u0-0.asm) |
| DFGTruckCulling | _TruckCulling | 64 × 1 × 1 | [asm](generated/shaders/resources.assets/10239/v0-k0-u0-0.asm) |
| DysonSwarm | UpdatePos | 256 × 1 × 1 | [asm](generated/shaders/resources.assets/10240/v0-k0-u0-0.asm) |
| DysonSwarm | UpdateVel | 256 × 1 × 1 | [asm](generated/shaders/resources.assets/10240/v0-k1-u0-0.asm) |
| DysonSwarm | BlitBuffer | 512 × 1 × 1 | [asm](generated/shaders/resources.assets/10240/v0-k2-u0-0.asm) |
| DysonSwarm | AppendNear | 128 × 1 × 1 | [asm](generated/shaders/resources.assets/10240/v0-k3-u0-0.asm) |
| DFGRaiderCulling | _1LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10241/v0-k0-u0-0.asm) |
| DFGRaiderCulling | _2LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10241/v0-k1-u0-0.asm) |
| DFGRaiderCulling | _3LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10241/v0-k2-u0-0.asm) |
| DFGRaiderCulling | _4LODCulling | 16 × 1 × 1 | [asm](generated/shaders/resources.assets/10241/v0-k3-u0-0.asm) |

## 图形 Shader

| 来源 / Path ID | 名称 | DXBC 数 | 元数据 |
|---|---|---:|---|
| globalgamemanagers.assets / 5 | VF Override/Halo | 2 | [json](generated/shaders/globalgamemanagers.assets/5/metadata.json) |
| globalgamemanagers.assets / 6 | VF Overwrite/DepthNormalsTexture | 166 | [json](generated/shaders/globalgamemanagers.assets/6/metadata.json) |
| Resources/unity default resources / 17 | Hidden/InternalErrorShader | 16 | [json](generated/shaders/Resources/unity%20default%20resources/17/metadata.json) |
| Resources/unity default resources / 68 | Hidden/InternalClear | 8 | [json](generated/shaders/Resources/unity%20default%20resources/68/metadata.json) |
| Resources/unity default resources / 69 | Hidden/Internal-Colored | 8 | [json](generated/shaders/Resources/unity%20default%20resources/69/metadata.json) |
| Resources/unity default resources / 70 | Hidden/Internal-Loading | 8 | [json](generated/shaders/Resources/unity%20default%20resources/70/metadata.json) |
| Resources/unity default resources / 10101 | GUI/Text Shader | 8 | [json](generated/shaders/Resources/unity%20default%20resources/10101/metadata.json) |
| Resources/unity default resources / 10755 | Hidden/FrameDebuggerRenderTargetDisplay | 10 | [json](generated/shaders/Resources/unity%20default%20resources/10755/metadata.json) |
| Resources/unity_builtin_extra / 6 | Legacy Shaders/VertexLit | 12 | [json](generated/shaders/Resources/unity_builtin_extra/6/metadata.json) |
| Resources/unity_builtin_extra / 7 | Legacy Shaders/Diffuse | 30 | [json](generated/shaders/Resources/unity_builtin_extra/7/metadata.json) |
| Resources/unity_builtin_extra / 19 | Hidden/Internal-StencilWrite | 2 | [json](generated/shaders/Resources/unity_builtin_extra/19/metadata.json) |
| Resources/unity_builtin_extra / 64 | Hidden/Internal-ScreenSpaceShadows | 20 | [json](generated/shaders/Resources/unity_builtin_extra/64/metadata.json) |
| Resources/unity_builtin_extra / 65 | Hidden/Internal-CombineDepthNormals | 2 | [json](generated/shaders/Resources/unity_builtin_extra/65/metadata.json) |
| Resources/unity_builtin_extra / 66 | Hidden/BlitCopy | 2 | [json](generated/shaders/Resources/unity_builtin_extra/66/metadata.json) |
| Resources/unity_builtin_extra / 67 | Hidden/BlitCopyDepth | 2 | [json](generated/shaders/Resources/unity_builtin_extra/67/metadata.json) |
| Resources/unity_builtin_extra / 68 | Hidden/ConvertTexture | 2 | [json](generated/shaders/Resources/unity_builtin_extra/68/metadata.json) |
| Resources/unity_builtin_extra / 69 | Hidden/Internal-DeferredShading | 54 | [json](generated/shaders/Resources/unity_builtin_extra/69/metadata.json) |
| Resources/unity_builtin_extra / 74 | Hidden/Internal-DeferredReflections | 6 | [json](generated/shaders/Resources/unity_builtin_extra/74/metadata.json) |
| Resources/unity_builtin_extra / 75 | Hidden/Internal-MotionVectors | 5 | [json](generated/shaders/Resources/unity_builtin_extra/75/metadata.json) |
| Resources/unity_builtin_extra / 102 | Hidden/Internal-Flare | 2 | [json](generated/shaders/Resources/unity_builtin_extra/102/metadata.json) |
| Resources/unity_builtin_extra / 107 | Hidden/BlitCopyWithDepth | 2 | [json](generated/shaders/Resources/unity_builtin_extra/107/metadata.json) |
| Resources/unity_builtin_extra / 109 | Hidden/BlitToDepth | 2 | [json](generated/shaders/Resources/unity_builtin_extra/109/metadata.json) |
| Resources/unity_builtin_extra / 110 | Hidden/BlitToDepth_MSAA | 2 | [json](generated/shaders/Resources/unity_builtin_extra/110/metadata.json) |
| Resources/unity_builtin_extra / 111 | Hidden/BlitCopyHDRTonemap | 2 | [json](generated/shaders/Resources/unity_builtin_extra/111/metadata.json) |
| Resources/unity_builtin_extra / 112 | Hidden/BlitCopyHDRTonemappedToHDRTonemap | 2 | [json](generated/shaders/Resources/unity_builtin_extra/112/metadata.json) |
| Resources/unity_builtin_extra / 113 | Hidden/Internal-DebugPattern | 4 | [json](generated/shaders/Resources/unity_builtin_extra/113/metadata.json) |
| Resources/unity_builtin_extra / 114 | Hidden/BlitCopyHDRTonemappedToSDR | 2 | [json](generated/shaders/Resources/unity_builtin_extra/114/metadata.json) |
| Resources/unity_builtin_extra / 9000 | Hidden/Internal-GUITextureClip | 2 | [json](generated/shaders/Resources/unity_builtin_extra/9000/metadata.json) |
| Resources/unity_builtin_extra / 9001 | Hidden/Internal-GUITextureClipText | 2 | [json](generated/shaders/Resources/unity_builtin_extra/9001/metadata.json) |
| Resources/unity_builtin_extra / 9002 | Hidden/Internal-GUITexture | 2 | [json](generated/shaders/Resources/unity_builtin_extra/9002/metadata.json) |
| Resources/unity_builtin_extra / 9003 | Hidden/Internal-GUITextureBlit | 2 | [json](generated/shaders/Resources/unity_builtin_extra/9003/metadata.json) |
| Resources/unity_builtin_extra / 9004 | Hidden/Internal-GUIRoundedRect | 2 | [json](generated/shaders/Resources/unity_builtin_extra/9004/metadata.json) |
| Resources/unity_builtin_extra / 9007 | Hidden/Internal-GUIRoundedRectWithColorPerBorder | 2 | [json](generated/shaders/Resources/unity_builtin_extra/9007/metadata.json) |
| Resources/unity_builtin_extra / 9100 | Hidden/Internal-UIRDefault | 4 | [json](generated/shaders/Resources/unity_builtin_extra/9100/metadata.json) |
| Resources/unity_builtin_extra / 9101 | Hidden/Internal-UIRAtlasBlitCopy | 2 | [json](generated/shaders/Resources/unity_builtin_extra/9101/metadata.json) |
| Resources/unity_builtin_extra / 9102 | Hidden/Internal-UIRDefaultWorld | 4 | [json](generated/shaders/Resources/unity_builtin_extra/9102/metadata.json) |
| Resources/unity_builtin_extra / 10753 | Sprites/Default | 8 | [json](generated/shaders/Resources/unity_builtin_extra/10753/metadata.json) |
| Resources/unity_builtin_extra / 10757 | Sprites/Mask | 8 | [json](generated/shaders/Resources/unity_builtin_extra/10757/metadata.json) |
| Resources/unity_builtin_extra / 10770 | UI/Default | 8 | [json](generated/shaders/Resources/unity_builtin_extra/10770/metadata.json) |
| Resources/unity_builtin_extra / 15104 | Hidden/CubeBlur | 3 | [json](generated/shaders/Resources/unity_builtin_extra/15104/metadata.json) |
| Resources/unity_builtin_extra / 15105 | Hidden/CubeCopy | 2 | [json](generated/shaders/Resources/unity_builtin_extra/15105/metadata.json) |
| Resources/unity_builtin_extra / 15106 | Hidden/CubeBlend | 2 | [json](generated/shaders/Resources/unity_builtin_extra/15106/metadata.json) |
| Resources/unity_builtin_extra / 15304 | Hidden/VR/BlitTexArraySlice | 2 | [json](generated/shaders/Resources/unity_builtin_extra/15304/metadata.json) |
| Resources/unity_builtin_extra / 15308 | Hidden/Internal-ODSWorldTexture | 15 | [json](generated/shaders/Resources/unity_builtin_extra/15308/metadata.json) |
| Resources/unity_builtin_extra / 15309 | Hidden/Internal-CubemapToEquirect | 2 | [json](generated/shaders/Resources/unity_builtin_extra/15309/metadata.json) |
| Resources/unity_builtin_extra / 15312 | Hidden/VR/BlitFromTex2DToTexArraySlice | 5 | [json](generated/shaders/Resources/unity_builtin_extra/15312/metadata.json) |
| Resources/unity_builtin_extra / 15313 | Hidden/VR/BlitCopyHDRTonemapTexArraySlice | 2 | [json](generated/shaders/Resources/unity_builtin_extra/15313/metadata.json) |
| Resources/unity_builtin_extra / 15314 | Hidden/VR/BlitCopyHDRTonemappedToHDRTonemapTexArraySlice | 2 | [json](generated/shaders/Resources/unity_builtin_extra/15314/metadata.json) |
| Resources/unity_builtin_extra / 15315 | Hidden/VR/BlitCopyHDRTonemappedToSDRTexArraySlice | 2 | [json](generated/shaders/Resources/unity_builtin_extra/15315/metadata.json) |
| Resources/unity_builtin_extra / 16000 | Hidden/VideoComposite | 2 | [json](generated/shaders/Resources/unity_builtin_extra/16000/metadata.json) |
| Resources/unity_builtin_extra / 16001 | Hidden/VideoDecode | 19 | [json](generated/shaders/Resources/unity_builtin_extra/16001/metadata.json) |
| Resources/unity_builtin_extra / 17000 | Hidden/Compositing | 2 | [json](generated/shaders/Resources/unity_builtin_extra/17000/metadata.json) |
| Resources/unity_builtin_extra / 19011 | Hidden/TextCore/Distance Field SSD | 2 | [json](generated/shaders/Resources/unity_builtin_extra/19011/metadata.json) |
| Resources/unity_builtin_extra / 4800000 | VF Override/Halo | 2 | [json](generated/shaders/Resources/unity_builtin_extra/4800000/metadata.json) |
| resources.assets / 9818 | Legacy Shaders/Transparent/VertexLit | 8 | [json](generated/shaders/resources.assets/9818/metadata.json) |
| resources.assets / 9819 | Legacy Shaders/Transparent/Cutout/VertexLit | 12 | [json](generated/shaders/resources.assets/9819/metadata.json) |
| resources.assets / 9820 | Legacy Shaders/Transparent/Cutout/Diffuse | 30 | [json](generated/shaders/resources.assets/9820/metadata.json) |
| resources.assets / 9821 | Skybox/Cubemap | 2 | [json](generated/shaders/resources.assets/9821/metadata.json) |
| resources.assets / 9822 | Skybox/6 Sided | 2 | [json](generated/shaders/resources.assets/9822/metadata.json) |
| resources.assets / 9823 | Legacy Shaders/Particles/Additive | 4 | [json](generated/shaders/resources.assets/9823/metadata.json) |
| resources.assets / 9824 | Legacy Shaders/Particles/Additive (Soft) | 4 | [json](generated/shaders/resources.assets/9824/metadata.json) |
| resources.assets / 9825 | Legacy Shaders/Particles/Alpha Blended | 4 | [json](generated/shaders/resources.assets/9825/metadata.json) |
| resources.assets / 9826 | Legacy Shaders/Particles/Alpha Blended Premultiply | 4 | [json](generated/shaders/resources.assets/9826/metadata.json) |
| resources.assets / 9827 | Unlit/Color | 2 | [json](generated/shaders/resources.assets/9827/metadata.json) |
| resources.assets / 9828 | UI/Unlit/Detail | 8 | [json](generated/shaders/resources.assets/9828/metadata.json) |
| resources.assets / 9829 | VF Shaders/Forward/Unlit Lighthouse Effect | 8 | [json](generated/shaders/resources.assets/9829/metadata.json) |
| resources.assets / 9830 | VF Shaders/Procedure Particle/Space Explosion | 2 | [json](generated/shaders/resources.assets/9830/metadata.json) |
| resources.assets / 9831 | Test/zh/Unlit/Additive | 2 | [json](generated/shaders/resources.assets/9831/metadata.json) |
| resources.assets / 9832 | VF Shaders/Forward/Unlit Plasma Turret Effect | 8 | [json](generated/shaders/resources.assets/9832/metadata.json) |
| resources.assets / 9833 | VF Shaders/Forward Spacecraft/Enemy Tri-planar Lancer | 52 | [json](generated/shaders/resources.assets/9833/metadata.json) |
| resources.assets / 9834 | VF Shaders/Forward/PBR Turret Ground Plasma | 52 | [json](generated/shaders/resources.assets/9834/metadata.json) |
| resources.assets / 9835 | VF Shaders/Particle/Additive HSV | 4 | [json](generated/shaders/resources.assets/9835/metadata.json) |
| resources.assets / 9836 | VF Shaders/Forward/Logistic Ship Instancing | 52 | [json](generated/shaders/resources.assets/9836/metadata.json) |
| resources.assets / 9837 | Unlit/VFX_VertexColor&Transparent | 2 | [json](generated/shaders/resources.assets/9837/metadata.json) |
| resources.assets / 9838 | VF Shaders/Renderable Instancing/DF Humpback Projectile | 2 | [json](generated/shaders/resources.assets/9838/metadata.json) |
| resources.assets / 9839 | VF Shaders/Procedure Particle/General Bomb Explosion | 4 | [json](generated/shaders/resources.assets/9839/metadata.json) |
| resources.assets / 9840 | VF Shaders/Forward/Unlit DFRanger Effect Formation | 4 | [json](generated/shaders/resources.assets/9840/metadata.json) |
| resources.assets / 9841 | VF Shaders/SkinShader | 30 | [json](generated/shaders/resources.assets/9841/metadata.json) |
| resources.assets / 9842 | VF Shaders/Forward/PBR Standard VLSilo | 52 | [json](generated/shaders/resources.assets/9842/metadata.json) |
| resources.assets / 9843 | VF Shaders/Forward/PBR Standard Glass Vertex Toggle | 30 | [json](generated/shaders/resources.assets/9843/metadata.json) |
| resources.assets / 9844 | VF Cloud/Nephogram Sphere Type 1 | 2 | [json](generated/shaders/resources.assets/9844/metadata.json) |
| resources.assets / 9845 | Unlit/Planet ATField Shape | 2 | [json](generated/shaders/resources.assets/9845/metadata.json) |
| resources.assets / 9846 | VF Shaders/Forward/Black Mask Spraycoater Effect | 4 | [json](generated/shaders/resources.assets/9846/metadata.json) |
| resources.assets / 9847 | YC Shaders/Particle/Multiply | 4 | [json](generated/shaders/resources.assets/9847/metadata.json) |
| resources.assets / 9848 | VF Shaders/Forward/Fusion Reactor | 52 | [json](generated/shaders/resources.assets/9848/metadata.json) |
| resources.assets / 9849 | VF Shaders/Forward/Lambert Biomo Particle | 26 | [json](generated/shaders/resources.assets/9849/metadata.json) |
| resources.assets / 9850 | VF Shaders/Starmap Instancing/Logistic Ship UI Instancing | 4 | [json](generated/shaders/resources.assets/9850/metadata.json) |
| resources.assets / 9851 | VF Shaders/FX/Holographic Inserter Single ZTest On | 2 | [json](generated/shaders/resources.assets/9851/metadata.json) |
| resources.assets / 9852 | VF Shaders/Blueprint Anchor | 2 | [json](generated/shaders/resources.assets/9852/metadata.json) |
| resources.assets / 9853 | VF Shaders/Renderable Instancing/_Template (Frag) | 2 | [json](generated/shaders/resources.assets/9853/metadata.json) |
| resources.assets / 9854 | VF Shaders/Forward/PBR Ray Receiver | 52 | [json](generated/shaders/resources.assets/9854/metadata.json) |
| resources.assets / 9855 | VF Shaders/Forward Spacecraft/Enemy Tri-planar Relay | 52 | [json](generated/shaders/resources.assets/9855/metadata.json) |
| resources.assets / 9856 | Hidden/Post FX/Grain Generator | 3 | [json](generated/shaders/resources.assets/9856/metadata.json) |
| resources.assets / 9857 | VF Shaders/Forward Spacecraft/Unlit Alpha Relay Effect | 8 | [json](generated/shaders/resources.assets/9857/metadata.json) |
| resources.assets / 9858 | VF Shaders/Forward Spacecraft/Flare Billboard | 8 | [json](generated/shaders/resources.assets/9858/metadata.json) |
| resources.assets / 9859 | Hidden/EfficientBlur | 3 | [json](generated/shaders/resources.assets/9859/metadata.json) |
| resources.assets / 9860 | VF Shaders/Forward/PBR Standard Substation | 52 | [json](generated/shaders/resources.assets/9860/metadata.json) |
| resources.assets / 9861 | VF Shaders/Forward/Unlit Additive Turret Disturb Effect | 8 | [json](generated/shaders/resources.assets/9861/metadata.json) |
| resources.assets / 9862 | VF Shaders/Forward/Unlit Additive Turret Signal 1 | 8 | [json](generated/shaders/resources.assets/9862/metadata.json) |
| resources.assets / 9863 | VF Shaders/FX/Holographic Inserter | 4 | [json](generated/shaders/resources.assets/9863/metadata.json) |
| resources.assets / 9864 | VF Shaders/Forward/Unlit Additive Lighthouse Ground | 8 | [json](generated/shaders/resources.assets/9864/metadata.json) |
| resources.assets / 9865 | VF Shaders/Batching/Test Instancing | 50 | [json](generated/shaders/resources.assets/9865/metadata.json) |
| resources.assets / 9866 | VF Shaders/Forward/PBR Turret Cannon | 52 | [json](generated/shaders/resources.assets/9866/metadata.json) |
| resources.assets / 9867 | VF Shaders/Forward Spacecraft/Formation Flare Flame | 4 | [json](generated/shaders/resources.assets/9867/metadata.json) |
| resources.assets / 9868 | VF Shaders/Forward/PBR Standard Override AnimLength | 52 | [json](generated/shaders/resources.assets/9868/metadata.json) |
| resources.assets / 9869 | VF Shaders/Renderable Instancing/_Template (Surf) | 26 | [json](generated/shaders/resources.assets/9869/metadata.json) |
| resources.assets / 9870 | VF Shaders/FX/GPUIBuildPreviewBp_AppointTime | 4 | [json](generated/shaders/resources.assets/9870/metadata.json) |
| resources.assets / 9871 | VF Shaders/Forward/PBR Standard Fighter Ground | 52 | [json](generated/shaders/resources.assets/9871/metadata.json) |
| resources.assets / 9872 | Unlit/ZWrite | 2 | [json](generated/shaders/resources.assets/9872/metadata.json) |
| resources.assets / 9873 | VF Shaders/Forward/Black Mask Icon Gen | 2 | [json](generated/shaders/resources.assets/9873/metadata.json) |
| resources.assets / 9874 | VF Shaders/Gizmos/GizmoAlphaZ Stream | 4 | [json](generated/shaders/resources.assets/9874/metadata.json) |
| resources.assets / 9875 | VF Shaders/Starmap Instancing/Enemy Relay | 4 | [json](generated/shaders/resources.assets/9875/metadata.json) |
| resources.assets / 9876 | VF Shaders/Forward/Unlit Additive Turret Plasma Ground Effects | 8 | [json](generated/shaders/resources.assets/9876/metadata.json) |
| resources.assets / 9877 | VF Shaders/Gizmos/GizmoCircleZ | 4 | [json](generated/shaders/resources.assets/9877/metadata.json) |
| resources.assets / 9878 | Unlit/Spinning Effect | 2 | [json](generated/shaders/resources.assets/9878/metadata.json) |
| resources.assets / 9879 | VF Shaders/Forward Spacecraft/Unlit Alpha Tower 2 | 8 | [json](generated/shaders/resources.assets/9879/metadata.json) |
| resources.assets / 9880 | VF Shaders/Forward/PBR Standard Mining Drill Mk2 | 52 | [json](generated/shaders/resources.assets/9880/metadata.json) |
| resources.assets / 9881 | VF Shaders/Forward Spacecraft/DF Carrier Effect Instancing | 8 | [json](generated/shaders/resources.assets/9881/metadata.json) |
| resources.assets / 9882 | VF Shaders/Forward/Unlit Alpha | 8 | [json](generated/shaders/resources.assets/9882/metadata.json) |
| resources.assets / 9883 | Hidden/Post FX/FXAA | 8 | [json](generated/shaders/resources.assets/9883/metadata.json) |
| resources.assets / 9884 | Hidden/Post FX/Fog | 3 | [json](generated/shaders/resources.assets/9884/metadata.json) |
| resources.assets / 9885 | Outline/Outline Vein Highlight Mask | 2 | [json](generated/shaders/resources.assets/9885/metadata.json) |
| resources.assets / 9886 | VF Shaders/Forward/PBR Voxel Solid | 26 | [json](generated/shaders/resources.assets/9886/metadata.json) |
| resources.assets / 9887 | VF Shaders/Forward/Unlit Additive Shield Burst Prepare | 4 | [json](generated/shaders/resources.assets/9887/metadata.json) |
| resources.assets / 9888 | Universe/Planet/Halo Standard | 3 | [json](generated/shaders/resources.assets/9888/metadata.json) |
| resources.assets / 9889 | VF Shaders/Forward/PBR Standard Power Motor | 52 | [json](generated/shaders/resources.assets/9889/metadata.json) |
| resources.assets / 9890 | VF Shaders/Forward/PBR Standard Emission Color By State | 52 | [json](generated/shaders/resources.assets/9890/metadata.json) |
| resources.assets / 9891 | VF Shaders/Forward/Trail AlphaBlend SeqAnim | 4 | [json](generated/shaders/resources.assets/9891/metadata.json) |
| resources.assets / 9892 | VF Shaders/Forward/Lava | 2 | [json](generated/shaders/resources.assets/9892/metadata.json) |
| resources.assets / 9893 | VF Shaders/Forward/PBR Standard Dispenser | 52 | [json](generated/shaders/resources.assets/9893/metadata.json) |
| resources.assets / 9894 | VF Shaders/Gizmos/Sail Globe | 2 | [json](generated/shaders/resources.assets/9894/metadata.json) |
| resources.assets / 9895 | VF Shaders/Forward/PBR Standard Biomo Ice | 52 | [json](generated/shaders/resources.assets/9895/metadata.json) |
| resources.assets / 9896 | VF Shaders/Forward/Unlit Additive Assembler MK 4 | 8 | [json](generated/shaders/resources.assets/9896/metadata.json) |
| resources.assets / 9897 | VF Shaders/Forward/PBR Prototype | 22 | [json](generated/shaders/resources.assets/9897/metadata.json) |
| resources.assets / 9898 | VF Shaders/Milky Way/Nebula Particle Instancing | 2 | [json](generated/shaders/resources.assets/9898/metadata.json) |
| resources.assets / 9899 | VF Shaders/Batching/Turret Disk Batch | 2 | [json](generated/shaders/resources.assets/9899/metadata.json) |
| resources.assets / 9900 | VF Shaders/Forward/Logistic Courier Effect Instancing | 8 | [json](generated/shaders/resources.assets/9900/metadata.json) |
| resources.assets / 9901 | Test/zh/Unlit/AlphaBlend | 2 | [json](generated/shaders/resources.assets/9901/metadata.json) |
| resources.assets / 9902 | VF Shaders/FX/GPUIBuildPreview | 4 | [json](generated/shaders/resources.assets/9902/metadata.json) |
| resources.assets / 9903 | VF Shaders/Renderable Instancing/Local Laser Continuous | 2 | [json](generated/shaders/resources.assets/9903/metadata.json) |
| resources.assets / 9904 | Universe/Planet/Surface | 46 | [json](generated/shaders/resources.assets/9904/metadata.json) |
| resources.assets / 9905 | VF Shaders/Forward/Unlit Additive Smelter 3 Effects | 8 | [json](generated/shaders/resources.assets/9905/metadata.json) |
| resources.assets / 9906 | VF Shaders/Forward/PBR Standard Late Grab Class | 22 | [json](generated/shaders/resources.assets/9906/metadata.json) |
| resources.assets / 9907 | Universe/Black Hole Particles | 2 | [json](generated/shaders/resources.assets/9907/metadata.json) |
| resources.assets / 9908 | VF Shaders/Renderable Instancing/Water Bomb | 2 | [json](generated/shaders/resources.assets/9908/metadata.json) |
| resources.assets / 9909 | VF Shaders/Renderable Instancing/DF Lancer Laser OneShot | 2 | [json](generated/shaders/resources.assets/9909/metadata.json) |
| resources.assets / 9910 | VF Shaders/Forward/Unlit Additive EMP Hit Effect | 4 | [json](generated/shaders/resources.assets/9910/metadata.json) |
| resources.assets / 9911 | VF Shaders/Dyson Sphere/Sail Bullet Inst | 2 | [json](generated/shaders/resources.assets/9911/metadata.json) |
| resources.assets / 9912 | VF Shaders/FX/Planet Shield Impact | 4 | [json](generated/shaders/resources.assets/9912/metadata.json) |
| resources.assets / 9913 | VF Shaders/Gizmos/Solid Gizmo | 2 | [json](generated/shaders/resources.assets/9913/metadata.json) |
| resources.assets / 9914 | VF Shaders/Hex Grid | 4 | [json](generated/shaders/resources.assets/9914/metadata.json) |
| resources.assets / 9915 | VF Shaders/Renderable Instancing/DF Ground Tower Laser | 2 | [json](generated/shaders/resources.assets/9915/metadata.json) |
| resources.assets / 9916 | VF Shaders/Forward/Inserter Arrow | 8 | [json](generated/shaders/resources.assets/9916/metadata.json) |
| resources.assets / 9917 | VF Shaders/Procedure Particle/Trail Smoke | 2 | [json](generated/shaders/resources.assets/9917/metadata.json) |
| resources.assets / 9918 | VF Shaders/Forward Spacecraft/Enemy Tri-planar | 78 | [json](generated/shaders/resources.assets/9918/metadata.json) |
| resources.assets / 9919 | VF Shaders/Procedure Particle/Local Explosion | 2 | [json](generated/shaders/resources.assets/9919/metadata.json) |
| resources.assets / 9920 | VF Shaders/Forward/PBR Turret Signal | 52 | [json](generated/shaders/resources.assets/9920/metadata.json) |
| resources.assets / 9921 | Test/zh/Unlit/Multiply | 2 | [json](generated/shaders/resources.assets/9921/metadata.json) |
| resources.assets / 9922 | VF Shaders/Forward/PBR Standard Tank Vertex Toggle | 30 | [json](generated/shaders/resources.assets/9922/metadata.json) |
| resources.assets / 9923 | VF Shaders/Forward/Unlit Alpha Tiling | 4 | [json](generated/shaders/resources.assets/9923/metadata.json) |
| resources.assets / 9924 | VF Shaders/Forward/Unlit Additive Fuzzy Layer | 8 | [json](generated/shaders/resources.assets/9924/metadata.json) |
| resources.assets / 9925 | VF Shaders/Forward/Unlit Additive Fusion Sparks | 4 | [json](generated/shaders/resources.assets/9925/metadata.json) |
| resources.assets / 9926 | VF Shaders/Batching/Path Model Instancing | 26 | [json](generated/shaders/resources.assets/9926/metadata.json) |
| resources.assets / 9927 | VF Shaders/Starmap Instancing/Enemy Carrier | 4 | [json](generated/shaders/resources.assets/9927/metadata.json) |
| resources.assets / 9928 | VF Shaders/Forward/Unlit DFGuardian Effect Formation | 4 | [json](generated/shaders/resources.assets/9928/metadata.json) |
| resources.assets / 9929 | VF Shaders/Particle/Alpha Blend ZTest | 4 | [json](generated/shaders/resources.assets/9929/metadata.json) |
| resources.assets / 9930 | Universe/Planet/Atmosphere | 2 | [json](generated/shaders/resources.assets/9930/metadata.json) |
| resources.assets / 9931 | VF Shaders/Effect/Warp Distortion Color Shift | 4 | [json](generated/shaders/resources.assets/9931/metadata.json) |
| resources.assets / 9932 | VF Shaders/Forward/Standard Biomo Particle | 26 | [json](generated/shaders/resources.assets/9932/metadata.json) |
| resources.assets / 9933 | VF Shaders/Batching/Power GenDisk Batch | 2 | [json](generated/shaders/resources.assets/9933/metadata.json) |
| resources.assets / 9934 | Test/Test FerroFluid | 54 | [json](generated/shaders/resources.assets/9934/metadata.json) |
| resources.assets / 9935 | Hidden/Post FX/Bloom | 8 | [json](generated/shaders/resources.assets/9935/metadata.json) |
| resources.assets / 9936 | VF Shaders/Forward/PBR Standard Vertex Toggle | 52 | [json](generated/shaders/resources.assets/9936/metadata.json) |
| resources.assets / 9937 | Distortion/Standard Screen Distortion | 4 | [json](generated/shaders/resources.assets/9937/metadata.json) |
| resources.assets / 9938 | VF Shaders/Forward/Unlit Additive Disturb | 4 | [json](generated/shaders/resources.assets/9938/metadata.json) |
| resources.assets / 9939 | VF Shaders/Forward/Terrain Reform | 26 | [json](generated/shaders/resources.assets/9939/metadata.json) |
| resources.assets / 9940 | VF Shaders/Procedure Particle/Spark 1 | 2 | [json](generated/shaders/resources.assets/9940/metadata.json) |
| resources.assets / 9941 | VF Shaders/Forward/PBR Standard Vein Metal | 52 | [json](generated/shaders/resources.assets/9941/metadata.json) |
| resources.assets / 9942 | Universe/Star/Star Layered | 2 | [json](generated/shaders/resources.assets/9942/metadata.json) |
| resources.assets / 9943 | VF Shaders/Star Shaders/Star Flame | 2 | [json](generated/shaders/resources.assets/9943/metadata.json) |
| resources.assets / 9944 | VF Shaders/Forward/Logistic Courier Instancing | 52 | [json](generated/shaders/resources.assets/9944/metadata.json) |
| resources.assets / 9945 | VF Shaders/Gizmos/GizmoAlphaZ Anim Inserter | 4 | [json](generated/shaders/resources.assets/9945/metadata.json) |
| resources.assets / 9946 | VF Shaders/Renderable Instancing/DF Lancer Laser Sweep | 2 | [json](generated/shaders/resources.assets/9946/metadata.json) |
| resources.assets / 9947 | VF Shaders/Starmap Instancing/Space Warship | 4 | [json](generated/shaders/resources.assets/9947/metadata.json) |
| resources.assets / 9948 | VF Shaders/Forward/Planet Surface Specular | 22 | [json](generated/shaders/resources.assets/9948/metadata.json) |
| resources.assets / 9949 | VF Shaders/Forward/Unlit Additive Lab | 8 | [json](generated/shaders/resources.assets/9949/metadata.json) |
| resources.assets / 9950 | VF Shaders/Milky Way/Cluster Instancing | 2 | [json](generated/shaders/resources.assets/9950/metadata.json) |
| resources.assets / 9951 | VF Shaders/Forward/Lambert Biomo Heightmap | 52 | [json](generated/shaders/resources.assets/9951/metadata.json) |
| resources.assets / 9952 | VF Shaders/Forward/Unlit Ground AO | 8 | [json](generated/shaders/resources.assets/9952/metadata.json) |
| resources.assets / 9953 | VF Shaders/Dyson Sphere/Node Inst | 26 | [json](generated/shaders/resources.assets/9953/metadata.json) |
| resources.assets / 9954 | UI Ex/Widget Polyline | 2 | [json](generated/shaders/resources.assets/9954/metadata.json) |
| resources.assets / 9955 | VF Shaders/Forward/PBR Standard Piler | 52 | [json](generated/shaders/resources.assets/9955/metadata.json) |
| resources.assets / 9956 | VF Shaders/Procedure Particle/_Template | 2 | [json](generated/shaders/resources.assets/9956/metadata.json) |
| resources.assets / 9957 | VF Shaders/FX/Mecha Energy Shield Impact | 4 | [json](generated/shaders/resources.assets/9957/metadata.json) |
| resources.assets / 9958 | Unlit/Sixway Lightmap Template | 2 | [json](generated/shaders/resources.assets/9958/metadata.json) |
| resources.assets / 9959 | VF Shaders/Forward/PBR Standard DFGuardian | 52 | [json](generated/shaders/resources.assets/9959/metadata.json) |
| resources.assets / 9960 | VF Shaders/Procedure Particle/Water Splash | 2 | [json](generated/shaders/resources.assets/9960/metadata.json) |
| resources.assets / 9961 | VF Shaders/Forward/Planet Surface | 22 | [json](generated/shaders/resources.assets/9961/metadata.json) |
| resources.assets / 9962 | VF Shaders/Forward/PBR Turret Plasma | 52 | [json](generated/shaders/resources.assets/9962/metadata.json) |
| resources.assets / 9963 | VF Shaders/Dyson Sphere/Dyson Layer Painting Overlay | 4 | [json](generated/shaders/resources.assets/9963/metadata.json) |
| resources.assets / 9964 | VF Shaders/Forward/PBR Standard Chemical Glass MK2 | 30 | [json](generated/shaders/resources.assets/9964/metadata.json) |
| resources.assets / 9965 | VF Shaders/Forward/Grass Unlit | 52 | [json](generated/shaders/resources.assets/9965/metadata.json) |
| resources.assets / 9966 | VF Shaders/Forward/Construction Drone Effect | 8 | [json](generated/shaders/resources.assets/9966/metadata.json) |
| resources.assets / 9967 | VF Shaders/Forward Spacecraft/PBR Standard | 26 | [json](generated/shaders/resources.assets/9967/metadata.json) |
| resources.assets / 9968 | UI Ex/Dashboard Astro Resource Vein Blocks | 2 | [json](generated/shaders/resources.assets/9968/metadata.json) |
| resources.assets / 9969 | VF Shaders/Forward/Lambert OilField | 26 | [json](generated/shaders/resources.assets/9969/metadata.json) |
| resources.assets / 9970 | Hidden/Post FX/Screen Space Reflection | 10 | [json](generated/shaders/resources.assets/9970/metadata.json) |
| resources.assets / 9971 | VF Shaders/Renderable Instancing/DF Lancer Plasma | 2 | [json](generated/shaders/resources.assets/9971/metadata.json) |
| resources.assets / 9972 | VF Shaders/Forward Spacecraft/Enemy Tri-planar Connection | 78 | [json](generated/shaders/resources.assets/9972/metadata.json) |
| resources.assets / 9973 | VF Shaders/Starmap Instancing/Enemy Connection | 4 | [json](generated/shaders/resources.assets/9973/metadata.json) |
| resources.assets / 9974 | VF Shaders/Starmap Instancing/Space Enemy Unit | 4 | [json](generated/shaders/resources.assets/9974/metadata.json) |
| resources.assets / 9975 | VF Shaders/Batching/Trash Instancing | 26 | [json](generated/shaders/resources.assets/9975/metadata.json) |
| resources.assets / 9976 | VF Shaders/Forward/Planet Thumb | 2 | [json](generated/shaders/resources.assets/9976/metadata.json) |
| resources.assets / 9977 | VF Shaders/Forward/Unlit DFRanger Effect | 8 | [json](generated/shaders/resources.assets/9977/metadata.json) |
| resources.assets / 9978 | VF Shaders/Forward/PBR FieldGenerator | 52 | [json](generated/shaders/resources.assets/9978/metadata.json) |
| resources.assets / 9979 | VF Shaders/Batching/Trash Instancing Highlight | 4 | [json](generated/shaders/resources.assets/9979/metadata.json) |
| resources.assets / 9980 | Unlit/EmptyUnlit | 2 | [json](generated/shaders/resources.assets/9980/metadata.json) |
| resources.assets / 9981 | VF Shaders/Forward/Inserter Body AlphaClip | 52 | [json](generated/shaders/resources.assets/9981/metadata.json) |
| resources.assets / 9982 | VF Shaders/Forward/Lambert Biomo | 52 | [json](generated/shaders/resources.assets/9982/metadata.json) |
| resources.assets / 9983 | Effect/Iris Tail Line Renderer | 2 | [json](generated/shaders/resources.assets/9983/metadata.json) |
| resources.assets / 9984 | Hidden/Post FX/Motion Blur | 12 | [json](generated/shaders/resources.assets/9984/metadata.json) |
| resources.assets / 9985 | VF Shaders/Forward/Tree leaves | 52 | [json](generated/shaders/resources.assets/9985/metadata.json) |
| resources.assets / 9986 | VF Shaders/Forward/VLS Effect Instancing | 8 | [json](generated/shaders/resources.assets/9986/metadata.json) |
| resources.assets / 9987 | VF Shaders/Forward/Unlit Additive SHM EME | 8 | [json](generated/shaders/resources.assets/9987/metadata.json) |
| resources.assets / 9988 | VF Shaders/Line Wave | 2 | [json](generated/shaders/resources.assets/9988/metadata.json) |
| resources.assets / 9989 | VF Shaders/Forward/Unlit DFGuardian Effect | 8 | [json](generated/shaders/resources.assets/9989/metadata.json) |
| resources.assets / 9990 | VF Shaders/Forward/VFF Water GI | 2 | [json](generated/shaders/resources.assets/9990/metadata.json) |
| resources.assets / 9991 | VF Shaders/Starmap Instancing/Space Enemy Building | 4 | [json](generated/shaders/resources.assets/9991/metadata.json) |
| resources.assets / 9992 | VF Shaders/Forward/Black Mask Spraycoater | 4 | [json](generated/shaders/resources.assets/9992/metadata.json) |
| resources.assets / 9993 | VF Shaders/Gizmos/Solid Line | 2 | [json](generated/shaders/resources.assets/9993/metadata.json) |
| resources.assets / 9994 | VF Shaders/Forward/Grass Unlit Emission | 52 | [json](generated/shaders/resources.assets/9994/metadata.json) |
| resources.assets / 9995 | VF Shaders/Forward/Unlit Additive Mining Drill MK2 Out Box | 8 | [json](generated/shaders/resources.assets/9995/metadata.json) |
| resources.assets / 9996 | VF Shaders/Renderable Instancing/General Projectile | 2 | [json](generated/shaders/resources.assets/9996/metadata.json) |
| resources.assets / 9997 | Hidden/Post FX/Builtin Debug Views | 9 | [json](generated/shaders/resources.assets/9997/metadata.json) |
| resources.assets / 9998 | VF Shaders/Forward Spacecraft/Flare Flame Warship | 8 | [json](generated/shaders/resources.assets/9998/metadata.json) |
| resources.assets / 9999 | VF Shaders/Renderable Instancing/General Missile | 26 | [json](generated/shaders/resources.assets/9999/metadata.json) |
| resources.assets / 10000 | VF Shaders/Forward/Unlit Additive PSR | 4 | [json](generated/shaders/resources.assets/10000/metadata.json) |
| resources.assets / 10001 | VF Shaders/Forward/PBR Standard Vertex Toggle Lab | 52 | [json](generated/shaders/resources.assets/10001/metadata.json) |
| resources.assets / 10002 | VF Shaders/Custom Lighting/General Shield Burst Lightning | 2 | [json](generated/shaders/resources.assets/10002/metadata.json) |
| resources.assets / 10003 | VF Shaders/Forward/Unlit Additive Turret Shield Effect | 8 | [json](generated/shaders/resources.assets/10003/metadata.json) |
| resources.assets / 10004 | VF Shaders/Forward Spacecraft/DF Carrier Instancing | 52 | [json](generated/shaders/resources.assets/10004/metadata.json) |
| resources.assets / 10005 | VF Shaders/Forward/PBR Standard Vertex Toggle Spraycoater | 52 | [json](generated/shaders/resources.assets/10005/metadata.json) |
| resources.assets / 10006 | VF Shaders/Forward/Black Mask | 4 | [json](generated/shaders/resources.assets/10006/metadata.json) |
| resources.assets / 10007 | VF Shaders/Forward/PBR Glass RayReceiver | 8 | [json](generated/shaders/resources.assets/10007/metadata.json) |
| resources.assets / 10008 | VF Shaders/Forward/Unlit Additive Plasma | 4 | [json](generated/shaders/resources.assets/10008/metadata.json) |
| resources.assets / 10009 | VF Shaders/Batching/Power Disk Batch | 2 | [json](generated/shaders/resources.assets/10009/metadata.json) |
| resources.assets / 10010 | VF Shaders/Forward Spacecraft/Flare Flame Tinder | 8 | [json](generated/shaders/resources.assets/10010/metadata.json) |
| resources.assets / 10011 | VF Shaders/Batching/Power Conn Batch | 2 | [json](generated/shaders/resources.assets/10011/metadata.json) |
| resources.assets / 10012 | VF Shaders/Star Shaders/Star Atmosphere | 2 | [json](generated/shaders/resources.assets/10012/metadata.json) |
| resources.assets / 10013 | VF Shaders/Forward/Unlit Lighthouse Effect Starmap | 4 | [json](generated/shaders/resources.assets/10013/metadata.json) |
| resources.assets / 10014 | VF Shaders/Forward/PBR SolarPanel | 52 | [json](generated/shaders/resources.assets/10014/metadata.json) |
| resources.assets / 10015 | VF Shaders/Forward/PBR Standard Battle Base | 52 | [json](generated/shaders/resources.assets/10015/metadata.json) |
| resources.assets / 10016 | VF Shaders/Forward/DF Truck Instancing | 52 | [json](generated/shaders/resources.assets/10016/metadata.json) |
| resources.assets / 10017 | VF Shaders/Forward/PBR Standard Vertex Rotate Mining Drill | 52 | [json](generated/shaders/resources.assets/10017/metadata.json) |
| resources.assets / 10018 | Hidden/Post FX/Blit | 2 | [json](generated/shaders/resources.assets/10018/metadata.json) |
| resources.assets / 10019 | VF Shaders/Forward/Unlit DF Ground Tower 1 Effect | 8 | [json](generated/shaders/resources.assets/10019/metadata.json) |
| resources.assets / 10020 | VF Shaders/Forward/Unlit Alpha SHM | 4 | [json](generated/shaders/resources.assets/10020/metadata.json) |
| resources.assets / 10021 | VF Shaders/Forward/PBR Standard Orbit Collector | 52 | [json](generated/shaders/resources.assets/10021/metadata.json) |
| resources.assets / 10022 | Vegetation/Tree leaves | 50 | [json](generated/shaders/resources.assets/10022/metadata.json) |
| resources.assets / 10023 | VF Shaders/Ore/crystal-ore-multi-mat | 34 | [json](generated/shaders/resources.assets/10023/metadata.json) |
| resources.assets / 10024 | VF Shaders/Forward/Construction Drone | 52 | [json](generated/shaders/resources.assets/10024/metadata.json) |
| resources.assets / 10025 | VF Shaders/Forward/Planet Atmosphere Effect (Gas) | 2 | [json](generated/shaders/resources.assets/10025/metadata.json) |
| resources.assets / 10026 | Unlit/geo-test | 3 | [json](generated/shaders/resources.assets/10026/metadata.json) |
| resources.assets / 10027 | VF Shaders/Forward/Unlit Additive Turret Signal 2 | 8 | [json](generated/shaders/resources.assets/10027/metadata.json) |
| resources.assets / 10028 | Hidden/Post FX/Eye Adaptation | 10 | [json](generated/shaders/resources.assets/10028/metadata.json) |
| resources.assets / 10029 | VF Shaders/Forward Spacecraft/Enemy Formation Tri-planar | 26 | [json](generated/shaders/resources.assets/10029/metadata.json) |
| resources.assets / 10030 | VF Shaders/Batching/Entity Sign Batch | 2 | [json](generated/shaders/resources.assets/10030/metadata.json) |
| resources.assets / 10031 | VF Shaders/Renderable Instancing/DF Ground Tower Plasma | 4 | [json](generated/shaders/resources.assets/10031/metadata.json) |
| resources.assets / 10032 | VF Shaders/FX/Science Cube | 2 | [json](generated/shaders/resources.assets/10032/metadata.json) |
| resources.assets / 10033 | VF Shaders/Forward/Unlit Additive Accumulator | 8 | [json](generated/shaders/resources.assets/10033/metadata.json) |
| resources.assets / 10034 | VF Shaders/Forward Spacecraft/PBR Standard Warship Ruins | 52 | [json](generated/shaders/resources.assets/10034/metadata.json) |
| resources.assets / 10035 | VF Shaders/Dyson Sphere/Solar Sail Inst (Far) | 2 | [json](generated/shaders/resources.assets/10035/metadata.json) |
| resources.assets / 10036 | VF Shaders/Starmap Instancing/Space Enemy Formation | 2 | [json](generated/shaders/resources.assets/10036/metadata.json) |
| resources.assets / 10037 | Unlit/DepthTexturePreview | 2 | [json](generated/shaders/resources.assets/10037/metadata.json) |
| resources.assets / 10038 | VF Shaders/Gizmos/BillboardLogisticsPairing | 2 | [json](generated/shaders/resources.assets/10038/metadata.json) |
| resources.assets / 10039 | VF Shaders/Forward Spacecraft/Flare Billboard Tower 1 | 8 | [json](generated/shaders/resources.assets/10039/metadata.json) |
| resources.assets / 10040 | VF Shaders/Forward/Unlit Additive SHM | 8 | [json](generated/shaders/resources.assets/10040/metadata.json) |
| resources.assets / 10041 | VF Shaders/Procedure Particle/Spark 3 | 2 | [json](generated/shaders/resources.assets/10041/metadata.json) |
| resources.assets / 10042 | VF Shaders/Renderable Instancing/Mecha Space Laser | 2 | [json](generated/shaders/resources.assets/10042/metadata.json) |
| resources.assets / 10043 | UI Ex/Stacked Bar | 2 | [json](generated/shaders/resources.assets/10043/metadata.json) |
| resources.assets / 10044 | VF Shaders/Forward/Unlit Additive Substation | 8 | [json](generated/shaders/resources.assets/10044/metadata.json) |
| resources.assets / 10045 | VF Shaders/Renderable Instancing/Local General Projectile | 2 | [json](generated/shaders/resources.assets/10045/metadata.json) |
| resources.assets / 10046 | VF Shaders/Forward/Planet Surface Specular Color Shift | 22 | [json](generated/shaders/resources.assets/10046/metadata.json) |
| resources.assets / 10047 | Universe/Planet/Ice 2 | 2 | [json](generated/shaders/resources.assets/10047/metadata.json) |
| resources.assets / 10048 | VF Shaders/Custom Lighting/Fusion Reactor Lighting | 4 | [json](generated/shaders/resources.assets/10048/metadata.json) |
| resources.assets / 10049 | Unlit/tesl-test | 4 | [json](generated/shaders/resources.assets/10049/metadata.json) |
| resources.assets / 10050 | Hidden/Post FX/Uber Shader | 2592 | [json](generated/shaders/resources.assets/10050/metadata.json) |
| resources.assets / 10051 | VF Shaders/Particle/Unlit Additive Warp | 4 | [json](generated/shaders/resources.assets/10051/metadata.json) |
| resources.assets / 10052 | VF Shaders/Procedure Particle/Spark 2 | 2 | [json](generated/shaders/resources.assets/10052/metadata.json) |
| resources.assets / 10053 | VF Shaders/Forward/PBR EM Ejector | 52 | [json](generated/shaders/resources.assets/10053/metadata.json) |
| resources.assets / 10054 | VF Shaders/Forward/VFF Water | 2 | [json](generated/shaders/resources.assets/10054/metadata.json) |
| resources.assets / 10055 | VF Shaders/Custom Lighting/Space Explosion Lightning | 2 | [json](generated/shaders/resources.assets/10055/metadata.json) |
| resources.assets / 10056 | VF Shaders/Forward Spacecraft/Unlit Alpha | 8 | [json](generated/shaders/resources.assets/10056/metadata.json) |
| resources.assets / 10057 | VF Shaders/Forward/Unlit Alpha DayNight | 8 | [json](generated/shaders/resources.assets/10057/metadata.json) |
| resources.assets / 10058 | VF Shaders/Forward/PBR Standard Spraycoater Water | 30 | [json](generated/shaders/resources.assets/10058/metadata.json) |
| resources.assets / 10059 | VF Shaders/Forward/Unlit Additive Dispenser | 8 | [json](generated/shaders/resources.assets/10059/metadata.json) |
| resources.assets / 10060 | VF Shaders/Forward/VLS Distort Instancing | 8 | [json](generated/shaders/resources.assets/10060/metadata.json) |
| resources.assets / 10061 | VF Shaders/Dyson Sphere/Frame Inst | 26 | [json](generated/shaders/resources.assets/10061/metadata.json) |
| resources.assets / 10062 | VF Shaders/Forward/Inserter Body | 52 | [json](generated/shaders/resources.assets/10062/metadata.json) |
| resources.assets / 10063 | Test/Test Parallax Mapping | 60 | [json](generated/shaders/resources.assets/10063/metadata.json) |
| resources.assets / 10064 | Test/zh/Unlit/AlphaBlend With Light | 2 | [json](generated/shaders/resources.assets/10064/metadata.json) |
| resources.assets / 10065 | VF Shaders/Forward Spacecraft/Unlit Alpha Core Sphere | 8 | [json](generated/shaders/resources.assets/10065/metadata.json) |
| resources.assets / 10066 | VF Shaders/Renderable Instancing/Local Laser One Shot | 2 | [json](generated/shaders/resources.assets/10066/metadata.json) |
| resources.assets / 10067 | VF Shaders/Forward/Unlit Additive FieldGenerator | 4 | [json](generated/shaders/resources.assets/10067/metadata.json) |
| resources.assets / 10068 | VF Shaders/Forward/Unlit Cannon Turret Effect | 4 | [json](generated/shaders/resources.assets/10068/metadata.json) |
| resources.assets / 10069 | VF Shaders/Forward/PBR Energy Exchanger | 52 | [json](generated/shaders/resources.assets/10069/metadata.json) |
| resources.assets / 10070 | VF Shaders/Dyson Sphere/Solar Sail Inst (Near) | 2 | [json](generated/shaders/resources.assets/10070/metadata.json) |
| resources.assets / 10071 | VF Shaders/Forward/PBR Standard DFGround | 52 | [json](generated/shaders/resources.assets/10071/metadata.json) |
| resources.assets / 10072 | VF Shaders/Forward/PBR Standard Biomo | 52 | [json](generated/shaders/resources.assets/10072/metadata.json) |
| resources.assets / 10073 | VF Shaders/Forward/GeoObject Instancing | 26 | [json](generated/shaders/resources.assets/10073/metadata.json) |
| resources.assets / 10074 | UI Ex/Research Stat Histogram In Dashboard | 2 | [json](generated/shaders/resources.assets/10074/metadata.json) |
| resources.assets / 10075 | VF Shaders/Forward/PBR Standard Monitor | 52 | [json](generated/shaders/resources.assets/10075/metadata.json) |
| resources.assets / 10076 | VF Shaders/Forward Spacecraft/Unlit Alpha DF Photon | 8 | [json](generated/shaders/resources.assets/10076/metadata.json) |
| resources.assets / 10077 | VF Shaders/Forward Spacecraft/DF Ant | 52 | [json](generated/shaders/resources.assets/10077/metadata.json) |
| resources.assets / 10078 | VF Shaders/Forward/Planet Thumb New | 2 | [json](generated/shaders/resources.assets/10078/metadata.json) |
| resources.assets / 10079 | VF Shaders/Gizmos/BoundingBoxZ | 4 | [json](generated/shaders/resources.assets/10079/metadata.json) |
| resources.assets / 10080 | VF Shaders/Forward/Gauss Turret Fire | 8 | [json](generated/shaders/resources.assets/10080/metadata.json) |
| resources.assets / 10081 | VF Shaders/Batching/Warning Batch | 4 | [json](generated/shaders/resources.assets/10081/metadata.json) |
| resources.assets / 10082 | VF Shaders/Forward/PBR Standard Glass | 30 | [json](generated/shaders/resources.assets/10082/metadata.json) |
| resources.assets / 10083 | VF Shaders/Forward/Crystal Ore Multimat | 26 | [json](generated/shaders/resources.assets/10083/metadata.json) |
| resources.assets / 10084 | VF Shaders/Forward Spacecraft/DF Ant Flare Flame | 8 | [json](generated/shaders/resources.assets/10084/metadata.json) |
| resources.assets / 10085 | VF Shaders/Forward/PBR Standard Black Vertex Rotate | 52 | [json](generated/shaders/resources.assets/10085/metadata.json) |
| resources.assets / 10086 | Unlit/GLLines | 2 | [json](generated/shaders/resources.assets/10086/metadata.json) |
| resources.assets / 10087 | VF Shaders/Forward Spacecraft/Enemy Tri-planar Ruins | 52 | [json](generated/shaders/resources.assets/10087/metadata.json) |
| resources.assets / 10088 | UI Ex/Text Alpha 3D | 2 | [json](generated/shaders/resources.assets/10088/metadata.json) |
| resources.assets / 10089 | VF Shaders/Forward/PBR Standard DFGuardian Formation | 26 | [json](generated/shaders/resources.assets/10089/metadata.json) |
| resources.assets / 10090 | VF Shaders/Forward/PBR Standard Vertex Rotate | 26 | [json](generated/shaders/resources.assets/10090/metadata.json) |
| resources.assets / 10091 | VF Shaders/Forward/Unlit Lighthouse Effect Universal | 4 | [json](generated/shaders/resources.assets/10091/metadata.json) |
| resources.assets / 10092 | VF Shaders/Forward/PBR Standard Lava Biomo | 52 | [json](generated/shaders/resources.assets/10092/metadata.json) |
| resources.assets / 10093 | Gizmos/Transform Gizmo/GL GizmoLine | 2 | [json](generated/shaders/resources.assets/10093/metadata.json) |
| resources.assets / 10094 | Hidden/Post FX/Depth Of Field | 12 | [json](generated/shaders/resources.assets/10094/metadata.json) |
| resources.assets / 10095 | VF Shaders/Forward/Logistic Drone Instancing | 52 | [json](generated/shaders/resources.assets/10095/metadata.json) |
| resources.assets / 10096 | VF Shaders/Forward/PBR Standard Cosmic Message Pixel | 26 | [json](generated/shaders/resources.assets/10096/metadata.json) |
| resources.assets / 10097 | VF Shaders/Starmap Instancing/Enemy Tinder | 4 | [json](generated/shaders/resources.assets/10097/metadata.json) |
| resources.assets / 10098 | VF Shaders/Forward/Unlit Additive Geothermal | 8 | [json](generated/shaders/resources.assets/10098/metadata.json) |
| resources.assets / 10099 | VF Shaders/Forward Spacecraft/PBR Standard Ruins | 26 | [json](generated/shaders/resources.assets/10099/metadata.json) |
| resources.assets / 10100 | VF Shaders/Forward/Black Mask Vertex Toggle | 4 | [json](generated/shaders/resources.assets/10100/metadata.json) |
| resources.assets / 10101 | VF Shaders/Forward/Unlit DF Ground Tower 2 Effect | 8 | [json](generated/shaders/resources.assets/10101/metadata.json) |
| resources.assets / 10102 | VF Shaders/Forward/Unlit DFRaider Effect | 8 | [json](generated/shaders/resources.assets/10102/metadata.json) |
| resources.assets / 10103 | VF Shaders/Forward/PBR Standard Chemical Glass Back | 52 | [json](generated/shaders/resources.assets/10103/metadata.json) |
| resources.assets / 10104 | VF Shaders/Custom Lighting/Local Explosion Lightning | 2 | [json](generated/shaders/resources.assets/10104/metadata.json) |
| resources.assets / 10105 | VF Shaders/Renderable Instancing/Mecha Local Laser | 2 | [json](generated/shaders/resources.assets/10105/metadata.json) |
| resources.assets / 10106 | VF Shaders/Forward/Unlit Additive UV Radial | 4 | [json](generated/shaders/resources.assets/10106/metadata.json) |
| resources.assets / 10107 | VF Shaders/Renderable Instancing/DF Space Tower Plasma | 4 | [json](generated/shaders/resources.assets/10107/metadata.json) |
| resources.assets / 10108 | Test/zh/Unlit/Screen | 2 | [json](generated/shaders/resources.assets/10108/metadata.json) |
| resources.assets / 10109 | VF Shaders/Forward/Unlit DFRaider Effect Formation | 4 | [json](generated/shaders/resources.assets/10109/metadata.json) |
| resources.assets / 10110 | VF Cloud/Cloud Particle Type 1 | 2 | [json](generated/shaders/resources.assets/10110/metadata.json) |
| resources.assets / 10111 | UI Ex/Marker Scene UI | 2 | [json](generated/shaders/resources.assets/10111/metadata.json) |
| resources.assets / 10112 | VF Shaders/Forward/Logistic Drone Effect Instancing | 4 | [json](generated/shaders/resources.assets/10112/metadata.json) |
| resources.assets / 10113 | VF Shaders/Forward/Unlit Additive Mining Drill MK2 | 8 | [json](generated/shaders/resources.assets/10113/metadata.json) |
| resources.assets / 10114 | VF Shaders/Forward/Unlit Additive Glitch | 8 | [json](generated/shaders/resources.assets/10114/metadata.json) |
| resources.assets / 10115 | VF Shaders/Forward/Planet Atmosphere Effect | 2 | [json](generated/shaders/resources.assets/10115/metadata.json) |
| resources.assets / 10116 | VF Shaders/Dyson Sphere/Dyson Shell Unlit | 3 | [json](generated/shaders/resources.assets/10116/metadata.json) |
| resources.assets / 10117 | UI/TranslucentImage3D | 8 | [json](generated/shaders/resources.assets/10117/metadata.json) |
| resources.assets / 10118 | VF Shaders/FX/Holographic Inserter Single | 2 | [json](generated/shaders/resources.assets/10118/metadata.json) |
| resources.assets / 10119 | VF Shaders/Forward/Particle Inst AlphaBlend Lit SeqAnim | 4 | [json](generated/shaders/resources.assets/10119/metadata.json) |
| resources.assets / 10120 | UI Ex/Widget Alpha 3D | 2 | [json](generated/shaders/resources.assets/10120/metadata.json) |
| resources.assets / 10121 | VF Shaders/PBR/Standard-Inserter | 34 | [json](generated/shaders/resources.assets/10121/metadata.json) |
| resources.assets / 10122 | VF Shaders/Forward/Lambert | 26 | [json](generated/shaders/resources.assets/10122/metadata.json) |
| resources.assets / 10123 | VF Shaders/Renderable Instancing/General Bomb | 26 | [json](generated/shaders/resources.assets/10123/metadata.json) |
| resources.assets / 10124 | VF Shaders/Forward Spacecraft/PBR Standard Warship Mix | 52 | [json](generated/shaders/resources.assets/10124/metadata.json) |
| resources.assets / 10125 | VF Shaders/Batching/CombatStat Instancing | 2 | [json](generated/shaders/resources.assets/10125/metadata.json) |
| resources.assets / 10126 | VF Shaders/Forward/PBR Standard Fleet | 52 | [json](generated/shaders/resources.assets/10126/metadata.json) |
| resources.assets / 10127 | Unlit/DFBase Ruins Pit Mask | 4 | [json](generated/shaders/resources.assets/10127/metadata.json) |
| resources.assets / 10128 | VF Shaders/Forward/Rocket Instancing | 52 | [json](generated/shaders/resources.assets/10128/metadata.json) |
| resources.assets / 10129 | VF Shaders/Forward Spacecraft/PBR Standard Fleet | 52 | [json](generated/shaders/resources.assets/10129/metadata.json) |
| resources.assets / 10130 | VF Shaders/Forward/Gauss Turret Fire | 4 | [json](generated/shaders/resources.assets/10130/metadata.json) |
| resources.assets / 10131 | VF Shaders/Forward/Unlit Additive Disturb Bomb Effect | 4 | [json](generated/shaders/resources.assets/10131/metadata.json) |
| resources.assets / 10132 | Universe/Planet/Halo Sun | 2 | [json](generated/shaders/resources.assets/10132/metadata.json) |
| resources.assets / 10133 | UI Ex/Praseable Text | 4 | [json](generated/shaders/resources.assets/10133/metadata.json) |
| resources.assets / 10134 | VF Shaders/Procedure Particle/Local EMP | 4 | [json](generated/shaders/resources.assets/10134/metadata.json) |
| resources.assets / 10135 | VF Shaders/Forward/PBR Standard Accumulator | 52 | [json](generated/shaders/resources.assets/10135/metadata.json) |
| resources.assets / 10136 | Effect/Iris Info Line Renderer | 2 | [json](generated/shaders/resources.assets/10136/metadata.json) |
| resources.assets / 10137 | VF Shaders/Forward Spacecraft/Unlit Alpha Tower 1 | 8 | [json](generated/shaders/resources.assets/10137/metadata.json) |
| resources.assets / 10138 | VF Shaders/Forward/Unlit Additive NTR | 4 | [json](generated/shaders/resources.assets/10138/metadata.json) |
| resources.assets / 10139 | VF Shaders/Forward Spacecraft/Unlit Alpha Lancer Effect | 8 | [json](generated/shaders/resources.assets/10139/metadata.json) |
| resources.assets / 10140 | Universe/Compose/Brush | 2 | [json](generated/shaders/resources.assets/10140/metadata.json) |
| resources.assets / 10141 | UI Ex/Dashboard Grid Bg | 2 | [json](generated/shaders/resources.assets/10141/metadata.json) |
| resources.assets / 10142 | Custom/Cullfront | 46 | [json](generated/shaders/resources.assets/10142/metadata.json) |
| resources.assets / 10143 | VF Shaders/Procedure Particle/Space Explosion Blast | 2 | [json](generated/shaders/resources.assets/10143/metadata.json) |
| resources.assets / 10144 | VF Shaders/Forward/PBR Standard Pixel | 26 | [json](generated/shaders/resources.assets/10144/metadata.json) |
| resources.assets / 10145 | VF Shaders/Dyson Sphere/Dyson Shell | 26 | [json](generated/shaders/resources.assets/10145/metadata.json) |
| resources.assets / 10146 | VF Shaders/Forward/PBR Standard Storage | 52 | [json](generated/shaders/resources.assets/10146/metadata.json) |
| resources.assets / 10147 | Universe/Sun Distant Flare | 2 | [json](generated/shaders/resources.assets/10147/metadata.json) |
| resources.assets / 10148 | Hidden/Post FX/Lut Generator | 6 | [json](generated/shaders/resources.assets/10148/metadata.json) |
| resources.assets / 10149 | VF Shaders/Forward/PBR Standard DFRaider | 52 | [json](generated/shaders/resources.assets/10149/metadata.json) |
| resources.assets / 10150 | VF Shaders/Forward/PBR Standard DFRaider Formation | 26 | [json](generated/shaders/resources.assets/10150/metadata.json) |
| resources.assets / 10151 | VF Shaders/FX/GPUIBuildPreviewBp | 4 | [json](generated/shaders/resources.assets/10151/metadata.json) |
| resources.assets / 10152 | VF Shaders/Forward/Flare Billboard | 8 | [json](generated/shaders/resources.assets/10152/metadata.json) |
| resources.assets / 10153 | VF Shaders/Batching/Cargo Batch | 2 | [json](generated/shaders/resources.assets/10153/metadata.json) |
| resources.assets / 10154 | VF Shaders/Forward/Unlit Additive Laser | 4 | [json](generated/shaders/resources.assets/10154/metadata.json) |
| resources.assets / 10155 | VF Shaders/Forward/PBR Voxel Glass | 15 | [json](generated/shaders/resources.assets/10155/metadata.json) |
| resources.assets / 10156 | VF Shaders/Forward/PBR Standard Vein Stone | 52 | [json](generated/shaders/resources.assets/10156/metadata.json) |
| resources.assets / 10157 | UI Ex/Widget Planet Icon | 2 | [json](generated/shaders/resources.assets/10157/metadata.json) |
| resources.assets / 10158 | VF Shaders/Forward/PBR Standard DFGround Formation | 26 | [json](generated/shaders/resources.assets/10158/metadata.json) |
| resources.assets / 10159 | Unlit/DF Base Ruins Pit Effect | 4 | [json](generated/shaders/resources.assets/10159/metadata.json) |
| resources.assets / 10160 | Hidden/FillCrop | 2 | [json](generated/shaders/resources.assets/10160/metadata.json) |
| resources.assets / 10161 | VF Shaders/Forward/PBR Standard DFGround Tower1 | 52 | [json](generated/shaders/resources.assets/10161/metadata.json) |
| resources.assets / 10162 | VF Shaders/Forward/PBR Standard UV Rotate | 52 | [json](generated/shaders/resources.assets/10162/metadata.json) |
| resources.assets / 10163 | VF Shaders/Forward/PBR Turret | 52 | [json](generated/shaders/resources.assets/10163/metadata.json) |
| resources.assets / 10164 | VF Shaders/Forward/PBR Standard Piler Effect | 52 | [json](generated/shaders/resources.assets/10164/metadata.json) |
| resources.assets / 10165 | VF Shaders/Forward/Exchanger Effect | 8 | [json](generated/shaders/resources.assets/10165/metadata.json) |
| resources.assets / 10166 | Test/Hexagon Shield Sphere | 2 | [json](generated/shaders/resources.assets/10166/metadata.json) |
| resources.assets / 10167 | Universe/Compose/Unlit Layer | 2 | [json](generated/shaders/resources.assets/10167/metadata.json) |
| resources.assets / 10168 | VF Shaders/Forward/PBR Standard Tank Glass | 15 | [json](generated/shaders/resources.assets/10168/metadata.json) |
| resources.assets / 10169 | VF Shaders/Batching/Miniblock Instancing | 2 | [json](generated/shaders/resources.assets/10169/metadata.json) |
| resources.assets / 10170 | VF Shaders/Renderable Instancing/Warship Type F Laser OneShot | 2 | [json](generated/shaders/resources.assets/10170/metadata.json) |
| resources.assets / 10171 | VF Shaders/FX/GPUIBuildPreview Inserter | 4 | [json](generated/shaders/resources.assets/10171/metadata.json) |
| resources.assets / 10172 | VF Shaders/Forward/PBR Standard Geothermal | 52 | [json](generated/shaders/resources.assets/10172/metadata.json) |
| resources.assets / 10173 | VF Shaders/Batching/Cargo Instancing | 26 | [json](generated/shaders/resources.assets/10173/metadata.json) |
| resources.assets / 10174 | VF Shaders/Forward/Unlit Additive Mecha Respawn | 2 | [json](generated/shaders/resources.assets/10174/metadata.json) |
| resources.assets / 10175 | UI Ex/Dashboard Vein Blocks | 2 | [json](generated/shaders/resources.assets/10175/metadata.json) |
| resources.assets / 10176 | Unlit/DefaultShadowCaster | 4 | [json](generated/shaders/resources.assets/10176/metadata.json) |
| resources.assets / 10177 | VF Shaders/FX/Holographic ZTestOff | 2 | [json](generated/shaders/resources.assets/10177/metadata.json) |
| resources.assets / 10178 | VF Shaders/Batching/Belt Instancing | 26 | [json](generated/shaders/resources.assets/10178/metadata.json) |
| resources.assets / 10179 | VF Shaders/Forward/PBR Standard Cosmic Message | 52 | [json](generated/shaders/resources.assets/10179/metadata.json) |
| resources.assets / 10180 | Hidden/Post FX/Ambient Occlusion | 12 | [json](generated/shaders/resources.assets/10180/metadata.json) |
| resources.assets / 10181 | VF Shaders/Forward/PBR Standard Glass Mining Drill Mk2 | 52 | [json](generated/shaders/resources.assets/10181/metadata.json) |
| resources.assets / 10182 | VF Shaders/Procedure Particle/Local Explosion Blast | 2 | [json](generated/shaders/resources.assets/10182/metadata.json) |
| resources.assets / 10183 | VF Shaders/Forward Spacecraft/Enemy Tri-planar Building Tower 2 | 52 | [json](generated/shaders/resources.assets/10183/metadata.json) |
| resources.assets / 10184 | UI Ex/Dashboard Vein Group Map | 2 | [json](generated/shaders/resources.assets/10184/metadata.json) |
| resources.assets / 10185 | VF Shaders/Forward/Unlit Fighter Effect | 8 | [json](generated/shaders/resources.assets/10185/metadata.json) |
| resources.assets / 10186 | Universe/Planet/Water | 2 | [json](generated/shaders/resources.assets/10186/metadata.json) |
| resources.assets / 10187 | Unlit/DepthNormalsPreview | 2 | [json](generated/shaders/resources.assets/10187/metadata.json) |
| resources.assets / 10188 | Hidden/Post FX/Temporal Anti-aliasing | 10 | [json](generated/shaders/resources.assets/10188/metadata.json) |
| resources.assets / 10189 | VF Shaders/Renderable Instancing/DF Space Tower Laser OneShot | 2 | [json](generated/shaders/resources.assets/10189/metadata.json) |
| resources.assets / 10190 | VF Shaders/Gizmos/GizmoLogisticsPairingStream | 4 | [json](generated/shaders/resources.assets/10190/metadata.json) |
| resources.assets / 10191 | VF Shaders/Forward/Rocket Effect Instancing | 4 | [json](generated/shaders/resources.assets/10191/metadata.json) |
| resources.assets / 10192 | VF Shaders/Forward/Unlit Additive Ray Receiver Gravity | 8 | [json](generated/shaders/resources.assets/10192/metadata.json) |
| resources.assets / 10193 | VF Shaders/Replacement/Terrain Height Map | 4 | [json](generated/shaders/resources.assets/10193/metadata.json) |
| resources.assets / 10194 | VF Shaders/Forward/PBR Standard Vein Crystal | 52 | [json](generated/shaders/resources.assets/10194/metadata.json) |
| resources.assets / 10195 | VF Shaders/Forward/Creation/Vehicle Part Stretchable | 104 | [json](generated/shaders/resources.assets/10195/metadata.json) |
| resources.assets / 10196 | VF Shaders/Forward/PBR Standard Stretchable Prototype | 26 | [json](generated/shaders/resources.assets/10196/metadata.json) |
| resources.assets / 10197 | VF Shaders/Renderable Instancing/Space Laser One Shot | 2 | [json](generated/shaders/resources.assets/10197/metadata.json) |
| resources.assets / 10198 | Universe/Black Hole Acdisk | 2 | [json](generated/shaders/resources.assets/10198/metadata.json) |
| resources.assets / 10199 | VF Shaders/Starmap Instancing/Instancing Standard | 4 | [json](generated/shaders/resources.assets/10199/metadata.json) |
| resources.assets / 10200 | VF Shaders/Forward/Unlit Laser Turret Effect | 8 | [json](generated/shaders/resources.assets/10200/metadata.json) |
| resources.assets / 10201 | VF Shaders/Renderable Instancing/Space Laser Sweep | 2 | [json](generated/shaders/resources.assets/10201/metadata.json) |
| resources.assets / 10202 | VF Shaders/Batching/Cargo Batch Lighting | 30 | [json](generated/shaders/resources.assets/10202/metadata.json) |
| resources.assets / 10203 | VF Shaders/Forward/PBR Standard DFRail | 52 | [json](generated/shaders/resources.assets/10203/metadata.json) |
| resources.assets / 10204 | VF Shaders/Renderable Instancing/Mecha Shield Burst | 4 | [json](generated/shaders/resources.assets/10204/metadata.json) |
| resources.assets / 10205 | VF Shaders/Forward/PBR Standard DFBase Pit | 52 | [json](generated/shaders/resources.assets/10205/metadata.json) |
| resources.assets / 10206 | VF Shaders/Forward/Fusion Sun | 8 | [json](generated/shaders/resources.assets/10206/metadata.json) |
| resources.assets / 10207 | VF Shaders/Forward Spacecraft/Flare Flame Relay | 8 | [json](generated/shaders/resources.assets/10207/metadata.json) |
| resources.assets / 10208 | Universe/Planet/Ice | 2 | [json](generated/shaders/resources.assets/10208/metadata.json) |
| resources.assets / 10209 | VF Shaders/Forward/PBR Standard Chemical Glass | 30 | [json](generated/shaders/resources.assets/10209/metadata.json) |
| resources.assets / 10210 | UI Ex/Widget Light | 2 | [json](generated/shaders/resources.assets/10210/metadata.json) |
| resources.assets / 10211 | VF Shaders/Forward/Logistic Ship Effect Instancing | 4 | [json](generated/shaders/resources.assets/10211/metadata.json) |
| resources.assets / 10212 | VF Shaders/PBR/Standard | 34 | [json](generated/shaders/resources.assets/10212/metadata.json) |
| sharedassets0.assets / 1388 | Standard | 431 | [json](generated/shaders/sharedassets0.assets/1388/metadata.json) |
| sharedassets0.assets / 1389 | Particles/Standard Unlit | 28 | [json](generated/shaders/sharedassets0.assets/1389/metadata.json) |
| sharedassets0.assets / 1390 | Unlit/Transparent | 2 | [json](generated/shaders/sharedassets0.assets/1390/metadata.json) |
| sharedassets0.assets / 1391 | UI Ex/Kill Stat Histogram | 2 | [json](generated/shaders/sharedassets0.assets/1391/metadata.json) |
| sharedassets0.assets / 1392 | UI Ex/Highlight Background | 2 | [json](generated/shaders/sharedassets0.assets/1392/metadata.json) |
| sharedassets0.assets / 1393 | VF Shaders/Particle/Additive ZTest | 4 | [json](generated/shaders/sharedassets0.assets/1393/metadata.json) |
| sharedassets0.assets / 1394 | VF Shaders/Particle/Additive | 4 | [json](generated/shaders/sharedassets0.assets/1394/metadata.json) |
| sharedassets0.assets / 1395 | UI Ex/Power Stat Histogram | 2 | [json](generated/shaders/sharedassets0.assets/1395/metadata.json) |
| sharedassets0.assets / 1396 | Universe/Sun | 2 | [json](generated/shaders/sharedassets0.assets/1396/metadata.json) |
| sharedassets0.assets / 1397 | VF Shaders/Gizmos/GizmoAlpha | 4 | [json](generated/shaders/sharedassets0.assets/1397/metadata.json) |
| sharedassets0.assets / 1398 | VF Shaders/Gizmos/GizmoAlphaZ Anim | 4 | [json](generated/shaders/sharedassets0.assets/1398/metadata.json) |
| sharedassets0.assets / 1399 | UI Ex/Color Panel 256 | 2 | [json](generated/shaders/sharedassets0.assets/1399/metadata.json) |
| sharedassets0.assets / 1400 | YC Shaders/Particle/Alpha Blended | 4 | [json](generated/shaders/sharedassets0.assets/1400/metadata.json) |
| sharedassets0.assets / 1401 | VF Shaders/Dyson Sphere/Drawing Grid | 4 | [json](generated/shaders/sharedassets0.assets/1401/metadata.json) |
| sharedassets0.assets / 1402 | VF Shaders/Forward/PBR Standard Capsule | 26 | [json](generated/shaders/sharedassets0.assets/1402/metadata.json) |
| sharedassets0.assets / 1403 | VF Shaders/Particle/Alpha Blend | 4 | [json](generated/shaders/sharedassets0.assets/1403/metadata.json) |
| sharedassets0.assets / 1404 | UI Ex/Color Panel 249 | 2 | [json](generated/shaders/sharedassets0.assets/1404/metadata.json) |
| sharedassets0.assets / 1405 | Universe/Starmap/Planet Orbit | 2 | [json](generated/shaders/sharedassets0.assets/1405/metadata.json) |
| sharedassets0.assets / 1406 | VF Shaders/Forward Spacecraft/Unlit Alpha Core Sphere Demo | 4 | [json](generated/shaders/sharedassets0.assets/1406/metadata.json) |
| sharedassets0.assets / 1407 | VF Shaders/Gizmos/Galaxy Select Grid | 4 | [json](generated/shaders/sharedassets0.assets/1407/metadata.json) |
| sharedassets0.assets / 1408 | VF Shaders/Gizmos/Solid Gizmo General | 2 | [json](generated/shaders/sharedassets0.assets/1408/metadata.json) |
| sharedassets0.assets / 1409 | Deep Profiler/Timeline Ruler | 2 | [json](generated/shaders/sharedassets0.assets/1409/metadata.json) |
| sharedassets0.assets / 1410 | VF Shaders/Milky Way/Nebula Additive | 4 | [json](generated/shaders/sharedassets0.assets/1410/metadata.json) |
| sharedassets0.assets / 1411 | VF Shaders/Star Shaders/Star Body | 2 | [json](generated/shaders/sharedassets0.assets/1411/metadata.json) |
| sharedassets0.assets / 1412 | UI Ex/Widget Alpha | 2 | [json](generated/shaders/sharedassets0.assets/1412/metadata.json) |
| sharedassets0.assets / 1413 | VF Shaders/Gizmos/BlueprintBuilding Grid | 4 | [json](generated/shaders/sharedassets0.assets/1413/metadata.json) |
| sharedassets0.assets / 1414 | VF Shaders/Forward/PBR Standard Ruins | 52 | [json](generated/shaders/sharedassets0.assets/1414/metadata.json) |
| sharedassets0.assets / 1415 | Universe/GasGiant | 22 | [json](generated/shaders/sharedassets0.assets/1415/metadata.json) |
| sharedassets0.assets / 1416 | UI Ex/DE Point Graph | 2 | [json](generated/shaders/sharedassets0.assets/1416/metadata.json) |
| sharedassets0.assets / 1417 | UI Ex/Text Alpha YCGen(IconSet) | 2 | [json](generated/shaders/sharedassets0.assets/1417/metadata.json) |
| sharedassets0.assets / 1418 | Universe/Starmap/Point | 4 | [json](generated/shaders/sharedassets0.assets/1418/metadata.json) |
| sharedassets0.assets / 1419 | UI Ex/Research Stat Histogram | 2 | [json](generated/shaders/sharedassets0.assets/1419/metadata.json) |
| sharedassets0.assets / 1420 | Outline/Exclude | 2 | [json](generated/shaders/sharedassets0.assets/1420/metadata.json) |
| sharedassets0.assets / 1421 | UI Ex/Dashboard Bg | 2 | [json](generated/shaders/sharedassets0.assets/1421/metadata.json) |
| sharedassets0.assets / 1422 | Outline/Down sampling | 2 | [json](generated/shaders/sharedassets0.assets/1422/metadata.json) |
| sharedassets0.assets / 1423 | UI Ex/Widget Alpha Bias | 2 | [json](generated/shaders/sharedassets0.assets/1423/metadata.json) |
| sharedassets0.assets / 1424 | VF Shaders/Forward/Unlit Additive | 8 | [json](generated/shaders/sharedassets0.assets/1424/metadata.json) |
| sharedassets0.assets / 1425 | Universe/Star/Star Mono-Layer | 2 | [json](generated/shaders/sharedassets0.assets/1425/metadata.json) |
| sharedassets0.assets / 1426 | VF Shaders/Forward/Planet Atmosphere Blur | 2 | [json](generated/shaders/sharedassets0.assets/1426/metadata.json) |
| sharedassets0.assets / 1427 | Gizmos/Transform Gizmo/GizmoDiffuse | 22 | [json](generated/shaders/sharedassets0.assets/1427/metadata.json) |
| sharedassets0.assets / 1428 | VF Shaders/Forward/PBR Standard Icarus | 52 | [json](generated/shaders/sharedassets0.assets/1428/metadata.json) |
| sharedassets0.assets / 1429 | VF Shaders/Particle/Additive ZTestCam | 4 | [json](generated/shaders/sharedassets0.assets/1429/metadata.json) |
| sharedassets0.assets / 1430 | YC Shaders/Particle/Add | 4 | [json](generated/shaders/sharedassets0.assets/1430/metadata.json) |
| sharedassets0.assets / 1431 | VF Shaders/Gizmos/Space Command Grid | 4 | [json](generated/shaders/sharedassets0.assets/1431/metadata.json) |
| sharedassets0.assets / 1432 | UI Ex/Text Alpha Tech (for credits) | 2 | [json](generated/shaders/sharedassets0.assets/1432/metadata.json) |
| sharedassets0.assets / 1433 | UI Ex/Histogram | 2 | [json](generated/shaders/sharedassets0.assets/1433/metadata.json) |
| sharedassets0.assets / 1434 | VF Shaders/Gizmos/Building Grid | 4 | [json](generated/shaders/sharedassets0.assets/1434/metadata.json) |
| sharedassets0.assets / 1435 | VF Shaders/Particle/Alpha Blend Lit | 4 | [json](generated/shaders/sharedassets0.assets/1435/metadata.json) |
| sharedassets0.assets / 1436 | Universe/Starmap/Line | 2 | [json](generated/shaders/sharedassets0.assets/1436/metadata.json) |
| sharedassets0.assets / 1437 | Outline/Up sampling | 2 | [json](generated/shaders/sharedassets0.assets/1437/metadata.json) |
| sharedassets0.assets / 1438 | Unlit/UnlitColorZ | 3 | [json](generated/shaders/sharedassets0.assets/1438/metadata.json) |
| sharedassets0.assets / 1439 | Hidden/SunShaftsComposite | 7 | [json](generated/shaders/sharedassets0.assets/1439/metadata.json) |
| sharedassets0.assets / 1440 | VF Shaders/Dyson Sphere/Dyson Painting Grid | 2 | [json](generated/shaders/sharedassets0.assets/1440/metadata.json) |
| sharedassets0.assets / 1441 | UI Ex/Dyson Stat Histogram | 2 | [json](generated/shaders/sharedassets0.assets/1441/metadata.json) |
| sharedassets0.assets / 1442 | UI Ex/Cargo Bytes Flow Stat Histogram | 2 | [json](generated/shaders/sharedassets0.assets/1442/metadata.json) |
| sharedassets0.assets / 1443 | Minimap/Globe Body | 2 | [json](generated/shaders/sharedassets0.assets/1443/metadata.json) |
| sharedassets0.assets / 1444 | VF Shaders/Forward/Unlit Mecha Editor Sky Box | 2 | [json](generated/shaders/sharedassets0.assets/1444/metadata.json) |
| sharedassets0.assets / 1445 | Outline/Solid | 2 | [json](generated/shaders/sharedassets0.assets/1445/metadata.json) |
| sharedassets0.assets / 1446 | VF Shaders/Forward Spacecraft/Enemy Tri-planar Building | 78 | [json](generated/shaders/sharedassets0.assets/1446/metadata.json) |
| sharedassets0.assets / 1447 | Deep Profiler/Bar (Indirect) | 2 | [json](generated/shaders/sharedassets0.assets/1447/metadata.json) |
| sharedassets0.assets / 1448 | UI Ex/Text Additive | 2 | [json](generated/shaders/sharedassets0.assets/1448/metadata.json) |
| sharedassets0.assets / 1449 | UI Ex/Sail Globe | 2 | [json](generated/shaders/sharedassets0.assets/1449/metadata.json) |
| sharedassets0.assets / 1450 | GL/Colored Blended | 2 | [json](generated/shaders/sharedassets0.assets/1450/metadata.json) |
| sharedassets0.assets / 1451 | UI Ex/Station Transport Astro | 2 | [json](generated/shaders/sharedassets0.assets/1451/metadata.json) |
| sharedassets0.assets / 1452 | VF Shaders/Gizmos/Starmap Base Grid | 4 | [json](generated/shaders/sharedassets0.assets/1452/metadata.json) |
| sharedassets0.assets / 1453 | Universe/GasGiant2 | 22 | [json](generated/shaders/sharedassets0.assets/1453/metadata.json) |
| sharedassets0.assets / 1454 | UI/TranslucentImage | 8 | [json](generated/shaders/sharedassets0.assets/1454/metadata.json) |
| sharedassets0.assets / 1455 | VF Shaders/Star Shaders/Black Mask | 2 | [json](generated/shaders/sharedassets0.assets/1455/metadata.json) |
| sharedassets0.assets / 1456 | UI Ex/Storage Icons | 2 | [json](generated/shaders/sharedassets0.assets/1456/metadata.json) |
| sharedassets0.assets / 1457 | UI Ex/Dark Fog Logo Pulse | 2 | [json](generated/shaders/sharedassets0.assets/1457/metadata.json) |
| sharedassets0.assets / 1458 | VF Shaders/Forward Spacecraft/Flare Flame | 16 | [json](generated/shaders/sharedassets0.assets/1458/metadata.json) |
| sharedassets0.assets / 1459 | UI Ex/Text Alpha | 2 | [json](generated/shaders/sharedassets0.assets/1459/metadata.json) |
| sharedassets0.assets / 1460 | VF Shaders/Particle/Additive UVAnim | 4 | [json](generated/shaders/sharedassets0.assets/1460/metadata.json) |
| sharedassets0.assets / 1461 | VF Shaders/Forward Spacecraft/Enemy Tri-planar Tinder | 52 | [json](generated/shaders/sharedassets0.assets/1461/metadata.json) |
| sharedassets0.assets / 1462 | Hidden/SimpleClear | 2 | [json](generated/shaders/sharedassets0.assets/1462/metadata.json) |
| sharedassets0.assets / 1463 | VF Shaders/FX/Holographic | 6 | [json](generated/shaders/sharedassets0.assets/1463/metadata.json) |
| sharedassets0.assets / 1464 | VF Shaders/Particle/Additive Rim | 4 | [json](generated/shaders/sharedassets0.assets/1464/metadata.json) |
| sharedassets0.assets / 1465 | UI Ex/Dark Fog Logo Breath | 2 | [json](generated/shaders/sharedassets0.assets/1465/metadata.json) |
| sharedassets0.assets / 1466 | UI Ex/Station Transport Route | 2 | [json](generated/shaders/sharedassets0.assets/1466/metadata.json) |
| sharedassets0.assets / 1467 | UI Ex/Storage Bg | 2 | [json](generated/shaders/sharedassets0.assets/1467/metadata.json) |
| sharedassets0.assets / 1468 | VF Shaders/Forward/PBR Standard | 52 | [json](generated/shaders/sharedassets0.assets/1468/metadata.json) |
| sharedassets0.assets / 1469 | Hidden/Image Effects/Cinematic/Bloom | 12 | [json](generated/shaders/sharedassets0.assets/1469/metadata.json) |
| sharedassets0.assets / 1470 | UI Ex/Radar Map | 2 | [json](generated/shaders/sharedassets0.assets/1470/metadata.json) |
| sharedassets0.assets / 1471 | VF Shaders/Gizmos/GizmoAlphaZ | 4 | [json](generated/shaders/sharedassets0.assets/1471/metadata.json) |
| sharedassets0.assets / 1472 | UI Ex/Milestone Current | 2 | [json](generated/shaders/sharedassets0.assets/1472/metadata.json) |
| sharedassets0.assets / 1473 | UI Ex/Widget Additive | 2 | [json](generated/shaders/sharedassets0.assets/1473/metadata.json) |
| sharedassets0.assets / 1474 | UI Ex/Production Stat Histogram | 2 | [json](generated/shaders/sharedassets0.assets/1474/metadata.json) |
| sharedassets0.assets / 1475 | VF Shaders/Unlit/AlphaBlend ZWrite | 2 | [json](generated/shaders/sharedassets0.assets/1475/metadata.json) |
| sharedassets0.assets / 1476 | UI Ex/Mecha Curve Grid | 2 | [json](generated/shaders/sharedassets0.assets/1476/metadata.json) |
| sharedassets0.assets / 1477 | Universe/Dysonmap/Dyson Orbit | 2 | [json](generated/shaders/sharedassets0.assets/1477/metadata.json) |
| sharedassets0.assets / 1478 | UI Ex/Widget Alpha Audio | 2 | [json](generated/shaders/sharedassets0.assets/1478/metadata.json) |
