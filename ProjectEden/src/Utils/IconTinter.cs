using UnityEngine;

namespace ProjectEden.Utils
{
    /// <summary>
    /// 把原版图标整张改色，做出「同一个东西的另一种金属」。
    ///
    /// 为什么不直接画一张：新矿石要的就是「铁那张图，但换个色调」，
    /// 运行时改色比画图稳，也不用往仓库里塞美术资源，铁的图标以后被官方改了这边自动跟着变。
    ///
    /// <b>原版贴图是不可读的</b>（Resources 里的 Texture2D 没开 Read/Write），
    /// 直接 GetPixels 会抛 UnityException。所以先 Blit 进 RenderTexture 再 ReadPixels——
    /// 这条路不要求源贴图可读，是 Unity 里取任意贴图像素的通用办法。
    ///
    /// 改色走 HSV：色相换成目标色，饱和度和明度按倍率缩放，<b>alpha 原样保留</b>
    /// （图标是带透明边的，动了 alpha 边缘就毛了）。灰像素（饱和度接近 0）色相对它们无效，
    /// 所以另给一个饱和度下限，否则铁图标里那些接近灰的部分会留在原地不变蓝。
    /// </summary>
    internal static class IconTinter
    {
        /// <summary>
        /// 按目标色相重新着色一个 Sprite。源 sprite 为 null 时返回 null（调用方负责打日志）。
        /// </summary>
        internal static Sprite Tint(Sprite source, float hueDegrees, float saturationScale, float minSaturation, float valueScale)
        {
            if (source == null) return null;

            Texture2D readable = MakeReadable(source.texture);

            if (readable == null) return null;

            float hue = Mathf.Repeat(hueDegrees, 360f) / 360f;
            Color[] pixels = readable.GetPixels();

            for (var i = 0; i < pixels.Length; i++)
            {
                Color c = pixels[i];

                // 全透明的像素没有颜色可言，动它只会让边缘出现彩边
                if (c.a <= 0f) continue;

                Color.RGBToHSV(c, out float _, out float s, out float v);

                s *= saturationScale;

                if (s < minSaturation) s = minSaturation;
                if (s > 1f) s = 1f;

                v *= valueScale;

                if (v > 1f) v = 1f;

                Color tinted = Color.HSVToRGB(hue, s, v);

                tinted.a = c.a;
                pixels[i] = tinted;
            }

            var result = new Texture2D(readable.width, readable.height, TextureFormat.RGBA32, false)
            {
                filterMode = source.texture.filterMode,
                wrapMode = TextureWrapMode.Clamp,
                name = source.texture.name + "_tinted",
                // 关键：运行时新建、只被普通 C# 字段引用的资源，会被
                // Resources.UnloadUnusedAssets() 当成没人用而销毁（进游戏时就会调一次）。
                // 销毁之后 Sprite 对象还在、rect 也还对，但底下的贴图没了，
                // Image 就什么都画不出来——表现为「数据全对、屏幕上没有」。
                // HideAndDontSave 让它不参与那轮回收。
                hideFlags = HideFlags.HideAndDontSave,
            };

            result.SetPixels(pixels);
            result.Apply();

            Object.Destroy(readable);

            // 用源 sprite 的 rect / pivot / pixelsPerUnit，图标在界面里的大小和位置才不会变
            Rect rect = source.rect;

            Sprite sprite = Sprite.Create(result, rect, new Vector2(0.5f, 0.5f), source.pixelsPerUnit);

            // Sprite 本身同样要挡住那轮回收
            sprite.hideFlags = HideFlags.HideAndDontSave;
            sprite.name = result.name;

            return sprite;
        }

        /// <summary>
        /// 拿到一张能 GetPixels 的副本。Blit 到 RenderTexture 再 ReadPixels，
        /// 不要求源贴图开了 Read/Write。
        /// </summary>
        private static Texture2D MakeReadable(Texture source)
        {
            if (source == null) return null;

            RenderTexture temp = RenderTexture.GetTemporary(
                source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

            RenderTexture previous = RenderTexture.active;

            try
            {
                Graphics.Blit(source, temp);
                RenderTexture.active = temp;

                var readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };

                readable.ReadPixels(new Rect(0, 0, temp.width, temp.height), 0, 0);
                readable.Apply();

                return readable;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temp);
            }
        }
    }
}
