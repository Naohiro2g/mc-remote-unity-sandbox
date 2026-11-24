using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Code2Create.UnitySandbox.LocalDemo
{
    // ローカルな材質の並び。正式catalogのIDへはResolveMaterialで対応づける。
    internal enum DemoMaterial
    {
        Gold = 0,
        Iron = 1,
        Lantern = 2,
        Glass = 3,
        Diamond = 4,
        Grass = 5,
        Dirt = 6,
        Stone = 7,
        Tnt = 8,
        Unknown = 9
    }

    internal sealed class DemoPalette : System.IDisposable
    {
        private readonly List<Object> owned = new List<Object>();
        private readonly Material[] materials = new Material[10];
        internal Material GrassTop { get; }
        internal Material SnowyGrassTop { get; }
        internal Material UnstableTnt { get; }
        internal Material Guide { get; }
        internal Material GuideDotted { get; }
        internal Material Highlight { get; }

        internal DemoPalette()
        {
            var shader = Shader.Find("Standard");
            if (shader == null) throw new System.InvalidOperationException("Standard shaderが必要です。Built-in Render Pipelineで開いてください。");

            var colors = new[]
            {
                new Color(1f, .72f, .17f),      // Gold
                new Color(.78f, .83f, .88f),    // Iron
                new Color(.74f, .89f, .85f),    // Lantern (pale low-saturation aqua, avoids sun clip)
                Color.white,                    // Glass (colored via GlassTexture)
                new Color(.08f, .58f, .98f),    // Diamond (bluer, higher saturation)
                new Color(.42f, .27f, .13f),    // Grass side (dirt-brown)
                new Color(.45f, .29f, .15f),    // Dirt
                new Color(.52f, .54f, .57f),    // Stone
                new Color(1f, 1f, 1f),          // Tnt (textured)
                new Color(.74f, .46f, .85f)     // Unknown / extensible fallback
            };

            var border = BorderTexture();
            var glassTex = GlassTexture();
            var tntTex = TntTexture();

            for (int i = 0; i < materials.Length; i++)
            {
                Texture2D tex;
                if (i == (int)DemoMaterial.Grass || i == (int)DemoMaterial.Dirt || i == (int)DemoMaterial.Stone)
                {
                    tex = null;
                }
                else if (i == (int)DemoMaterial.Glass)
                {
                    tex = glassTex;
                }
                else if (i == (int)DemoMaterial.Tnt)
                {
                    tex = tntTex;
                }
                else
                {
                    tex = border;
                }

                var m = Own(new Material(shader) { name = "Demo " + (DemoMaterial)i, color = colors[i], mainTexture = tex });
                bool isMatteGround = i == (int)DemoMaterial.Grass || i == (int)DemoMaterial.Dirt;
                m.SetFloat("_Metallic", i < 2 ? .55f : (isMatteGround ? 0f : .05f));
                m.SetFloat("_Glossiness", i < 2 ? .45f : (isMatteGround ? .03f : .22f));

                if (i == (int)DemoMaterial.Lantern)
                {
                    m.EnableKeyword("_EMISSION");
                    m.SetTexture("_EmissionMap", border);
                    m.SetColor("_EmissionColor", colors[i] * .10f);
                }

                if (i == (int)DemoMaterial.Glass)
                {
                    ConfigureTransparent(m);
                }

                materials[i] = m;
            }

            GrassTop = Own(new Material(shader) { name = "Demo grass top", color = new Color(.29f, .53f, .18f) });
            GrassTop.SetFloat("_Metallic", 0f);
            GrassTop.SetFloat("_Glossiness", 0f);
            GrassTop.SetFloat("_SpecularHighlights", 0f);
            GrassTop.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");

            SnowyGrassTop = Own(new Material(shader) { name = "Demo snowy grass top", color = new Color(.92f, .96f, 1f) });
            SnowyGrassTop.SetFloat("_Metallic", 0f);
            SnowyGrassTop.SetFloat("_Glossiness", 0.08f);

            UnstableTnt = Own(new Material(shader) { name = "Demo unstable tnt", color = new Color(1f, .88f, .82f), mainTexture = tntTex });
            UnstableTnt.EnableKeyword("_EMISSION");
            UnstableTnt.SetColor("_EmissionColor", new Color(.35f, .06f, .02f));

            Guide = Own(new Material(Shader.Find("Sprites/Default")) { name = "Demo guides" });
            GuideDotted = Own(new Material(Shader.Find("Sprites/Default"))
            {
                name = "Demo dotted guides",
                mainTexture = DottedLineTexture()
            });

            Highlight = Own(new Material(shader) { name = "Demo highlight", color = new Color(1f, 0.28f, 0.72f, 0.44f) });
            ConfigureTransparent(Highlight);
            Highlight.EnableKeyword("_EMISSION");
            Highlight.SetColor("_EmissionColor", new Color(1f, 0.22f, 0.68f) * 0.65f);
        }

        internal Material this[DemoMaterial kind] => materials[(int)kind];

        internal static DemoMaterial FromBlockId(string blockId)
        {
            if (string.IsNullOrEmpty(blockId)) return DemoMaterial.Unknown;
            switch (blockId)
            {
                case "minecraft:gold_block": return DemoMaterial.Gold;
                case "minecraft:iron_block": return DemoMaterial.Iron;
                case "minecraft:sea_lantern": return DemoMaterial.Lantern;
                case "minecraft:glass": return DemoMaterial.Glass;
                case "minecraft:diamond_block": return DemoMaterial.Diamond;
                case "minecraft:grass_block": return DemoMaterial.Grass;
                case "minecraft:dirt": return DemoMaterial.Dirt;
                case "minecraft:stone": return DemoMaterial.Stone;
                case "minecraft:tnt": return DemoMaterial.Tnt;
                default: return DemoMaterial.Unknown;
            }
        }

        internal static string BlockIdOf(DemoMaterial kind)
        {
            switch (kind)
            {
                case DemoMaterial.Gold: return "minecraft:gold_block";
                case DemoMaterial.Iron: return "minecraft:iron_block";
                case DemoMaterial.Lantern: return "minecraft:sea_lantern";
                case DemoMaterial.Glass: return "minecraft:glass";
                case DemoMaterial.Diamond: return "minecraft:diamond_block";
                case DemoMaterial.Grass: return "minecraft:grass_block";
                case DemoMaterial.Dirt: return "minecraft:dirt";
                case DemoMaterial.Stone: return "minecraft:stone";
                case DemoMaterial.Tnt: return "minecraft:tnt";
                default: return "minecraft:stone";
            }
        }

        internal Material MaterialFor(string blockId, bool unstable = false)
        {
            var kind = FromBlockId(blockId);
            if (kind == DemoMaterial.Tnt && unstable) return UnstableTnt;
            return materials[(int)kind];
        }

        private static void ConfigureTransparent(Material m)
        {
            m.SetFloat("_Mode", 3);
            m.SetInt("_SrcBlend", (int)BlendMode.One);
            m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = (int)RenderQueue.Transparent;
        }

        private Texture2D BorderTexture()
        {
            var texture = Own(new Texture2D(32, 32, TextureFormat.RGBA32, false)
            {
                name = "Generated cell border",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat
            });
            // Standardシェーダは color * mainTexture を乗算するため、白背景に灰色枠で各ブロック固有色の暗い縁取りを作る。
            var borderColor = new Color(.62f, .62f, .62f, 1f);
            var bevelColor = new Color(.95f, .95f, .95f, 1f);
            var pixels = new Color[32 * 32];
            for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                if (x < 2 || y < 2 || x > 29 || y > 29)
                    pixels[y * 32 + x] = borderColor;
                else if (x == 2 || y == 2 || x == 29 || y == 29)
                    pixels[y * 32 + x] = bevelColor;
                else
                    pixels[y * 32 + x] = Color.white;
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private Texture2D GlassTexture()
        {
            var texture = Own(new Texture2D(32, 32, TextureFormat.RGBA32, false)
            {
                name = "Generated glass border",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat
            });
            var borderColor = new Color(.28f, .58f, .76f, .78f);
            var interiorColor = new Color(.48f, .85f, 1f, .34f);
            var pixels = new Color[32 * 32];
            for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                pixels[y * 32 + x] = (x < 2 || y < 2 || x > 29 || y > 29) ? borderColor : interiorColor;
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private Texture2D TntTexture()
        {
            var texture = Own(new Texture2D(32, 32, TextureFormat.RGBA32, false)
            {
                name = "Generated TNT",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat
            });
            var red = new Color(.84f, .18f, .14f);
            var darkRed = new Color(.65f, .12f, .1f);
            var band = new Color(.94f, .92f, .86f);
            var letterBorder = new Color(.16f, .12f, .12f);
            var edgeBorder = new Color(.48f, .10f, .08f);
            var pixels = new Color[32 * 32];
            for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                Color c = ((x / 4) % 2 == 0) ? red : darkRed;
                if (y >= 11 && y <= 20) c = band;
                if (y >= 13 && y <= 18 && ((x >= 5 && x <= 9) || (x >= 13 && x <= 18) || (x >= 22 && x <= 26)))
                    c = letterBorder;
                if (x < 2 || y < 2 || x > 29 || y > 29) c = edgeBorder;
                pixels[y * 32 + x] = c;
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private Texture2D DottedLineTexture()
        {
            var texture = Own(new Texture2D(32, 4, TextureFormat.RGBA32, false)
            {
                name = "Generated dotted guide",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat
            });
            var transparent = new Color(1f, 1f, 1f, 0f);
            var pixels = new Color[32 * 4];
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 32; x++)
            {
                pixels[y * 32 + x] = x < 16 ? Color.white : transparent;
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private T Own<T>(T value) where T : Object { owned.Add(value); return value; }

        public void Dispose()
        {
            foreach (var value in owned) Release(value);
            owned.Clear();
        }

        internal static void Release(Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Object.Destroy(value);
            else Object.DestroyImmediate(value);
        }
    }
}
