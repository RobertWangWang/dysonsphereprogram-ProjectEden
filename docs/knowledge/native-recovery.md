# 原生失败函数补充恢复

补充输出不覆盖第一次导出，也不修复 OOM 导致的分析缺口。C-only 仅关闭可选 Java 高层语法树转换，仍由 Ghidra 原生后端生成 C；temporary-untyped 临时移除局部类型并将参数/返回类型改为等宽未定义类型，随后回滚。后者保留原签名，但字段类型的解释必须结合汇编。

工具：`DspExportCOnly.java`、`DspRetryUntyped.java`、`python -X utf8 tools/dsp_native_recovery.py`。均在保存的项目上运行，原游戏二进制不修改。补充来源与文件哈希见各模块 recovery-manifest.json。

## MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll

补充恢复 68 个函数的 C，原失败列表中仍有 1 个未恢复 C。

上游汇编源码匹配单独记录于 [覆盖证据](native-edge-cases.md)，不计入本页的 C 恢复数。

| 地址 | 函数 | 方式 | 伪代码 |
|---|---|---|---|
| 18009b770 | mono_get_corlib_version | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/18009b770.c) |
| 1800b7f60 | mono_type_custom_modifier_count | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1800b7f60.c) |
| 1800b9980 | inflate_generic_type | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1800b9980.c) |
| 1800b9e20 | inflate_generic_custom_modifiers | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1800b9e20.c) |
| 1800c48d0 | mono_field_get_flags | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1800c48d0.c) |
| 1800c6e00 | mono_field_resolve_flags | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1800c6e00.c) |
| 1800c7c60 | mono_class_setup_fields | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1800c7c60.c) |
| 1800c8320 | mono_class_create_from_typedef | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1800c8320.c) |
| 1800cc660 | mono_class_layout_fields | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1800cc660.c) |
| 1800d91a0 | cominterop_method_signature | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1800d91a0.c) |
| 1800dd8f0 | mono_cominterop_emit_marshal_com_interface | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1800dd8f0.c) |
| 1800e4ed0 | mono_cominterop_emit_marshal_safearray | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1800e4ed0.c) |
| 1800e87c0 | mono_type_custom_modifier_count | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1800e87c0.c) |
| 1800e8ab0 | find_system_class | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1800e8ab0.c) |
| 1800e8df0 | mono_custom_modifiers_get_desc | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1800e8df0.c) |
| 1800fa7e0 | mono_type_custom_modifier_count | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1800fa7e0.c) |
| 1800ff1e0 | ves_icall_type_GetTypeCodeInternal | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1800ff1e0.c) |
| 180100010 | ves_icall_RuntimeFieldInfo_SetValueInternal | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/180100010.c) |
| 180111790 | type_array_from_modifiers | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/180111790.c) |
| 18015aef0 | mono_type_custom_modifier_count | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/18015aef0.c) |
| 180165e60 | mono_marshal_set_callconv_from_modopt | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/180165e60.c) |
| 18016b9b0 | ves_icall_System_Runtime_InteropServices_Marshal_OffsetOf | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/18016b9b0.c) |
| 18016be10 | mono_struct_delete_old | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/18016be10.c) |
| 18016cc40 | mono_marshal_load_type_info | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/18016cc40.c) |
| 18016e070 | mono_marshal_get_thunk_invoke_wrapper | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/18016e070.c) |
| 1801712b0 | emit_struct_conv_full | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1801712b0.c) |
| 180174ba0 | emit_marshal_array_ilgen | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/180174ba0.c) |
| 18017ce10 | emit_marshal_asany_ilgen | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/18017ce10.c) |
| 18017d010 | emit_marshal_vtype_ilgen | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/18017d010.c) |
| 18017dac0 | emit_marshal_string_ilgen | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/18017dac0.c) |
| 18017ea70 | emit_marshal_object_ilgen | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/18017ea70.c) |
| 18017fea0 | emit_marshal_variant_ilgen | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/18017fea0.c) |
| 1801801f0 | emit_managed_wrapper_ilgen | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1801801f0.c) |
| 180183ee0 | mono_type_with_mods_init | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/180183ee0.c) |
| 180183f20 | mono_type_custom_modifier_count | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/180183f20.c) |
| 180188f80 | metadata_signature_set_modopt_call_conv | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/180188f80.c) |
| 180191910 | mono_metadata_custom_modifiers_equal | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/180191910.c) |
| 1801922f0 | mono_type_get_custom_modifier | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1801922f0.c) |
| 1801926d0 | do_metadata_type_dup_append_cmods | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1801926d0.c) |
| 180192af0 | mono_metadata_type_dup_with_cmods | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/180192af0.c) |
| 180195920 | mono_type_get_attrs | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/180195920.c) |
| 180197e60 | mono_guid_signature_append_method | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/180197e60.c) |
| 180198620 | mono_generate_v3_guid_for_interface | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/180198620.c) |
| 1801c88f0 | compute_class_bitmap | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1801c88f0.c) |
| 1801cabf0 | mono_class_create_runtime_vtable | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1801cabf0.c) |
| 1801cbe20 | mono_class_field_is_special_static | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1801cbe20.c) |
| 1801cbe90 | mono_class_field_get_special_static_type | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1801cbe90.c) |
| 1801cbef0 | mono_class_has_special_static_fields | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1801cbef0.c) |
| 1801ce070 | mono_field_get_addr | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1801ce070.c) |
| 1801f3590 | mono_marshal_get_xappdomain_invoke | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1801f3590.c) |
| 180223220 | do_push_field | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/180223220.c) |
| 180242cb0 | add_custom_modifiers_to_type | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/180242cb0.c) |
| 1802475f0 | reflection_methodbuilder_to_mono_method | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1802475f0.c) |
| 18024a700 | typebuilder_setup_one_field | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/18024a700.c) |
| 18027e3c0 | mono_unity_get_field_address | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/18027e3c0.c) |
| 18033e5a0 | mono_type_custom_modifier_count | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/18033e5a0.c) |
| 1803425a0 | encode_type | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1803425a0.c) |
| 180350580 | emit_klass_info | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/180350580.c) |
| 180380900 | mini_type_is_hfa | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/180380900.c) |
| 1803b4730 | emit_class_dwarf_info | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1803b4730.c) |
| 1803cad40 | set_interp_var | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1803cad40.c) |
| 1803d3270 | type_commands_internal | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1803d3270.c) |
| 1803d9010 | object_commands | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1803d9010.c) |
| 1803f5440 | create_write_barrier_bitmap | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/1803f5440.c) |
| 18041dbf0 | interp_handle_magic_type_intrinsics | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/18041dbf0.c) |
| 18041e8c0 | interp_handle_intrinsics | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/18041e8c0.c) |
| 180424720 | interp_emit_sfld_access | C-only | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/c-only/180424720.c) |
| 180110fe0 | ves_icall_RuntimeMethodInfo_get_name | temporary-untyped | [C](generated/native/MonoBleedingEdge__EmbedRuntime__mono-2.0-bdwgc.dll--pdb-96g-serial/untyped/180110fe0.c) |

剩余：

- `1802b5740` `mono_method_to_ir`：timeout。

## UnityPlayer.dll

补充恢复 0 个函数的 C，原失败列表中仍有 1 个未恢复 C。

上游汇编源码匹配单独记录于 [覆盖证据](native-edge-cases.md)，不计入本页的 C 恢复数。

| 地址 | 函数 | 方式 | 伪代码 |
|---|---|---|---|

剩余：

- `1800145ca` `FUN_1800145ca`：failed。
