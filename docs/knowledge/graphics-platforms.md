# 图形 shader 的其他平台程序

按元数据引用恢复 89 段 GLSL、58 段 Metal 文本；33 段 Vulkan 载荷已解码为 66 个 SPIR-V 阶段程序；仍待解码载荷 0 段。

每个程序核验索引、片段边界、202012090 版本、程序类型、关键词与输入通道，并严格消费整个子程序条目。参数条目与程序条目分开；此工具不声称解析了全部参数布局。实现参考 [AssetRipper 程序布局](https://github.com/AssetRipper/AssetRipper/blob/612d389/Source/AssetRipper.Export.Modules.Shader/ShaderBlob/ShaderSubProgram.cs) 与 [Metal 导出器](https://github.com/AssetRipper/ShaderRecoveryPlugin/blob/da11bd56bc4b149f63ee6c55520c328489d07055/ShaderTextRestorer/Exporters/ShaderMetalExporter.cs)。

运行：`python -X utf8 tools/dsp_graphics_platforms.py --smolv-decoder 路径/dsp_smolv_decode.exe --spirv-dis 路径/spirv-dis.exe`。解码器以本仓库 tools/dsp_smolv_decode.cpp 与 [smol-v](https://github.com/aras-p/smol-v/tree/55000efe742f56d8b51223b9ea7775a8f0501881) 构建；每个解码结果重新编码再解码，验证 SPIR-V 字节一致，之后由 Khronos spirv-dis 读取。依赖基础 shader 导出；源元数据、程序块和输出哈希均记录在清单。每个 Vulkan 载荷的全部阶段文件见 index.json 的 snippets；下表链接该载荷的首个阶段。

| Shader | 平台 | 条目 | 格式 | 结果 |
|---|---:|---:|---|---|
| Hidden/InternalErrorShader | 5 | 1 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p5-e1.glsl) |
| Hidden/InternalErrorShader | 5 | 2 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p5-e2.glsl) |
| Hidden/InternalErrorShader | 5 | 3 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p5-e3.glsl) |
| Hidden/InternalErrorShader | 5 | 4 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p5-e4.glsl) |
| Hidden/InternalErrorShader | 5 | 5 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p5-e5.glsl) |
| Hidden/InternalErrorShader | 5 | 6 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p5-e6.glsl) |
| Hidden/InternalErrorShader | 9 | 3 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p9-e3.glsl) |
| Hidden/InternalErrorShader | 9 | 4 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p9-e4.glsl) |
| Hidden/InternalErrorShader | 9 | 5 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p9-e5.glsl) |
| Hidden/InternalErrorShader | 9 | 6 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p9-e6.glsl) |
| Hidden/InternalErrorShader | 9 | 8 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p9-e8.glsl) |
| Hidden/InternalErrorShader | 9 | 9 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p9-e9.glsl) |
| Hidden/InternalErrorShader | 9 | 10 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p9-e10.glsl) |
| Hidden/InternalErrorShader | 9 | 11 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p9-e11.glsl) |
| Hidden/InternalErrorShader | 14 | 3 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p14-e3.metal) |
| Hidden/InternalErrorShader | 14 | 4 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p14-e4.metal) |
| Hidden/InternalErrorShader | 14 | 5 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p14-e5.metal) |
| Hidden/InternalErrorShader | 14 | 6 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p14-e6.metal) |
| Hidden/InternalErrorShader | 14 | 8 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p14-e8.metal) |
| Hidden/InternalErrorShader | 14 | 9 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p14-e9.metal) |
| Hidden/InternalErrorShader | 14 | 10 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p14-e10.metal) |
| Hidden/InternalErrorShader | 14 | 11 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p14-e11.metal) |
| Hidden/InternalErrorShader | 14 | 13 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p14-e13.metal) |
| Hidden/InternalErrorShader | 14 | 14 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p14-e14.metal) |
| Hidden/InternalErrorShader | 14 | 15 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p14-e15.metal) |
| Hidden/InternalErrorShader | 14 | 16 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p14-e16.metal) |
| Hidden/InternalErrorShader | 14 | 18 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p14-e18.metal) |
| Hidden/InternalErrorShader | 14 | 19 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p14-e19.metal) |
| Hidden/InternalErrorShader | 14 | 20 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p14-e20.metal) |
| Hidden/InternalErrorShader | 14 | 21 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p14-e21.metal) |
| Hidden/InternalErrorShader | 15 | 3 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p15-e3.glsl) |
| Hidden/InternalErrorShader | 15 | 4 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p15-e4.glsl) |
| Hidden/InternalErrorShader | 15 | 5 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p15-e5.glsl) |
| Hidden/InternalErrorShader | 15 | 6 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p15-e6.glsl) |
| Hidden/InternalErrorShader | 15 | 8 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p15-e8.glsl) |
| Hidden/InternalErrorShader | 15 | 9 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p15-e9.glsl) |
| Hidden/InternalErrorShader | 15 | 10 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p15-e10.glsl) |
| Hidden/InternalErrorShader | 15 | 11 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p15-e11.glsl) |
| Hidden/InternalErrorShader | 18 | 4 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p18-e4-s0.spvasm) |
| Hidden/InternalErrorShader | 18 | 5 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p18-e5-s0.spvasm) |
| Hidden/InternalErrorShader | 18 | 6 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p18-e6-s0.spvasm) |
| Hidden/InternalErrorShader | 18 | 7 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p18-e7-s0.spvasm) |
| Hidden/InternalErrorShader | 18 | 12 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p18-e12-s0.spvasm) |
| Hidden/InternalErrorShader | 18 | 13 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p18-e13-s0.spvasm) |
| Hidden/InternalErrorShader | 18 | 14 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p18-e14-s0.spvasm) |
| Hidden/InternalErrorShader | 18 | 15 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/17/p18-e15-s0.spvasm) |
| Hidden/InternalClear | 5 | 1 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p5-e1.glsl) |
| Hidden/InternalClear | 5 | 2 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p5-e2.glsl) |
| Hidden/InternalClear | 9 | 3 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p9-e3.glsl) |
| Hidden/InternalClear | 9 | 4 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p9-e4.glsl) |
| Hidden/InternalClear | 9 | 5 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p9-e5.glsl) |
| Hidden/InternalClear | 9 | 6 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p9-e6.glsl) |
| Hidden/InternalClear | 14 | 3 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p14-e3.metal) |
| Hidden/InternalClear | 14 | 4 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p14-e4.metal) |
| Hidden/InternalClear | 14 | 5 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p14-e5.metal) |
| Hidden/InternalClear | 14 | 6 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p14-e6.metal) |
| Hidden/InternalClear | 14 | 8 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p14-e8.metal) |
| Hidden/InternalClear | 14 | 9 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p14-e9.metal) |
| Hidden/InternalClear | 14 | 10 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p14-e10.metal) |
| Hidden/InternalClear | 14 | 11 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p14-e11.metal) |
| Hidden/InternalClear | 15 | 3 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p15-e3.glsl) |
| Hidden/InternalClear | 15 | 4 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p15-e4.glsl) |
| Hidden/InternalClear | 15 | 5 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p15-e5.glsl) |
| Hidden/InternalClear | 15 | 6 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p15-e6.glsl) |
| Hidden/InternalClear | 18 | 4 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p18-e4-s0.spvasm) |
| Hidden/InternalClear | 18 | 5 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p18-e5-s0.spvasm) |
| Hidden/InternalClear | 18 | 6 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p18-e6-s0.spvasm) |
| Hidden/InternalClear | 18 | 7 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/68/p18-e7-s0.spvasm) |
| Hidden/Internal-Colored | 5 | 1 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p5-e1.glsl) |
| Hidden/Internal-Colored | 5 | 2 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p5-e2.glsl) |
| Hidden/Internal-Colored | 9 | 3 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p9-e3.glsl) |
| Hidden/Internal-Colored | 9 | 4 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p9-e4.glsl) |
| Hidden/Internal-Colored | 9 | 5 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p9-e5.glsl) |
| Hidden/Internal-Colored | 9 | 6 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p9-e6.glsl) |
| Hidden/Internal-Colored | 14 | 3 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p14-e3.metal) |
| Hidden/Internal-Colored | 14 | 4 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p14-e4.metal) |
| Hidden/Internal-Colored | 14 | 5 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p14-e5.metal) |
| Hidden/Internal-Colored | 14 | 6 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p14-e6.metal) |
| Hidden/Internal-Colored | 14 | 8 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p14-e8.metal) |
| Hidden/Internal-Colored | 14 | 9 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p14-e9.metal) |
| Hidden/Internal-Colored | 14 | 10 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p14-e10.metal) |
| Hidden/Internal-Colored | 14 | 11 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p14-e11.metal) |
| Hidden/Internal-Colored | 15 | 3 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p15-e3.glsl) |
| Hidden/Internal-Colored | 15 | 4 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p15-e4.glsl) |
| Hidden/Internal-Colored | 15 | 5 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p15-e5.glsl) |
| Hidden/Internal-Colored | 15 | 6 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p15-e6.glsl) |
| Hidden/Internal-Colored | 18 | 4 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p18-e4-s0.spvasm) |
| Hidden/Internal-Colored | 18 | 5 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p18-e5-s0.spvasm) |
| Hidden/Internal-Colored | 18 | 6 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p18-e6-s0.spvasm) |
| Hidden/Internal-Colored | 18 | 7 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/69/p18-e7-s0.spvasm) |
| Hidden/Internal-Loading | 5 | 1 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p5-e1.glsl) |
| Hidden/Internal-Loading | 5 | 2 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p5-e2.glsl) |
| Hidden/Internal-Loading | 9 | 3 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p9-e3.glsl) |
| Hidden/Internal-Loading | 9 | 4 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p9-e4.glsl) |
| Hidden/Internal-Loading | 9 | 5 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p9-e5.glsl) |
| Hidden/Internal-Loading | 9 | 6 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p9-e6.glsl) |
| Hidden/Internal-Loading | 14 | 3 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p14-e3.metal) |
| Hidden/Internal-Loading | 14 | 4 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p14-e4.metal) |
| Hidden/Internal-Loading | 14 | 5 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p14-e5.metal) |
| Hidden/Internal-Loading | 14 | 6 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p14-e6.metal) |
| Hidden/Internal-Loading | 14 | 8 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p14-e8.metal) |
| Hidden/Internal-Loading | 14 | 9 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p14-e9.metal) |
| Hidden/Internal-Loading | 14 | 10 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p14-e10.metal) |
| Hidden/Internal-Loading | 14 | 11 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p14-e11.metal) |
| Hidden/Internal-Loading | 15 | 3 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p15-e3.glsl) |
| Hidden/Internal-Loading | 15 | 4 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p15-e4.glsl) |
| Hidden/Internal-Loading | 15 | 5 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p15-e5.glsl) |
| Hidden/Internal-Loading | 15 | 6 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p15-e6.glsl) |
| Hidden/Internal-Loading | 18 | 4 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p18-e4-s0.spvasm) |
| Hidden/Internal-Loading | 18 | 5 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p18-e5-s0.spvasm) |
| Hidden/Internal-Loading | 18 | 6 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p18-e6-s0.spvasm) |
| Hidden/Internal-Loading | 18 | 7 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/70/p18-e7-s0.spvasm) |
| GUI/Text Shader | 5 | 1 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p5-e1.glsl) |
| GUI/Text Shader | 5 | 2 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p5-e2.glsl) |
| GUI/Text Shader | 9 | 3 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p9-e3.glsl) |
| GUI/Text Shader | 9 | 4 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p9-e4.glsl) |
| GUI/Text Shader | 9 | 5 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p9-e5.glsl) |
| GUI/Text Shader | 9 | 6 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p9-e6.glsl) |
| GUI/Text Shader | 14 | 3 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p14-e3.metal) |
| GUI/Text Shader | 14 | 4 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p14-e4.metal) |
| GUI/Text Shader | 14 | 5 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p14-e5.metal) |
| GUI/Text Shader | 14 | 6 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p14-e6.metal) |
| GUI/Text Shader | 14 | 8 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p14-e8.metal) |
| GUI/Text Shader | 14 | 9 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p14-e9.metal) |
| GUI/Text Shader | 14 | 10 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p14-e10.metal) |
| GUI/Text Shader | 14 | 11 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p14-e11.metal) |
| GUI/Text Shader | 15 | 3 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p15-e3.glsl) |
| GUI/Text Shader | 15 | 4 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p15-e4.glsl) |
| GUI/Text Shader | 15 | 5 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p15-e5.glsl) |
| GUI/Text Shader | 15 | 6 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p15-e6.glsl) |
| GUI/Text Shader | 18 | 4 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p18-e4-s0.spvasm) |
| GUI/Text Shader | 18 | 5 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p18-e5-s0.spvasm) |
| GUI/Text Shader | 18 | 6 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p18-e6-s0.spvasm) |
| GUI/Text Shader | 18 | 7 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10101/p18-e7-s0.spvasm) |
| Hidden/FrameDebuggerRenderTargetDisplay | 5 | 1 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p5-e1.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 5 | 2 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p5-e2.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 5 | 3 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p5-e3.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 5 | 4 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p5-e4.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 5 | 5 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p5-e5.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 5 | 6 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p5-e6.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 5 | 7 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p5-e7.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 5 | 8 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p5-e8.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 5 | 9 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p5-e9.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 9 | 5 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p9-e5.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 9 | 6 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p9-e6.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 9 | 7 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p9-e7.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 9 | 8 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p9-e8.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 9 | 9 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p9-e9.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 9 | 10 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p9-e10.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 9 | 11 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p9-e11.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 9 | 12 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p9-e12.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 9 | 13 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p9-e13.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 14 | 1 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p14-e1.metal) |
| Hidden/FrameDebuggerRenderTargetDisplay | 14 | 7 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p14-e7.metal) |
| Hidden/FrameDebuggerRenderTargetDisplay | 14 | 8 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p14-e8.metal) |
| Hidden/FrameDebuggerRenderTargetDisplay | 14 | 9 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p14-e9.metal) |
| Hidden/FrameDebuggerRenderTargetDisplay | 14 | 10 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p14-e10.metal) |
| Hidden/FrameDebuggerRenderTargetDisplay | 14 | 11 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p14-e11.metal) |
| Hidden/FrameDebuggerRenderTargetDisplay | 14 | 12 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p14-e12.metal) |
| Hidden/FrameDebuggerRenderTargetDisplay | 14 | 13 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p14-e13.metal) |
| Hidden/FrameDebuggerRenderTargetDisplay | 14 | 14 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p14-e14.metal) |
| Hidden/FrameDebuggerRenderTargetDisplay | 14 | 15 | Metal | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p14-e15.metal) |
| Hidden/FrameDebuggerRenderTargetDisplay | 15 | 5 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p15-e5.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 15 | 6 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p15-e6.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 15 | 7 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p15-e7.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 15 | 8 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p15-e8.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 15 | 9 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p15-e9.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 15 | 10 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p15-e10.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 15 | 11 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p15-e11.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 15 | 12 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p15-e12.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 15 | 13 | GLSL | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p15-e13.glsl) |
| Hidden/FrameDebuggerRenderTargetDisplay | 18 | 9 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p18-e9-s0.spvasm) |
| Hidden/FrameDebuggerRenderTargetDisplay | 18 | 10 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p18-e10-s0.spvasm) |
| Hidden/FrameDebuggerRenderTargetDisplay | 18 | 11 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p18-e11-s0.spvasm) |
| Hidden/FrameDebuggerRenderTargetDisplay | 18 | 12 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p18-e12-s0.spvasm) |
| Hidden/FrameDebuggerRenderTargetDisplay | 18 | 13 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p18-e13-s0.spvasm) |
| Hidden/FrameDebuggerRenderTargetDisplay | 18 | 14 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p18-e14-s0.spvasm) |
| Hidden/FrameDebuggerRenderTargetDisplay | 18 | 15 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p18-e15-s0.spvasm) |
| Hidden/FrameDebuggerRenderTargetDisplay | 18 | 16 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p18-e16-s0.spvasm) |
| Hidden/FrameDebuggerRenderTargetDisplay | 18 | 17 | SPIR-V | [查看](generated/graphics-platforms/Resources/unity%20default%20resources/10755/p18-e17-s0.spvasm) |
