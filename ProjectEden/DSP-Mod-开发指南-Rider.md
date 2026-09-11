# 戴森球计划 Mod 开发完整指南

> 工具链：JetBrains Rider + .NET 8 SDK
> 目标：从零写出第一个可加载的 BepInEx 插件，并了解进阶内容开发路径

---

## 0. 先读这一段：关于 .NET 8

**.NET 8 SDK 是你的构建工具，不是目标框架。**

戴森球计划是 Unity + **Mono** 运行时的 x64 游戏。它加载的程序集必须是 Mono 能识别的旧版 CLI 格式，所以：

| 项目 | 取值 |
|---|---|
| 本机安装的 SDK | .NET 8 SDK（`dotnet --version` 应显示 8.x） |
| csproj 的 `TargetFramework` | **`netstandard2.0`**（最保险）或 `net472` |
| 绝对不能写 | `net8.0` / `net6.0` |

如果写成 `net8.0`，BepInEx 在启动时会加载失败，控制台通常报 `Could not load file or assembly` 或类型初始化异常，而且报错信息不会告诉你是框架版本问题，很容易卡半天。

### C# 语言版本的额外说明

用 .NET 8 SDK 编译 `netstandard2.0` 时，你**可以**把 `LangVersion` 设成 `latest`，大部分新语法（模式匹配、`switch` 表达式、目标类型 `new()`、局部函数等）都能用，因为它们是纯编译期特性。

但以下特性依赖运行时/BCL 类型，在 `netstandard2.0` 下需要手动补一个 shim 才能编译：

- `record` 类型、`init` 访问器 → 需要 `IsExternalInit`
- 可空引用类型注解 `[NotNull]` 等 → 需要相关 Attribute 定义
- `required` 成员 → 需要 `RequiredMemberAttribute`

最简单的补法：在项目里加一个文件 `Polyfills.cs`：

```csharp
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct |
                    AttributeTargets.Field | AttributeTargets.Property,
                    AllowMultiple = false, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;
        public string FeatureName { get; }
    }
}
```

**新手建议：一开始别用这些新语法。** 先把 mod 跑通，语法糖以后再说。

---

## 1. 技术底座

DSP 没有官方 Mod API。所有 mod 的本质是：

```
BepInEx (注入器)  →  加载你的 DLL  →  Harmony (运行时打补丁)  →  改写 Assembly-CSharp.dll 里的游戏逻辑
```

游戏的全部 C# 逻辑都编译在：

```
<游戏目录>/DSPGAME_Data/Managed/Assembly-CSharp.dll
```

所以开发流程固定是四步：

1. 反编译 `Assembly-CSharp.dll`，读懂原版实现
2. 写一个 BepInEx 插件工程
3. 用 Harmony 拦截 / 修改 / 替换目标方法
4. 需要加新物品、配方、科技时，接入社区库（LDBTool / CommonAPI / DSPModSave）

**需要的前置技能**：C# 基础、一点 Unity 概念（MonoBehaviour、Prefab、AssetBundle、UGUI），以及读别人代码的耐心。

---

## 2. 环境准备

### 2.1 安装 .NET 8 SDK

```bash
dotnet --version    # 应输出 8.x.x
dotnet --list-sdks  # 确认 SDK 已装
```

Rider 会自动识别系统安装的 SDK。如果没识别到：
`Settings → Build, Execution, Deployment → Toolset and Build → .NET CLI executable path`

### 2.2 安装 BepInEx

装 **BepInEx 5.4.x 的 x64 版本**。

> ⚠️ **不要装 BepInEx 6** —— 那是给 IL2CPP 游戏用的，DSP 是 Mono，装了不工作。

步骤：

1. 从 GitHub Releases 或 Thunderstore 下载 `BepInEx_x64_5.4.x.zip`
2. 解压到游戏根目录（Steam → 右键戴森球计划 → 管理 → 浏览本地文件）
3. **启动一次游戏**，让它生成 `BepInEx/` 目录结构
4. 确认出现了 `BepInEx/plugins/`、`BepInEx/config/`、`BepInEx/core/`

如果没生成，检查三件事：压缩包是否完整解压、游戏路径里有没有中文或特殊字符、`winhttp.dll` 是否和 `DSPGAME.exe` 在同一层。

### 2.3 开启控制台日志（必做）

