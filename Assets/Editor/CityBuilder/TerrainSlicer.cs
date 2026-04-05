using UnityEngine;
using UnityEditor;
using System.IO;
using System.Threading.Tasks;

namespace CityBuilder
{
    public static class TerrainSlicer
    {
        // Risoluzioni heightmap valide per Unity: (2^n)+1
        private static readonly int[] ValidResolutions = { 33, 65, 129, 257, 513, 1025, 2049, 4097 };

        /// <summary>
        /// Trova la risoluzione heightmap Unity valida ottimale per ogni tile.
        /// Sceglie la piu alta che non supera i pixel sorgente disponibili per tile.
        /// </summary>
        private static int FindBestTileResolution(int totalRes, int gridCount)
        {
            int pixelsPerTile = (totalRes - 1) / gridCount;
            int best = ValidResolutions[0];
            foreach (int r in ValidResolutions)
            {
                if (r - 1 <= pixelsPerTile) best = r;
                else break;
            }
            return best;
        }

        /// <summary>
        /// Interpolazione bicubica (Catmull-Rom) — preserva meglio creste e valli rispetto al bilinear.
        /// </summary>
        private static float SampleBicubic(float[,] data, float y, float x, int maxRes)
        {
            int xi = (int)x;
            int yi = (int)y;
            float fx = x - xi;
            float fy = y - yi;

            float result = 0;
            for (int m = -1; m <= 2; m++)
            {
                int sy = Mathf.Clamp(yi + m, 0, maxRes - 1);
                float wy = CubicWeight(m - fy);

                float rowVal = 0;
                for (int n = -1; n <= 2; n++)
                {
                    int sx = Mathf.Clamp(xi + n, 0, maxRes - 1);
                    rowVal += data[sy, sx] * CubicWeight(n - fx);
                }
                result += rowVal * wy;
            }
            return Mathf.Clamp01(result);
        }

        /// <summary>
        /// Peso Catmull-Rom: buon compromesso tra sharpness e smooth.
        /// </summary>
        private static float CubicWeight(float t)
        {
            float at = Mathf.Abs(t);
            if (at <= 1f) return (1.5f * at - 2.5f) * at * at + 1f;
            if (at < 2f) return ((-0.5f * at + 2.5f) * at - 4f) * at + 2f;
            return 0f;
        }

        public static async Task<Terrain[,]> BuildSmartTerrainAsync(string rawPath, TerrainMetaData tMeta, int gridCount, bool useAutoCrop, GameObject worldParent, System.Action<float, float> setCropBounds)
        {
            if (tMeta == null || !File.Exists(rawPath)) return null;

            // Risoluzione dal metadata (adattata alla sorgente da Python)
            int res = tMeta.rawResolution > 0 ? tMeta.rawResolution : 4097;
            int expectedBytes = res * res * 4; // float32 = 4 bytes per pixel

            // Verifica dimensione file senza caricare tutto
            long fileSize = new System.IO.FileInfo(rawPath).Length;
            if (fileSize != expectedBytes)
            {
                UnityEngine.Debug.LogError($"File RAW dimensione errata: atteso {expectedBytes} bytes ({res}x{res} float32), trovato {fileSize}");
                return null;
            }

            // Carica heightmap riga per riga per ridurre il picco di memoria
            // (evita di avere byte[] + float[,] contemporaneamente)
            float[,] heights = new float[res, res];

            int cropMinX = res, cropMaxX = 0, cropMinY = res, cropMaxY = 0;
            EditorUtility.DisplayProgressBar("CityBuilder", "Analisi topologica...", 0.4f);

            float baselineThreshold = tMeta.seaLevelNorm + 0.0005f;

            int rowBytes = res * 4;
            byte[] rowBuffer = new byte[rowBytes];

            using (var fs = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.Read, rowBytes))
            {
                for (int y = 0; y < res; y++)
                {
                    fs.Read(rowBuffer, 0, rowBytes);
                    for (int x = 0; x < res; x++)
                    {
                        float h = System.BitConverter.ToSingle(rowBuffer, x * 4);
                        heights[y, x] = h;
                        if (h > baselineThreshold)
                        {
                            if (x < cropMinX) cropMinX = x;
                            if (x > cropMaxX) cropMaxX = x;
                            if (y < cropMinY) cropMinY = y;
                            if (y > cropMaxY) cropMaxY = y;
                        }
                    }
                }
            }
            rowBuffer = null;

