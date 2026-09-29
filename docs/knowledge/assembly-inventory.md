# 完整程序集导出核验清单

由 `python -X utf8 tools/dsp_verify_full.py` 在全部检查通过后生成。当前安装目录 Managed 下 **95 / 95 个 DLL** 完成导出；没有按类型抽样。

合计 19,832 个类型、188,638 个方法（162,414 个有托管方法体）、114,510 个字段、14,672 份 C# 文件。包括游戏代码、Unity、平台 SDK 和 .NET 运行库，不能把总数称为全部都是游戏业务代码。

检查项目：游戏与依赖 SHA-256、所有已记录输出 SHA-256、元数据统计、ILSpy `.class`/`.method` 数量与独立 Cecil 元数据数量逐程序集一致。该覆盖检查不保证 C# 重建语义完全等价，也不意味着原生方法体或 shader 已恢复。

| 程序集 | 类型 | 方法 | 有方法体 | 字段 | C# 文件 | SHA-256 |
|---|---:|---:|---:|---:|---:|---|
| Assembly-CSharp.dll | 3,496 | 33,376 | 26,776 | 34,010 | 3,114 | `c43a484f6adf8a9e4b956156047070891b46860d5b5c707ba1377b6a2af25732` |
| Mono.Security.dll | 180 | 1,432 | 1,314 | 1,028 | 112 | `576f911be2e0db795ea78f78828f3faf4b20900baf1ce8e33ca8fe599370fc1d` |
| mscorlib.dll | 3,023 | 27,492 | 24,614 | 15,986 | 2,312 | `98c9b62133db5dd33b22bc6aab6f28e4f04f5b5be6559b28ba5c2738241434d0` |
| netstandard.dll | 1 | 0 | 0 | 0 | 1 | `6ae62e082dc494a2433984177f60ca4db5fae69b1f360a8b33754172b310b8c5` |
| System.ComponentModel.Composition.dll | 265 | 1,805 | 1,754 | 789 | 151 | `2535fd6fac676852936cb435d917b2d69cc6fcdd391f7b3009898153ba04919f` |
| System.Configuration.dll | 144 | 1,176 | 1,047 | 363 | 139 | `ec883b49c12ab0e5110337e817a72cf781878d7faf904aa8abb1ab1c4b99bd78` |
| System.Core.dll | 953 | 7,256 | 7,000 | 3,446 | 579 | `f1b33eb1ae8522edcf9ba4c2b85f532d4bab68bfa2a51527264bb61619bed4ea` |
| System.Data.DataSetExtensions.dll | 19 | 101 | 99 | 69 | 14 | `1c88d17e686caf77551a18d3672a1589143e6b8ae83bfcf7c306478cdb64ab90` |
| System.Data.dll | 1,015 | 12,212 | 11,501 | 7,357 | 704 | `534ac87e6a7c1e3ce02ff2d88f4ffa45e7b5fc44e6236922c2f46f8174a29e56` |
| System.dll | 2,173 | 17,609 | 15,701 | 10,581 | 1,626 | `ace2dd6a7d534b581bf4a9d316e743d64508ca2135db73dbff0ddb9365fc454e` |
| System.Drawing.dll | 347 | 3,882 | 2,973 | 2,947 | 258 | `435cc673a1aba5af488d8af379d96f7e351a4b5ed2a1772892287a8268567deb` |
| System.EnterpriseServices.dll | 121 | 471 | 398 | 208 | 120 | `03f240d259b9cf27dbc5c9e8f42b8746a50cc5c52395d8e2de5730321f9de3b8` |
| System.IO.Compression.dll | 77 | 486 | 468 | 565 | 51 | `15e84f38fb5e6bb926fe92a79374a083b8dfbabfa11f8cd1d409f21b98447cfd` |
| System.IO.Compression.FileSystem.dll | 6 | 26 | 26 | 45 | 6 | `02f749fbe31e54cd8b86a35738e1dddae00ccc2e9fdb8990b93f1a494e6e753a` |
| System.Net.Http.dll | 111 | 1,016 | 963 | 344 | 65 | `85f6d618b5df944d2271e2d028b94a93cd371c6134507997441475b1700623cf` |
| System.Numerics.dll | 29 | 665 | 665 | 168 | 21 | `55eda0fb467f9356d8f63933fb4b237732f1b215cb8eb0380398f05d3e8a3461` |
| System.Runtime.Serialization.dll | 440 | 5,490 | 5,332 | 2,722 | 300 | `c1058a78e3828052b92d535d8fb27f9f3c9eb9cbfcdee1904898611b44d7cdb7` |
| System.Security.dll | 285 | 1,846 | 1,766 | 1,139 | 236 | `f7f53eb1401f2bd70ea1dcab1c6c30ae7a4a142e26781c4a78bb885d2f6816a3` |
| System.ServiceModel.Internals.dll | 185 | 1,208 | 1,151 | 877 | 82 | `5f926225818163439d823f9ef0f6e798e16604d8b75b66f22a133b40615f1b05` |
| System.Transactions.dll | 50 | 242 | 213 | 126 | 49 | `8983073e9627b1086102a9871a23e2ba53a0bb9fcc76826cab9f5dec9107e21a` |
| System.Xml.dll | 1,682 | 17,197 | 16,625 | 12,185 | 1,080 | `7a3587565cabf8294cfc372991545f3ae5790d54264e40069f5e0e460b66bc55` |
| System.Xml.Linq.dll | 106 | 991 | 981 | 504 | 54 | `48b11f5d3a51034f9b9d330082f9099a0e68956ee2153d151cdf146bf9e76c78` |
| Unity.InternalAPIEngineBridge.004.dll | 7 | 3 | 3 | 7 | 3 | `eac115924a33e07ce9c1091a16b8c4d8f216bca4132f7470b245ab9f8c57fc7a` |
| Unity.Mathematics.dll | 103 | 9,338 | 9,338 | 367 | 77 | `6f3f4319c2e2ceab416bd847b0634e066e835133c8bbe11e4888c9db5406caa2` |
| Unity.Timeline.dll | 126 | 885 | 843 | 391 | 85 | `a4993f0d8aa9afcd64c832b2f60798e6f3fbe17a58831577d0e876698cd73d27` |
| UnityEngine.AccessibilityModule.dll | 4 | 12 | 12 | 6 | 2 | `d0304db18312a61448c2641dd68c5d155d2440f958cd704faabd874ada413706` |
| UnityEngine.AIModule.dll | 33 | 449 | 253 | 120 | 32 | `516951797ee8652e9f41238e0ac8736d272ea6b147eebd3935f5306bfee0bf0f` |
| UnityEngine.AndroidJNIModule.dll | 31 | 608 | 440 | 91 | 30 | `10d7d43426a674a4237732c5fb11933c065a44551e794170828aef03be9ad0b1` |
| UnityEngine.AnimationModule.dll | 108 | 1,634 | 944 | 388 | 104 | `87159a8541cdb95bbc7c09ba301293951d3c8fc8aa7f7a03b03211957bad7932` |
| UnityEngine.ARModule.dll | 4 | 4 | 3 | 13 | 4 | `c6b4cd9fa33057a953f4f3998c18319d0c7e7ad9eceb90ef0d4e87816cc40304` |
| UnityEngine.AssetBundleModule.dll | 10 | 103 | 61 | 17 | 10 | `64a7b1c4299e40d0e81fe347d43c20cde1ec164da0edc988df998ae86e9a8796` |
| UnityEngine.AudioModule.dll | 54 | 548 | 221 | 120 | 47 | `6d5dbf8432da538f0d4641736a54d60c0d8939812dac22a909a95aaee4885794` |
| UnityEngine.ClothModule.dll | 4 | 73 | 18 | 6 | 4 | `b565d44d79f72e63a98119b1dda76d963fea449e07a9bd9c9fd80b8820e8e51f` |
| UnityEngine.ClusterInputModule.dll | 3 | 16 | 5 | 5 | 3 | `05f72820806ab665b84fd21b9e773601244e53e41eb09ce42bda8b882d1d4a9b` |
| UnityEngine.ClusterRendererModule.dll | 3 | 33 | 17 | 0 | 3 | `b030ad3e08b0eecc31de09d2683abaa3fd1eadd436d4f080dd81c3f402b73c1c` |
| UnityEngine.ContentLoadModule.dll | 9 | 66 | 46 | 19 | 9 | `23b92407bdc5082567ec3de82cb9ce6c8f9eb4619f14bacf3fddc0a8ab126883` |
| UnityEngine.CoreModule.dll | 1,281 | 11,408 | 7,657 | 4,415 | 921 | `e2b5ae2fd12646d03fc3d04d1a37d522572a3b97022fe1b95bbf2a2f2b04853a` |
| UnityEngine.CrashReportingModule.dll | 2 | 9 | 1 | 0 | 2 | `0fad3efb2b59cb162712f89ff06059f49b9aa5ff79517fc127e7887192e63508` |
| UnityEngine.DirectorModule.dll | 2 | 61 | 28 | 3 | 2 | `731e6c7d428967573136e84a37b2eb1065ebca324229874ed14c48a1c3e20db2` |
| UnityEngine.dll | 21 | 11 | 11 | 7 | 21 | `72cc73eef0036530abe21f82971ff06002cee37effdd4dd7d5d4ec8df3911f8d` |
| UnityEngine.DSPGraphModule.dll | 11 | 73 | 14 | 14 | 10 | `43c42200a147553dd73d47c927b3962a49f3e2cc0edc10b2f0224c90a577a035` |
| UnityEngine.GameCenterModule.dll | 23 | 205 | 138 | 65 | 21 | `71ac1ef13a4a09ab0c38f869085ba60e80dbd725db25774f3a184c7bf49e6e04` |
| UnityEngine.GIModule.dll | 1 | 0 | 0 | 0 | 1 | `8ecd961f53a03465d862f58cf0788a33656da35cafaaefa711b64c4b3ee7f073` |
| UnityEngine.GridModule.dll | 5 | 50 | 25 | 12 | 3 | `f4d49d56116e69dfae6ee6554b4d0da56753a4a185bc8a27d715ae2eb17b2120` |
| UnityEngine.HotReloadModule.dll | 1 | 0 | 0 | 0 | 1 | `d366b43d5917a4f36794aab705b0b23803b8f1277dde60b9e8250874810c1443` |
| UnityEngine.ImageConversionModule.dll | 2 | 24 | 9 | 0 | 2 | `0906cd77324885832823926f81e5349940302f2bc1b6a19eeb5809639184e2d8` |
| UnityEngine.IMGUIModule.dll | 71 | 1,308 | 1,074 | 491 | 45 | `0cc7e3746403b2ab8173027906891940ffb59ff272bc09494543fc8942940aab` |
| UnityEngine.InputLegacyModule.dll | 22 | 194 | 109 | 95 | 19 | `a3894425014dd2fc741e995f033935dcacb7a055b6c54979884a79bcc0fbaddf` |
| UnityEngine.InputModule.dll | 7 | 26 | 11 | 27 | 7 | `584bb6c1210ae69e90138df298ed2a29959a41811e2cdf8ae01f98a65cbe74fd` |
| UnityEngine.JSONSerializeModule.dll | 2 | 7 | 5 | 0 | 2 | `4660d891a495e698cb1832257ba7b62519af5ceeb118a23d8e4d1578cec4f168` |
| UnityEngine.LocalizationModule.dll | 2 | 8 | 1 | 0 | 2 | `45ef46f1805121d8a9653af9da1a14645da073e7cd182bae4351a22d5a8cc2fe` |
| UnityEngine.NVIDIAModule.dll | 20 | 139 | 125 | 91 | 19 | `b45f455699d5538b9763d24edb5a1d5cf9824fa637f82287c5d8ad8026a8cf0f` |
| UnityEngine.ParticleSystemModule.dll | 115 | 1,927 | 1,085 | 451 | 67 | `70a4f6b044410981ab15fccc8661baab30b217232de33d6f8cebde915593f8ad` |
| UnityEngine.PerformanceReportingModule.dll | 2 | 3 | 0 | 0 | 2 | `116b9fdbf5529f6f83cd34900d0ff2a7464cf2f074452180b22acbb611320464` |
| UnityEngine.Physics2DModule.dll | 63 | 1,271 | 754 | 164 | 59 | `8349799fcdb1df28510c11b46d3f41ffde5dd6e203df992d38fe70c6e26b2fcc` |
| UnityEngine.PhysicsModule.dll | 74 | 1,358 | 819 | 306 | 71 | `9b8c76635404945ad690b219415151e244c3d6d0a76851fbd40045272f160d38` |
| UnityEngine.ProfilerModule.dll | 1 | 0 | 0 | 0 | 1 | `84851711f739c98d689f252bf1259a82701304364b18f92961386425efcede0e` |
| UnityEngine.PropertiesModule.dll | 209 | 1,060 | 940 | 421 | 114 | `a5b715b7230ab68ba728a279abbccaecb29e64f2c04a3ac9c58a985a3fb9bb3a` |
| UnityEngine.RuntimeInitializeOnLoadManagerInitializerModule.dll | 1 | 0 | 0 | 0 | 1 | `da1b55d05f646fda7e92615c9935f859adbdb1ed423a719215882d53caf173ec` |
| UnityEngine.ScreenCaptureModule.dll | 3 | 9 | 6 | 4 | 2 | `a51be397b96921b7447bab389891003694f3e6bdc762ce4a1a22fcd42bdb8cac` |
| UnityEngine.SharedInternalsModule.dll | 46 | 154 | 130 | 52 | 46 | `1540198384f49e969b7d83d6a7aff0ea79b77ae0e146464e8e0a0b93af4ccade` |
| UnityEngine.SpriteMaskModule.dll | 3 | 21 | 3 | 0 | 3 | `4e9724f606cbf84a989f6c4fc14910d768760954513129400a714d8ceb9f5050` |
| UnityEngine.SpriteShapeModule.dll | 9 | 41 | 28 | 40 | 9 | `b0216db69be183fde2920784e2a6a59c4ebd00f4ea9c58e2730c7f41904a8571` |
| UnityEngine.StreamingModule.dll | 2 | 6 | 1 | 0 | 2 | `f7d00904687bdb5ceb52a6d0ab1f924f60e569a30c3cf9116bbb760593b1ea47` |
| UnityEngine.SubstanceModule.dll | 9 | 52 | 52 | 52 | 9 | `5d728a5b98e6eaa5603b07c54cb8a330760e40976dfe6e50084b07a3f4e371a9` |
| UnityEngine.SubsystemsModule.dll | 28 | 150 | 110 | 25 | 21 | `1a0e27b3c78cd8ded5d416182474a922c5141d6910a2914cd93fb3578d4f8574` |
| UnityEngine.TerrainModule.dll | 48 | 568 | 327 | 208 | 28 | `3a858ba0173bd4d25c3ccecec1cc178ba099426ed1fa167cdaae3455015f5a54` |
| UnityEngine.TerrainPhysicsModule.dll | 2 | 6 | 3 | 0 | 2 | `5bcae44d1dcfa3c96ca66ab4f75f583ae8640ef683d520f8ff8278f57df8ff1e` |
| UnityEngine.TextCoreFontEngineModule.dll | 44 | 382 | 288 | 224 | 44 | `742892c4ba2bbe5c70ebfec5caaa541dc56bb7cb2bf71e8dd5a39e3672ed86ca` |
| UnityEngine.TextCoreTextEngineModule.dll | 81 | 630 | 626 | 1,046 | 71 | `289688eafb61839ab40ac0dd8dc449b7c27d3ab5780eb3af7ca7d9bdfe079fc9` |
| UnityEngine.TextRenderingModule.dll | 17 | 166 | 105 | 85 | 16 | `d70709fee2d288d5293fff38821c00c4d6a8f16ebf1645d856a7148f8e26b5cf` |
| UnityEngine.TilemapModule.dll | 25 | 294 | 203 | 95 | 15 | `26c68f57d7fc3c6349ce1f05012bff34e29df9dd3bce78ce9139fe02d38573e2` |
| UnityEngine.TLSModule.dll | 13 | 39 | 0 | 103 | 3 | `b2d5c74912177e517bec6bebc90bbb173cc917f6354d3c7b97fb7075498d1ff8` |
| UnityEngine.UI.dll | 223 | 2,140 | 2,045 | 1,190 | 127 | `c5acc25ebd597b586d323af506fb88e760322550940f1a3b0ea4fc8143fcb26c` |
| UnityEngine.UIElementsModule.dll | 1,240 | 9,928 | 8,874 | 4,792 | 768 | `0a1f79ef874addd4602d24baa8d55bdcad06e496fafa47ba48824ceb4cad534e` |
| UnityEngine.UIModule.dll | 12 | 163 | 62 | 24 | 10 | `c37bb3eace97302fb3aa9e17eac4446f8b010307637aa9cc5cb84c59828b0ab2` |
| UnityEngine.UmbraModule.dll | 1 | 0 | 0 | 0 | 1 | `2c056be4c11ff2584d8510d208578ddd8cb33b110db078b3c8c68a9631cd9858` |
| UnityEngine.UnityAnalyticsCommonModule.dll | 3 | 5 | 3 | 0 | 3 | `e641f8bccd94a87db380b65e0e4c8697622bbfbf7fba58bdc2df31f96efeea44` |
| UnityEngine.UnityAnalyticsModule.dll | 16 | 216 | 102 | 52 | 12 | `e87fb283c12f4d0bc2be90f423d440beb1d9c020b966cb6a39dcf26202b0ba5c` |
| UnityEngine.UnityConnectModule.dll | 3 | 23 | 3 | 0 | 3 | `5dfd99fb6be00dcaf1de51516e12f01d21c972b8f6ae810b33e1fa56544daf68` |
| UnityEngine.UnityCurlModule.dll | 4 | 17 | 1 | 9 | 4 | `cfe8f292795cb3143636de3844058df51ea370cdf12e568baa50c0b4da9a8144` |
| UnityEngine.UnityTestProtocolModule.dll | 1 | 0 | 0 | 0 | 1 | `29555a3c2077bda97df39d8433e319da2478b04605e8c1f15297a47e5a8bbd3d` |
| UnityEngine.UnityWebRequestAssetBundleModule.dll | 3 | 27 | 21 | 0 | 3 | `357bd4bd6d1c4e37b22581a5d80b5096caadb7fcd67f1390df0f73741292bbe5` |
| UnityEngine.UnityWebRequestAudioModule.dll | 4 | 23 | 17 | 1 | 4 | `ab0d0369bccd17b0896f0ff6fe0fe9988b7a3e43eb6e39eaaed6fa0decac97ee` |
| UnityEngine.UnityWebRequestModule.dll | 20 | 290 | 228 | 105 | 17 | `4bb6f8ea3131142c3b16161e2a58c2bda2538fac59f4b1d0dba03abf3a4150f8` |
| UnityEngine.UnityWebRequestTextureModule.dll | 3 | 13 | 11 | 2 | 3 | `10329167e9c145a8ca3a989f0bef1feedc1cc2295231949f6926fddfd3cfa05f` |
| UnityEngine.UnityWebRequestWWWModule.dll | 4 | 56 | 55 | 4 | 4 | `1a3c7827b855223418560dca5ed40bbae6c5e2a13ae1fc937aeb3965e1544101` |
| UnityEngine.VehiclesModule.dll | 3 | 61 | 25 | 8 | 3 | `c1a0b202a512bde53741af6fe75eac26f0ff09a410c51bcc23185363b4c9b21a` |
| UnityEngine.VFXModule.dll | 31 | 344 | 178 | 299 | 31 | `3d5c18ede9c8cf9c164edfd3fa3be9b44de16dc1a125ca67e3c63c3583e783e5` |
| UnityEngine.VideoModule.dll | 25 | 232 | 52 | 66 | 18 | `ede5c86b4a0a527cf48ba4bc2465140a0cefa1b8509166547854aca9cc3e725d` |
| UnityEngine.VirtualTexturingModule.dll | 27 | 109 | 60 | 76 | 9 | `356a423e5bd5e1f27f1919bd4467556b5399c89ef2a32ff0a41c96e216023ac8` |
| UnityEngine.VRModule.dll | 11 | 45 | 11 | 22 | 10 | `f01876174aa9af368b0163a17b0e39d13abbe79d2900f821fb8fd6c47c737e63` |
| UnityEngine.WindModule.dll | 3 | 13 | 1 | 3 | 3 | `51ed0dd32f97570f9875b0603e917113d205c1f9b68b9543af740d88dc23edd4` |
| UnityEngine.XRModule.dll | 51 | 384 | 265 | 282 | 40 | `c723201c7321e897f260f4c3d8c5a38824d054a3394c80b3473018ab4be4a9e7` |
| XGamingRuntime.dll | 669 | 3,138 | 2,141 | 2,070 | 447 | `0dcf138603e1fb734cfb73035f4e007dfadbf0ea06b67f0106b201edc4dcd64a` |