编辑 `BepInEx/config/BepInEx.cfg`：

```ini
[Logging.Console]
Enabled = true

[Logging.Disk]
WriteUnityLog = true
```

之后启动游戏会多弹一个黑色控制台窗口，实时显示日志和异常堆栈。日志同时写入 `BepInEx/LogOutput.log`。**这是你唯一的调试生命线，务必打开。**

### 2.4 隔离测试环境

强烈建议用 **r2modman**（Thunderstore Mod Manager）建一个独立 profile 做开发测试，别在你正常游玩的存档目录上折腾。r2modman 会创建独立的 mod 目录和存档目录，搞坏了一键重置。

如果坚持用游戏本体目录，至少先备份 `%USERPROFILE%/Documents/Dyson Sphere Program/Save/`。

---

## 3. 反编译游戏代码（最关键的一步）

### 3.1 Rider 内置反编译器（推荐路径）

Rider 内置了 dotPeek 的反编译引擎，不用装额外工具：

1. 在项目里引用 `Assembly-CSharp.dll`（见第 4 节的 csproj）
2. 在代码里写 `GameMain.`，然后 `Ctrl + 左键` 点击类名
3. Rider 会自动反编译并打开源码，可以继续在反编译结果里跳转、查找用法（`Alt + F7`）

启用更好的反编译体验：
```
Settings → Build, Execution, Deployment → Debugger
  ☑ Enable external source debug
Settings → Editor → General → Navigation
  ☑ Decompile external sources when navigating
```

### 3.2 导出成完整项目（做大 mod 时推荐）

Rider 的跳转适合查单个类，但要「全局搜索某个字符串出现在哪些方法里」，还是导出整个项目更方便：

- 用 **ILSpy**（免费开源）打开 `Assembly-CSharp.dll` → File → Save Code → 导出为 `.csproj`
- 用 Rider 打开导出的项目（**只读，不要编译它**）
- 现在可以用 `Ctrl+Shift+F` 全文搜索、`Ctrl+Alt+F7` 查调用链

macOS / Linux 上可以用 `ilspycmd`：

```bash
dotnet tool install -g ilspycmd
ilspycmd -p -o ./dsp-decompiled "<游戏目录>/DSPGAME_Data/Managed/Assembly-CSharp.dll"
```

### 3.3 核心类速查表

| 类 | 作用 |
|---|---|
| `GameMain` | 游戏主循环入口：`GameTick`、`FixedUpdate`、`Begin`、`End` |
| `GameData` | 当前存档的全部数据：星球、玩家、历史记录 |
| `PlanetFactory` | 单个星球的工厂容器 |
| `FactorySystem` | 星球上的生产逻辑（组装机、熔炉、采矿机…） |
| `LDB` | 静态数据库：`LDB.items`、`LDB.recipes`、`LDB.techs`、`LDB.veins` |
| `ItemProto` / `RecipeProto` / `TechProto` | 物品、配方、科技的数据定义 |
| `CargoTraffic` | 传送带系统 |
| `PowerSystem` | 电网 |
| `PlanetTransport` | 物流塔 |
| `Player` / `Mecha` | 玩家和机甲 |
| `UIRoot` + `UI*` 系列 | 所有界面 |
| `VFInput` | 输入处理 |
| `DSPGame` | 游戏级别的静态入口 |

**工作方法**：挑一个你想改的功能 → 在反编译代码里搜关键词 → 摸清调用链 → 再动手写补丁。这一步通常占整个 mod 开发时间的一半以上，别急着写代码。

---

## 4. 用 Rider 建工程

### 4.1 创建项目

Rider → New Solution → **Class Library**

- Language: C#
- Framework: 先随便选，稍后手动改 csproj
- Solution name: `MyDspMod`

或者用 CLI：

```bash
dotnet new classlib -n MyDspMod
cd MyDspMod
```

### 4.2 csproj 完整配置