            // Smoothing pass: attenua i picchi per un look più morbido (stile BotW)
            // Applica un box blur 3x3 solo ai pixel sopra il livello del mare
            // per non sfumare la linea costiera
            EditorUtility.DisplayProgressBar("CityBuilder", "Smoothing terreno...", 0.45f);
            {
                float[,] smoothed = new float[res, res];
                for (int y = 0; y < res; y++)
                {
                    for (int x = 0; x < res; x++)
                    {
                        if (heights[y, x] <= baselineThreshold)
                        {
                            smoothed[y, x] = heights[y, x];
                            continue;
                        }
                        float sum = 0; int count = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int ny = Mathf.Clamp(y + dy, 0, res - 1);
                                int nx = Mathf.Clamp(x + dx, 0, res - 1);
                                sum += heights[ny, nx];
                                count++;
                            }
                        }
                        smoothed[y, x] = sum / count;
                    }
                }
                heights = smoothed;
            }

            if (useAutoCrop && cropMaxX > cropMinX && cropMaxY > cropMinY)
            {
                // Conta i pixel effettivamente sotto il livello del mare
                int totalPixels = res * res;
                int seaPixels = 0;
                for (int y = 0; y < res; y++)
                    for (int x = 0; x < res; x++)
                        if (heights[y, x] <= baselineThreshold)
                            seaPixels++;
                float seaPct = (float)seaPixels / totalPixels;

                // Se il mare copre più del 30%, è una zona costiera:
                // NON croppare, il mare è parte della scena
                if (seaPct > 0.30f)
                {
                    UnityEngine.Debug.Log($"AutoCrop: {seaPct*100:F0}% mare — zona costiera, crop disabilitato");
                    cropMinX = 0; cropMaxX = res - 1;
                    cropMinY = 0; cropMaxY = res - 1;
                }
                else
                {
                    int margin = Mathf.Max(50, res / 80);
                    cropMinX = Mathf.Clamp(cropMinX - margin, 0, res - 1);
                    cropMaxX = Mathf.Clamp(cropMaxX + margin, 0, res - 1);
                    cropMinY = Mathf.Clamp(cropMinY - margin, 0, res - 1);
                    cropMaxY = Mathf.Clamp(cropMaxY + margin, 0, res - 1);
                }
            }
            else
            {
                cropMinX = 0; cropMaxX = res - 1;
                cropMinY = 0; cropMaxY = res - 1;
            }

            int croppedW = cropMaxX - cropMinX;
            int croppedH = cropMaxY - cropMinY;

            float cropMinLon = Mathf.Lerp(tMeta.minLon, tMeta.maxLon, (float)cropMinX / res);
            float cropMaxLon = Mathf.Lerp(tMeta.minLon, tMeta.maxLon, (float)cropMaxX / res);
            float cropMinLat = Mathf.Lerp(tMeta.minLat, tMeta.maxLat, (float)cropMinY / res);
            float cropMaxLat = Mathf.Lerp(tMeta.minLat, tMeta.maxLat, (float)cropMaxY / res);

            float cropWidthM = (croppedW / (float)res) * tMeta.widthM;
            float cropLengthM = (croppedH / (float)res) * tMeta.lengthM;

            setCropBounds?.Invoke(cropWidthM, cropLengthM);

            // Aggiorna tMeta con i bounds croppati — necessario perché OsmParser mappa lon/lat -> world
            // NB: questo muta l'oggetto originale. Chi chiama deve passare un Clone() se vuole preservare i bounds originali.
            tMeta.minLon = cropMinLon; tMeta.maxLon = cropMaxLon;
            tMeta.minLat = cropMinLat; tMeta.maxLat = cropMaxLat;
            tMeta.widthM = cropWidthM;
            tMeta.lengthM = cropLengthM;

            int tileRes = FindBestTileResolution(res, gridCount);
            UnityEngine.Debug.Log($"Griglia {gridCount}x{gridCount}, raw={res}, tile={tileRes}, crop={croppedW}x{croppedH}px");

            Terrain[,] generatedTerrains = new Terrain[gridCount, gridCount];

            float tileWidth = cropWidthM / gridCount;
            float tileLength = cropLengthM / gridCount;

            Texture2D grassTex = new Texture2D(2, 2);
            grassTex.SetPixels(new Color[] {
                new Color(0.2f, 0.4f, 0.2f), new Color(0.22f, 0.42f, 0.2f),
                new Color(0.18f, 0.38f, 0.18f), new Color(0.2f, 0.4f, 0.2f)
            });
            grassTex.Apply();
            TerrainLayer grassLayer = new TerrainLayer() { diffuseTexture = grassTex, tileSize = new Vector2(10, 10) };

            // Riusa lo stesso array tileH per tutti i tile (stessa dimensione)
            float[,] tileH = new float[tileRes, tileRes];

            for (int gy = 0; gy < gridCount; gy++)
            {
                for (int gx = 0; gx < gridCount; gx++)
                {
                    float progress = 0.5f + 0.4f * (gy * gridCount + gx) / (float)(gridCount * gridCount);
                    EditorUtility.DisplayProgressBar("CityBuilder", $"Costruzione Terreno {gx},{gy}...", progress);

                    TerrainData td = new TerrainData();
                    td.heightmapResolution = tileRes;
                    // Riduci risoluzioni per contenere la dimensione della build
                    // alphamap (splatmap): 256 basta per la nostra texture procedurale
                    td.alphamapResolution = 256;
                    // basemap: la texture composita a bassa res vista da lontano
                    td.baseMapResolution = 128;
                    // detail: non usiamo grass/detail meshes, minimizza
                    td.SetDetailResolution(32, 8);
                    td.size = new Vector3(tileWidth, tMeta.heightM, tileLength);
                    td.terrainLayers = new TerrainLayer[] { grassLayer };

                    float startSrcX = cropMinX + (gx / (float)gridCount) * croppedW;
                    float endSrcX = cropMinX + ((gx + 1) / (float)gridCount) * croppedW;
                    float startSrcY = cropMinY + (gy / (float)gridCount) * croppedH;
                    float endSrcY = cropMinY + ((gy + 1) / (float)gridCount) * croppedH;

                    for (int ty = 0; ty < tileRes; ty++)
                    {
                        for (int tx = 0; tx < tileRes; tx++)
                        {
                            float u = (float)tx / (tileRes - 1);
                            float v = (float)ty / (tileRes - 1);
                            float exactSrcX = Mathf.Lerp(startSrcX, endSrcX, u);
                            float exactSrcY = Mathf.Lerp(startSrcY, endSrcY, v);
                            tileH[ty, tx] = SampleBicubic(heights, exactSrcY, exactSrcX, res);
                        }
                    }

                    td.SetHeights(0, 0, tileH);

                    GameObject tObj = Terrain.CreateTerrainGameObject(td);
                    tObj.name = $"Terrain_{gx}_{gy}";
                    tObj.transform.parent = worldParent.transform;
                    tObj.transform.position = new Vector3(gx * tileWidth, 0, gy * tileLength);
                    generatedTerrains[gx, gy] = tObj.GetComponent<Terrain>();
                    await Task.Yield();
                }
            }

            // ── Stitch borders: forza bordi adiacenti a valori identici ──
            // Unity richiede che l'ultimo pixel di un tile sia uguale al primo del tile vicino.
            // Anche se il nostro campionamento è continuo, errori floating point e Catmull-Rom
            // possono creare micro-discontinuità. Forziamo i bordi a matchare.
            for (int gy = 0; gy < gridCount; gy++)
            {
                for (int gx = 0; gx < gridCount; gx++)
                {
                    if (generatedTerrains[gx, gy] == null) continue;
                    TerrainData td = generatedTerrains[gx, gy].terrainData;
                    float[,] th = td.GetHeights(0, 0, tileRes, tileRes);
                    bool changed = false;

                    // Bordo destro (x=tileRes-1) -> match con bordo sinistro (x=0) del tile gx+1
                    if (gx + 1 < gridCount && generatedTerrains[gx + 1, gy] != null)
                    {
                        float[,] neighborH = generatedTerrains[gx + 1, gy].terrainData.GetHeights(0, 0, 1, tileRes);
                        for (int ty = 0; ty < tileRes; ty++)
                        {
                            float avg = (th[ty, tileRes - 1] + neighborH[ty, 0]) * 0.5f;
                            th[ty, tileRes - 1] = avg;
                        }
                        // Aggiorna anche il vicino
                        for (int ty = 0; ty < tileRes; ty++)
                            neighborH[ty, 0] = th[ty, tileRes - 1];
                        generatedTerrains[gx + 1, gy].terrainData.SetHeights(0, 0, neighborH);
                        changed = true;
                    }

                    // Bordo superiore (y=tileRes-1) -> match con bordo inferiore (y=0) del tile gy+1
                    if (gy + 1 < gridCount && generatedTerrains[gx, gy + 1] != null)
                    {
                        float[,] neighborH = generatedTerrains[gx, gy + 1].terrainData.GetHeights(0, 0, tileRes, 1);
                        for (int tx = 0; tx < tileRes; tx++)
                        {
                            float avg = (th[tileRes - 1, tx] + neighborH[0, tx]) * 0.5f;
                            th[tileRes - 1, tx] = avg;
                        }
                        for (int tx = 0; tx < tileRes; tx++)
                            neighborH[0, tx] = th[tileRes - 1, tx];
                        generatedTerrains[gx, gy + 1].terrainData.SetHeights(0, 0, neighborH);
                        changed = true;
                    }

                    if (changed)
                        td.SetHeights(0, 0, th);
                }
            }

            // Libera la heightmap sorgente (non serve piu')
            heights = null;
            tileH = null;

            // SetNeighbors + Flush per saldatura LOD
            for (int y = 0; y < gridCount; y++)
            {
                for (int x = 0; x < gridCount; x++)
                {
                    if (generatedTerrains[x, y] == null) continue;
                    generatedTerrains[x, y].SetNeighbors(
                        x > 0 ? generatedTerrains[x - 1, y] : null,
                        y < gridCount - 1 ? generatedTerrains[x, y + 1] : null,
                        x < gridCount - 1 ? generatedTerrains[x + 1, y] : null,
                        y > 0 ? generatedTerrains[x, y - 1] : null);
                    generatedTerrains[x, y].Flush();
                }
            }

            // Forza lo stesso LOD error per evitare cuciture da LOD diversi
            for (int y = 0; y < gridCount; y++)
                for (int x = 0; x < gridCount; x++)
                    if (generatedTerrains[x, y] != null)
                        generatedTerrains[x, y].heightmapPixelError = 5f;

            UnityEngine.Debug.Log($"Terreno completato: {gridCount}x{gridCount} tiles, crop {cropWidthM:F0}x{cropLengthM:F0}m, GPS [{cropMinLon:F4},{cropMinLat:F4}]-[{cropMaxLon:F4},{cropMaxLat:F4}]");
            return generatedTerrains;
        }
    }
}
