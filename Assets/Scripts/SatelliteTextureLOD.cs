using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// LOD texture satellite a runtime: zoom basso ovunque, zoom alto solo vicino alla camera.
/// Massimo N tile ad alta risoluzione caricate simultaneamente (LRU eviction).
///
/// Aggiungere al GameObject CityBuilder_World dopo la generazione.
/// SceneSetup lo aggiunge automaticamente.
/// </summary>
public class SatelliteTextureLOD : MonoBehaviour
{
    [Header("Zoom Levels")]
    [Tooltip("Zoom base (sempre caricato, leggero)")]
    public int baseZoom = 14;
    [Tooltip("Zoom alto (caricato vicino alla camera)")]
    public int highZoom = 17;

    [Header("Distanze")]
    [Tooltip("Distanza sotto la quale caricare il zoom alto")]
    public float highResDistance = 300f;
    [Tooltip("Distanza sopra la quale scaricare il zoom alto (isteresi)")]
    public float unloadDistance = 500f;

    [Header("Limiti memoria")]
    [Tooltip("Massimo tile ad alta risoluzione in memoria")]
    public int maxHighResTiles = 4;

    [Header("Aggiornamento")]
    public float checkInterval = 1.0f;

    // Tile server
    private const string TileUrl = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{0}/{1}/{2}";
    private const int TilePixels = 256;

    // Stato per tile terreno
    private class TerrainTileState
    {
        public Terrain terrain;
        public int gx, gy;
        public double minLon, maxLon, minLat, maxLat;
        public Texture2D baseTex;      // zoom basso (sempre in memoria)
        public Texture2D highTex;      // zoom alto (on-demand)
        public TerrainLayer baseLayer;
        public TerrainLayer highLayer;
        public bool isHighRes;
        public float lastAccessTime;
        public bool loading;
    }

    private List<TerrainTileState> tiles = new List<TerrainTileState>();
    private string cacheDir;
    private Transform cam;

    /// <summary>
    /// Inizializza il sistema LOD. Chiamato da SceneSetup o manualmente.
    /// </summary>
    public void Initialize(Terrain[,] terrains, int gridCount,
        float minLon, float maxLon, float minLat, float maxLat)
    {
        cacheDir = Path.Combine(Application.dataPath, "..", "DATA", "satellite_cache");
        Directory.CreateDirectory(cacheDir);

        double lonStep = (maxLon - minLon) / (double)gridCount;
        double latStep = (maxLat - minLat) / (double)gridCount;

        for (int gy = 0; gy < gridCount; gy++)
        {
            for (int gx = 0; gx < gridCount; gx++)
            {
                if (terrains[gx, gy] == null) continue;

                var state = new TerrainTileState
                {
                    terrain = terrains[gx, gy],
                    gx = gx, gy = gy,
                    minLon = minLon + gx * lonStep,
                    maxLon = minLon + (gx + 1) * lonStep,
                    minLat = minLat + gy * latStep,
                    maxLat = minLat + (gy + 1) * latStep,
                };

                // Salva la texture base attuale (gia applicata dal loader)
                if (state.terrain.terrainData.terrainLayers.Length > 0)
                {
                    state.baseLayer = state.terrain.terrainData.terrainLayers[0];
                    state.baseTex = state.baseLayer?.diffuseTexture;
                }

                tiles.Add(state);
            }
        }

        Debug.Log($"SatelliteTextureLOD: {tiles.Count} tile, base zoom {baseZoom}, high zoom {highZoom}, max {maxHighResTiles} HR");
    }

    void Start()
    {
        cam = Camera.main?.transform;
        StartCoroutine(LODUpdateLoop());
    }

