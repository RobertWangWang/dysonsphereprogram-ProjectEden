# OpenSSL 汇编源码参考核验

## SHA-512 XOP：已匹配规范化指令

`rail_api64.dll` 的 `18001cf40..18001df3c` 区域匹配 OpenSSL 1.1.0g 的 `sha512_block_data_order_xop`。Ghidra 将其 Win64 前置包装与主体分别识别为 `18001cf40`、`18001cf4d`；这里作为一个汇编过程记录，不重复增加函数统计。

来源为 [OpenSSL 官方固定提交](https://github.com/openssl/openssl/blob/b2758a2292aceda93e9f44c219b94fe21bb9a650/crypto/sha/asm/sha512-x86_64.pl)，提交 `b2758a2292aceda93e9f44c219b94fe21bb9a650` 对应 `OpenSSL_1_1_0g` 标签。模块内的版本字符串只是寻找来源的线索，最终结论基于下述机器指令比较。

`dsp_native_openssl_match.py` 固定生成器 `sha512-x86_64.pl` 和翻译器 `x86_64-xlate.pl` 的输入 SHA-256，使用 Perl 和 NASM 3.02 重新生成 Win64 COFF 对象。从对象符号表定位 XOP 入口、结束标记及 K512 常量；再与当前游戏 DLL 比较：

- 游戏代码 4,093 字节，重建代码 4,109 字节，完整解码均无剩余未识别字节。
- 仅移除被解码为 NOP 的对齐指令；游戏含 15 条，重建含 25 条。
- 剩余 **1,086 条指令**逐项相等：保留助记符、寄存器、操作数宽度、寻址形式及普通立即数。
- 分支目标映射为相同的有效指令序号，不能简单忽略地址差异；跳到 NOP 时映射至其后有效指令。
- RIP 相对访问映射为 K512 常量区的偏移，所有偏移必须落在验证范围内；**1,312 字节常量完整逐字节相等**。
- 在内存副本中修改指令前缀，负例被拒绝。未修改或执行游戏 DLL。

这是一份 **normalized-source-matched** 结果，区别于 Unity VP8 的逐字节匹配。NOP 对齐及其导致的分支位移变化已记录；没有宣称原始源工程、编译配置或整个 OpenSSL 库都完全一致，也没有把它计为新增 C 伪代码。规范化比较提供明确的机器指令对应关系，不验证所有运行环境及外部异常行为。

查询工具支持名称、包装入口和主体入口：

```powershell
python -X utf8 tools/dsp_native_query.py sha512_block_data_order_xop --module rail_api64.dll
python -X utf8 tools/dsp_native_query.py 18001cf4d --module rail_api64.dll --show
```

`--show` 显示该过程在游戏中的完整反汇编，包含原先使 Ghidra 截断的 `vprotq`；上游 Perl 生成器与生成汇编可以同时查阅。文件保存在 `generated/native/DSPGAME_Data__Plugins__x86_64__rail_api64.dll/upstream-openssl/sha512-xop/`：

- `sha512-x86_64.pl`、`x86_64-xlate.pl`、`LICENSE`：固定上游输入及许可证。
- `sha512.asm`、`sha512.obj`：本机重建文件。
- `game.asm`、`rebuilt.asm`：完整比较范围的两份解码清单。
- `report.json`、`manifest.json`：来源、限制、数量、工具哈希及文件哈希。

重现命令：

```powershell
# 使用安装了 Capstone 的 Python；reference 中放上述两份固定提交 Perl 源码及 LICENSE
python -X utf8 tools/dsp_native_openssl_match.py --reference <上游目录> --perl <perl.exe> --nasm <nasm.exe>
```

核验每次重新生成并汇编对象，不直接信任旧对象。查询每次检查当前游戏来源哈希和缓存文件哈希。其他 XOP/FMA4 截断函数仍需分别核验，不能由这一处匹配推断全部解决。

## ChaCha20 四路 XOP：第二处匹配

`rail_api64.dll` 的 `18003e3a0..18003eb75` 已匹配同一固定提交的 [`ChaCha20_4xop` 汇编生成器](https://github.com/openssl/openssl/blob/b2758a2292aceda93e9f44c219b94fe21bb9a650/crypto/chacha/asm/chacha-x86_64.pl)。此前 Ghidra 在该过程的 `vprotd` 处截断；现在可以查阅完整上游实现及游戏反汇编。

使用同一规范化规则重新构建和比较，得到：

| 项目 | 游戏 | 重建 |
|---|---:|---:|
| 完整过程字节 | 2,006 | 2,046 |
| 已解码指令 | 467 | 489 |
| NOP 指令 | 79 | 101 |
| 非 NOP 指令 | 388 | 388 |

388 条有效指令、对应的分支目标、寄存器和操作数全部匹配；映射到的 192 字节常量区逐字节一致。RIP 引用的完整操作数宽度必须位于该常量区内。指令前缀变异负例同样被拒绝。仍属于规范化匹配，未将差异字节掩盖为“二进制完全相同”。

固定输入 `chacha-x86_64.pl` 的 SHA-256 为 `748319e12105b4686da3b83208ff26add86cf5eecfb11d71199ba3707c92b2e4`；文件及 OpenSSL LICENSE、汇编器输出、完整比较清单、报告和 manifest 保存在 `upstream-openssl/chacha4-xop/`。`dsp_native_openssl_match.py` 现通过 `--routine` 选择过程，默认仍为 `sha512-xop`。

```powershell
python -X utf8 tools/dsp_native_openssl_match.py --routine chacha4-xop --reference <上游目录> --perl <perl.exe> --nasm <nasm.exe>
python -X utf8 tools/dsp_native_query.py ChaCha20_4xop --module rail_api64.dll
python -X utf8 tools/dsp_native_query.py 18003e3a0 --module rail_api64.dll --show
```

查询工具现在枚举已核验的 OpenSSL 过程，不再只支持单个固定目录。本次扩展后重新构建核验 SHA-512 XOP 仍通过。当前有两个 OpenSSL 汇编过程获得源码参考；这是对指令截断的补充，不增加 C 导出计数，其他截断项继续保留。

## PadLock SHA-512：完整机器码逐字节匹配

此前两套解码器均无法识别的 `180015e88: F3 0F A6 E0` 已定位到同一 OpenSSL 提交中的 [`padlock_sha512_blocks`](https://github.com/openssl/openssl/blob/b2758a2292aceda93e9f44c219b94fe21bb9a650/engines/asm/e_padlock-x86_64.pl)。上游汇编本身也用 `DB 0xf3,0x0f,0xa6,0xe0` 表达该指令。

从固定生成器重建 Win64 COFF 后，`padlock_sha512_blocks` 到其 SEH 结束标记之间的 **129 字节**，与游戏的 `180015e40..180015ec0` **完整逐字节相等**。本过程没有待处理 COFF 重定位，不需要 NOP 归一化、地址替换或跳过特殊指令。改动内存副本中该指令的一个字节，比较立即失败。

生成器输入 `e_padlock-x86_64.pl` 的 SHA-256 固定为 `020e2469de5f1c7543b4b28948636c16656046e263e7e0b4f652e4325d379ff1`；共享翻译器同前。`dsp_native_padlock_match.py` 每次重新生成和汇编，逐字节比较完整范围，并单独核验那 4 个不透明字节及前后指令的完整解码覆盖。

文件保存在 `upstream-openssl/padlock-sha512/`，包括源文件、LICENSE、重建汇编和对象、游戏范围清单、工具哈希、报告及 manifest。状态为 **byte-exact-source-matched**，与前两项 normalized-source-matched 区分。该结果确认了完整过程的源码对应关系，但没有凭空生成该硬件指令的 C 或 p-code，也不声称完整恢复其所有硬件副作用；清单继续使用上游 DB 字面量。

```powershell
# 需安装 Capstone；reference 包含固定生成器、x86_64-xlate.pl 和 LICENSE
python -X utf8 tools/dsp_native_padlock_match.py --reference <上游目录> --perl <perl.exe> --nasm <nasm.exe>
python -X utf8 tools/dsp_native_query.py padlock_sha512_blocks --module rail_api64.dll
python -X utf8 tools/dsp_native_query.py 180015e40 --module rail_api64.dll --show
```

截至 PadLock 核验，共有 3 个 OpenSSL 过程具有已核验的源码参考：2 项规范化指令匹配、1 项完整字节匹配。它们均单独记录，不增加 C 输出函数数，原始 Ghidra 截断结果保留供追溯。

## AES-NI CBC 与 SHA-256 XOP：第四处源码参考

`rail_api64.dll` 的 `18002e280..18002f35e` 已对应同一固定提交的 `aesni_cbc_sha256_enc_xop`。完整过程包括 Win64 前置保存代码；原 Ghidra 函数索引只有主体入口 `18002e28d`，没有 `18002e280`。查询工具现在在完整入口未被索引时，将函数名称与完整入口地址关联到主体记录，避免漏查；既有过程不会因此重复命中。

固定生成器为 `crypto/aes/asm/aesni-sha256-x86_64.pl`，输入 SHA-256 为 `2c227ce42a68f2b3ae6ff798df2f81b80723028eba2d22d14ec353a68988f9da`。从该生成器与共享翻译器重新生成并汇编 Win64 COFF 后，核验结果如下：

| 项目 | 游戏 | 重建 |
|---|---:|---:|
| 完整过程字节 | 4,319 | 4,319 |
| 已解码指令 | 1,174 | 1,168 |
| NOP 指令 | 22 | 16 |
| 非 NOP 指令 | 1,152 | 1,152 |

仅按前述规则移除 NOP 并规范化地址后，全部 1,152 条有效指令匹配，分支目标、寄存器、宽度和操作数均参与比较。`18002dfc0` 起 704 字节逐字节相等，包括 K256、掩码、标识字符串及对齐数据，不能将其全部描述为密码算法常量。指令前缀变异负例被拒绝。虽然两个过程总字节数相同，其 NOP 布局不同，状态仍为 **normalized-source-matched**。

证据保存于 `upstream-openssl/aesni-sha256-xop/`，包含固定源文件、LICENSE、重建汇编和对象、游戏完整反汇编、报告及哈希清单。报告记录固定上游提交和源码 URL，游戏代码 SHA-256 为 `ec254b7bd8fecdb59334b3adec6bdb67c3ddc2e97f2c1a9bb91cc92620b2d2dd`。

```powershell
python -X utf8 tools/dsp_native_openssl_match.py --routine aesni-sha256-xop --reference <上游目录> --perl <perl.exe> --nasm <nasm.exe>
python -X utf8 tools/dsp_native_query.py aesni_cbc_sha256_enc_xop --module rail_api64.dll
python -X utf8 tools/dsp_native_query.py 18002e280 --module rail_api64.dll --show
python -X utf8 tools/dsp_native_query.py 18002e28d --module rail_api64.dll
```

上述三种查询均已验证命中一项；此前 SHA-512、ChaCha20 和 PadLock 名称查询仍通过。当前共 **4 个 OpenSSL 过程**具有源码参考：3 项规范化匹配、1 项完整字节匹配。未增加 C 输出统计，也未宣称其他 XOP/FMA4 缺口或整个库均已恢复。

## 32 位 ChaCha20：标量、SSSE3 与 XOP 完整字节匹配

`rail_api.dll` 虽位于安装目录的 `Plugins/x86_64/` 下，其 PE machine 为 `0x14c`，实际为 32 位模块。原 Ghidra `1001c780` 函数在 `1001d168` 和 `1001d6c8` 的 XOP 旋转指令处截断；现已将整段代码与固定提交中的 [OpenSSL chacha-x86.pl](https://github.com/openssl/openssl/blob/b2758a2292aceda93e9f44c219b94fe21bb9a650/crypto/chacha/asm/chacha-x86.pl) 重建结果对应。

`dsp_native_chacha32_match.py` 使用 `win32n -DOPENSSL_IA32_SSE2` 生成 NASM 汇编，再以 `-f win32` 汇编为 i386 COFF。输入 SHA-256 固定为：

| 文件 | SHA-256 |
|---|---|
| `chacha-x86.pl` | `4d09c59ef1bf92dec9b15a58070172308633381caf3c0e48d67a1926c89c2cef` |
| `x86asm.pl` | `0b0e8c1aaac5cab5126dce2798a964ab86a366ce8efd9a98fea8ded296216c80` |
| `x86nasm.pl` | `2d64ebec48cb21bab16cecc4d8852ad73c290809100466a7f2bb41cae5f5dc8d` |

对象 `.text` 共 **4,209 字节**，覆盖游戏 `1001c780..1001d7f0`，包括三个上游入口：

| 上游名称 | 游戏入口 |
|---|---|
| `ChaCha20_ctr32` | `1001c780` |
| `ChaCha20_ssse3` | `1001cc80` |
| `ChaCha20_xop` | `1001cf80` |

其中 `1001cec0..1001cf7f` 为 **192 字节常量及静态数据**，单独按 DB 数据展示，不误当作指令。其余 4,017 字节代码完整解码为 **1,045 条指令**，含 **40 条 `vprotd`**，包括原来两处截断边界。

### 重定位证据

COFF 仅有一处 `IMAGE_REL_I386_DIR32`：`.text+0x18` 的 `_OPENSSL_ia32cap_P` 引用，原始加数为零。工具核验该符号为 16 字节 common 存储，将其绑定到游戏指令中的 `10ea4990` 地址；该地址的 16 字节范围落在可写映像节内。游戏自身 PE 重定位目录在对应的 `1001c798` 位置存在 `IMAGE_REL_BASED_HIGHLOW`，且整个比较范围没有其他非空重定位项。

处理这一个外部符号地址后，**全部 4,209 字节逐字节相等**，没有删除 NOP、跳过旋转指令、忽略分支位移或遮盖常量差异。外部能力变量的运行时值不属于比较范围；地址绑定提供源码引用对应关系，不据此推断整个 DLL 的原始符号表或其他库代码身份。

分别修改内存副本的重定位地址、常量字节和 XOP 旋转立即数，三个比较负例均失败。游戏文件未被修改或执行。游戏来源 SHA-256 为 `c0094678b3cf3a026413f3fcddab3982e3381f1f34ece3473bdb865f8184cad1`。

### 查询与复现

```powershell
python -X utf8 tools/dsp_native_chacha32_match.py --reference <上游目录> --perl <perl.exe> --nasm <nasm.exe>
python -X utf8 tools/dsp_native_query.py ChaCha20_ctr32 --module rail_api.dll
python -X utf8 tools/dsp_native_query.py ChaCha20_ssse3 --module rail_api.dll
python -X utf8 tools/dsp_native_query.py ChaCha20_xop --module rail_api.dll --show
```

也支持入口 `1001c780`、`1001cc80`、`1001cf80` 及两个内部快捷入口 `1001cc84`、`1001cf84`。原 Ghidra 索引只有统一的 `1001c780` 记录，因此这些名称和地址都关联到同一份完整范围，不人为增加函数完成数。三个名称、三个外部入口及完整正文查询已通过；Unity VP8 和 64 位 PadLock 旧查询也通过回归检查。

证据保存在 `generated/native/DSPGAME_Data__Plugins__x86_64__rail_api.dll/upstream-openssl/chacha32/`，包含三个固定源文件、LICENSE、生成汇编、COFF 对象、重定位后的字节、代码/数据清单及报告和 manifest。共享 PE/COFF 读取器新增明确的 i386 支持，AMD64 默认行为保留。

当前共有 **5 组 OpenSSL 源码参考**：此前 4 个 64 位过程，加上本次覆盖 3 个 32 位上游入口的完整范围。参考组与 Ghidra 函数数不是同一统计单位，C 成功数保持不变。原始截断记录保留，其他原生控制流、类型及完整功能恢复仍需继续。

## 64 位 ChaCha20 五过程联合匹配与调度边

此前单独核验的 `ChaCha20_4xop` 现扩展为同一固定生成器的全部五个过程。`dsp_native_chacha64_match.py` 重新生成 Win64 COFF，以对象中的过程入口及 `L$SEH_end_*` 定位上游范围，对游戏中对应的五段代码完整解码后联合比较：

| 上游过程 | 游戏入口 | 结束地址（不含） | 游戏字节 | 非 NOP 指令 |
|---|---|---|---:|---:|
| `ChaCha20_ctr32` | `18003d2c0` | `18003d671` | 945 | 242 |
| `ChaCha20_ssse3` | `18003d680` | `18003d8e8` | 616 | 132 |
| `ChaCha20_4x` | `18003d900` | `18003e39b` | 2,715 | 520 |
| `ChaCha20_4xop` | `18003e3a0` | `18003eb76` | 2,006 | 388 |
| `ChaCha20_8x` | `18003eb80` | `18003f775` | 3,061 | 558 |
| 合计 | | | **9,343** | **1,840** |

旧章节的 XOP 游戏字节数曾误写为 1,974，本次根据实际地址范围和原始 `report.json` 更正为 **2,006**；原始核验报告本身已记录正确数值。

规范化只移除已解码的 NOP，并将分支目的地址映射到联合序列的有效指令序号。短/长跳转编码和对齐差异可以不同，但条件、寄存器、操作数宽度、内存寻址与实际目标必须一致。192 字节静态数据完整相等。过程之间的填充不纳入函数体比较，PE 异常展开元数据不属于此次证明。

本次还明确处理了源码对象的唯一外部 `IMAGE_REL_AMD64_REL32` 引用：`.text+0x12b` 指向 `OPENSSL_ia32cap_P`，加数为 4；相应的 `18003d2e8` 指令从游戏地址 `181273994` 读取 8 字节。比较保留“符号＋4＋8 字节宽度”，不把这个外部地址当成普通常量区，也不任意清除 RIP 相对位移。运行时 CPU 能力位内容未被视为固定常量。

### 已匹配的跨过程边

| 原始分支 | 目标 | 过程关系 |
|---|---|---|
| `18003d2f6` | `18003d69e` | ctr32 → ssse3 内部入口 |
| `18003d6a5` | `18003e3be` | ssse3 → 4xop 内部入口 |
| `18003d6b2` | `18003d91e` | ssse3 → 4x 内部入口 |
| `18003d92c` | `18003eb9e` | 4x → 8x 内部入口 |
| `18003d949` | `18003d6b8` | 4x → ssse3 内部入口 |

这五条边都参加联合指令比较，目标必须落在已核验过程内。将第一条跳转改向同一目标过程的另一个合法指令入口后，规范化比较拒绝该变异，表明验证没有仅按过程名粗略匹配目的地。

原 `18003d2c0` 和 `18003d680` 的 Ghidra bad-instruction 警告通过跨体跳转传播到 XOP 路径；现在包含调度代码的联合源码参考覆盖了这些边及目标过程。原 C 和警告仍保留，这不是声称 Ghidra 已正确生成全路径 C。

```powershell
python -X utf8 tools/dsp_native_chacha64_match.py --reference <上游目录> --perl <perl.exe> --nasm <nasm.exe>
python -X utf8 tools/dsp_native_query.py ChaCha20_ctr32 --module rail_api64.dll
python -X utf8 tools/dsp_native_query.py ChaCha20_ssse3 --module rail_api64.dll
python -X utf8 tools/dsp_native_query.py 18003d900 --module rail_api64.dll --show
python -X utf8 tools/dsp_native_query.py ChaCha20_8x --module rail_api64.dll
```

新证据位于 `upstream-openssl/chacha64-family/`。查询将五个入口分别命名，并注明 `--show` 展示五过程联合范围；名字查询仍采用子串匹配，因此 `ChaCha20_4x` 可同时命中 `ChaCha20_4xop`，精确选取可用入口地址。旧 `chacha4-xop/` 证据保留，但查询优先采用联合结果，避免重复展示。四个无歧义名称和五个入口查询均已验证各命中一项。

按去除被替代旧组的口径，目前仍为五组源码参考：三个其他 64 位过程、64 位 ChaCha20 五过程组、32 位 ChaCha20 三入口组。联合匹配没有增加 C 成功数，也未验证异常展开、整个库身份或所有运行时 CPU 特性组合。

## SHA-512 四过程联合核验

此前的 SHA-512 XOP 单过程参考已扩展为同一固定生成器的标量、XOP、AVX、AVX2 四个计算过程。`dsp_native_sha512_match.py` 使用原先固定哈希的 `sha512-x86_64.pl` 和 `x86_64-xlate.pl` 重新生成并汇编，得到以下对应关系：

| 过程 | 游戏入口 | 结束地址（不含） | 游戏字节 | 非 NOP 指令 |
|---|---|---|---:|---:|
| `sha512_block_data_order` | `18001b740` | `18001c9a2` | 4,706 | 1,345 |
| `sha512_block_data_order_xop` | `18001cf40` | `18001df3d` | 4,093 | 1,086 |
| `sha512_block_data_order_avx` | `18001df40` | `18001f14d` | 4,621 | 1,150 |
| `sha512_block_data_order_avx2` | `18001f180` | `1800208f0` | 6,000 | 1,329 |
| 合计 | | | **19,420** | **4,910** |

四个过程均完整解码。删除已解码 NOP 并规范化地址后，全部 4,910 条指令的助记符、寄存器、寻址形式、操作数宽度及分支目标一致。`18001c9c0` 起 1,312 字节静态数据与源码对象的 K512 区逐字节相等。

入口 `18001b756` 通过 LEA 取得能力变量地址 `181273990`，对应源码 `.text+0x19` 的 `OPENSSL_ia32cap_P` REL32 引用，原始加数为零。该引用单独映射为符号引用，不混入常量区；后续从该地址读取的能力位由完整指令序列保留，未假设实际运行时的位值。

三条跨过程调度边也参加联合比较：

| 分支 | 目标 | 分支路径 |
|---|---|---|
| `18001b76f` | `18001cf56` | XOP 内部入口 |
| `18001b783` | `18001f196` | AVX2 内部入口 |
| `18001b7a1` | `18001df56` | AVX 内部入口 |

将 XOP 调度边改向该过程的包装入口，虽然目标仍是合法指令，比较也会拒绝，避免把不同调用约定位置误当成相同目的地。这覆盖了原审计中 `18001b740 → 18001b74d` 和主体向 XOP 路径传播的截断来源；原 Ghidra 结果与警告保持可追溯，不增加 C 成功数。

### 明确排除的范围

生成对象的 `.text` 还含异常处理器，其中 `.text+0x533f` 引用 `__imp_RtlVirtualUnwind`。工具核验此项存在于四个计算过程以外，但**没有将处理器、`.pdata`、`.xdata` 或过程间填充纳入匹配结论**。原目标是恢复全部功能，因此这些排除项仍需后续处理，不能因为计算过程通过就宣称整个对象已完成。

```powershell
python -X utf8 tools/dsp_native_sha512_match.py --reference <上游目录> --perl <perl.exe> --nasm <nasm.exe>
python -X utf8 tools/dsp_native_query.py 18001b740 --module rail_api64.dll
python -X utf8 tools/dsp_native_query.py 18001b74d --module rail_api64.dll --show
python -X utf8 tools/dsp_native_query.py sha512_block_data_order_avx2 --module rail_api64.dll
```

文件保存在 `upstream-openssl/sha512-family/`，包括固定源文件及 LICENSE、重建汇编和对象、两份完整指令清单、各过程代码哈希、调度边和范围限制。查询展开为四个入口，优先采用联合结果；旧 `sha512-xop/` 保留，不重复展示。六个入口/主体地址和两个无歧义名称查询均已验证各命中一项。名称查询仍为子串匹配，查询 `sha512_block_data_order` 会同时匹配其变体。

## AES/SHA-256 调度及四种实现联合核验

`dsp_native_aes_sha256_match.py` 使用此前固定哈希的 `aesni-sha256-x86_64.pl` 和共享翻译器重新生成 Win64 COFF，将调度入口与四种实现联合比较。结果如下：

| 过程 | 游戏入口 | 结束地址（不含） | 游戏字节 | 非 NOP 指令 |
|---|---|---|---:|---:|
| `aesni_cbc_sha256_enc` | `18002df40` | `18002dfa7` | 103 | 23 |
| `aesni_cbc_sha256_enc_xop` | `18002e280` | `18002f35f` | 4,319 | 1,152 |
| `aesni_cbc_sha256_enc_avx` | `18002f380` | `18003059f` | 4,639 | 1,184 |
| `aesni_cbc_sha256_enc_avx2` | `1800305c0` | `1800320f0` | 6,960 | 1,598 |
| `aesni_cbc_sha256_enc_shaext` | `180032100` | `1800327bc` | 1,724 | 357 |
| 合计 | | | **17,745** | **4,314** |

全部代码范围完整解码；4,314 条非 NOP 指令联合匹配，704 字节常量及邻接静态数据逐字节一致。调度入口的 `.text+3` REL32 引用对应 `OPENSSL_ia32cap_P`，加数为零，游戏地址为 `181273990`。此符号引用、后续读取能力位的指令以及分支条件均保留在比较中，没有假设运行时能力位值。

四条实现选择边均匹配：`18002df5e → 180032100`（SHA 扩展）、`18002df72 → 18002e280`（XOP）、`18002df86 → 1800305c0`（AVX2）、`18002df93 → 18002f380`（AVX）。将第一条分支改向另一个合法实现入口，规范化比较拒绝该变异。

### 两条 UD2 必须保留

`18002df99` 与 `18002dfa3` 的 `UD2` 在固定上游生成器中也明确存在（生成器的 AVX 检查后，以及探测返回路径前）。它们是原实现的陷阱指令，**不能因反编译器报告坏指令就当作填充删除**。联合核验将这两条指令按原样保留，并检查地址集合；这里只确认源码与机器指令对应，不证明触发后的操作系统异常处理行为。

对象 `.text+0x489c` 之后的异常处理器、`0x49fb` 的 `__imp_RtlVirtualUnwind` 引用、`.pdata` / `.xdata` 及过程间填充未纳入本次匹配。工具核验两处对象重定位的位置和符号，明确区分计算入口与未处理的异常范围。

```powershell
python -X utf8 tools/dsp_native_aes_sha256_match.py --reference <上游目录> --perl <perl.exe> --nasm <nasm.exe>
python -X utf8 tools/dsp_native_query.py 18002df40 --module rail_api64.dll --show
python -X utf8 tools/dsp_native_query.py aesni_cbc_sha256_enc_xop --module rail_api64.dll
python -X utf8 tools/dsp_native_query.py aesni_cbc_sha256_enc_shaext --module rail_api64.dll
```

证据位于 `upstream-openssl/aesni-sha256-family/`。查询优先采用联合记录，旧 `aesni-sha256-xop/` 仍保留。调度入口没有 Win64 参数重排包装，报告将其主体入口记为自身；其他实现则保留各自入口和 `+13` 的主体地址。原 Ghidra 缺失的 XOP 包装入口仍通过主体记录可查询。六个地址及两个无歧义名称均已验证各命中一项。

本次为既有源码参考组的扩展，未增加 C 完成数。原 bad-instruction 审计中的最后一个 OpenSSL 跨体入口 `18002df40` 现有完整计算路径的源码参考，但整个原生代码库及异常行为仍未全部恢复。

## SHA-512 异常处理器与展开记录补充

`1800208f0..180020a43`（末端不含）现已对应上游 `se_handler`，查询名为 `sha512_se_handler`。游戏代码 339 字节、上游生成代码 347 字节，共 86 条指令；分支编码长度存在差异，比较保留目标指令身份、寄存器、操作数宽度和寻址方式。处理器对 AVX2 入口及标量尾声的 RIP 引用，按此前四过程联合匹配的指令位置核对。间接调用从 PE 导入表识别为 `KERNEL32.dll!RtlVirtualUnwind`，没有仅凭反编译函数名推定身份。

此前 SHA-512 章节排除的 `.pdata` / `.xdata`，现在由单独证据补齐：四条运行时函数记录共 48 字节，四份自定义展开记录共 64 字节。COFF 中 24 处 `ADDR32NB` 重定位按已核验的代码及展开记录地址解析后，与游戏数据逐字节相同；各实现的开始位置是包装入口 `+13`，不是包装入口本身。四份记录共同引用该处理器，另各自保存序言/尾声地址。错误分支目标及 16 个展开字段变异均被拒绝。

处理器读取保存的上下文，按指令位置判断栈恢复路径；AVX2 路径额外调整对齐后的栈位置。随后恢复保存寄存器、复制上下文并调用 `RtlVirtualUnwind`。这些是源码及指令层面的对应关系，尚未通过真实异常注入验证 Windows 展开行为，不增加 C 完成数量。

复现前先运行已有 `dsp_native_sha512_match.py` 生成并核验上游对象，然后执行：

```powershell
python -X utf8 tools/dsp_native_sha512_unwind.py
python -X utf8 tools/dsp_native_query.py sha512_se_handler --module rail_api64.dll --show
```

证据保存至 `upstream-openssl/sha512-seh/`，包含汇编、固定上游源码及许可证、COFF 对象、已重定位展开数据、报告和哈希清单。该补充增加一组查询记录，当前共六组有效 OpenSSL 源码参考；其余算法的异常处理器仍需分别核验。

## AES/SHA-256 异常处理器与展开记录补充

另一个 `se_handler` 位于 `1800327c0..18003292c`（末端不含），查询名 `aesni_sha256_se_handler`。游戏 364 字节、上游重建 375 字节中的 **93 条指令**全部对应，核验包含内部条件跳转、SHA 扩展入口、AVX2 快捷入口及 PE 导入表中的 `KERNEL32.dll!RtlVirtualUnwind` 引用。由此补齐前文 AES/SHA-256 联合计算核验明确排除的处理器代码范围。

XOP、AVX、AVX2、SHA 扩展四种实现的 48 字节 `.pdata` 和 64 字节 `.xdata`，经 24 处 `ADDR32NB` 重定位后与游戏记录逐字节一致。展开记录保存各实现的主体范围、处理器及序言/尾声地址；没有给不在这四条上游展开记录中的调度入口补造元数据。

这里有一处边界区别：上游对象 `.text+0x41c0` 同时表示 AVX2 末端和 SHA 扩展入口；游戏中的两者分别是 `1800320f0` 和 `180032100`，中间相隔 16 字节。因此查询报告单独记录末端映射，核验器按 `EndAddress` 字段的语义解析，避免把填充算进 AVX2 函数体。SHA-512 原有核验在此修正后重新通过。

处理器为 SHA 扩展单独恢复保存的向量寄存器区域及栈位置；其他路径根据 AVX2 范围进行栈对齐调整，恢复通用和向量寄存器，再复制上下文并调用展开函数。这里只恢复了源码与指令对应关系，以及静态展开元数据；实际异常注入和系统展开行为仍未验证。

在运行 `dsp_native_aes_sha256_match.py` 生成固定上游对象后执行：

```powershell
python -X utf8 tools/dsp_native_aes_sha256_unwind.py
python -X utf8 tools/dsp_native_query.py aesni_sha256_se_handler --module rail_api64.dll --show
```

新增证据位于 `upstream-openssl/aesni-sha256-seh/`。当前共七组有效 OpenSSL 源码参考，C 完成数量不变。两种异常处理器分别保留来源及报告，不将静态匹配当作完整异常语义证明。

## 静态展开目录覆盖审计

`dsp_native_unwind_coverage.py` 完整读取当前 `rail_api64.dll` 异常目录的 **66,023 条记录**，与三个联合源码组的 **14 个计算过程**逐一进行区间相交检查。报告保存目录哈希、源码证据哈希、所有相交记录，以及各过程没有被静态展开记录覆盖的地址范围。

结果是 SHA-512 四个实现、AES/SHA-256 四个实现各有一条主体展开记录；包装入口的前 13 字节不在这些记录内。AES/SHA-256 调度过程没有相交记录。**ChaCha20 的五个过程均没有相交记录**，对应的固定上游 `chacha.obj` 也只有 `.text`，没有 `.pdata` / `.xdata`。因此不能给它们套用另两个算法的处理器，也不能把源码匹配统计视作异常恢复证明。

这项审计只判定静态 PE 元数据的存在和覆盖，未检查运行时动态函数表注册或实际故障行为，不据此断言缺失记录必然导致异常。查询这些入口时会提示该限制；证据位于模块目录的 `unwind-coverage/report.json`。复现使用已安装 Capstone 的 Python 环境执行 `tools/dsp_native_unwind_coverage.py`。

## 两个处理器的真实指令离线模拟

`dsp_native_seh_probe.py` 使用 Unicorn 执行上述两个处理器的原始机器指令，合计 **224 个用例**通过。每个处理器对应四个计算实现，分别选取主体入口、序言界限前后、主体中间、尾声界限前后及末端位置，并使用四组内存/保存寄存器值。测试所有 86 / 93 条处理器指令均被覆盖，但这不代表全部路径组合或全部输入已证明。

独立的上下文模型与执行后的整块数据区逐字节比较，核对 1,232 字节上下文复制、保存通用寄存器的恢复、向量保存区域复制、栈、标志、返回值和八个 `RtlVirtualUnwind` 参数。两个把上下文复制长度从 154 个 QWORD 缩短为 153 个的机器码变异均被拒绝；初始内存图案包含页号差异，避免源和目标的初始尾部相同掩盖短复制错误。

SHA-512 有一处必须按原指令保留的行为：主体恢复保存的 RBX 后，后续向量区域复制条件比较的是这个已恢复值与标量尾声地址，不是先前加载的 RIP。测试分别提供低/高 RBX 保存值覆盖两边；目前只记录这一事实，不推断为上游缺陷或尝试修改游戏。

**`RtlVirtualUnwind` 使用 RET 返回桩。** 本测试验证处理器到该调用边界的上下文准备、参数及返回路径，没有执行 Windows 展开算法，没有触发真实进程异常，也没有证明正常游戏执行必然产生这些合成上下文。证据位于 `seh-behavior/report.json`，查询处理器时显示此补充。复现需在安装 Unicorn 的 Python 环境中执行 `tools/dsp_native_seh_probe.py`。
