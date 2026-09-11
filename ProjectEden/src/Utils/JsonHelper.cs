using System;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace ProjectEden.Utils
{
    /// <summary>
    /// 读取 data/*.json（借鉴 ProjectGenesis 的做法）。
    /// 数值改动只要改 JSON 重新编译即可，不用碰 C#。
    /// 用 Newtonsoft 而非 Unity 的 JsonUtility：后者填不了嵌套的自定义类数组，
    /// 顶层标量能解析、buildings 却会是 null，而且不报任何错。
    ///
    /// <b>两个来源，磁盘优先。</b> 先看
    /// <c>BepInEx/config/ProjectEden/&lt;name&gt;.json</c>，没有再用内嵌的那份。
    ///
    /// 加这条是因为内嵌资源有个很硬的副作用：<b>改一个开关就得重新编译一次</b>。
    /// 内容类配置（矿种、配方、数值）本来就跟着版本走，编译无所谓；但
    /// cheats.json 那种要反复开关的东西，没有磁盘副本等于根本没法用——
    /// 实测就是这么栽的：五个开关全是 false，玩家在 profile 里翻遍了也找不到能改的文件。
    ///
    /// <b>磁盘覆盖每次都会打一条 WARNING。</b> 这是 LDBTool 的 CustomID.cfg 那个坑的同一形状：
    /// 一份忘了删的旧覆盖文件会让之后所有对内嵌 JSON 的修改看起来「没生效」，
    /// 而且一声不吭。宁可日志吵一点。
    /// </summary>
    internal static class JsonHelper
    {
        internal static T Load<T>(string name)
        {
            string path = OverridePath(name);

            if (path != null && File.Exists(path))
            {
                try
                {
                    var text = File.ReadAllText(path, Encoding.UTF8);
                    var value = JsonConvert.DeserializeObject<T>(text);

                    ProjectEdenPlugin.Log.LogWarning(
                        $"{name}.json 读的是磁盘覆盖文件，内嵌的那份被忽略：{path}");

                    return value;
                }
                catch (Exception e)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"磁盘覆盖文件 {path} 解析失败（{e.Message}），改用内嵌的 {name}.json");
                }
            }

            // csproj 里 data\*.json 作为 EmbeddedResource，逻辑名为 <RootNamespace>.data.<name>.json
            string resourceName = "ProjectEden.data." + name + ".json";

            Assembly assembly = Assembly.GetExecutingAssembly();

            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException(
                        $"找不到嵌入资源 {resourceName}。检查 csproj 的 EmbeddedResource 配置，" +
                        "以及是否用了 -t:Compile 之类不生成资源的构建方式。");

                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return JsonConvert.DeserializeObject<T>(reader.ReadToEnd());
            }
        }

        /// <summary>磁盘覆盖文件应该在哪。取不到 BepInEx 的路径就当没有这回事。</summary>
        internal static string OverridePath(string name)
        {
            try
            {
                return Path.Combine(Path.Combine(BepInEx.Paths.ConfigPath, "ProjectEden"), name + ".json");
            }
            catch
            {
                return null;
            }
        }
    }
}