把生成的 `MyDspMod.csproj` 替换为：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!-- 关键：目标框架必须是 netstandard2.0，不是 net8.0 -->
    <TargetFramework>netstandard2.0</TargetFramework>
    <AssemblyName>MyDspMod</AssemblyName>
    <Product>My DSP Mod</Product>
    <Version>1.0.0</Version>

    <LangVersion>latest</LangVersion>
    <Nullable>disable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>

    <!-- 不生成多余文件 -->
    <GenerateAssemblyInfo>true</GenerateAssemblyInfo>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <DebugType>portable</DebugType>

    <!-- 游戏路径：按你自己的机器改 -->
    <GameDir Condition="'$(GameDir)' == ''">C:\Program Files (x86)\Steam\steamapps\common\Dyson Sphere Program</GameDir>
    <ManagedDir>$(GameDir)\DSPGAME_Data\Managed</ManagedDir>
  </PropertyGroup>

  <ItemGroup>
    <!-- BepInEx 5 核心，包含 Harmony -->
    <PackageReference Include="BepInEx.Core" Version="5.*" PrivateAssets="all" />
    <!-- Unity 引擎程序集（编译期引用，不打包） -->
    <PackageReference Include="UnityEngine.Modules" Version="2018.4.12" IncludeAssets="compile" PrivateAssets="all" />
    <!-- 让你能直接访问游戏里的 private / internal 成员 -->
    <PackageReference Include="Krafs.Publicizer" Version="2.*" PrivateAssets="all" />
  </ItemGroup>

  <ItemGroup>
    <Reference Include="Assembly-CSharp">
      <HintPath>$(ManagedDir)\Assembly-CSharp.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <!-- 打开 Assembly-CSharp 的所有私有成员 -->
    <Publicize Include="Assembly-CSharp" />
  </ItemGroup>

  <!-- 编译后自动部署到游戏 plugins 目录 -->
  <Target Name="DeployToGame" AfterTargets="Build" Condition="Exists('$(GameDir)')">
    <MakeDir Directories="$(GameDir)\BepInEx\plugins\$(AssemblyName)" />
    <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(GameDir)\BepInEx\plugins\$(AssemblyName)\" />
    <Message Importance="high" Text="已部署到 $(GameDir)\BepInEx\plugins\$(AssemblyName)\" />
  </Target>

</Project>
```

**关于 `UnityEngine.Modules` 的版本**：这个 NuGet 包的版本要尽量贴近游戏实际使用的 Unity 版本。查看方法：

```bash
# Windows PowerShell
(Get-Item "<游戏目录>\DSPGAME.exe").VersionInfo.FileVersion
```

或者直接看 `DSPGAME_Data/globalgamemanagers` 文件开头的字符串。如果版本对不上导致 API 缺失，改用直接引用游戏目录里的 Unity DLL：

```xml
<ItemGroup>
  <Reference Include="UnityEngine">
    <HintPath>$(ManagedDir)\UnityEngine.dll</HintPath>
    <Private>false</Private>
  </Reference>
  <Reference Include="UnityEngine.CoreModule">
    <HintPath>$(ManagedDir)\UnityEngine.CoreModule.dll</HintPath>
    <Private>false</Private>
  </Reference>
  <Reference Include="UnityEngine.UI">
    <HintPath>$(ManagedDir)\UnityEngine.UI.dll</HintPath>
    <Private>false</Private>
  </Reference>
  <Reference Include="UnityEngine.IMGUIModule">
    <HintPath>$(ManagedDir)\UnityEngine.IMGUIModule.dll</HintPath>
    <Private>false</Private>
  </Reference>
</ItemGroup>
```

### 4.3 把游戏路径挪出 csproj（多人协作时推荐）

在解决方案根目录建 `Directory.Build.props`（加进 `.gitignore`）：

```xml
<Project>
  <PropertyGroup>
    <GameDir>D:\Steam\steamapps\common\Dyson Sphere Program</GameDir>
  </PropertyGroup>
