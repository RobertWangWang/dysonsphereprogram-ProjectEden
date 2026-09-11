using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ProjectEden.Compatibility;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 巨型建筑机的传送带直连 I/O。移植自 ProjectGenesis 的 MegaAssemblerPatches，
    /// 去掉了物流塔存储、Nebula 联机同步、物质分解特例和自定义配方类型。
    ///
    /// 判定方式沿用原作：speed >= 阈值的组装机即视为巨型，不需要独立的实体类型。
    /// </summary>
    [HarmonyPatch]
    internal static class MegaAssemblerPatches
    {
        /// <summary>
        /// 组装机的 tick 有两条路径：单线程的 FactorySystem.GameTick，和多线程的
        /// GameLogic._assembler_parallel。游戏默认走后者，所以只挂 FactorySystem.GameTick
        /// 的 Postfix 是收不到回调的——本 mod 之前就栽在这里，传送带 I/O 和物流都从未执行。
        ///
        /// 这里对两条路径都做注入：在每次 AssemblerComponent.InternalUpdate 调用之前，
        /// 插入一次带 PlanetFactory 的回调。做法与 ProjectGenesis 的
        /// AssemblerComponent_InternalUpdate_PrePatch 一致。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(FactorySystem), nameof(FactorySystem.GameTick))]
        [HarmonyPatch(typeof(GameLogic), nameof(GameLogic._assembler_parallel))]
        private static IEnumerable<CodeInstruction> AssemblerTick_Transpiler(IEnumerable<CodeInstruction> instructions,
            MethodBase original)
        {
            var matcher = new CodeMatcher(instructions);

            // 取 PlanetFactory 的方式两条路径不同：
            //   FactorySystem.GameTick   -> this.factory
            //   _assembler_parallel      -> 局部变量，用 “ldloc + ldfld entityAnimPool” 认出来
            CodeInstruction[] loadFactory;

            if (original.Name == nameof(GameLogic._assembler_parallel))
            {
                matcher.MatchForward(false,
                    new CodeMatch(OpCodes.Ldloc_S),
                    new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(PlanetFactory), nameof(PlanetFactory.entityAnimPool))));

                if (matcher.IsInvalid)
                {
                    ProjectEdenPlugin.Log.LogError(
                        "GameLogic._assembler_parallel 里找不到 PlanetFactory 局部变量，巨型建筑的多线程路径未接管");

                    return matcher.InstructionEnumeration();
                }

                loadFactory = new[] { new CodeInstruction(matcher.Instruction) };
            }
            else
            {
                loadFactory = new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(FactorySystem), nameof(FactorySystem.factory))),
                };
            }

            matcher.Start();

            var injected = 0;

            while (true)
            {
                matcher.MatchForward(false,
                    new CodeMatch(OpCodes.Call,
                        AccessTools.Method(typeof(AssemblerComponent), nameof(AssemblerComponent.InternalUpdate))));

                if (matcher.IsInvalid) break;

                // 调用点往前数 4 条是组装机局部变量，紧随其后的是 power
                // 调用点之前依次是：组装机、power、productRegister、consumeRegister
                CodeInstruction component = matcher.Advance(-4).Instruction;
                CodeInstruction power = matcher.InstructionAt(1);
                CodeInstruction productRegister = matcher.InstructionAt(2);
                CodeInstruction consumeRegister = matcher.InstructionAt(3);

                matcher.InsertAndAdvance(loadFactory);
                matcher.InsertAndAdvance(new CodeInstruction(component),
                                         new CodeInstruction(power),
                                         new CodeInstruction(productRegister),
                                         new CodeInstruction(consumeRegister),
                                         new CodeInstruction(OpCodes.Call, MegaTickMethod));

                injected++;

                // 跳过刚插入的内容和原本的 4+1 条，继续找下一个调用点
                matcher.Advance(5);
            }

            if (injected == 0)
                ProjectEdenPlugin.Log.LogError($"{original.Name}：没找到 AssemblerComponent.InternalUpdate 调用点，巨型建筑不会工作");
            else
                ProjectEdenPlugin.Log.LogInfo($"{original.Name}：已在 {injected} 处组装机 tick 前注入巨型建筑逻辑");

            return matcher.InstructionEnumeration();
        }

        private static readonly MethodInfo MegaTickMethod =
            AccessTools.Method(typeof(MegaAssemblerPatches), nameof(MegaTick));

        /// <summary>供 IL 调用：每台组装机 tick 前跑一次，只对巨型建筑做事。</summary>
        internal static void MegaTick(PlanetFactory factory, ref AssemblerComponent component, float power,
            int[] productRegister, int[] consumeRegister)
        {
            if (!GenesisBookCompat.MegaAssemblerEnabled) return;
            if (factory == null) return;
            if (component.speed < MegaBuildingRegistry.MegaSpeedThreshold) return;

            ApplySpeed(ref component);

            UpdateSlots(factory, ref component);

            RunExtraCycles(ref component, power, productRegister, consumeRegister);
        }

        /// <summary>
        /// 单 tick 多周期结算。
        ///
        /// 原版每 tick 只结算一个配方周期：产物写入是 produced[i] += productCounts[i]，
        /// 不乘任何周期数，time 也只扣一次 timeSpend。所以无论速度多高，上限都是 60 周期/秒。
        ///
        /// 这里不去改结算逻辑本身——产物、原料扣减、time 三处分散在不同 tick，
        /// 手工同步很容易出现「产物乘了而原料没乘」的凭空造物。
        /// 改为在同一 tick 内多跑几遍原版的 InternalUpdate：每一遍都是完整的原版流程，
        /// 自带扣料与产出核算，原料不足时它自己就不产出，结构上不可能失衡。
        ///
        /// 之所以多跑几遍就能多产出：高速下 time 会累积成一个「待结算周期」的缓冲，
        /// 每次调用消化其中一个，而 time &gt;= timeSpend 期间不再累加。
        /// </summary>
        private static void RunExtraCycles(ref AssemblerComponent component, float power, int[] productRegister,
            int[] consumeRegister)
        {
            int cycles = MegaBuildingRegistry.Config?.cyclesPerTick ?? 1;

            // 原本那次调用紧随其后，所以这里只补差额
            for (var i = 1; i < cycles; i++) component.InternalUpdate(power, productRegister, consumeRegister);
        }

        private static bool _reportedSpeed;

        /// <summary>
        /// 把已建成建筑的速度提到配置值。
        ///
        /// AssemblerComponent.speed 是建造时从 prefabDesc 取的并存进存档，
        /// 改 prefabDesc 只影响之后新建的——和站点容量、采矿机功率是同一个坑。
        ///
        /// 提速对功耗无影响：AssemblerComponent.SetPCState 用的是
        /// workEnergyPerTick × (1000 + extraPowerRatio) / 1000，与 speed 无关。
        /// </summary>
        private static void ApplySpeed(ref AssemblerComponent component)
        {
            int target = MegaBuildingRegistry.Config?.assemblerSpeed ?? 0;

            if (target <= 0 || component.speed >= target) return;

            if (!_reportedSpeed)
            {
                _reportedSpeed = true;

                ProjectEdenPlugin.Log.LogInfo(
                    $"已建成的巨型建筑速度补正：{component.speed} → {target}" +
                    $"（{component.speed / 10000.0:0.#} 倍 → {target / 10000.0:0.#} 倍）");
            }

            component.speed = target;
        }

        private static void UpdateSlots(PlanetFactory factory, ref AssemblerComponent component)
        {
            SlotData[] slots = SlotDataStore.GetSlots(factory.planetId, component.entityId);

            UpdateOutputSlots(ref component, factory.cargoTraffic, slots, factory.entitySignPool,
                              GameMain.history.stationPilerLevel);
            UpdateInputSlots(ref component, factory.cargoTraffic, slots, factory.entitySignPool);

            // 传送带之外，再走一遍行星内物流：储物格与制造台之间搬运，运输机自动送料取货
            MegaStationPatches.UpdateStationStorage(factory, ref component);
        }

        /// <summary>把产物和多余的原料推上输出带。</summary>
        private static void UpdateOutputSlots(ref AssemblerComponent __instance, CargoTraffic traffic, SlotData[] slotdata,
            SignData[] signPool, int maxPilerCount)
        {
            if (maxPilerCount < 1) maxPilerCount = 1;

            for (var index1 = 0; index1 < slotdata.Length; ++index1)
            {
                ref SlotData slotData = ref slotdata[index1];

                if (slotData.dir != IODir.Output)
                {
                    // 槽位不是输出也不是输入时，清掉残留的带子引用
                    if (slotData.dir != IODir.Input)
                    {
                        slotData.beltId = 0;
                        slotData.counter = 0;
                    }

                    continue;
                }

                int beltId = slotData.beltId;

                if (beltId <= 0) continue;

                BeltComponent beltComponent = traffic.beltPool[beltId];
                CargoPath cargoPath = traffic.GetCargoPath(beltComponent.segPathId);

                if (cargoPath == null) continue;

                int index2 = slotData.storageIdx - 1;
                var itemId = 0;

                if (index2 >= 0)
                {
                    RecipeExecuteData executeData = __instance.recipeExecuteData;

                    if (index2 < executeData.products.Length)
                    {
                        // 输出产物
                        itemId = executeData.products[index2];
                        int produced = __instance.produced[index2];

                        if (itemId > 0 && produced > 0)
                        {
                            int num = produced < maxPilerCount ? produced : maxPilerCount;

                            if (CargoWidening.InsertAtHead(cargoPath, itemId, num, 0))
                                __instance.produced[index2] -= num;
                        }
                    }
                    else
                    {
                        // 输出多余的原料（槽位索引落在 products 之后即指向 requires）
                        int index3 = index2 - executeData.products.Length;

                        if (index3 < executeData.requires.Length)
                        {
                            itemId = executeData.requires[index3];
                            int served = __instance.served[index3];

                            if (itemId > 0 && served > 0)
                            {
                                int num = served < maxPilerCount ? served : maxPilerCount;
                                var inc = (int)((double)__instance.incServed[index3] * num / served);

                                if (CargoWidening.InsertAtHead(cargoPath, itemId, num, inc))
                                {
                                    __instance.incServed[index3] -= inc;
                                    __instance.served[index3] -= num;
                                }
                            }
                        }
                    }
                }

                if (itemId <= 0) continue;

                // 在带子上打出物品图标，和物流塔的表现一致
                int entityId = beltComponent.entityId;
                signPool[entityId].iconType = 1U;
                signPool[entityId].iconId0 = (uint)itemId;
            }
        }

        /// <summary>从输入带取料，填进 served；也接受回流的产物。</summary>
        private static void UpdateInputSlots(ref AssemblerComponent __instance, CargoTraffic traffic, SlotData[] slotdata,
            SignData[] signPool)
        {
            for (var index = 0; index < slotdata.Length; ++index)
            {
                if (slotdata[index].dir != IODir.Input)
                {
                    if (slotdata[index].dir != IODir.Output)
                    {
                        slotdata[index].beltId = 0;
                        slotdata[index].counter = 0;
                    }

                    continue;
                }

                int beltId = slotdata[index].beltId;

                if (beltId <= 0) continue;

                BeltComponent beltComponent = traffic.beltPool[beltId];
                CargoPath cargoPath = traffic.GetCargoPath(beltComponent.segPathId);

                if (cargoPath == null) continue;

                int itemId = CargoWidening.PickAtRear(cargoPath, __instance.needs, out int needIdx, out int stack, out int inc);

                RecipeExecuteData executeData = __instance.recipeExecuteData;

                if (needIdx >= 0 && itemId > 0 && __instance.needs[needIdx] == itemId)
                {
                    __instance.served[needIdx] += stack;
                    __instance.incServed[needIdx] += inc;
                    slotdata[index].storageIdx = executeData.products.Length + needIdx + 1;
                }

                for (var i = 0; i < executeData.products.Length; i++)
                {
                    if (__instance.produced[i] >= 50) continue;

                    itemId = CargoWidening.PickAtRear(traffic, beltId, executeData.products[i], null, out stack, out int _);

                    if (executeData.products[i] != itemId) continue;

                    __instance.produced[i] += stack;
                    slotdata[index].storageIdx = i + 1;

                    break;
                }

                if (itemId <= 0) continue;

                int entityId = beltComponent.entityId;
                signPool[entityId].iconType = 1U;
                signPool[entityId].iconId0 = (uint)itemId;
            }
        }

        /// <summary>传送带接到建筑上（建筑 → 带）时记录成输出槽。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.ApplyInsertTarget))]
        private static void PlanetFactory_ApplyInsertTarget(PlanetFactory __instance, int entityId, int insertTarget, int slotId)
        {
            if (!IsMegaAssembler(__instance, entityId)) return;

            int beltId = __instance.entityPool[insertTarget].beltId;

            if (beltId <= 0) return;

            SlotData[] slots = SlotDataStore.GetSlots(__instance.planetId, entityId);

            if (slotId < 0 || slotId >= slots.Length) return;

            slots[slotId].dir = IODir.Output;
            slots[slotId].beltId = beltId;
            slots[slotId].counter = 0;
        }

        /// <summary>传送带接到建筑上（带 → 建筑）时记录成输入槽。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.ApplyPickTarget))]
        private static void PlanetFactory_ApplyPickTarget(PlanetFactory __instance, int entityId, int pickTarget, int slotId)
        {
            if (!IsMegaAssembler(__instance, entityId)) return;

            int beltId = __instance.entityPool[pickTarget].beltId;

            if (beltId <= 0) return;

            SlotData[] slots = SlotDataStore.GetSlots(__instance.planetId, entityId);

            if (slotId < 0 || slotId >= slots.Length) return;

            slots[slotId].dir = IODir.Input;
            slots[slotId].beltId = beltId;
            slots[slotId].storageIdx = 0;
            slots[slotId].counter = 0;
        }

        /// <summary>建筑拆除时清掉槽位，避免字典无限增长。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.RemoveEntityWithComponents))]
        private static void PlanetFactory_RemoveEntityWithComponents(PlanetFactory __instance, int id)
        {
            SlotDataStore.Remove(__instance.planetId, id);
        }

        private static bool IsMegaAssembler(PlanetFactory factory, int entityId)
        {
            if (entityId <= 0) return false;
            if (!GenesisBookCompat.MegaAssemblerEnabled) return false;

            int assemblerId = factory.entityPool[entityId].assemblerId;

            if (assemblerId <= 0) return false;

            ref AssemblerComponent assembler = ref factory.factorySystem.assemblerPool[assemblerId];

            return assembler.id == assemblerId && assembler.speed >= MegaBuildingRegistry.MegaSpeedThreshold;
        }
    }
}
