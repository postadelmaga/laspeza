using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Piazza alberi cartoon sulle colline e nelle aree verdi usando GPU Instancing.
/// Genera mesh low-poly procedurali e le renderizza con DrawMeshInstanced
/// = migliaia di alberi con pochissime draw call.
///
/// Funziona a runtime (Play mode) o chiamato dall'editor.
/// Si appoggia al terreno Unity esistente.
///
/// Regole di piazzamento:
///   - Sopra il livello del mare (seaLevel + margine)
///   - Pendenza non troppo ripida (no rocce verticali)
///   - Densita' variabile per altitudine (piu' in basso = piu' alberi)
///   - Rispetta edifici/strade esistenti (evita collisioni)
/// </summary>
public class ToonTreePlacer : MonoBehaviour
{
    [Header("Density")]
    [Tooltip("Alberi per 1000m² nelle zone basse")]
    public float treeDensityLow = 3f;
    [Tooltip("Alberi per 1000m² nelle zone alte")]
    public float treeDensityHigh = 1.5f;
    [Tooltip("Altitudine normalizzata sotto cui non piazzare (auto-detect dal terreno)")]
    public float seaLevelNorm = 0.08f;
    [Tooltip("Margine sopra il livello del mare (metri)")]
    public float seaMarginM = 5f;
    [Tooltip("Pendenza massima in gradi per piazzare alberi")]
    public float maxSlope = 35f;
    [Tooltip("Distanza minima da edifici/strade (metri)")]
    public float avoidRadius = 8f;

    [Header("Tree Types")]
    public int maxTrees = 30000;
    [Tooltip("% pini marittimi (costa e mezza collina)")]
    [Range(0, 1)] public float pinePct = 0.45f;
    [Tooltip("% castagni (collina e montagna)")]
    [Range(0, 1)] public float chestnutPct = 0.40f;
    [Tooltip("% macchia mediterranea (cespugli bassi)")]
    [Range(0, 1)] public float bushPct = 0.15f;

    [Header("Rendering")]
    public float drawDistance = 3000f;
    public float lodDistance = 800f;

    [Header("BotW Colors — Pino Marittimo")]
    public Color pineColor = new Color(0.12f, 0.32f, 0.10f);
    public Color pineShadow = new Color(0.06f, 0.12f, 0.15f); // ombra blu-verde (BotW)
    public Color pineTrunkColor = new Color(0.48f, 0.28f, 0.14f);
    public Color pineTrunkShadow = new Color(0.20f, 0.12f, 0.15f); // ombra viola (BotW)

    [Header("BotW Colors — Castagno")]
    public Color chestnutColor = new Color(0.22f, 0.50f, 0.15f);
    public Color chestnutShadow = new Color(0.08f, 0.18f, 0.18f); // ombra teal
    public Color chestnutTrunkColor = new Color(0.32f, 0.22f, 0.12f);
    public Color chestnutTrunkShadow = new Color(0.14f, 0.10f, 0.14f); // ombra viola

    [Header("BotW Colors — Macchia")]
    public Color bushColor = new Color(0.28f, 0.50f, 0.18f);
    public Color bushShadow = new Color(0.10f, 0.18f, 0.16f); // ombra teal

    [Header("Erba")]
    [Tooltip("Ciuffi d'erba per 1000m² nelle zone pianeggianti")]
    public float grassDensity = 12f;
    public int maxGrassClumps = 40000;
    public float grassDrawDistance = 600f;
    public float grassMaxSlope = 20f;
    public Color grassColor = new Color(0.32f, 0.58f, 0.18f);
    public Color grassShadow = new Color(0.10f, 0.20f, 0.12f);

    // Runtime
    private Mesh pineCrownMesh, chestnutCrownMesh, bushMesh, grassMesh;
    private Mesh pineTrunkMesh, chestnutTrunkMesh;
    private Material pineCrownMat, chestnutCrownMat, bushMat, grassMat;
    private Material pineTrunkMat, chestnutTrunkMat;

    private List<Matrix4x4> pineMatrices = new List<Matrix4x4>();
    private List<Matrix4x4> chestnutMatrices = new List<Matrix4x4>();
    private List<Matrix4x4> bushMatrices = new List<Matrix4x4>();
    private List<Matrix4x4> pineTrunkMatrices = new List<Matrix4x4>();
    private List<Matrix4x4> chestnutTrunkMatrices = new List<Matrix4x4>();
    private List<Matrix4x4> grassMatrices = new List<Matrix4x4>();

    // Batched arrays for DrawMeshInstanced (max 1023 per call)
    private Matrix4x4[][] pineBatches, chestnutBatches, bushBatches, grassBatches;
    private Matrix4x4[][] pineTrunkBatches, chestnutTrunkBatches;

