using UnityEngine;
using UnityEditor;
using System.Threading.Tasks;

namespace CityBuilder
{
    /// <summary>
    /// Genera texture PBR procedurali per il terreno basate su elevazione e pendenza.
    /// 4 layer: sabbia, erba mediterranea, terra/macchia, roccia.
    /// Texture 256x256 con FBM noise multi-ottava + normal map.
    /// </summary>
    public static class TerrainTextureGenerator
    {
        private const int TEX_SIZE = 256;
        private const float SAND_MAX_HEIGHT = 0.03f;
        private const float GRASS_MAX_HEIGHT = 0.6f;
        private const float ROCK_SLOPE = 35f;
        private const float DIRT_SLOPE = 20f;

        // Stile BotW: colori piu' saturi e painterly
        private static bool botWStyle = true;

        public static async Task ApplyProceduralTexturesAsync(
            Terrain[,] terrains, int gridCount, float seaLevelNorm)
        {
            if (terrains == null) return;

            EditorUtility.DisplayProgressBar("CityBuilder", "Generazione texture terreno...", 0.1f);

            // Buffer pixel condiviso — riusato da tutte le texture
            // (evita 8 allocazioni Color[] separate)
            Color[] sharedPixels = new Color[TEX_SIZE * TEX_SIZE];

            // Genera le 4 texture PBR
            var sandAlbedo = GenerateSandTexture(sharedPixels);
            var grassAlbedo = GenerateGrassTexture(sharedPixels);
            var dirtAlbedo = GenerateDirtTexture(sharedPixels);
            var rockAlbedo = GenerateRockTexture(sharedPixels);

            // Normal maps
            var sandNormal = GenerateNormalFromHeight(sandAlbedo, 1.5f, sharedPixels);
            var grassNormal = GenerateNormalFromHeight(grassAlbedo, 0.8f, sharedPixels);
            var dirtNormal = GenerateNormalFromHeight(dirtAlbedo, 2.0f, sharedPixels);
            var rockNormal = GenerateNormalFromHeight(rockAlbedo, 3.0f, sharedPixels);

            sharedPixels = null; // non serve piu'

            TerrainLayer sandLayer = new TerrainLayer
            {
                diffuseTexture = sandAlbedo, normalMapTexture = sandNormal,
                tileSize = new Vector2(8, 8), smoothness = 0.15f
            };
            TerrainLayer grassLayer = new TerrainLayer
            {
                diffuseTexture = grassAlbedo, normalMapTexture = grassNormal,
                tileSize = new Vector2(6, 6), smoothness = 0.1f
            };
            TerrainLayer dirtLayer = new TerrainLayer
            {
                diffuseTexture = dirtAlbedo, normalMapTexture = dirtNormal,
                tileSize = new Vector2(5, 5), smoothness = 0.05f
            };
            TerrainLayer rockLayer = new TerrainLayer
            {
                diffuseTexture = rockAlbedo, normalMapTexture = rockNormal,
                tileSize = new Vector2(4, 4), smoothness = 0.3f
            };

            TerrainLayer[] layers = { sandLayer, grassLayer, dirtLayer, rockLayer };

            int done = 0;
            int total = gridCount * gridCount;

            for (int gy = 0; gy < gridCount; gy++)
            {
                for (int gx = 0; gx < gridCount; gx++)
                {
                    Terrain terrain = terrains[gx, gy];
                    if (terrain == null) { done++; continue; }

                    EditorUtility.DisplayProgressBar("CityBuilder",
                        $"Splatmap terreno {gx},{gy}...", 0.3f + 0.7f * done / total);

                    terrain.terrainData.terrainLayers = layers;
                    GenerateSplatmap(terrain.terrainData, seaLevelNorm);

                    done++;
                    await Task.Yield();
                }
            }

            EditorUtility.ClearProgressBar();
            Debug.Log($"TerrainTextureGenerator: {done} tile texturate.");
        }

        // ================================================================
        //  TEXTURE GENERATORS (FBM multi-octave)
        // ================================================================

