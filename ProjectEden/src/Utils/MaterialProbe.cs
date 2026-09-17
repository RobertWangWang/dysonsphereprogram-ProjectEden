using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ProjectEden.Utils
{
    /// <summary>
    /// 把一份材质原样打进日志：着色器名、渲染队列、关键字，以及着色器声明的
    /// 每一个属性和它当前的值。
    ///
    /// <b>存在的理由：材质在 <c>resources.assets</c> 里，离线一个字都读不到。</b>
    /// 和 <c>BuildingTexture.MeasuredMetalSmooth</c> 去读原版金属度贴图的均值是
    /// 同一个路子——不知道的东西就让它自己报，别猜。
    ///
    /// <b>这件事的代价是量出来的。</b> 小型速采机「叠放发亮」那个问题查了七轮，
    /// 其中四轮浪费在猜属性名上：先猜了个根本不存在的 <c>_EmissionColor</c>，
    /// 又只压了 <c>_Color</c> 而漏掉后面串着的 <c>_AlbedoMultiplier</c>，
    /// 再去压 PBR 那份的金属度/光滑度——而真正累加的是另一份 Unlit Additive 材质，
    /// 它<b>不吃光照</b>，那些属性对它一个字都不起作用。
    /// 每错一次就是一轮「改了、生效了、没用」。
    ///
    /// <b>而且「打一次」必须是每一种着色器一次。</b> 最早的版本只打一台建筑的第一份
    /// 材质，于是那份属性表是残缺的，还残缺得毫无征兆——答案就坐在第三种着色器里。
    /// </summary>
    internal static class MaterialProbe
    {
        private static readonly HashSet<string> _dumped = new HashSet<string>();

        /// <summary>每种着色器只打一次。返回是否真的打了。</summary>
        internal static bool DumpOncePerShader(string who, Material material)
        {
            if (material == null) return false;

            string key = material.shader == null ? "空" : material.shader.name;

            if (!_dumped.Add(key)) return false;

            Dump(who, material);

            return true;
        }

        internal static void Dump(string who, Material material)
        {
            if (material == null)
            {
                ProjectEdenPlugin.Log.LogInfo($"材质核对：「{who}」材质为空");
                return;
            }

            Shader shader = material.shader;

            var sb = new StringBuilder();

            sb.Append($"材质核对：「{who}」着色器 {(shader == null ? "空" : shader.name)}，")
              .Append($"渲染队列 {material.renderQueue}，")
              .Append($"关键字 [{string.Join(", ", material.shaderKeywords)}]");

            if (shader == null)
            {
                ProjectEdenPlugin.Log.LogInfo(sb.ToString());
                return;
            }

            int count = shader.GetPropertyCount();

            for (var i = 0; i < count; i++)
            {
                string name = shader.GetPropertyName(i);

                sb.Append("\n    ").Append(name).Append(' ');

                switch (shader.GetPropertyType(i))
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        sb.Append("颜色 = ").Append(material.GetColor(name));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        sb.Append("向量 = ").Append(material.GetVector(name));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        sb.Append("数值 = ").Append(material.GetFloat(name).ToString("0.###"));
                        break;
                    default:
                        sb.Append("贴图");
                        break;
                }
            }

            ProjectEdenPlugin.Log.LogInfo(sb.ToString());
        }

        /// <summary>把一个 ModelProto 的每一种着色器都打一遍。</summary>
        internal static void DumpModel(string who, ModelProto model)
        {
            if (model?.prefabDesc?.lodMaterials == null)
            {
                ProjectEdenPlugin.Log.LogInfo($"材质核对：「{who}」没有 lodMaterials，读不到材质");
                return;
            }

            var any = false;

            foreach (Material[] lod in model.prefabDesc.lodMaterials)
            {
                if (lod == null) continue;

                foreach (Material material in lod) any |= DumpOncePerShader(who, material);
            }

            if (!any) ProjectEdenPlugin.Log.LogInfo($"材质核对：「{who}」的着色器此前已经打过，不重复");
        }
    }
}