</Project>
```

这样每个人可以配自己的路径，csproj 保持干净。

### 4.4 Rider 里配置「一键构建并启动游戏」

`Run → Edit Configurations → + → .NET Executable`

| 字段 | 值 |
|---|---|
| Name | `Build & Launch DSP` |
| Exe path | `<游戏目录>\DSPGAME.exe` |
| Working directory | `<游戏目录>` |
| Before launch | 添加 `Build Project` → 选你的项目 |

之后按 `Shift + F10` 就是「编译 → 复制 DLL → 启动游戏」一条龙。

> 注意：直接启动 `DSPGAME.exe` 会绕过 Steam。DSP 本体一般能正常运行，但如果遇到成就或 Steam 相关功能异常，改用 Steam 启动，或在 Steam 的启动项里配置。

---

## 5. 第一个插件

### 5.1 主类

`Plugin.cs`：

```csharp
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace MyDspMod
{
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInProcess("DSPGAME.exe")]
    public class MyDspMod : BaseUnityPlugin
    {
        public const string GUID    = "com.yourname.mydspmod";
        public const string NAME    = "My DSP Mod";
        public const string VERSION = "1.0.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<float> SpeedMultiplier;
        internal static ConfigEntry<bool>  EnableFeature;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            // 配置项会自动写入 BepInEx/config/com.yourname.mydspmod.cfg
            SpeedMultiplier = Config.Bind(
                "General", "SpeedMultiplier", 2.0f,
                new ConfigDescription("采矿速度倍率",
                    new AcceptableValueRange<float>(0.1f, 100f)));

            EnableFeature = Config.Bind(
                "General", "EnableFeature", true,
                "是否启用主要功能");

            _harmony = new Harmony(GUID);
            _harmony.PatchAll(typeof(MyPatches));

            Logger.LogInfo($"{NAME} v{VERSION} 已加载");
        }

        // 支持 ScriptEngine 热重载，也让卸载更干净
        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            Logger.LogInfo($"{NAME} 已卸载");
        }
    }
}
```

### 5.2 补丁类

`MyPatches.cs`：

> ⚠️ **下面的方法名和签名仅作示例。** 不同游戏版本会变，动手前一定要用 Rider 跳进 `Assembly-CSharp` 确认真实签名。

```csharp
using HarmonyLib;
using UnityEngine;