        private static Texture2D GenerateSandTexture(Color[] px)
        {
            var tex = new Texture2D(TEX_SIZE, TEX_SIZE, TextureFormat.RGB24, true);
            float ox = Random.Range(0f, 999f), oy = Random.Range(0f, 999f);

            for (int y = 0; y < TEX_SIZE; y++)
            for (int x = 0; x < TEX_SIZE; x++)
            {
                float fx = (float)x / TEX_SIZE, fy = (float)y / TEX_SIZE;
                float n = FBM(ox + fx, oy + fy, 3, 6f, 0.5f); // meno ottave = piu' morbido
                float v = 0.5f + n * 0.2f;

                // BotW sand: toni caldi dorati, meno dettaglio
                float r = Mathf.Lerp(0.78f, 0.92f, v);
                float g = Mathf.Lerp(0.68f, 0.82f, v);
                float b = Mathf.Lerp(0.48f, 0.60f, v);

                px[y * TEX_SIZE + x] = new Color(r, g, b);
            }

            tex.SetPixels(px);
            tex.Apply(true);
            tex.wrapMode = TextureWrapMode.Repeat;
            return tex;
        }

        private static Texture2D GenerateGrassTexture(Color[] px)
        {
            var tex = new Texture2D(TEX_SIZE, TEX_SIZE, TextureFormat.RGB24, true);
            float ox = Random.Range(0f, 999f), oy = Random.Range(0f, 999f);

            for (int y = 0; y < TEX_SIZE; y++)
            for (int x = 0; x < TEX_SIZE; x++)
            {
                float fx = (float)x / TEX_SIZE, fy = (float)y / TEX_SIZE;
                float n = FBM(ox + fx, oy + fy, 3, 5f, 0.5f);
                float patch = Mathf.PerlinNoise(ox + fx * 2.5f, oy + fy * 2.5f);
                float v = 0.5f + n * 0.25f;

                // BotW grass: verde saturo e luminoso con chiazze
                float r, g, b;
                if (patch < 0.4f)
                {
                    // Verde brillante (prato BotW)
                    r = Mathf.Lerp(0.22f, 0.35f, v);
                    g = Mathf.Lerp(0.55f, 0.72f, v);
                    b = Mathf.Lerp(0.12f, 0.22f, v);
                }
                else if (patch < 0.75f)
                {
                    // Verde medio
                    r = Mathf.Lerp(0.18f, 0.30f, v);
                    g = Mathf.Lerp(0.48f, 0.62f, v);
                    b = Mathf.Lerp(0.15f, 0.25f, v);
                }
                else
                {
                    // Verde scuro
                    r = Mathf.Lerp(0.12f, 0.22f, v);
                    g = Mathf.Lerp(0.38f, 0.52f, v);
                    b = Mathf.Lerp(0.10f, 0.18f, v);
                }

                // Fiorellini BotW (piccole macchie colorate)
                float flower = Mathf.PerlinNoise(ox + fx * 50f, oy + fy * 50f);
                if (flower > 0.90f)
                {
                    float fr = Mathf.PerlinNoise(ox + fx * 10f, oy + fy * 10f);
                    if (fr < 0.3f) { r = Mathf.Lerp(r, 0.9f, 0.5f); g *= 0.8f; } // rosso
                    else if (fr < 0.5f) { r = Mathf.Lerp(r, 0.85f, 0.4f); g = Mathf.Lerp(g, 0.8f, 0.3f); } // giallo
                    else if (fr < 0.7f) { b = Mathf.Lerp(b, 0.7f, 0.4f); } // viola
                    else { r = Mathf.Lerp(r, 0.95f, 0.3f); b = Mathf.Lerp(b, 0.6f, 0.3f); } // rosa
                }

                px[y * TEX_SIZE + x] = new Color(r, g, b);
            }

            tex.SetPixels(px);
            tex.Apply(true);
            tex.wrapMode = TextureWrapMode.Repeat;
            return tex;
        }