    IEnumerator LODUpdateLoop()
    {
        // Aspetta che la scena si stabilizzi
        yield return new WaitForSeconds(0.5f);

        while (true)
        {
            yield return new WaitForSeconds(checkInterval);

            if (cam == null) { cam = Camera.main?.transform; continue; }
            if (tiles.Count == 0) continue;

            Vector3 camPos = cam.position;

            // Calcola distanza camera da ogni tile
            foreach (var t in tiles)
            {
                if (t.terrain == null) continue;

                Vector3 center = t.terrain.transform.position +
                    t.terrain.terrainData.size * 0.5f;
                center.y = camPos.y; // distanza orizzontale
                float dist = Vector3.Distance(camPos, center);

                if (dist < highResDistance && !t.isHighRes && !t.loading)
                {
                    // Vicino: carica alta risoluzione
                    t.lastAccessTime = Time.time;
                    StartCoroutine(LoadHighRes(t));
                }
                else if (dist > unloadDistance && t.isHighRes)
                {
                    // Lontano: scarica
                    UnloadHighRes(t);
                }
                else if (t.isHighRes)
                {
                    t.lastAccessTime = Time.time;
                }
            }

            // Evict se troppe tile HR caricate
            EnforceLRULimit();
        }
    }

    IEnumerator LoadHighRes(TerrainTileState t)
    {
        t.loading = true;

        // Usa un wrapper array per ottenere il risultato dalla coroutine
        Texture2D[] result = new Texture2D[1];
        yield return StartCoroutine(
            BuildTextureCoroutine(t.minLon, t.maxLon, t.minLat, t.maxLat, highZoom, result));

        Texture2D tex = result[0];
        if (tex != null && t.terrain != null)
        {
            t.highTex = tex;
            t.highTex.name = $"SatHR_{t.gx}_{t.gy}_z{highZoom}";

            Vector3 size = t.terrain.terrainData.size;
            t.highLayer = new TerrainLayer
            {
                diffuseTexture = tex,
                tileSize = new Vector2(size.x, size.z),
                tileOffset = Vector2.zero
            };

            t.terrain.terrainData.terrainLayers = new TerrainLayer[] { t.highLayer };
            SetFullAlphamap(t.terrain.terrainData);
            t.isHighRes = true;
        }

        t.loading = false;
    }

    void UnloadHighRes(TerrainTileState t)
    {
        if (!t.isHighRes) return;

        // Ripristina texture base
        if (t.baseLayer != null && t.terrain != null)
        {
            t.terrain.terrainData.terrainLayers = new TerrainLayer[] { t.baseLayer };
            SetFullAlphamap(t.terrain.terrainData);
        }

        // Libera memoria
        if (t.highTex != null)
        {
            Destroy(t.highTex);
            t.highTex = null;
        }
        t.highLayer = null;
        t.isHighRes = false;
    }

    void EnforceLRULimit()
    {
        // Conta tile HR attive
        var hrTiles = new List<TerrainTileState>();
        foreach (var t in tiles)
            if (t.isHighRes) hrTiles.Add(t);

        while (hrTiles.Count > maxHighResTiles)
        {
            // Trova la tile HR meno recente
            TerrainTileState oldest = hrTiles[0];
            for (int i = 1; i < hrTiles.Count; i++)
                if (hrTiles[i].lastAccessTime < oldest.lastAccessTime)
                    oldest = hrTiles[i];

            UnloadHighRes(oldest);
            hrTiles.Remove(oldest);
        }
    }

    // ================================================================
    //  DOWNLOAD + STITCH (runtime coroutine)
    // ================================================================

