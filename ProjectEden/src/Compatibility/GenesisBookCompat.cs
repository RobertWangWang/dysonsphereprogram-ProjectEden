namespace ProjectEden.Compatibility
{
    /// <summary>
    /// 创世之书（GenesisBook）兼容。
    ///
    /// 冲突是实打实的：GenesisBook 用"组装机 speed >= 300000"作为它自己巨型建筑机的判定条件，
    /// 和本 mod 完全一致。两者同时装载时，同一台组装机会被两套传送带 I/O 逻辑各驱动一次，
    /// 表现为取料/出货翻倍。检测到它就让本 mod 的巨型逻辑退场——它的实现远比这里完整。
    ///
    /// 另外它的预加载器会删掉 EVeinType.Max 并新增 6 个矿种（15~20），
    /// 所以任何按 EVeinType.Max 定长的数组都会漏统计；VeinDumpPatch 已改为运行时取枚举上界。
    /// </summary>
    internal static class GenesisBookCompat
    {
        internal const string MODGUID = "org.LoShin.GenesisBook";

        internal static bool Installed { get; private set; }

        internal static void Init()
        {
            Installed = CompatibilityRegistry.IsLoaded(MODGUID);

            if (!Installed) return;

            ProjectEdenPlugin.Log.LogWarning(
                "检测到创世之书（GenesisBook）。它同样以 speed >= 300000 判定巨型组装机，" +
                "为避免同一台建筑被两套传送带逻辑重复驱动，本 mod 的巨型建筑机功能已停用。");
        }

        /// <summary>本 mod 的巨型建筑机逻辑是否应当生效。</summary>
        internal static bool MegaAssemblerEnabled => !Installed;
    }
}