        private static Texture2D GenerateDirtTexture(Color[] px)
        {
            var tex = new Texture2D(TEX_SIZE, TEX_SIZE, TextureFormat.RGB24, true);
            float ox = Random.Range(0f, 999f), oy = Random.Range(0f, 999f);

            for (int y = 0; y < TEX_SIZE; y++)
            for (int x = 0; x < TEX_SIZE; x++)
            {
                float fx = (float)x / TEX_SIZE, fy = (float)y / TEX_SIZE;
                float n = FBM(ox + fx, oy + fy, 3, 6f, 0.5f);
                float v = 0.5f + n * 0.25f;

                // BotW dirt: toni caldi, terra arancione-marrone
                float r = Mathf.Lerp(0.45f, 0.60f, v);
                float g = Mathf.Lerp(0.32f, 0.45f, v);
                float b = Mathf.Lerp(0.20f, 0.30f, v);

                px[y * TEX_SIZE + x] = new Color(r, g, b);
            }

            tex.SetPixels(px);
            tex.Apply(true);
            tex.wrapMode = TextureWrapMode.Repeat;
            return tex;
        }

        private static Texture2D GenerateRockTexture(Color[] px)
        {
            var tex = new Texture2D(TEX_SIZE, TEX_SIZE, TextureFormat.RGB24, true);
            float ox = Random.Range(0f, 999f), oy = Random.Range(0f, 999f);

            for (int y = 0; y < TEX_SIZE; y++)
            for (int x = 0; x < TEX_SIZE; x++)
            {
                float fx = (float)x / TEX_SIZE, fy = (float)y / TEX_SIZE;
                float n = FBM(ox + fx, oy + fy, 4, 5f, 0.5f);
                float v = 0.45f + n * 0.3f;

                // BotW rock: grigio-beige caldo, chiazze colorate
                float warmth = Mathf.PerlinNoise(ox + fx * 3f, oy + fy * 3f);
                float r, g, b;

                if (warmth < 0.45f)
                {
                    // Grigio azzurro (BotW cool rock)
                    r = Mathf.Lerp(0.48f, 0.62f, v);
                    g = Mathf.Lerp(0.48f, 0.62f, v);
                    b = Mathf.Lerp(0.52f, 0.68f, v);
                }
                else
                {
                    // Beige caldo (BotW warm rock)
                    r = Mathf.Lerp(0.52f, 0.68f, v);
                    g = Mathf.Lerp(0.48f, 0.62f, v);
                    b = Mathf.Lerp(0.40f, 0.52f, v);
                }

                // Muschio verde in BotW sulle rocce
                float moss = Mathf.PerlinNoise(ox + fx * 4f + 500f, oy + fy * 4f + 500f);
                if (moss > 0.65f)
                {
                    float mt = (moss - 0.65f) / 0.35f * 0.3f;
                    r = Mathf.Lerp(r, 0.30f, mt);
                    g = Mathf.Lerp(g, 0.50f, mt);
                    b = Mathf.Lerp(b, 0.20f, mt);
                }

                px[y * TEX_SIZE + x] = new Color(
                    Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b));
            }

            tex.SetPixels(px);
            tex.Apply(true);
            tex.wrapMode = TextureWrapMode.Repeat;
            return tex;
        }

        // ================================================================
        //  NORMAL MAP FROM HEIGHTMAP
        // ================================================================

        private static Texture2D GenerateNormalFromHeight(Texture2D source, float strength, Color[] buffer)
        {
            int w = source.width, h = source.height;
            var normal = new Texture2D(w, h, TextureFormat.RGBA32, true);
            Color[] src = source.GetPixels();
            // Riusa il buffer condiviso come destinazione
            Color[] dst = buffer;

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float l = Luminance(src[y * w + ((x - 1 + w) % w)]);
                float r = Luminance(src[y * w + ((x + 1) % w)]);
                float d = Luminance(src[((y - 1 + h) % h) * w + x]);
                float u = Luminance(src[((y + 1) % h) * w + x]);

                Vector3 n = new Vector3((l - r) * strength, (d - u) * strength, 1f).normalized;

                // Encode da [-1,1] a [0,1]
                dst[y * w + x] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
            }

