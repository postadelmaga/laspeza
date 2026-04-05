using UnityEngine;
using UnityEditor;
using UnityEngine.Networking;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace CityBuilder
{
    public static class SatelliteTextureLoader
    {
        private const string TileUrlPattern =
            "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{0}/{1}/{2}";
        private const int SlippyTileSize = 256;

        private static string CacheDir
        {
            get
            {
                string dir = Path.Combine(
                    Path.GetDirectoryName(Application.dataPath), "DATA", "satellite_cache");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                return dir;
            }
        }

        // ───────────────────────── Slippy-map tile math ─────────────────────────

        private static int LonToTileX(double lon, int zoom)
        {
            return (int)Math.Floor((lon + 180.0) / 360.0 * (1 << zoom));
        }

        private static int LatToTileY(double lat, int zoom)
        {
            double latRad = lat * Math.PI / 180.0;
            return (int)Math.Floor(
                (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0
                * (1 << zoom));
        }

        /// <summary>
        /// Inverse: tile X left-edge longitude.
        /// </summary>
        private static double TileXToLon(int x, int zoom)
        {
            return x / (double)(1 << zoom) * 360.0 - 180.0;
        }

        /// <summary>
        /// Inverse: tile Y top-edge latitude.
        /// </summary>
        private static double TileYToLat(int y, int zoom)
        {
            double n = Math.PI - 2.0 * Math.PI * y / (1 << zoom);
            return 180.0 / Math.PI * Math.Atan(0.5 * (Math.Exp(n) - Math.Exp(-n)));
        }

        // ──────────────────────── Download helpers ──────────────────────────────

        /// <summary>
        /// Downloads a single tile, using disk cache when available.
        /// Returns raw JPEG bytes or null on failure.
        /// </summary>
        private static async Task<byte[]> DownloadTileAsync(int zoom, int tileY, int tileX)
        {
            string cachePath = Path.Combine(CacheDir, $"{zoom}_{tileY}_{tileX}.jpg");

            if (File.Exists(cachePath))
                return File.ReadAllBytes(cachePath);

            string url = string.Format(TileUrlPattern, zoom, tileY, tileX);
            var request = UnityWebRequest.Get(url);
            var op = request.SendWebRequest();

            while (!op.isDone)
                await Task.Yield();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"Satellite tile download failed [{zoom}/{tileY}/{tileX}]: {request.error}");
                request.Dispose();
                return null;
            }

            byte[] data = request.downloadHandler.data;
            request.Dispose();

            try { File.WriteAllBytes(cachePath, data); }
            catch (Exception e) { Debug.LogWarning($"Cache write failed: {e.Message}"); }

            return data;
        }

        // ────────────────────── Texture stitching ───────────────────────────────

        /// <summary>
        /// Downloads and stitches all slippy-map tiles covering the given GPS extent
        /// into a single Texture2D.
        /// </summary>
        private static async Task<Texture2D> BuildTextureForExtent(
            double minLon, double maxLon, double minLat, double maxLat, int zoom)
        {
            // Tile indices covering the bounding box.
            // Note: in slippy-map, lower latitude → higher Y index.
            int txMin = LonToTileX(minLon, zoom);
            int txMax = LonToTileX(maxLon, zoom);
            int tyMin = LatToTileY(maxLat, zoom); // top edge = higher lat → smaller Y
            int tyMax = LatToTileY(minLat, zoom);  // bottom edge = lower lat → larger Y

            int cols = txMax - txMin + 1;
            int rows = tyMax - tyMin + 1;

            // Download all tiles in parallel
            var tasks = new Dictionary<(int, int), Task<byte[]>>();
            for (int ty = tyMin; ty <= tyMax; ty++)
                for (int tx = txMin; tx <= txMax; tx++)
                    tasks[(tx, ty)] = DownloadTileAsync(zoom, ty, tx);

            await Task.WhenAll(tasks.Values);

            // Decode each tile into a Texture2D
            var tileTex = new Dictionary<(int, int), Texture2D>();
            foreach (var kv in tasks)
            {
                byte[] jpg = kv.Value.Result;
                if (jpg == null || jpg.Length == 0) continue;
                var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (tex.LoadImage(jpg))
                    tileTex[kv.Key] = tex;
                else
                    UnityEngine.Object.DestroyImmediate(tex);
            }

            int fullW = cols * SlippyTileSize;
            int fullH = rows * SlippyTileSize;
            var merged = new Texture2D(fullW, fullH, TextureFormat.RGB24, false);

            // Fill with dark grey fallback (ocean / missing tiles)
            var fallback = new Color32(40, 50, 55, 255);
            var pixels = new Color32[fullW * fullH];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = fallback;
            merged.SetPixels32(pixels);

            // Blit each tile into the merged texture.
            // Slippy Y increases downward, but Texture2D Y=0 is bottom → flip row.
            foreach (var kv in tileTex)
            {
                int col = kv.Key.Item1 - txMin;
                int row = kv.Key.Item2 - tyMin;
                int pixelX = col * SlippyTileSize;
                int pixelY = (rows - 1 - row) * SlippyTileSize; // flip Y
                var src = kv.Value;

                // Some tiles may not be exactly 256x256 after JPEG decode
                int copyW = Mathf.Min(src.width, SlippyTileSize);
                int copyH = Mathf.Min(src.height, SlippyTileSize);

                Color32[] srcPixels = src.GetPixels32();
                for (int sy = 0; sy < copyH; sy++)
                {
                    for (int sx = 0; sx < copyW; sx++)
                    {
                        int dstX = pixelX + sx;
                        int dstY = pixelY + sy;
                        if (dstX < fullW && dstY < fullH)
                            pixels[dstY * fullW + dstX] = srcPixels[sy * src.width + sx];
                    }
                }

                UnityEngine.Object.DestroyImmediate(src);
            }

            merged.SetPixels32(pixels);

            // Now crop/resample to only the exact GPS extent requested.
            // The full texture covers the tile grid which is slightly larger than the request.
            double gridMinLon = TileXToLon(txMin, zoom);
            double gridMaxLon = TileXToLon(txMax + 1, zoom);
            double gridMaxLat = TileYToLat(tyMin, zoom);     // top of top-row tile
            double gridMinLat = TileYToLat(tyMax + 1, zoom); // bottom of bottom-row tile

            // UV coordinates of the requested extent within the full grid texture
            float uMin = (float)((minLon - gridMinLon) / (gridMaxLon - gridMinLon));
            float uMax = (float)((maxLon - gridMinLon) / (gridMaxLon - gridMinLon));
            float vMin = (float)((minLat - gridMinLat) / (gridMaxLat - gridMinLat));
            float vMax = (float)((maxLat - gridMinLat) / (gridMaxLat - gridMinLat));

            int cropX = Mathf.Clamp(Mathf.FloorToInt(uMin * fullW), 0, fullW - 1);
            int cropY = Mathf.Clamp(Mathf.FloorToInt(vMin * fullH), 0, fullH - 1);
            int cropW = Mathf.Clamp(Mathf.CeilToInt((uMax - uMin) * fullW), 1, fullW - cropX);
            int cropH = Mathf.Clamp(Mathf.CeilToInt((vMax - vMin) * fullH), 1, fullH - cropY);

            Color[] croppedPixels = merged.GetPixels(cropX, cropY, cropW, cropH);
            UnityEngine.Object.DestroyImmediate(merged);

            var result = new Texture2D(cropW, cropH, TextureFormat.RGB24, false);
            result.SetPixels(croppedPixels);
            result.Apply(true);
            result.wrapMode = TextureWrapMode.Clamp;
            return result;
        }

        // ────────────────────── Public API ──────────────────────────────────────

        /// <summary>
        /// Downloads ESRI World Imagery tiles for each terrain tile and applies them
        /// as TerrainLayers, replacing the default green texture.
        /// </summary>
        /// <param name="terrains">Grid of Unity Terrain objects [gx, gy].</param>
        /// <param name="minLon">Western longitude of the full terrain extent.</param>
        /// <param name="maxLon">Eastern longitude of the full terrain extent.</param>
        /// <param name="minLat">Southern latitude of the full terrain extent.</param>
        /// <param name="maxLat">Northern latitude of the full terrain extent.</param>
        /// <param name="gridCount">Number of tiles along each axis.</param>
        /// <param name="zoomLevel">Slippy-map zoom level (15-18). 16 ≈ 2.4m/px.</param>
        public static async Task ApplySatelliteTextureAsync(
            Terrain[,] terrains,
            float minLon, float maxLon, float minLat, float maxLat,
            int gridCount, int zoomLevel = 16)
        {
            if (terrains == null)
            {
                Debug.LogError("SatelliteTextureLoader: terrains array is null.");
                return;
            }

            double lonStep = (maxLon - minLon) / (double)gridCount;
            double latStep = (maxLat - minLat) / (double)gridCount;

            int total = gridCount * gridCount;
            int done = 0;

            for (int gy = 0; gy < gridCount; gy++)
            {
                for (int gx = 0; gx < gridCount; gx++)
                {
                    Terrain terrain = terrains[gx, gy];
                    if (terrain == null) { done++; continue; }

                    EditorUtility.DisplayProgressBar(
                        "CityBuilder — Satellite Imagery",
                        $"Downloading tiles for terrain [{gx},{gy}]...",
                        (float)done / total);

                    // GPS extent of this specific terrain tile
                    double tileMinLon = minLon + gx * lonStep;
                    double tileMaxLon = minLon + (gx + 1) * lonStep;
                    double tileMinLat = minLat + gy * latStep;
                    double tileMaxLat = minLat + (gy + 1) * latStep;

                    try
                    {
                        Texture2D satTex = await BuildTextureForExtent(
                            tileMinLon, tileMaxLon, tileMinLat, tileMaxLat, zoomLevel);

                        if (satTex == null)
                        {
                            Debug.LogWarning($"SatelliteTextureLoader: no texture for tile [{gx},{gy}]");
                            done++;
                            continue;
                        }

                        satTex.name = $"SatelliteTex_{gx}_{gy}";

                        TerrainData td = terrain.terrainData;
                        Vector3 size = td.size;

                        var layer = new TerrainLayer
                        {
                            diffuseTexture = satTex,
                            // tileSize == terrain size → texture stretches exactly once over the tile
                            tileSize = new Vector2(size.x, size.z),
                            tileOffset = Vector2.zero
                        };

                        td.terrainLayers = new TerrainLayer[] { layer };

                        // Set splat to 100% first layer everywhere
                        int alphaW = td.alphamapWidth;
                        int alphaH = td.alphamapHeight;
                        float[,,] alphas = new float[alphaH, alphaW, 1];
                        for (int ay = 0; ay < alphaH; ay++)
                            for (int ax = 0; ax < alphaW; ax++)
                                alphas[ay, ax, 0] = 1f;
                        td.SetAlphamaps(0, 0, alphas);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"SatelliteTextureLoader: tile [{gx},{gy}] failed: {e.Message}");
                    }

                    done++;
                    await Task.Yield();
                }
            }

            EditorUtility.ClearProgressBar();
            Debug.Log($"SatelliteTextureLoader: finished — {done} terrain tiles textured (zoom {zoomLevel}).");
        }
    }
}