namespace MyDspMod
{
    public static class MyPatches
    {
        // ── Postfix：原方法执行完后追加逻辑 ─────────────────
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameMain), nameof(GameMain.Begin))]
        private static void OnGameBegin()
        {
            MyDspMod.Log.LogInfo("进入游戏了");
        }

        // ── Prefix：原方法之前执行，可改参数，返回 false 跳过原方法 ──
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SomeComponent), nameof(SomeComponent.InternalUpdate))]
        private static bool BeforeUpdate(ref SomeComponent __instance, ref float power)
        {
            if (!MyDspMod.EnableFeature.Value) return true;

            power *= MyDspMod.SpeedMultiplier.Value;
            return true;   // true = 继续执行原方法；false = 完全跳过
        }

        // ── 用 __result 改返回值 ────────────────────────
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SomeClass), nameof(SomeClass.GetSomething))]
        private static void AfterGetSomething(ref int __result)
        {
            __result *= 2;
        }

        // ── 用三下划线访问私有字段（没用 Publicizer 时的写法）──
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SomeClass), nameof(SomeClass.SomeMethod))]
        private static void AccessPrivate(ref int ___privateCounter)
        {
            ___privateCounter = 0;
        }

        // ── 监听按键：Postfix 到每帧都会跑的方法上 ────────────
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameMain), "FixedUpdate")]
        private static void OnFixedUpdate()
        {
            if (Input.GetKeyDown(KeyCode.F5))
            {
                DumpCurrentPlanetVeins();
            }
        }

        private static void DumpCurrentPlanetVeins()
        {
            var factory = GameMain.mainPlayer?.factory;
            if (factory == null)
            {
                MyDspMod.Log.LogWarning("当前不在星球上");
                return;
            }

            var veinPool = factory.veinPool;
            long total = 0;
            for (int i = 1; i < factory.veinCursor; i++)
            {
                if (veinPool[i].id != i) continue;
                total += veinPool[i].amount;
            }
            MyDspMod.Log.LogInfo($"当前星球矿脉总储量: {total}");
        }
    }
}
```

### 5.3 Harmony 核心用法速查

| 补丁类型 | 时机 | 能力 |
|---|---|---|
| `[HarmonyPrefix]` | 原方法之前 | 改参数（`ref`）；返回 `false` 阻止原方法执行 |
| `[HarmonyPostfix]` | 原方法之后 | 改返回值（`ref __result`）；追加逻辑 |
| `[HarmonyFinalizer]` | 类似 finally | 捕获/吞掉原方法抛出的异常 |
| `[HarmonyTranspiler]` | 编译期改 IL | 精确修改方法内部某几行，最强也最脆弱 |

**特殊参数名**（Harmony 按名字注入）：

| 参数 | 含义 |
|---|---|
| `__instance` | 实例对象（非静态方法） |
| `__result` | 返回值，配 `ref` 可修改 |
| `___fieldName` | **三个**下划线 + 字段名 = 访问私有字段 |
| `__state` | Prefix 存值传给 Postfix |
| `__originalMethod` | 被打补丁的原方法信息 |

**重要原则：优先用 Prefix / Postfix，能不用 Transpiler 就不用。** Transpiler 直接操作 IL 指令，游戏一更新就容易整个崩掉。确实必须用时，用 `CodeMatcher` 按指令**特征**匹配，而不是按**索引**硬编码：

```csharp
[HarmonyTranspiler]
[HarmonyPatch(typeof(SomeClass), nameof(SomeClass.SomeMethod))]
private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
{
    return new CodeMatcher(instructions)
        // 按特征找锚点，不要写 instructions[47]
        .MatchForward(false,
            new CodeMatch(OpCodes.Ldfld,
                AccessTools.Field(typeof(SomeClass), "someField")),
            new CodeMatch(OpCodes.Ldc_I4_1))
        .ThrowIfInvalid("找不到目标指令 —— 游戏版本可能变了")
        .Advance(1)
        .SetOperandAndAdvance(10)
        .InstructionEnumeration();
}
```

### 5.4 运行时动态查找目标（做版本兼容）

硬引用类型在游戏更新后会直接编译失败或运行时崩溃。做兼容性更好的写法：

```csharp
private void Awake()
{
    var target = AccessTools.Method("SomeClass:SomeMethod");
    if (target == null)
    {
        Logger.LogWarning("目标方法不存在，跳过该补丁（游戏版本可能不兼容）");
    }
    else
    {
        _harmony.Patch(target,
            postfix: new HarmonyMethod(typeof(MyPatches), nameof(MyPatches.AfterSomeMethod)));
    }
}
```

---

## 6. 进阶：添加真正的新内容

改数值靠 Harmony 就够了。要加**新物品 / 新建筑 / 新科技**，需要社区库：

| 库 | 作用 |
|---|---|
| **LDBTool**（xiaoye97 / kremnev8） | 添加和修改 Proto 数据，管理 mod 物品的 ID 分配与本地化字符串。加新内容的标准方案 |
| **CommonAPI**（kremnev8） | 更高层封装：ID 冲突处理、自定义建筑注册、Tab 页、自定义配方类型 |
| **DSPModSave** | 把 mod 的自定义数据存进存档文件。**不用它玩家一读档数据就没了** |
| **NebulaMultiplayerModApi** | 兼容多人联机，处理主机 / 客户端数据同步 |

大型内容 mod（比如 GenesisBook）通常同时依赖 **BepInEx + LDBTool + DSPModSave + CommonAPI + Nebula API**，这套组合基本是社区标准栈。

### 6.1 加到项目里

这些库大多发布在 NuGet 上：

```xml
<ItemGroup>
  <PackageReference Include="DysonSphereProgram.Modding.LDBTool" Version="3.*" PrivateAssets="all" />
</ItemGroup>
```

Rider 里也可以用 `Ctrl + Alt + Shift + N` 搜 NuGet 包，或直接在 NuGet 工具窗口搜 `DysonSphereProgram.Modding`。

没上 NuGet 的库，就下载 Thunderstore 包，把 DLL 引用进来（`<Private>false</Private>`，不要打包进你的输出）。

### 6.2 别忘了声明依赖

```csharp
[BepInPlugin(GUID, NAME, VERSION)]
[BepInDependency("me.xiaoye97.plugin.Dyson.LDBTool")]
[BepInDependency("dsp.nebula-multiplayer-api", BepInDependency.DependencyFlags.SoftDependency)]
public class MyDspMod : BaseUnityPlugin { }
```

`SoftDependency` = 有就用，没有也能跑。

### 6.3 美术资源

新建筑的模型和图标要用 **Unity Editor 打成 AssetBundle**，Unity 版本必须和游戏一致，否则加载会失败。运行时：

```csharp
var path = Path.Combine(
    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
    "mymod.assetbundle");