            normal.SetPixels(dst);
            normal.Apply(true);
            normal.wrapMode = TextureWrapMode.Repeat;
            return normal;
        }

        private static float Luminance(Color c) => c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;

        // ================================================================
        //  FBM (Fractal Brownian Motion)
        // ================================================================

        /// <summary>
        /// Rumore frattale multi-ottava. Produce pattern naturali molto piu
        /// convincenti del semplice Perlin singolo.
        /// </summary>
        private static float FBM(float x, float y, int octaves, float frequency, float persistence)
        {
            float value = 0f, amplitude = 1f, maxAmp = 0f;
            for (int i = 0; i < octaves; i++)
            {
                value += Mathf.PerlinNoise(x * frequency, y * frequency) * amplitude;
                maxAmp += amplitude;
                amplitude *= persistence;
                frequency *= 2f;
            }
            return value / maxAmp;
        }

        // ================================================================
        //  SPLATMAP
        // ================================================================

        private static void GenerateSplatmap(TerrainData td, float seaLevelNorm)
        {
            int alphaW = td.alphamapWidth;
            int alphaH = td.alphamapHeight;
            int hmW = td.heightmapResolution;
            int hmH = td.heightmapResolution;

            float[,] heights = td.GetHeights(0, 0, hmW, hmH);
            float[,,] alphas = new float[alphaH, alphaW, 4];

            float terrainH = td.size.y;
            float cellX = td.size.x / (hmW - 1);
            float cellZ = td.size.z / (hmH - 1);

            for (int ay = 0; ay < alphaH; ay++)
            for (int ax = 0; ax < alphaW; ax++)
            {
                float hmX = (float)ax / (alphaW - 1) * (hmW - 1);
                float hmY = (float)ay / (alphaH - 1) * (hmH - 1);
                int hx = Mathf.Clamp((int)hmX, 1, hmW - 2);
                int hy = Mathf.Clamp((int)hmY, 1, hmH - 2);

                float h = heights[hy, hx];
                float relH = h - seaLevelNorm;

                // Pendenza
                float dhdx = (heights[hy, hx + 1] - heights[hy, hx - 1]) * terrainH / (2 * cellX);
                float dhdz = (heights[hy + 1, hx] - heights[hy - 1, hx]) * terrainH / (2 * cellZ);
                float slope = Mathf.Atan(Mathf.Sqrt(dhdx * dhdx + dhdz * dhdz)) * Mathf.Rad2Deg;

                // Rumore per bordi naturali (non lineari)
                float noise = Mathf.PerlinNoise(ax * 0.15f, ay * 0.15f) * 0.04f;

                float sand = 0, grass = 0, dirt = 0, rock = 0;

                // Sotto il livello del mare: fondale (dirt scuro) — verra' coperto dall'acqua
                if (relH < -0.002f)
                {
                    dirt = 1f;
                }
                // Fascia costiera: sabbia che sfuma in erba
                else if (relH < SAND_MAX_HEIGHT + noise && slope < 12f)
                {
                    sand = 1f - Mathf.Clamp01(relH / SAND_MAX_HEIGHT);
                    grass = 1f - sand;
                }
                else if (slope > ROCK_SLOPE + noise * 100f)
                {
                    float t = Mathf.Clamp01((slope - ROCK_SLOPE) / 15f);
                    rock = t;
                    dirt = (1f - t) * 0.4f;
                    grass = (1f - t) * 0.6f;
                }
                else if (slope > DIRT_SLOPE + noise * 50f)
                {
                    float t = Mathf.Clamp01((slope - DIRT_SLOPE) / (ROCK_SLOPE - DIRT_SLOPE));
                    dirt = t;
                    grass = 1f - t;
                }
                else if (relH > GRASS_MAX_HEIGHT + noise)
                {
                    float t = Mathf.Clamp01((relH - GRASS_MAX_HEIGHT) / (1f - GRASS_MAX_HEIGHT));
                    rock = t * 0.5f;
                    dirt = t * 0.3f;
                    grass = 1f - t * 0.8f;
                }
                else
                {
                    grass = 1f;
                }

                float total = sand + grass + dirt + rock;
                if (total > 0.001f)
                {
                    alphas[ay, ax, 0] = sand / total;
                    alphas[ay, ax, 1] = grass / total;
                    alphas[ay, ax, 2] = dirt / total;
                    alphas[ay, ax, 3] = rock / total;
                }
                else
                {
                    alphas[ay, ax, 1] = 1f;
                }
            }

            td.SetAlphamaps(0, 0, alphas);
        }
    }
}