    IEnumerator BuildTextureCoroutine(double minLon, double maxLon, double minLat, double maxLat, int zoom, Texture2D[] outResult)
    {
        int txMin = LonToTileX(minLon, zoom);
        int txMax = LonToTileX(maxLon, zoom);
        int tyMin = LatToTileY(maxLat, zoom);
        int tyMax = LatToTileY(minLat, zoom);

        int cols = txMax - txMin + 1;
        int rows = tyMax - tyMin + 1;

        // Download tile one by one (runtime-friendly, no burst)
        var tileData = new Dictionary<(int, int), byte[]>();
        for (int ty = tyMin; ty <= tyMax; ty++)
        {
            for (int tx = txMin; tx <= txMax; tx++)
            {
                byte[] data = null;
                string cachePath = Path.Combine(cacheDir, $"{zoom}_{ty}_{tx}.jpg");

                if (File.Exists(cachePath))
                {
                    data = File.ReadAllBytes(cachePath);
                }
                else
                {
                    string url = string.Format(TileUrl, zoom, ty, tx);
                    using (var req = UnityWebRequest.Get(url))
                    {
                        yield return req.SendWebRequest();
                        if (req.result == UnityWebRequest.Result.Success)
                        {
                            data = req.downloadHandler.data;
                            try { File.WriteAllBytes(cachePath, data); } catch { }
                        }
                    }
                }

                if (data != null) tileData[(tx, ty)] = data;
                yield return null; // frame break
            }
        }

        // Stitch
        int fullW = cols * TilePixels;
        int fullH = rows * TilePixels;
        var pixels = new Color32[fullW * fullH];
        Color32 fallback = new Color32(40, 50, 55, 255);
        for (int i = 0; i < pixels.Length; i++) pixels[i] = fallback;

        foreach (var kv in tileData)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            if (!tex.LoadImage(kv.Value)) { Destroy(tex); continue; }

            int col = kv.Key.Item1 - txMin;
            int row = kv.Key.Item2 - tyMin;
            int px = col * TilePixels;
            int py = (rows - 1 - row) * TilePixels;

            Color32[] src = tex.GetPixels32();
            int sw = tex.width, sh = tex.height;
            Destroy(tex);

            for (int sy = 0; sy < Mathf.Min(sh, TilePixels); sy++)
                for (int sx = 0; sx < Mathf.Min(sw, TilePixels); sx++)
                {
                    int dx = px + sx, dy = py + sy;
                    if (dx < fullW && dy < fullH)
                        pixels[dy * fullW + dx] = src[sy * sw + sx];
                }
        }

        // Crop to exact extent
        double gMinLon = TileXToLon(txMin, zoom);
        double gMaxLon = TileXToLon(txMax + 1, zoom);
        double gMaxLat = TileYToLat(tyMin, zoom);
        double gMinLat = TileYToLat(tyMax + 1, zoom);

        float uMin = (float)((minLon - gMinLon) / (gMaxLon - gMinLon));
        float uMax = (float)((maxLon - gMinLon) / (gMaxLon - gMinLon));
        float vMin = (float)((minLat - gMinLat) / (gMaxLat - gMinLat));
        float vMax = (float)((maxLat - gMinLat) / (gMaxLat - gMinLat));

        int cx = Mathf.Clamp(Mathf.FloorToInt(uMin * fullW), 0, fullW - 1);
        int cy = Mathf.Clamp(Mathf.FloorToInt(vMin * fullH), 0, fullH - 1);
        int cw = Mathf.Clamp(Mathf.CeilToInt((uMax - uMin) * fullW), 1, fullW - cx);
        int ch = Mathf.Clamp(Mathf.CeilToInt((vMax - vMin) * fullH), 1, fullH - cy);

        var merged = new Texture2D(fullW, fullH, TextureFormat.RGB24, false);
        merged.SetPixels32(pixels);

        Color[] cropped = merged.GetPixels(cx, cy, cw, ch);
        Destroy(merged);

        var result = new Texture2D(cw, ch, TextureFormat.RGB24, false);
        result.SetPixels(cropped);
        result.Apply(true);
        result.wrapMode = TextureWrapMode.Clamp;

        outResult[0] = result;
    }

    // ================================================================
    //  UTILITY
    // ================================================================

    static void SetFullAlphamap(TerrainData td)
    {
        int w = td.alphamapWidth, h = td.alphamapHeight;
        float[,,] a = new float[h, w, 1];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                a[y, x, 0] = 1f;
        td.SetAlphamaps(0, 0, a);
    }

    static int LonToTileX(double lon, int z) =>
        (int)Math.Floor((lon + 180.0) / 360.0 * (1 << z));

    static int LatToTileY(double lat, int z)
    {
        double r = lat * Math.PI / 180.0;
        return (int)Math.Floor((1.0 - Math.Log(Math.Tan(r) + 1.0 / Math.Cos(r)) / Math.PI) / 2.0 * (1 << z));
    }

    static double TileXToLon(int x, int z) =>
        x / (double)(1 << z) * 360.0 - 180.0;

    static double TileYToLat(int y, int z)
    {
        double n = Math.PI - 2.0 * Math.PI * y / (1 << z);
        return 180.0 / Math.PI * Math.Atan(0.5 * (Math.Exp(n) - Math.Exp(-n)));
    }

    void OnDestroy()
    {
        // Libera tutte le texture HR
        foreach (var t in tiles)
        {
            if (t.highTex != null) Destroy(t.highTex);
        }
    }
}
