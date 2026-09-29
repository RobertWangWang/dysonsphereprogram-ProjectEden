# 非 DXBC compute 程序

已提取 56 段 GLSL、38 段 SPIR-V；全部线程组与 ComputeShader 元数据一致。GLSL 保留资源原始字节，SPIR-V 输出二进制与可读指令，不声称恢复原始 HLSL。

使用 [Khronos SPIRV-Tools v2026.3](https://github.com/KhronosGroup/SPIRV-Tools/tree/v2026.3) 构建的 spirv-dis，工具提交 b707790a898e44038547df54580022fc1cf89c3d，SPIRV-Headers 提交 29981f65241605e08b0ede4cfeb999fe3b723c6a。具体工具与输出哈希见 generated/compute-platforms/index.json 和 manifest.json。

重建：`python -X utf8 tools/dsp_compute_platforms.py --spirv-dis 路径/spirv-dis.exe`。依赖基础 shader 导出；独立补充清单不修改基础导出问题记录，图形 shader 的其他平台程序仍需单独解析。

| Shader | Kernel | 平台编号 | 格式 | 线程组 | 结果 |
|---|---|---:|---|---|---|
| Internal-Skinning | main | 11 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v1-k0-u0.glsl) |
| Internal-Skinning | main | 11 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v1-k1-u0.glsl) |
| Internal-Skinning | main | 11 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v1-k2-u0.glsl) |
| Internal-Skinning | main | 11 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v1-k3-u0.glsl) |
| Internal-Skinning | main | 11 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v1-k4-u0.glsl) |
| Internal-Skinning | main | 11 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v1-k5-u0.glsl) |
| Internal-Skinning | main | 11 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v1-k6-u0.glsl) |
| Internal-Skinning | main | 11 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v1-k7-u0.glsl) |
| Internal-Skinning | main | 11 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v1-k8-u0.glsl) |
| Internal-Skinning | main | 11 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v1-k9-u0.glsl) |
| Internal-Skinning | main | 11 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v1-k10-u0.glsl) |
| Internal-Skinning | main | 11 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v1-k11-u0.glsl) |
| Internal-Skinning | main | 11 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v1-k12-u0.glsl) |
| Internal-Skinning | main | 11 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v1-k13-u0.glsl) |
| Internal-Skinning | main | 11 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v1-k14-u0.glsl) |
| Internal-Skinning | main | 11 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v1-k15-u0.glsl) |
| Internal-Skinning | main | 17 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v2-k0-u0.glsl) |
| Internal-Skinning | main | 17 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v2-k1-u0.glsl) |
| Internal-Skinning | main | 17 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v2-k2-u0.glsl) |
| Internal-Skinning | main | 17 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v2-k3-u0.glsl) |
| Internal-Skinning | main | 17 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v2-k4-u0.glsl) |
| Internal-Skinning | main | 17 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v2-k5-u0.glsl) |
| Internal-Skinning | main | 17 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v2-k6-u0.glsl) |
| Internal-Skinning | main | 17 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v2-k7-u0.glsl) |
| Internal-Skinning | main | 17 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v2-k8-u0.glsl) |
| Internal-Skinning | main | 17 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v2-k9-u0.glsl) |
| Internal-Skinning | main | 17 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v2-k10-u0.glsl) |
| Internal-Skinning | main | 17 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v2-k11-u0.glsl) |
| Internal-Skinning | main | 17 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v2-k12-u0.glsl) |
| Internal-Skinning | main | 17 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v2-k13-u0.glsl) |
| Internal-Skinning | main | 17 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v2-k14-u0.glsl) |
| Internal-Skinning | main | 17 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v2-k15-u0.glsl) |
| Internal-Skinning | main | 21 | SPIR-V | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v4-k0-u0.spvasm) |
| Internal-Skinning | main | 21 | SPIR-V | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v4-k1-u0.spvasm) |
| Internal-Skinning | main | 21 | SPIR-V | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v4-k2-u0.spvasm) |
| Internal-Skinning | main | 21 | SPIR-V | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v4-k3-u0.spvasm) |
| Internal-Skinning | main | 21 | SPIR-V | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v4-k4-u0.spvasm) |
| Internal-Skinning | main | 21 | SPIR-V | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v4-k5-u0.spvasm) |
| Internal-Skinning | main | 21 | SPIR-V | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v4-k6-u0.spvasm) |
| Internal-Skinning | main | 21 | SPIR-V | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v4-k7-u0.spvasm) |
| Internal-Skinning | main | 21 | SPIR-V | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v4-k8-u0.spvasm) |
| Internal-Skinning | main | 21 | SPIR-V | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v4-k9-u0.spvasm) |
| Internal-Skinning | main | 21 | SPIR-V | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v4-k10-u0.spvasm) |
| Internal-Skinning | main | 21 | SPIR-V | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v4-k11-u0.spvasm) |
| Internal-Skinning | main | 21 | SPIR-V | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v4-k12-u0.spvasm) |
| Internal-Skinning | main | 21 | SPIR-V | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v4-k13-u0.spvasm) |
| Internal-Skinning | main | 21 | SPIR-V | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v4-k14-u0.spvasm) |
| Internal-Skinning | main | 21 | SPIR-V | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/300/v4-k15-u0.spvasm) |
| Internal-BlendShape | main | 11 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/301/v1-k0-u0.glsl) |
| Internal-BlendShape | main | 11 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/301/v1-k1-u0.glsl) |
| Internal-BlendShape | main | 11 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/301/v1-k2-u0.glsl) |
| Internal-BlendShape | main | 17 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/301/v2-k0-u0.glsl) |
| Internal-BlendShape | main | 17 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/301/v2-k1-u0.glsl) |
| Internal-BlendShape | main | 17 | GLSL | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/301/v2-k2-u0.glsl) |
| Internal-BlendShape | main | 21 | SPIR-V | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/301/v4-k0-u0.spvasm) |
| Internal-BlendShape | main | 21 | SPIR-V | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/301/v4-k1-u0.spvasm) |
| Internal-BlendShape | main | 21 | SPIR-V | 64 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/301/v4-k2-u0.spvasm) |
| Internal-VT-TranslationTableReplace | ReplaceTranslationTable | 11 | GLSL | 256 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/400/v1-k0-u0.glsl) |
| Internal-VT-TranslationTableReplace | ReplaceTranslationTable | 17 | GLSL | 256 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/400/v2-k0-u0.glsl) |
| Internal-VT-TranslationTableReplace | ReplaceTranslationTable | 21 | SPIR-V | 256 × 1 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/400/v4-k0-u0.spvasm) |
| Internal-VT-TranslationTableUpsample | Main | 17 | GLSL | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v1-k0-u0.glsl) |
| Internal-VT-TranslationTableUpsample | Main | 17 | GLSL | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v1-k1-u0.glsl) |
| Internal-VT-TranslationTableUpsample | Main | 17 | GLSL | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v1-k2-u0.glsl) |
| Internal-VT-TranslationTableUpsample | Main | 17 | GLSL | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v1-k3-u0.glsl) |
| Internal-VT-TranslationTableUpsample | Main | 17 | GLSL | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v1-k4-u0.glsl) |
| Internal-VT-TranslationTableUpsample | Main | 17 | GLSL | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v1-k5-u0.glsl) |
| Internal-VT-TranslationTableUpsample | Main | 17 | GLSL | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v1-k6-u0.glsl) |
| Internal-VT-TranslationTableUpsample | Main | 17 | GLSL | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v1-k7-u0.glsl) |
| Internal-VT-TranslationTableUpsample | Main | 17 | GLSL | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v1-k8-u0.glsl) |
| Internal-VT-TranslationTableUpsample | Main | 17 | GLSL | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v1-k9-u0.glsl) |
| Internal-VT-TranslationTableUpsample | Main | 17 | GLSL | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v1-k10-u0.glsl) |
| Internal-VT-TranslationTableUpsample | Main | 17 | GLSL | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v1-k11-u0.glsl) |
| Internal-VT-TranslationTableUpsample | Main | 17 | GLSL | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v1-k12-u0.glsl) |
| Internal-VT-TranslationTableUpsample | Main | 17 | GLSL | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v1-k13-u0.glsl) |
| Internal-VT-TranslationTableUpsample | Main | 17 | GLSL | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v1-k14-u0.glsl) |
| Internal-VT-TranslationTableUpsample | Main | 17 | GLSL | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v1-k15-u0.glsl) |
| Internal-VT-TranslationTableUpsample | Main | 21 | SPIR-V | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v3-k0-u0.spvasm) |
| Internal-VT-TranslationTableUpsample | Main | 21 | SPIR-V | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v3-k1-u0.spvasm) |
| Internal-VT-TranslationTableUpsample | Main | 21 | SPIR-V | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v3-k2-u0.spvasm) |
| Internal-VT-TranslationTableUpsample | Main | 21 | SPIR-V | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v3-k3-u0.spvasm) |
| Internal-VT-TranslationTableUpsample | Main | 21 | SPIR-V | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v3-k4-u0.spvasm) |
| Internal-VT-TranslationTableUpsample | Main | 21 | SPIR-V | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v3-k5-u0.spvasm) |
| Internal-VT-TranslationTableUpsample | Main | 21 | SPIR-V | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v3-k6-u0.spvasm) |
| Internal-VT-TranslationTableUpsample | Main | 21 | SPIR-V | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v3-k7-u0.spvasm) |
| Internal-VT-TranslationTableUpsample | Main | 21 | SPIR-V | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v3-k8-u0.spvasm) |
| Internal-VT-TranslationTableUpsample | Main | 21 | SPIR-V | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v3-k9-u0.spvasm) |
| Internal-VT-TranslationTableUpsample | Main | 21 | SPIR-V | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v3-k10-u0.spvasm) |
| Internal-VT-TranslationTableUpsample | Main | 21 | SPIR-V | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v3-k11-u0.spvasm) |
| Internal-VT-TranslationTableUpsample | Main | 21 | SPIR-V | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v3-k12-u0.spvasm) |
| Internal-VT-TranslationTableUpsample | Main | 21 | SPIR-V | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v3-k13-u0.spvasm) |
| Internal-VT-TranslationTableUpsample | Main | 21 | SPIR-V | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v3-k14-u0.spvasm) |
| Internal-VT-TranslationTableUpsample | Main | 21 | SPIR-V | 32 × 32 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/401/v3-k15-u0.spvasm) |
| Internal-CreateFoveatedShadingRateTextureArray | CreateFoveatedShadingRateTexture | 21 | SPIR-V | 8 × 8 × 2 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/600/v0-k0-u0.spvasm) |
| Internal-CreateFoveatedShadingRateTextureNoArray | CreateFoveatedShadingRateTexture | 21 | SPIR-V | 8 × 8 × 1 | [查看](generated/compute-platforms/Resources/unity%20default%20resources/601/v2-k0-u0.spvasm) |