var bundle = AssetBundle.LoadFromFile(path);
var icon = bundle.LoadAsset<Sprite>("assets/icons/myitem.png");
```

### 6.4 UI

最省事的做法是**复制游戏现有的 UI 组件**：`Object.Instantiate` 一个原版窗口，改标题和内容，再挂上自己的逻辑。从零搭 UGUI 层级非常痛苦。

快速原型阶段可以先用 `OnGUI()` 画 IMGUI 界面，丑但能用，验证逻辑够了。

---

## 7. 调试

### 7.1 日志（最常用）

```csharp
MyDspMod.Log.LogInfo("普通信息");
MyDspMod.Log.LogWarning("警告");
MyDspMod.Log.LogError("错误");
MyDspMod.Log.LogDebug("调试信息");  // 需在 BepInEx.cfg 里放开 LogLevel
```

输出位置：BepInEx 控制台窗口 + `BepInEx/LogOutput.log`。

### 7.2 必装的辅助工具

| 工具 | 用途 |
|---|---|
| **UnityExplorer** | 游戏内实时查看对象树、字段值、直接调用方法。排查效率翻倍 |
| **ScriptEngine** | 热重载插件 DLL，不用每次重启游戏 |
| **WhatTheBreak** | 异常信息一键复制 + 堆栈分析，方便报错反馈 |

### 7.3 Rider 附加调试器（进阶，可选）

Unity Mono 游戏默认不开调试端口。要用 Rider 打断点，需要：

1. 从 dnSpy 的 `dnSpy-Unity-mono` 发布页下载**与游戏 Unity 版本匹配**的调试版 `mono-2.0-bdwgc.dll`
2. 备份并替换 `DSPGAME_Data/MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll`
3. 启动游戏时加参数：
   ```
   --debugger-agent=transport=dt_socket,server=y,address=127.0.0.1:56000
   ```
4. Rider → `Run → Attach to Process` → 选 **Mono / Unity 调试器** → 连 `127.0.0.1:56000`

**注意事项**：这套流程比较折腾，Unity 版本对不上就无法启动；Harmony 生成的动态方法通常没法直接下断点（要在你自己的补丁方法里下）。

**实话说，绝大多数 DSP mod 作者靠日志就够了。** 除非你在调很复杂的状态问题，否则不建议一开始就折腾这个。

### 7.4 常见坑

| 现象 | 原因与处理 |
|---|---|
| `MissingFieldException` / `MissingMethodException` | 游戏更新后字段或方法签名变了。重新反编译对一遍 |
| 堆栈里出现 `(wrapper dynamic-method)` | 这是 Harmony 生成的补丁方法，说明异常来自你的补丁 |
| 插件完全没加载，控制台没任何输出 | 检查 `TargetFramework` 是不是误写成了 `net8.0`；检查 DLL 是否在 `BepInEx/plugins/` 下 |
| `HarmonyX` 警告找不到方法 | `AccessTools` 查找失败，通常是类名或方法名拼错，或该方法在当前版本不存在 |
| 游戏进主菜单就崩 | 补丁打在了太早执行的方法上。把初始化逻辑挪到 `GameMain.Begin` 之后 |

### 7.5 性能红线

DSP 每秒跑 60 tick，工厂逻辑跑在多线程里且经过极致优化，一个星球几万个建筑很正常。

**在 `GameTick` / `InternalUpdate` 路径上绝对不要做**：

- 分配内存（`new`、装箱、闭包）
- LINQ
- 字符串拼接、`string.Format`、插值字符串
- `GetComponent` / `Find` / `FindObjectOfType`
- 每帧写日志

这些东西单次开销看着不大，乘以几万个实体乘以每秒 60 次，直接卡死。需要缓存的东西提前算好存起来。

---

## 8. 发布

DSP 的 mod 平台是 **Thunderstore**。

### 8.1 包结构

```
MyDspMod.zip
├── manifest.json      （必需）
├── icon.png           （必需，正好 256×256）
├── README.md          （必需）
├── CHANGELOG.md       （可选）
└── plugins/
    └── MyDspMod/
        ├── MyDspMod.dll
        └── mymod.assetbundle
