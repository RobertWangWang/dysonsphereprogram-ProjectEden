using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 行星气体采集器（轨道采集器）的采集速度。
    ///
    /// 采集本身在 StationComponent.UpdateCollection 里，逻辑很短：
    ///     currentCollections[i] += collectionPerTick[i] * rate;
    ///     int num = (int)currentCollections[i];
    ///     if (num != 0) { storage[i].count += num; currentCollections[i] -= num; }
    /// 其中 rate 由 PlanetTransport.GameTick 现算，等于采矿科技倍率（扣掉能耗那一份）。
    /// 真正决定速度的是 collectionPerTick——而它是 PlanetTransport.NewStationComponent
    /// 在<b>建造时</b>算好、并且<b>存进存档</b>的：
    ///     penalty = 1 - workEnergyPerTick / (collectSpeed × gasTotalHeat / 60)
    ///     collectionPerTick[i] = gasSpeeds[i] / 60 × collectSpeed × penalty
    /// 所以只改 PrefabDesc.stationCollectSpeed 对已经建好的采集器毫无效果，
    /// 必须按同一条式子在运行时重算。老规矩，两头都做。
    ///
    /// 顺带一提，把 collectSpeed 抬上去会让 penalty 里那一项趋近于 0，
    /// 也就是能耗折扣自动消失，不需要另外处理。
    ///
    /// 上限：UpdateCollection 里那个 (int) 转换是 Int32，收集速率乘上采矿科技倍率
    /// 不能越过 21.4 亿。collectionPerTick 又是 float，超过 1677 万之后整数就不再精确。
    /// 所以每 tick 的量按 collectorMaxPerTick 夹住（默认取物流站单格容量），
    /// 意思是「一 tick 就能把一格填满」——再快也没有任何意义了。
    /// </summary>
    [HarmonyPatch]
    internal static class GasCollectorPatches
    {
        private static StationsConfig Config => ProjectEdenPlugin.StationsConfig;

        /// <summary>
        /// 每个行星算好的采集速率，key 是 planet.id。
        /// gasSpeeds 和 gasTotalHeat 是行星生成时定死的，所以每个行星只算一次。
        ///
        /// 用并发字典而不是普通 Dictionary：PlanetTransport.GameTick 会在
        /// GameLogic.FactoryTransportGameTick_Parallel 里<b>并行处理多个行星</b>，
        /// 普通字典被多个线程同时写会直接损坏（报 non-concurrent collections 那个异常）。
        /// </summary>
        private static readonly ConcurrentDictionary<int, float[]> TargetsByPlanet = new ConcurrentDictionary<int, float[]>();

        private static int _speed;
        private static long _workEnergyPerTick;
        private static float _maxPerTick;

        /// <summary>proto 就绪后调用：改 prefabDesc，并记下运行时重算要用的参数。</summary>
        internal static void ApplyPrefabSpeed()
        {
            TargetsByPlanet.Clear();
            _speed = 0;

            if (Config == null || Config.collectorSpeed <= 0) return;

            // 不写死物品 ID：凡是 prefabDesc 上打了 isCollectStation 的都算气体采集器，
            // 这样第三方 mod 加的采集器也一并覆盖到。
            var found = 0;

            foreach (ItemProto item in LDB.items.dataArray)
            {
                if (item == null) continue;

                ModelProto model = LDB.models.Select(item.ModelIndex);

                if (model?.prefabDesc == null || !model.prefabDesc.isCollectStation) continue;

                int before = model.prefabDesc.stationCollectSpeed;

                model.prefabDesc.stationCollectSpeed = Config.collectorSpeed;

                _workEnergyPerTick = model.prefabDesc.workEnergyPerTick;
                found++;

                ProjectEdenPlugin.Log.LogInfo(
                    $"{item.name} 采集倍率：{before} → {Config.collectorSpeed}");
            }

            if (found == 0)
            {
                ProjectEdenPlugin.Log.LogWarning("没找到任何气体采集器（prefabDesc.isCollectStation），采集速度未改");

                return;
            }

            _speed = Config.collectorSpeed;

            // 没配就退回物流站单格容量；连那个都没有就不夹
            _maxPerTick = Config.collectorMaxPerTick > 0
                ? Config.collectorMaxPerTick
                : Config.slotCapacity > 0
                    ? Config.slotCapacity
                    : 0f;
        }

        /// <summary>
        /// 按 NewStationComponent 的原式子算出这个行星上每种气体的每 tick 采集量。
        /// 只有某个行星第一次进 tick 时会分配一次数组，之后全是字典命中。
        /// </summary>
        private static float[] TargetsFor(PlanetData planet)
        {
            if (TargetsByPlanet.TryGetValue(planet.id, out float[] cached)) return cached;

            float[] speeds = planet.gasSpeeds;
            double heat = planet.gasTotalHeat;

            // 原版：collectSpeed × gasTotalHeat 为 0 时不打能耗折扣
            var penalty = 0.0;

            if (_speed * heat != 0.0)
                penalty = 1.0 - _workEnergyPerTick / (_speed * heat * (1.0 / 60.0));

            var result = new float[speeds.Length];

            for (var i = 0; i < speeds.Length; i++)
            {
                float value = speeds[i] * (1f / 60f) * _speed;

                if (penalty != 0.0) value *= (float)penalty;

                if (value < 0f) value = 0f;
                if (_maxPerTick > 0f && value > _maxPerTick) value = _maxPerTick;

                result[i] = value;
            }

            TargetsByPlanet.TryAdd(planet.id, result);

            ProjectEdenPlugin.Log.LogInfo(
                $"行星 {planet.displayName} 气体采集速率：{string.Join(" / ", System.Array.ConvertAll(result, v => $"{v * 60f:0.#}/秒"))}");

            return result;
        }

        /// <summary>
        /// 已建成采集器的补齐。挂 PlanetTransport.GameTick，和容量补齐同一个道理。
        /// 非气体巨星每 tick 只多一次空数组判断就返回。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.GameTick))]
        private static void PlanetTransport_GameTick(PlanetTransport __instance)
        {
            // 诊断打点：放在一切 return 之前（理由见 LabLogisticSupplyPatches 同一行）
            Diagnostics.TransportSplitProbe.Phase("轨道采集器");

            if (_speed <= 0) return;

            PlanetData planet = __instance.planet;

            if (planet?.gasSpeeds == null || planet.gasSpeeds.Length == 0) return;
            if (__instance.stationPool == null) return;

            float[] target = TargetsFor(planet);

            for (var i = 1; i < __instance.stationCursor; i++)
            {
                StationComponent station = __instance.stationPool[i];

                if (station == null || station.id != i || !station.isCollector) continue;

                float[] perTick = station.collectionPerTick;

                if (perTick == null) continue;

                int count = perTick.Length < target.Length ? perTick.Length : target.Length;

                for (var g = 0; g < count; g++)
                    if (perTick[g] != target[g])
                        perTick[g] = target[g];

                if (station.collectSpeed != _speed) station.collectSpeed = _speed;
            }
        }
    }
}
