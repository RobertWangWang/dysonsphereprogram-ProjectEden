using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 无碰撞：把原版的「Planet Collider Pool」整个对象关掉。
    ///
    /// <b>这一条没有补丁，只有一次 SetActive。</b> 行星上所有建筑的 Unity 碰撞体都是
    /// <c>ColliderPool</c> 这个对象池发出来的——<c>TakeCollider</c> 把实例化出来的
    /// BoxCollider / CapsuleCollider / SphereCollider 挂在池子自己的 transform 下面
    /// （IL 005E 处 <c>Component::get_transform()</c>）。把池子的 GameObject 关掉，
    /// 这些碰撞体一起失活，<c>Physics.CheckBox</c> 之类的查询就再也打不到东西，
    /// 于是建筑可以随便重叠。
    ///
    /// <b>这一半默认是关的（<c>noCollisionPhysics</c>），而且重叠建造并不需要它。</b>
    /// 让建筑能重叠的是「放行建造判定」那一半；池子这一半只是额外附送的机甲穿墙。
    ///
    /// <b>为什么默认关：它会把传送带连接弄坏。</b>
    /// <c>BuildTool_Path.UpdateRaycast</c> 里有<b>五处</b> <c>Physics.Raycast</c>，
    /// 传送带工具就是靠它认出光标下面是什么——包括「要接上的那条已有传送带」。
    /// 池子一关，这些射线什么都打不到，于是<b>没法再接到已有传送带的前半截上</b>。实测报障。
    /// 建筑的点选和拆除不受影响——那条路走的是 <c>PlanetPhysics.raycastLogic</c>，
    /// 游戏自己实现的一套射线检测，不碰 Unity 物理。但 <b>建造工具的取点走的是 Unity 物理</b>，
    /// 这两者当初被我混为一谈了。
    ///
    /// <b>副作用（开了才有）：机甲也会穿过建筑、传送带接不上。</b> 同一批碰撞体，分不开。
    ///
    /// 挂两个钩子，因为池子的生命周期比本 mod 短：
    ///   · <c>ColliderPool.Awake</c> —— 池子由 <c>Create()</c> 现场 new 出来，每局一个，
    ///     Awake 的第一条指令就是 <c>set_instance(this)</c>，所以后置里一定拿得到。
    ///   · <c>GameMain.Begin</c> —— 兜底。ScriptEngine 热重载时池子可能早就建好了，
    ///     那时 Awake 已经跑过，只剩这条能补上。
    /// </summary>
    [HarmonyPatch]
    internal static class NoCollisionPatches
    {
        private static CheatsConfig Config => ProjectEdenPlugin.CheatsConfig;

        private static bool On => Config != null && Config.enabled && Config.noCollision && Config.noCollisionPhysics;

        private static int _logged;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ColliderPool), nameof(ColliderPool.Awake))]
        private static void ColliderPool_Awake() => Apply();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameMain), nameof(GameMain.Begin))]
        private static void GameMain_Begin() => Apply();

        private static void Apply()
        {
            if (!On) return;

            ColliderPool pool = ColliderPool.instance;

            if (pool == null) return;

            GameObject go = pool.gameObject;

            if (go == null || !go.activeSelf) return;

            go.SetActive(false);

            if (Interlocked.Exchange(ref _logged, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo(
                    "作弊：noCollisionPhysics 已生效，行星碰撞体对象池已关闭。" +
                    "代价：机甲会穿过建筑，而且**传送带无法再接到已有传送带上**" +
                    "（BuildTool_Path.UpdateRaycast 的 5 处 Physics.Raycast 全部打空）。" +
                    "重叠建造并不需要这一项，关掉它即可。");
        }
    }
}