    private bool initialized;

    /// <summary>
    /// Legge seaLevelNorm dal terrain_meta_saved.json se disponibile.
    /// Fallback: stima dal terreno (altezza minima / altezza totale).
    /// </summary>
    void AutoDetectSeaLevel()
    {
        // Prova a leggere dal metadata
        string metaPath = System.IO.Path.Combine(Application.dataPath, "..", "DATA", "terrain_meta_saved.json");
        if (System.IO.File.Exists(metaPath))
        {
            try
            {
                string json = System.IO.File.ReadAllText(metaPath);
                // Parse minimale: cerca "seaLevelNorm": valore
                int idx = json.IndexOf("seaLevelNorm");
                if (idx >= 0)
                {
                    int colon = json.IndexOf(':', idx);
                    int comma = json.IndexOf(',', colon);
                    int end2 = json.IndexOf('}', colon);
                    int end = comma >= 0 ? Mathf.Min(comma, end2 >= 0 ? end2 : comma) : end2;
                    if (colon >= 0 && end > colon)
                    {
                        string val = json.Substring(colon + 1, end - colon - 1).Trim();
                        if (float.TryParse(val, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float parsed))
                        {
                            seaLevelNorm = parsed;
                            Debug.Log($"ToonTreePlacer: seaLevelNorm={seaLevelNorm:F4} (da metadata)");
                            return;
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"ToonTreePlacer: errore lettura metadata: {e.Message}");
            }
        } // fine if File.Exists

        // Fallback: stima dal terreno
        var world = GameObject.Find("CityBuilder_World");
        if (world != null)
        {
            Terrain[] terrains = world.GetComponentsInChildren<Terrain>();
            if (terrains.Length > 0)
            {
                // Il punto piu' basso del primo terreno / altezza totale
                float[,] hs = terrains[0].terrainData.GetHeights(0, 0, 3, 3);
                float minH = float.MaxValue;
                foreach (float hv in hs) if (hv < minH) minH = hv;
                seaLevelNorm = minH + 0.01f;
                Debug.Log($"ToonTreePlacer: seaLevelNorm={seaLevelNorm:F4} (stimato da terreno)");
            }
        }
    } // fine AutoDetectSeaLevel

    void Start()
    {
        GenerateMeshes();
        GenerateMaterials();
        PlaceTrees();
        PlaceGrass();
        BuildBatches();
        initialized = true;
    }

    // ================================================================
    //  MESH PROCEDURALI LOW-POLY
    // ================================================================

    void GenerateMeshes()
    {
        // Pino marittimo: tronco alto e snello, chioma a ombrello piatta in cima
        // Tronco leggermente curvo (inclinato), corteccia rossastra
        pineTrunkMesh = CreatePineTrunk(6, 0.12f, 7f);
        pineCrownMesh = CreatePineUmbrella(7, 3, 2.8f, 0.9f, 7f);

        // Castagno: tronco robusto e corto, chioma ampia e tondeggiante
        // Corona piu' larga che alta, molto ramificata
        chestnutTrunkMesh = CreateCylinder(6, 0.22f, 0f, 3.5f);
        chestnutCrownMesh = CreateChestnutCrown(8, 4, 3.0f, 2.5f, 4.5f);

        // Macchia mediterranea: cespugli bassi e compatti
        bushMesh = CreateFlatSphere(5, 2, 1.2f, 0.5f, 0f);

        // Erba: due quad incrociati a X (billboard cross) — 4 triangoli, leggerissimo
        grassMesh = CreateGrassBillboard(0.4f, 0.6f);
    }

    void GenerateMaterials()
    {
        Shader toonShader = Shader.Find("Custom/ToonVegetation");
        if (toonShader == null)
        {
            Debug.LogWarning("ToonVegetation shader non trovato, uso URP/Lit");
            toonShader = Shader.Find("Universal Render Pipeline/Lit");
        }

        pineCrownMat = CreateToonMat(toonShader, pineColor, pineShadow);
        pineTrunkMat = CreateToonMat(toonShader, pineTrunkColor, pineTrunkShadow);
        chestnutCrownMat = CreateToonMat(toonShader, chestnutColor, chestnutShadow);
        chestnutTrunkMat = CreateToonMat(toonShader, chestnutTrunkColor, chestnutTrunkShadow);
        bushMat = CreateToonMat(toonShader, bushColor, bushShadow);
        grassMat = CreateToonMat(toonShader, grassColor, grassShadow);
    }

    Material CreateToonMat(Shader shader, Color baseCol, Color shadowCol)
    {
        var mat = new Material(shader);
        mat.SetColor("_BaseColor", baseCol);
        mat.SetColor("_ShadowColor", shadowCol);
        // BotW rim light: slightly brighter version of base
        if (mat.HasProperty("_RimColor"))
            mat.SetColor("_RimColor", Color.Lerp(baseCol, Color.white, 0.3f));
        mat.enableInstancing = true;
        return mat;
    }

    // ================================================================
    //  PIAZZAMENTO ALBERI
    // ================================================================

    void PlaceTrees()
    {
        var world = GameObject.Find("CityBuilder_World");
        if (world == null) { Debug.LogWarning("ToonTreePlacer: CityBuilder_World non trovato"); return; }

        Terrain[] terrains = world.GetComponentsInChildren<Terrain>();
        if (terrains.Length == 0) { Debug.LogWarning("ToonTreePlacer: nessun terreno trovato"); return; }

        // Calcola bounds totali
        Bounds totalBounds = new Bounds(terrains[0].transform.position, Vector3.zero);
        float terrainHeightM = 0;
        foreach (var t in terrains)
        {
            totalBounds.Encapsulate(t.transform.position);
            totalBounds.Encapsulate(t.transform.position + t.terrainData.size);
            terrainHeightM = Mathf.Max(terrainHeightM, t.terrainData.size.y);
        }

        float seaLevelY = seaLevelNorm * terrainHeightM + seaMarginM;

        // Raccogli posizioni da evitare (edifici, strade)
        HashSet<Vector2Int> occupiedCells = BuildOccupancyGrid(world, totalBounds, avoidRadius);

        // Campiona il terreno con griglia + jitter
        System.Random rng = new System.Random(42);
        float area = totalBounds.size.x * totalBounds.size.z;
        float avgDensity = (treeDensityLow + treeDensityHigh) * 0.5f / 1000f; // alberi/m²
        int targetTrees = Mathf.Min(maxTrees, Mathf.RoundToInt(area * avgDensity));

        // Spacing tra campioni
        float spacing = Mathf.Sqrt(area / targetTrees);

        int placed = 0;
        float minX = totalBounds.min.x, maxX = totalBounds.max.x;
        float minZ = totalBounds.min.z, maxZ = totalBounds.max.z;

        for (float z = minZ; z < maxZ && placed < maxTrees; z += spacing)
        {
            for (float x = minX; x < maxX && placed < maxTrees; x += spacing)
            {
                // Jitter
                float jx = x + ((float)rng.NextDouble() - 0.5f) * spacing * 0.8f;
                float jz = z + ((float)rng.NextDouble() - 0.5f) * spacing * 0.8f;

                if (jx < minX || jx > maxX || jz < minZ || jz > maxZ) continue;

                // Check occupancy
                Vector2Int cell = new Vector2Int(
                    Mathf.FloorToInt(jx / avoidRadius),
                    Mathf.FloorToInt(jz / avoidRadius));
                if (occupiedCells.Contains(cell)) continue;

                // Sample terrain
                float y = SampleHeight(jx, jz, terrains);
                if (y < seaLevelY) continue;

                // Slope check
                float slope = SampleSlope(jx, jz, terrains, spacing * 0.3f);
                if (slope > maxSlope) continue;

                // Density varies by altitude
                float altNorm = Mathf.InverseLerp(seaLevelY, terrainHeightM, y);
                float localDensity = Mathf.Lerp(treeDensityLow, treeDensityHigh, altNorm) / 1000f;
                float prob = localDensity * spacing * spacing;
                if ((float)rng.NextDouble() > prob) continue;

                // Choose tree type
                float r = (float)rng.NextDouble();
                float scale = 0.7f + (float)rng.NextDouble() * 0.6f;
                float rotY = (float)rng.NextDouble() * 360f;

                Vector3 pos = new Vector3(jx, y, jz);
                Matrix4x4 mtx = Matrix4x4.TRS(pos, Quaternion.Euler(0, rotY, 0), Vector3.one * scale);

                // Distribuzione per altitudine:
                //   Costa e bassa collina (0-40% altitudine): pini marittimi + macchia
                //   Mezza collina (30-70%): mix pini e castagni
                //   Alta collina/montagna (60-100%): castagni dominanti
                float localPinePct, localChestnutPct;
                if (altNorm < 0.3f)
                {
                    localPinePct = 0.65f; localChestnutPct = 0.10f;
                }
                else if (altNorm < 0.6f)
                {
                    localPinePct = 0.35f; localChestnutPct = 0.45f;
                }
                else
                {
                    localPinePct = 0.10f; localChestnutPct = 0.70f;
                }

                if (r < localPinePct)
                {
                    pineTrunkMatrices.Add(mtx);
                    pineMatrices.Add(mtx);
                }
                else if (r < localPinePct + localChestnutPct)
                {
                    chestnutTrunkMatrices.Add(mtx);
                    chestnutMatrices.Add(mtx);
                }
                else
                {
                    bushMatrices.Add(mtx);
                }

                placed++;
            }
        }

        Debug.Log($"ToonTreePlacer: {placed} alberi piazzati " +
                  $"(pini marittimi: {pineMatrices.Count}, castagni: {chestnutMatrices.Count}, " +
                  $"macchia: {bushMatrices.Count})");
    }

    // ================================================================
    //  PIAZZAMENTO ERBA
    // ================================================================

    void PlaceGrass()
    {
        var world = GameObject.Find("CityBuilder_World");
        if (world == null) return;

        Terrain[] terrains = world.GetComponentsInChildren<Terrain>();
        if (terrains.Length == 0) return;

        Bounds totalBounds = new Bounds(terrains[0].transform.position, Vector3.zero);
        float terrainHeightM = 0;
        foreach (var t in terrains)
        {
            totalBounds.Encapsulate(t.transform.position);
            totalBounds.Encapsulate(t.transform.position + t.terrainData.size);
            terrainHeightM = Mathf.Max(terrainHeightM, t.terrainData.size.y);
        }
        float seaLevelY = seaLevelNorm * terrainHeightM + seaMarginM;

        // Usa stessa occupancy grid degli alberi
        HashSet<Vector2Int> occupied = BuildOccupancyGrid(world, totalBounds, avoidRadius * 0.5f);

        System.Random rng = new System.Random(123);
        float grassSpacing = Mathf.Sqrt(1000f / grassDensity); // distanza tra ciuffi
        int placed = 0;

        float minX = totalBounds.min.x, maxX = totalBounds.max.x;
        float minZ = totalBounds.min.z, maxZ = totalBounds.max.z;

        for (float z = minZ; z < maxZ && placed < maxGrassClumps; z += grassSpacing)
        {
            for (float x = minX; x < maxX && placed < maxGrassClumps; x += grassSpacing)
            {
                float jx = x + ((float)rng.NextDouble() - 0.5f) * grassSpacing * 0.9f;
                float jz = z + ((float)rng.NextDouble() - 0.5f) * grassSpacing * 0.9f;
                if (jx < minX || jx > maxX || jz < minZ || jz > maxZ) continue;

                // Evita edifici/strade
                Vector2Int cell = new Vector2Int(
                    Mathf.FloorToInt(jx / (avoidRadius * 0.5f)),
                    Mathf.FloorToInt(jz / (avoidRadius * 0.5f)));
                if (occupied.Contains(cell)) continue;

                float y = SampleHeight(jx, jz, terrains);
                if (y < seaLevelY) continue;

                float slope = SampleSlope(jx, jz, terrains, grassSpacing * 0.3f);
                if (slope > grassMaxSlope) continue;

                // Piu' erba in basso (pianura/costa), meno in alto
                float altNorm = Mathf.InverseLerp(seaLevelY, terrainHeightM * 0.5f, y);
                float prob = Mathf.Lerp(1f, 0.2f, altNorm);
                if ((float)rng.NextDouble() > prob) continue;

                float scale = 0.6f + (float)rng.NextDouble() * 0.8f;
                float rotY2 = (float)rng.NextDouble() * 360f;
                Vector3 pos = new Vector3(jx, y, jz);
                grassMatrices.Add(Matrix4x4.TRS(pos, Quaternion.Euler(0, rotY2, 0), Vector3.one * scale));
                placed++;
            }
        }

        Debug.Log($"ToonTreePlacer: {placed} ciuffi d'erba piazzati");
    }

    // ================================================================
    //  OCCUPANCY GRID (evita edifici/strade)
    // ================================================================

    HashSet<Vector2Int> BuildOccupancyGrid(GameObject world, Bounds bounds, float cellSize)
    {
        var grid = new HashSet<Vector2Int>();

        // Cerca edifici e strade
        string[] checkNames = { "Edifici", "Strade", "Piazze" };
        foreach (string name in checkNames)
        {
            Transform parent = world.transform.Find(name);
            if (parent == null) continue;

            var renderers = parent.GetComponentsInChildren<MeshRenderer>();
            foreach (var r in renderers)
            {
                Bounds b = r.bounds;
                int minCX = Mathf.FloorToInt(b.min.x / cellSize);
                int maxCX = Mathf.CeilToInt(b.max.x / cellSize);
                int minCZ = Mathf.FloorToInt(b.min.z / cellSize);
                int maxCZ = Mathf.CeilToInt(b.max.z / cellSize);

                for (int cx = minCX; cx <= maxCX; cx++)
                    for (int cz = minCZ; cz <= maxCZ; cz++)
                        grid.Add(new Vector2Int(cx, cz));
            }
        }

        return grid;
    }

    // ================================================================
    //  TERRAIN SAMPLING
    // ================================================================

    float SampleHeight(float x, float z, Terrain[] terrains)
    {
        // Find which terrain contains this point
        foreach (var t in terrains)
        {
            Vector3 tPos = t.transform.position;
            Vector3 tSize = t.terrainData.size;
            if (x >= tPos.x && x <= tPos.x + tSize.x &&
                z >= tPos.z && z <= tPos.z + tSize.z)
            {
                return t.SampleHeight(new Vector3(x, 0, z));
            }
        }
        return 0;
    }

    float SampleSlope(float x, float z, Terrain[] terrains, float delta)
    {
        float h0 = SampleHeight(x, z, terrains);
        float hx = SampleHeight(x + delta, z, terrains);
        float hz = SampleHeight(x, z + delta, terrains);
        float dx = (hx - h0) / delta;
        float dz = (hz - h0) / delta;
        return Mathf.Atan(Mathf.Sqrt(dx * dx + dz * dz)) * Mathf.Rad2Deg;
    }

    // ================================================================
    //  BATCH BUILDING
    // ================================================================

    void BuildBatches()
    {
        pineBatches = ToBatches(pineMatrices);
        chestnutBatches = ToBatches(chestnutMatrices);
        bushBatches = ToBatches(bushMatrices);
        grassBatches = ToBatches(grassMatrices);
        pineTrunkBatches = ToBatches(pineTrunkMatrices);
        chestnutTrunkBatches = ToBatches(chestnutTrunkMatrices);

        // Free source lists
        pineMatrices = null; chestnutMatrices = null;
        bushMatrices = null; grassMatrices = null;
        pineTrunkMatrices = null; chestnutTrunkMatrices = null;
    }

    Matrix4x4[][] ToBatches(List<Matrix4x4> list)
    {
        if (list == null || list.Count == 0) return new Matrix4x4[0][];

        int batchCount = Mathf.CeilToInt(list.Count / 1023f);
        var batches = new Matrix4x4[batchCount][];
        for (int i = 0; i < batchCount; i++)
        {
            int start = i * 1023;
            int count = Mathf.Min(1023, list.Count - start);
            batches[i] = new Matrix4x4[count];
            list.CopyTo(start, batches[i], 0, count);
        }
        return batches;
    }

    // ================================================================
    //  RENDERING (GPU Instanced)
    // ================================================================

    void Update()
    {
        if (!initialized) return;

        // Chiome
        DrawBatches(pineCrownMesh, pineCrownMat, pineBatches);
        DrawBatches(chestnutCrownMesh, chestnutCrownMat, chestnutBatches);
        DrawBatches(bushMesh, bushMat, bushBatches);

        // Tronchi (materiali distinti: pino rossastro, castagno scuro)
        DrawBatches(pineTrunkMesh, pineTrunkMat, pineTrunkBatches);
        DrawBatches(chestnutTrunkMesh, chestnutTrunkMat, chestnutTrunkBatches);

        // Erba (solo se vicina alla camera)
        DrawBatchesInRange(grassMesh, grassMat, grassBatches, grassDrawDistance);
    }

    void DrawBatches(Mesh mesh, Material mat, Matrix4x4[][] batches)
    {
        if (mesh == null || mat == null || batches == null) return;
        for (int i = 0; i < batches.Length; i++)
        {
            Graphics.DrawMeshInstanced(mesh, 0, mat, batches[i]);
        }
    }

    void DrawBatchesInRange(Mesh mesh, Material mat, Matrix4x4[][] batches, float maxDist)
    {
        if (mesh == null || mat == null || batches == null) return;
        Camera cam = Camera.main;
        if (cam == null) return;
        Vector3 camPos = cam.transform.position;
        float maxDist2 = maxDist * maxDist;

        for (int i = 0; i < batches.Length; i++)
        {
            // Quick check: test del primo elemento del batch
            if (batches[i].Length == 0) continue;
            Vector3 batchPos = new Vector3(
                batches[i][0].m03, batches[i][0].m13, batches[i][0].m23);
            float d2 = (batchPos - camPos).sqrMagnitude;
            // Se il primo elemento e' troppo lontano, salta il batch
            // (approssimazione: batch vicini sono raggruppati spazialmente)
            if (d2 > maxDist2 * 4f) continue;
            Graphics.DrawMeshInstanced(mesh, 0, mat, batches[i]);
        }
    }

    // ================================================================
    //  MESH GENERATORS (low-poly cartoon)
    // ================================================================

    /// <summary>Cilindro low-poly per tronchi.</summary>
    static Mesh CreateCylinder(int segments, float radius, float yBase, float height)
    {
        var verts = new List<Vector3>();
        var tris = new List<int>();

        // Bottom + top rings
        for (int i = 0; i < segments; i++)
        {
            float a = (float)i / segments * Mathf.PI * 2f;
            float x = Mathf.Cos(a) * radius;
            float z = Mathf.Sin(a) * radius;
            verts.Add(new Vector3(x, yBase, z));
            verts.Add(new Vector3(x, yBase + height, z));
        }

        // Side faces
        for (int i = 0; i < segments; i++)
        {
            int c = i * 2, n = ((i + 1) % segments) * 2;
            tris.Add(c); tris.Add(c + 1); tris.Add(n);
            tris.Add(n); tris.Add(c + 1); tris.Add(n + 1);
        }

        // Top cap
        int topC = verts.Count;
        verts.Add(new Vector3(0, yBase + height, 0));
        for (int i = 0; i < segments; i++)
        {
            int c = i * 2 + 1, n = ((i + 1) % segments) * 2 + 1;
            tris.Add(topC); tris.Add(n); tris.Add(c);
        }

        var mesh = new Mesh();
        mesh.vertices = verts.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>Ellissoide schiacciato per chiome di pini mediterranei e cespugli.</summary>
    static Mesh CreateFlatSphere(int segments, int rings, float radiusXZ, float radiusY, float yOffset)
    {
        var verts = new List<Vector3>();
        var tris = new List<int>();

        // North pole
        verts.Add(new Vector3(0, yOffset + radiusY, 0));

        for (int r = 1; r < rings; r++)
        {
            float phi = Mathf.PI * r / rings;
            float y = Mathf.Cos(phi) * radiusY + yOffset;
            float ringR = Mathf.Sin(phi) * radiusXZ;
            for (int s = 0; s < segments; s++)
            {
                float theta = 2f * Mathf.PI * s / segments;
                // Add slight random-ish offset for organic look (deterministic)
                float wobble = 1f + Mathf.Sin(s * 2.7f + r * 1.3f) * 0.15f;
                verts.Add(new Vector3(Mathf.Cos(theta) * ringR * wobble, y, Mathf.Sin(theta) * ringR * wobble));
            }
        }

        // South pole
        verts.Add(new Vector3(0, yOffset - radiusY, 0));

        int northPole = 0;
        int southPole = verts.Count - 1;

        // Top cap
        for (int s = 0; s < segments; s++)
        {
            int c = 1 + s, n = 1 + (s + 1) % segments;
            tris.Add(northPole); tris.Add(n); tris.Add(c);
        }

        // Middle rings
        for (int r = 0; r < rings - 2; r++)
        {
            for (int s = 0; s < segments; s++)
            {
                int c = 1 + r * segments + s;
                int n = 1 + r * segments + (s + 1) % segments;
                int cb = 1 + (r + 1) * segments + s;
                int nb = 1 + (r + 1) * segments + (s + 1) % segments;
                tris.Add(c); tris.Add(n); tris.Add(cb);
                tris.Add(n); tris.Add(nb); tris.Add(cb);
            }
        }

        // Bottom cap
        int lastRing = 1 + (rings - 2) * segments;
        for (int s = 0; s < segments; s++)
        {
            int c = lastRing + s, n = lastRing + (s + 1) % segments;
            tris.Add(c); tris.Add(n); tris.Add(southPole);
        }

        var mesh = new Mesh();
        mesh.vertices = verts.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>Sfera irregolare per chiome di quercia (blob organico).</summary>
    static Mesh CreateBlobSphere(int segments, int rings, float radius, float yOffset)
    {
        var verts = new List<Vector3>();
        var tris = new List<int>();

        verts.Add(new Vector3(0, yOffset + radius, 0));

        for (int r = 1; r < rings; r++)
        {
            float phi = Mathf.PI * r / rings;
            float y = Mathf.Cos(phi) * radius + yOffset;
            float ringR = Mathf.Sin(phi) * radius;
            for (int s = 0; s < segments; s++)
            {
                float theta = 2f * Mathf.PI * s / segments;
                // Blob distortion
                float wobble = 1f + Mathf.Sin(s * 3.1f + r * 2.7f) * 0.2f;
                float yWobble = Mathf.Sin(s * 1.9f + r * 4.1f) * radius * 0.1f;
                verts.Add(new Vector3(Mathf.Cos(theta) * ringR * wobble,
                                      y + yWobble,
                                      Mathf.Sin(theta) * ringR * wobble));
            }
        }

        verts.Add(new Vector3(0, yOffset - radius * 0.5f, 0)); // flat bottom

        int northPole = 0;
        int southPole = verts.Count - 1;

        for (int s = 0; s < segments; s++)
        {
            int c = 1 + s, n = 1 + (s + 1) % segments;
            tris.Add(northPole); tris.Add(n); tris.Add(c);
        }

        for (int r = 0; r < rings - 2; r++)
        {
            for (int s = 0; s < segments; s++)
            {
                int c = 1 + r * segments + s;
                int n = 1 + r * segments + (s + 1) % segments;
                int cb = 1 + (r + 1) * segments + s;
                int nb = 1 + (r + 1) * segments + (s + 1) % segments;
                tris.Add(c); tris.Add(n); tris.Add(cb);
                tris.Add(n); tris.Add(nb); tris.Add(cb);
            }
        }

        int lastRing = 1 + (rings - 2) * segments;
        for (int s = 0; s < segments; s++)
        {
            int c = lastRing + s, n = lastRing + (s + 1) % segments;
            tris.Add(c); tris.Add(n); tris.Add(southPole);
        }

        var mesh = new Mesh();
        mesh.vertices = verts.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Tronco pino marittimo: leggermente curvo/inclinato, piu' sottile in alto.
    /// Il pino marittimo ha un tronco che spesso si piega verso la luce.
    /// </summary>
    static Mesh CreatePineTrunk(int segments, float baseRadius, float height)
    {
        var verts = new List<Vector3>();
        var tris = new List<int>();

        int rings = 5;
        float lean = 0.4f; // inclinazione laterale

        for (int r = 0; r <= rings; r++)
        {
            float t = (float)r / rings;
            float y = t * height;
            float radius = Mathf.Lerp(baseRadius, baseRadius * 0.4f, t); // assottigliamento
            // Curva leggera: offset X che cresce col quadrato dell'altezza
            float xOff = lean * t * t;

            for (int s = 0; s < segments; s++)
            {
                float a = (float)s / segments * Mathf.PI * 2f;
                float x = Mathf.Cos(a) * radius + xOff;
                float z = Mathf.Sin(a) * radius;
                verts.Add(new Vector3(x, y, z));
            }
        }

        // Side faces
        for (int r = 0; r < rings; r++)
        {
            for (int s = 0; s < segments; s++)
            {
                int c = r * segments + s;
                int n = r * segments + (s + 1) % segments;
                int cb = (r + 1) * segments + s;
                int nb = (r + 1) * segments + (s + 1) % segments;
                tris.Add(c); tris.Add(cb); tris.Add(n);
                tris.Add(n); tris.Add(cb); tris.Add(nb);
            }
        }

        var mesh = new Mesh();
        mesh.vertices = verts.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Chioma pino marittimo: ombrello piatto e asimmetrico.
    /// Forma tipica: larga, piatta sopra, leggermente irregolare.
    /// </summary>
    static Mesh CreatePineUmbrella(int segments, int rings, float radiusXZ, float radiusY, float yOffset)
    {
        var verts = new List<Vector3>();
        var tris = new List<int>();

        // Centro spostato per seguire l'inclinazione del tronco
        float xOff = 0.3f;

        // Top (piatto)
        verts.Add(new Vector3(xOff, yOffset + radiusY * 0.6f, 0));

        for (int r = 1; r <= rings; r++)
        {
            float t = (float)r / rings;
            float phi = Mathf.PI * 0.35f + t * Mathf.PI * 0.5f; // solo meta' inferiore (ombrello)
            float y = Mathf.Cos(phi) * radiusY + yOffset;
            float ringR = Mathf.Sin(phi) * radiusXZ;

            for (int s = 0; s < segments; s++)
            {
                float theta = 2f * Mathf.PI * s / segments;
                // Irregolarita' organica
                float wobble = 1f + Mathf.Sin(s * 3.1f + r * 2.3f) * 0.18f;
                float rr = ringR * wobble;
                verts.Add(new Vector3(
                    Mathf.Cos(theta) * rr + xOff,
                    y,
                    Mathf.Sin(theta) * rr));
            }
        }

        // Fondo (piatto sotto)
        verts.Add(new Vector3(xOff, yOffset - radiusY * 0.15f, 0));

        int top = 0;
        int bottom = verts.Count - 1;

        // Top fan
        for (int s = 0; s < segments; s++)
        {
            int c = 1 + s, n = 1 + (s + 1) % segments;
            tris.Add(top); tris.Add(n); tris.Add(c);
        }

        // Ring strips
        for (int r = 0; r < rings - 1; r++)
        {
            for (int s = 0; s < segments; s++)
            {
                int c = 1 + r * segments + s;
                int n = 1 + r * segments + (s + 1) % segments;
                int cb = 1 + (r + 1) * segments + s;
                int nb = 1 + (r + 1) * segments + (s + 1) % segments;
                tris.Add(c); tris.Add(n); tris.Add(cb);
                tris.Add(n); tris.Add(nb); tris.Add(cb);
            }
        }

        // Bottom fan
        int lastRing = 1 + (rings - 1) * segments;
        for (int s = 0; s < segments; s++)
        {
            int c = lastRing + s, n = lastRing + (s + 1) % segments;
            tris.Add(c); tris.Add(n); tris.Add(bottom);
        }

        var mesh = new Mesh();
        mesh.vertices = verts.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Chioma castagno: ampia, tondeggiante, piu' larga che alta.
    /// Il castagno ha una chioma espansa con rami che si aprono a ventaglio.
    /// Multi-lobo: sembra fatta di piu' bolle fuse insieme.
    /// </summary>
    static Mesh CreateChestnutCrown(int segments, int rings, float radiusXZ, float radiusY, float yOffset)
    {
        var verts = new List<Vector3>();
        var tris = new List<int>();

        // Top
        verts.Add(new Vector3(0, yOffset + radiusY, 0));

        for (int r = 1; r < rings; r++)
        {
            float phi = Mathf.PI * r / rings;
            float y = Mathf.Cos(phi) * radiusY + yOffset;
            float ringR = Mathf.Sin(phi) * radiusXZ;

            for (int s = 0; s < segments; s++)
            {
                float theta = 2f * Mathf.PI * s / segments;
                // Multi-lobo: 3-4 protuberanze che danno aspetto di chioma composita
                float lobe = 1f + Mathf.Sin(theta * 3f + 0.5f) * 0.2f
                               + Mathf.Sin(theta * 5f + 1.2f) * 0.08f;
                // Variazione per anello (piu' irregolare in basso)
                float ringVar = 1f + Mathf.Sin(s * 2.1f + r * 3.7f) * 0.12f * r / rings;
                float rr = ringR * lobe * ringVar;

                verts.Add(new Vector3(
                    Mathf.Cos(theta) * rr,
                    y + Mathf.Sin(theta * 4f) * radiusY * 0.05f, // ondulazione verticale
                    Mathf.Sin(theta) * rr));
            }
        }

        // Bottom (piatto — base della chioma)
        verts.Add(new Vector3(0, yOffset - radiusY * 0.3f, 0));

        int top = 0;
        int bottom = verts.Count - 1;

        // Top fan
        for (int s = 0; s < segments; s++)
        {
            int c = 1 + s, n = 1 + (s + 1) % segments;
            tris.Add(top); tris.Add(n); tris.Add(c);
        }

        // Ring strips
        for (int r = 0; r < rings - 2; r++)
        {
            for (int s = 0; s < segments; s++)
            {
                int c = 1 + r * segments + s;
                int n = 1 + r * segments + (s + 1) % segments;
                int cb = 1 + (r + 1) * segments + s;
                int nb = 1 + (r + 1) * segments + (s + 1) % segments;
                tris.Add(c); tris.Add(n); tris.Add(cb);
                tris.Add(n); tris.Add(nb); tris.Add(cb);
            }
        }

        // Bottom fan
        int lastRing = 1 + (rings - 2) * segments;
        for (int s = 0; s < segments; s++)
        {
            int c = lastRing + s, n = lastRing + (s + 1) % segments;
            tris.Add(c); tris.Add(n); tris.Add(bottom);
        }

        var mesh = new Mesh();
        mesh.vertices = verts.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Ciuffo d'erba: due quad incrociati a X (billboard cross).
    /// 8 vertici, 4 triangoli — leggerissimo, ottimo per GPU instancing.
    /// L'altezza varia da y=0 (base) a y=height (cima, mossa dal vento via shader).
    /// </summary>
    static Mesh CreateGrassBillboard(float halfWidth, float height)
    {
        float hw = halfWidth;
        float h = height;

        // Due quad perpendicolari che formano una X vista dall'alto
        Vector3[] verts = {
            // Quad 1 (orientato lungo X)
            new Vector3(-hw, 0, 0), new Vector3(hw, 0, 0),
            new Vector3(-hw, h, 0), new Vector3(hw, h, 0),
            // Quad 2 (orientato lungo Z)
            new Vector3(0, 0, -hw), new Vector3(0, 0, hw),
            new Vector3(0, h, -hw), new Vector3(0, h, hw),
        };

        int[] tris = {
            // Quad 1 fronte/retro
            0, 2, 1,  1, 2, 3,
            0, 1, 2,  1, 3, 2,
            // Quad 2 fronte/retro
            4, 6, 5,  5, 6, 7,
            4, 5, 6,  5, 7, 6,
        };

        var mesh = new Mesh();
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