```

### 8.2 manifest.json

```json
{
  "name": "MyDspMod",
  "version_number": "1.0.0",
  "website_url": "https://github.com/yourname/MyDspMod",
  "description": "一句话描述，250 字符以内",
  "dependencies": [
    "xiaoye97-LDBTool-2.0.0",
    "BepInEx-BepInExPack_Dyson_Sphere_Program-5.4.21"
  ]
}
```

`dependencies` 里的字符串格式是 `作者-包名-版本`，写清楚后玩家用 r2modman 安装时会自动拉取前置。

### 8.3 自动打包（加到 csproj）

```xml
<Target Name="PackageForThunderstore" AfterTargets="Build"
        Condition="'$(Configuration)' == 'Release'">
  <PropertyGroup>
    <PkgDir>$(ProjectDir)dist\$(AssemblyName)</PkgDir>
  </PropertyGroup>
  <RemoveDir Directories="$(PkgDir)" />
  <MakeDir Directories="$(PkgDir)\plugins\$(AssemblyName)" />
  <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(PkgDir)\plugins\$(AssemblyName)\" />
  <Copy SourceFiles="$(ProjectDir)manifest.json;$(ProjectDir)icon.png;$(ProjectDir)README.md"
        DestinationFolder="$(PkgDir)\" />
  <ZipDirectory SourceDirectory="$(PkgDir)"
                DestinationFile="$(ProjectDir)dist\$(AssemblyName)-$(Version).zip"
                Overwrite="true" />
</Target>
```

在 Rider 里把配置切到 Release 构建一次，`dist/` 下就会出现可以直接上传的 zip。

### 8.4 版本兼容声明

游戏更新后 mod 挂掉是常态。在 README 里写清楚支持的游戏版本，并在代码里加个保护：

```csharp
private void Awake()
{
    Logger.LogInfo($"游戏版本: {GameConfig.gameVersion}");
    // 检测到不兼容版本时可以选择只警告，而不是直接崩
}
```

---

## 9. 学习资源

最有效的学习方式是**读开源 mod 的源码**：

| 仓库 / 资源 | 说明 |
|---|---|
| `jinxOAO/DSPmods_BepInEx` | 内容类 mod 合集，中英文注释 |
| `YukkuriC/DSP-BepInEx-Mods` | 中文作者的 mod 总集 |
| `kremnev8` 的 CommonAPI / LDBTool | 工具库最佳实践，想做大型 mod 必读 |
| `Therzok/dsp_modding` | 包含 MSBuild 自动化发布流程的参考 |
| BepInEx 官方文档 | Harmony 用法、配置系统、生命周期 |
| HarmonyX / Harmony 文档 | Prefix/Postfix/Transpiler 详细语义 |
| DSP 官方 Discord 的 modding 频道 | 社区技术讨论主阵地 |

---

## 10. 建议的起步顺序

不要一上来就做大 mod。按这个顺序走：

1. **跑通流程** —— 写一个只在 `Awake` 里打一行日志的插件，确认它能被 BepInEx 加载
2. **只读交互** —— 按 F5 打印当前星球所有矿脉储量（第 5.2 节有完整代码）
3. **改一个数值** —— 用 Postfix 把某个生产速度翻倍，理解 Harmony 的注入位置
4. **加配置项** —— 把倍率做成可配置，理解 `ConfigEntry` 和配置文件生成
5. **做个 UI** —— 先用 `OnGUI` 画个简单面板，再考虑复制原版 UI 组件
6. **加新内容** —— 接入 LDBTool 加一个新物品，接入 DSPModSave 存自定义数据
7. **发布** —— 打包上 Thunderstore

**第一个 mod 能成功加载、日志正常打出来，剩下的就只是查 API 和写业务逻辑了。**

---

## 附录：环境检查清单

开始写代码前逐项确认：

- [ ] `dotnet --version` 输出 8.x
- [ ] Rider 能正常识别 .NET 8 SDK
- [ ] BepInEx **5.4.x x64** 已解压到游戏根目录
- [ ] 启动过一次游戏，`BepInEx/plugins/` 目录已生成
- [ ] `BepInEx.cfg` 里 `[Logging.Console] Enabled = true`
- [ ] csproj 的 `TargetFramework` 是 `netstandard2.0`，**不是 net8.0**
- [ ] csproj 里的 `GameDir` 已改成你自己的路径
- [ ] 能在 Rider 里 `Ctrl+左键` 跳进 `GameMain` 看到反编译源码
- [ ] 存档已备份，或者在用 r2modman 的独立 profile
