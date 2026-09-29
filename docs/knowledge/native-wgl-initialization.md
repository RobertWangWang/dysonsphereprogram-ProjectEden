# WGL 动态加载与扩展标志初始化

固定 UnityPlayer 函数 `1810eb670` 中，`1810eb6c9..1810eb85b`（末端不含）的 **402 字节、76 条指令**负责一段动态符号查询和能力标志写入。此前仅核验了 `wglSwapIntervalEXT` 的装载，此次补齐该片段全部符号请求及标志映射。

## 符号请求和结果保存

六次请求都通过 PE 导入槽 `181890940`，对应 `OPENGL32.dll!wglGetProcAddress`。名称指针、原始字符串、调用顺序及保存地址均由指令和离线模拟核对：

| 请求顺序 | 符号 | 返回值保存槽 |
| --- | --- | --- |
| 1 | `wglGetExtensionsStringEXT` | `181cdc160` |
| 2 | `wglCreateContextAttribsARB` | `181cdc168` |
| 3 | `wglGetPixelFormatAttribfvARB` | 本片段不保存 |
| 4 | `wglGetPixelFormatAttribivARB` | 本片段不保存 |
| 5 | `wglChoosePixelFormatARB` | `181cdc158` |
| 6 | `wglSwapIntervalEXT` | `181cdc170` |

不保存结果不等于没有调用，不能删除第 3、4 次请求而声称行为不变。四个保存槽都会被写入，返回空指针也照样保存。

## 扩展标志

若第一个请求结果非零，片段调用该函数获取一个指针，再将它与下表字符串依次传给辅助函数 `181834e78`。根据每次辅助调用的 RAX 是否非零，将对应字节写成 1 或 0。

| 标志地址 | 传给辅助函数的字符串 |
| --- | --- |
| `181d482d0` | `WGL_ARB_framebuffer_sRGB` |
| `181d482d1` | `WGL_EXT_framebuffer_sRGB` |
| `181d482d2` | `WGL_ARB_create_context_profile` |
| `181d482d3` | `WGL_ARB_create_context_robustness` |
| `181d482d4` | `WGL_ARB_robustness_application_isolation` |
| `181d482d5` | `WGL_ARB_context_flush_control` |
| `181d482d6` | `WGL_EXT_create_context_es_profile` |
| `181d482d7` | `WGL_EXT_swap_control_tear` |
| `181d482d8` | `WGL_NV_delay_before_swap` |
| `181d482d9` | `WGL_EXT_colorspace` |
| `181d482da` | `WGL_ARB_create_context_no_error` |

若 `wglGetExtensionsStringEXT` 未取得地址，跳转会绕过全部 11 次检查，**这些标志保留先前值**。这个分支不清零标志，不能只由当前符号加载失败推导所有标志为假。本轮也未将保留旧值判为缺陷；完整生命周期仍需调查。

## 验证范围

`tools/dsp_native_wgl_initialization.py` 在离线模拟器执行原始指令：外部解析器、扩展字符串提供者及检查辅助函数均使用受控返回桩，没有调用真实 OpenGL、创建上下文或运行游戏。

共 **4,160 组**用例：11 项检查结果的全部 2,048 种真假组合，各用两组初始内存和非空返回位模式验证，共 4,096 组；另验证六个请求结果的全部 64 种可用性组合。检查六次请求和十一项检查的顺序/字符串参数、完整两个全局页内容、写入范围、汇合地址和栈平衡，全部 76 条原始指令被执行。将最后一个标志写入地址向前错移一字节的负向用例被拒绝。

用例覆盖的是原片段对外部结果的处理，不证明真实驱动支持这些扩展、入口前置条件一定成立、辅助函数的子串或分词语义、扩展字符串函数返回空指针时的真实行为，亦不验证后续使用和再次写入。

证据保存在 `generated/native/UnityPlayer.dll/wgl-initialization/` 的 `initializer.asm`、`report.json` 与 `manifest.json`。原函数 C 保持不变，查询增添经清单验证的说明，不增加函数完成数量。

```powershell
python -X utf8 tools/dsp_native_wgl_initialization.py
python -X utf8 tools/dsp_native_query.py 1810eb670 --module UnityPlayer.dll --show
```
