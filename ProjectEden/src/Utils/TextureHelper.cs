// 本文件移植自 ProjectGenesis（创世之书），属于其衍生作品。
// Portions of this file are derived from ProjectGenesis (GenesisBook).
//
//     Copyright (C) 2022-2026 Awbugl and ProjectGenesis contributors
//     https://github.com/Awbugl/ProjectGenesis
//
// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace ProjectEden.Utils
{
    /// <summary>
    /// 从嵌入资源里读 PNG，转成 Texture2D / Sprite。
    /// 结构移植自 ProjectGenesis 的 TextureHelper。
    /// </summary>
    internal static class TextureHelper
    {
        private static readonly Assembly Assembly = Assembly.GetExecutingAssembly();

        private static readonly Dictionary<string, Texture2D> TextureCache = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, Sprite> SpriteCache = new Dictionary<string, Sprite>();

        internal static Texture2D GetTexture(string name)
        {
            if (TextureCache.TryGetValue(name, out Texture2D cached)) return cached;

            // csproj 里 assets\icons\*.png 作为 EmbeddedResource，逻辑名为 ProjectEden.assets.icons.<name>.png
            string resourceName = "ProjectEden.assets.icons." + name + ".png";

            using (Stream stream = Assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    ProjectEdenPlugin.Log.LogWarning($"找不到嵌入图标资源 {resourceName}");
                    TextureCache[name] = null;

                    return null;
                }

                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);

                    // 尺寸随 LoadImage 自动修正，这里 2x2 只是占位
                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);

                    if (!texture.LoadImage(memory.ToArray()))
                    {
                        ProjectEdenPlugin.Log.LogWarning($"图标 {name} 解码失败");
                        TextureCache[name] = null;

                        return null;
                    }

                    texture.name = name;
                    TextureCache[name] = texture;

                    return texture;
                }
            }
        }

        internal static Sprite GetSprite(string name)
        {
            if (SpriteCache.TryGetValue(name, out Sprite cached)) return cached;

            Texture2D texture = GetTexture(name);

            Sprite sprite = texture == null
                ? null
                : Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));

            SpriteCache[name] = sprite;

            return sprite;
        }
    }
}
