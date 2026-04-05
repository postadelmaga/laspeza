using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CityBuilder
{
    /// <summary>
    /// Procedural building generator producing realistic facades, windows, and roofs.
    /// Buildings are batched per terrain tile with separate submeshes for walls,
    /// windows, roofs, and ground-floor facades.
    /// </summary>
    public static class BuildingGenerator
    {
        // ── Constants (all in real meters) ────────────────────────────────
        private const float FLOOR_HEIGHT    = 3.0f;   // meters per floor

        // Window geometry
        private const float WIN_WIDTH       = 1.2f;
        private const float WIN_HEIGHT      = 1.5f;
        private const float WIN_INSET       = 0.08f;   // recess depth
        private const float WIN_MARGIN_H    = 0.6f;    // horizontal margin between windows
        private const float WIN_SILL_H      = 0.9f;    // sill height from floor base
        private const float WIN_SPACING     = WIN_WIDTH + WIN_MARGIN_H;

        // Commercial ground floor
        private const float COMMERCIAL_WIN_WIDTH  = 2.0f;
        private const float COMMERCIAL_WIN_HEIGHT = 2.4f;
        private const float COMMERCIAL_SILL_H     = 0.3f;

        // Industrial
        private const float INDUSTRIAL_WIN_WIDTH  = 0.6f;
        private const float INDUSTRIAL_WIN_HEIGHT = 0.6f;
        private const float INDUSTRIAL_SPACING    = 4.0f;

        // Roof
        private const float PARAPET_HEIGHT  = 0.3f;
        private const float ROOF_SLOPE      = 0.35f;   // ratio of half-span for gabled/hipped
        private const float ROOF_OVERHANG   = 0.15f;

        // ── Mediterranean Palette (La Spezia) ──────────────────────────────
        private static readonly Color[] WarmPalette = new Color[]
        {
            new Color(0.96f, 0.90f, 0.72f),  // cream
            new Color(0.95f, 0.85f, 0.65f),  // warm yellow
            new Color(0.93f, 0.78f, 0.55f),  // sand / beige
            new Color(0.90f, 0.72f, 0.50f),  // golden ochre
            new Color(0.88f, 0.65f, 0.45f),  // terracotta light
            new Color(0.82f, 0.55f, 0.40f),  // terracotta
            new Color(0.95f, 0.75f, 0.65f),  // peach
            new Color(0.92f, 0.70f, 0.70f),  // pink
            new Color(0.85f, 0.60f, 0.55f),  // salmon
            new Color(0.98f, 0.92f, 0.80f),  // light ivory
            new Color(0.90f, 0.88f, 0.78f),  // warm grey-cream
            new Color(0.95f, 0.82f, 0.58f),  // light orange
        };

        private static readonly Color TERRACOTTA_ROOF = new Color(0.72f, 0.38f, 0.22f);
        private static readonly Color FLAT_ROOF_GREY  = new Color(0.55f, 0.55f, 0.55f);
        private static readonly Color WINDOW_COLOR    = new Color(0.22f, 0.28f, 0.35f);

        // ── Shader helper (mirrors ProceduralBuilder) ──────────────────────
        private static Material CreateMaterial(Color color, float smoothness = 0.3f, float metallic = 0f)
        {
            return MeshUtils.CreateMaterial(color, smoothness, metallic);
        }

        // ── Public entry point ─────────────────────────────────────────────
        /// <summary>
        /// Generate realistic buildings from OSM data, batched per tile.
        /// </summary>
        public static async Task GenerateBuildingsAsync(
            List<WorldBuildingData> buildings,
            GameObject parent,
            int gridCount,
            Terrain[,] terrains,
            float tileWidthM,
            float tileLengthM)
        {
            if (buildings == null || buildings.Count == 0) return;

            // Per-tile mesh accumulator
            TileBuildingMeshData[,] tileMeshes = new TileBuildingMeshData[gridCount, gridCount];
            for (int y = 0; y < gridCount; y++)
                for (int x = 0; x < gridCount; x++)
                    tileMeshes[x, y] = new TileBuildingMeshData();

            // Deterministic seed for reproducible colour variation
            System.Random rng = new System.Random(42);

            // ── LOD: compute terrain center and distance threshold ─────────
            // Use terrain grid center as reference point
            float terrainCenterX = gridCount * tileWidthM * 0.5f;
            float terrainCenterZ = gridCount * tileLengthM * 0.5f;

            // Max distance from center to the terrain corner
            float maxDistFromCenter = Mathf.Sqrt(
                terrainCenterX * terrainCenterX + terrainCenterZ * terrainCenterZ);

            // Buildings within 60% of max distance get full detail;
            // buildings in the outer 40% get simplified geometry (lowLOD)
            float lodThreshold = maxDistFromCenter * 0.6f;

            int total = buildings.Count;
            int lowLODCount = 0;
            for (int i = 0; i < total; i++)
            {
                if (i % 80 == 0)
                {
                    EditorUtility.DisplayProgressBar(
                        "BuildingGenerator",
                        $"Generazione edifici {i}/{total}...",
                        (float)i / total);
                    await Task.Yield();
                }

                WorldBuildingData b = buildings[i];
                if (b.footprint == null || b.footprint.Count < 3) continue;
                if (b.tileX < 0 || b.tileX >= gridCount || b.tileZ < 0 || b.tileZ >= gridCount) continue;

                // LOD decision based on distance from terrain center
                Vector3 centroid = Vector3.zero;
                for (int p = 0; p < b.footprint.Count; p++) centroid += b.footprint[p];
                centroid /= b.footprint.Count;

                float dx = centroid.x - terrainCenterX;
                float dz = centroid.z - terrainCenterZ;
                float distFromCenter = Mathf.Sqrt(dx * dx + dz * dz);
                bool lowLOD = distFromCenter > lodThreshold;
                if (lowLOD) lowLODCount++;

                TileBuildingMeshData td = tileMeshes[b.tileX, b.tileZ];
                GenerateBuilding(b, td, rng, lowLOD);
            }

            // ── Create shared materials ────────────────────────────────────
            // We pick a representative wall colour; individual building tint
            // is baked into vertex data via UV, but we use a neutral wall mat.
            Material wallMat       = CreateMaterial(new Color(0.92f, 0.87f, 0.75f), 0.15f);
            Material windowMat     = CreateMaterial(WINDOW_COLOR, 0.7f, 0.1f);
            Material roofMat       = CreateMaterial(TERRACOTTA_ROOF, 0.2f);
            Material groundMat     = CreateMaterial(new Color(0.78f, 0.73f, 0.62f), 0.15f);

            Material[] sharedMats = new Material[] { wallMat, windowMat, roofMat, groundMat };

            // ── Combine per tile ───────────────────────────────────────────
            EditorUtility.DisplayProgressBar("BuildingGenerator", "Fusione mesh per tile...", 0.92f);
            await Task.Yield();

            int builtTiles = 0;
            for (int gy = 0; gy < gridCount; gy++)
            {
                for (int gx = 0; gx < gridCount; gx++)
                {
                    TileBuildingMeshData td = tileMeshes[gx, gy];
                    if (td.vertices.Count == 0) continue;

                    GameObject tileObj = new GameObject($"Buildings_Tile_{gx}_{gy}");
                    tileObj.transform.parent = parent.transform;

                    Mesh mesh = new Mesh();
                    mesh.indexFormat = td.vertices.Count < 65000
                        ? UnityEngine.Rendering.IndexFormat.UInt16
                        : UnityEngine.Rendering.IndexFormat.UInt32;
                    mesh.SetVertices(td.vertices);
                    mesh.SetUVs(0, td.uvs);

                    // 4 submeshes: walls, windows, roofs, ground-floor
                    mesh.subMeshCount = 4;
                    mesh.SetTriangles(td.wallTriangles, 0);
                    mesh.SetTriangles(td.windowTriangles, 1);
                    mesh.SetTriangles(td.roofTriangles, 2);
                    mesh.SetTriangles(td.groundFloorTriangles, 3);

                    mesh.RecalculateNormals();
                    mesh.RecalculateBounds();

                    tileObj.AddComponent<MeshFilter>().sharedMesh = mesh;
                    MeshRenderer mr = tileObj.AddComponent<MeshRenderer>();
                    mr.sharedMaterials = sharedMats;

                    // Libera RAM dei dati intermedi per questo tile
                    td.Free();
                    tileMeshes[gx, gy] = null;

                    builtTiles++;
                }
            }
            tileMeshes = null;

            EditorUtility.ClearProgressBar();
            Debug.Log($"BuildingGenerator: {total} edifici generati in {builtTiles} tile. LOD basso: {lowLODCount} ({(total > 0 ? lowLODCount * 100 / total : 0)}%).");
        }

        // ── Per-building generation ────────────────────────────────────────
        private static void GenerateBuilding(WorldBuildingData b, TileBuildingMeshData td, System.Random rng, bool lowLOD = false)
        {
            List<Vector3> pts = b.footprint;
            if (pts.Count < 3) return;

            // Clamp altezza a valori ragionevoli (max ~30 piani = 90m reali = 9.0 world)
            float height = Mathf.Clamp(b.height, FLOOR_HEIGHT, 90f); // max ~30 floors

            // Assicura che il footprint sia in ordine CCW (necessario per winding corretto delle pareti)
            if (IsClockwiseXZ(pts))
                pts.Reverse();

            float baseY = float.MaxValue;
            for (int pi = 0; pi < pts.Count; pi++)
                if (pts[pi].y < baseY) baseY = pts[pi].y;
            float topY  = baseY + height;

            if (lowLOD)
            {
                // ── Low LOD: simple wall quads per edge + flat roof cap ────
                // No windows, no ground floor detail, no parapet, no shaped roofs
                GenerateWallsLowLOD(pts, baseY, topY, td);
                TriangulatePolygon(pts, topY, td.roofTriangles, td);
                return;
            }

            int   floors = Mathf.Max(1, Mathf.RoundToInt(height / FLOOR_HEIGHT));

            // Determine building type heuristic
            BuildingType bType = ClassifyBuilding(b, floors);

            // Determine roof shape
            string roofShape = ResolveRoofShape(b.roofShape, floors);

            // Actual wall top (roof starts here)
            float wallTopY = topY;

            // ── Walls with facade detail ───────────────────────────────────
            GenerateWalls(pts, baseY, wallTopY, floors, bType, td);

            // ── Roof ───────────────────────────────────────────────────────
            switch (roofShape)
            {
                case "gabled":
                    GenerateGabledRoof(pts, wallTopY, td);
                    break;
                case "hipped":
                    GenerateHippedRoof(pts, wallTopY, td);
                    break;
                case "pyramidal":
                    GeneratePyramidalRoof(pts, wallTopY, td);
                    break;
                default: // "flat"
                    GenerateFlatRoof(pts, wallTopY, td);
                    break;
            }
        }

        // ── Building classification ────────────────────────────────────────
        private enum BuildingType { Residential, Commercial, Industrial }

        private static BuildingType ClassifyBuilding(WorldBuildingData b, int floors)
        {
            // Check OSM material tag for hints
            if (!string.IsNullOrEmpty(b.material))
            {
                string m = b.material.ToLowerInvariant();
                if (m.Contains("metal") || m.Contains("steel") || m.Contains("concrete_block"))
                    return BuildingType.Industrial;
                if (m.Contains("glass"))
                    return BuildingType.Commercial;
            }

            // Heuristic: large footprint area + few floors = commercial/industrial
            float area = ComputeFootprintArea(b.footprint);
            if (floors <= 2 && area > 300f)  // >300 m²
                return BuildingType.Industrial;
            if (floors <= 3 && area > 150f)  // >150 m²
                return BuildingType.Commercial;

            return BuildingType.Residential;
        }

        /// <summary>
        /// Controlla se il poligono e in senso orario sul piano XZ.
        /// Le pareti usano Cross(Up, wallDir) che punta outward solo se CCW.
        /// </summary>
        private static bool IsClockwiseXZ(List<Vector3> pts)
        {
            float sum = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 a = pts[i];
                Vector3 b = pts[(i + 1) % pts.Count];
                sum += (b.x - a.x) * (b.z + a.z);
            }
            return sum > 0;
        }

        private static float ComputeFootprintArea(List<Vector3> pts)
        {
            float area = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 a = pts[i];
                Vector3 c = pts[(i + 1) % pts.Count];
                area += (c.x - a.x) * (c.z + a.z);
            }
            return Mathf.Abs(area) * 0.5f;
        }

        private static string ResolveRoofShape(string tag, int floors)
        {
            if (!string.IsNullOrEmpty(tag))
            {
                string t = tag.ToLowerInvariant().Trim();
                if (t == "gabled" || t == "hipped" || t == "pyramidal" || t == "flat")
                    return t;
            }
            // Default logic
            return floors > 4 ? "flat" : "gabled";
        }

        // ═══════════════════════════════════════════════════════════════════
        //  WALLS
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Low-LOD wall generation: one quad per footprint edge, full height.
        /// No per-floor subdivision, no windows, no ground floor detail.
        /// </summary>
        private static void GenerateWallsLowLOD(
            List<Vector3> pts, float baseY, float topY, TileBuildingMeshData td)
        {
            int n = pts.Count;
            for (int seg = 0; seg < n; seg++)
            {
                Vector3 p0 = pts[seg];
                Vector3 p1 = pts[(seg + 1) % n];
                float wallLen = new Vector2(p1.x - p0.x, p1.z - p0.z).magnitude;
                if (wallLen < 0.001f) continue;

                int idx = td.vertices.Count;

                td.vertices.Add(new Vector3(p0.x, baseY, p0.z));
                td.vertices.Add(new Vector3(p1.x, baseY, p1.z));
                td.vertices.Add(new Vector3(p0.x, topY,  p0.z));
                td.vertices.Add(new Vector3(p1.x, topY,  p1.z));

                td.uvs.Add(new Vector2(0, 0));
                td.uvs.Add(new Vector2(wallLen, 0));
                td.uvs.Add(new Vector2(0, 1));
                td.uvs.Add(new Vector2(wallLen, 1));

                td.wallTriangles.Add(idx);     td.wallTriangles.Add(idx + 2); td.wallTriangles.Add(idx + 1);
                td.wallTriangles.Add(idx + 1); td.wallTriangles.Add(idx + 2); td.wallTriangles.Add(idx + 3);
            }
        }

        private static void GenerateWalls(
            List<Vector3> pts, float baseY, float topY, int floors,
            BuildingType bType, TileBuildingMeshData td)
        {
            int n = pts.Count;

            for (int seg = 0; seg < n; seg++)
            {
                Vector3 p0 = pts[seg];
                Vector3 p1 = pts[(seg + 1) % n];

                Vector3 bottom0 = new Vector3(p0.x, baseY, p0.z);
                Vector3 bottom1 = new Vector3(p1.x, baseY, p1.z);

                Vector3 wallDir   = (bottom1 - bottom0);
                float   wallLen   = wallDir.magnitude;
                if (wallLen < 0.001f) continue;

                Vector3 wallDirN  = wallDir / wallLen;
                Vector3 wallNorm  = Vector3.Cross(Vector3.up, wallDirN).normalized;

                // Generate each floor section
                for (int floor = 0; floor < floors; floor++)
                {
                    float floorBase = baseY + floor * FLOOR_HEIGHT;
                    float floorTop  = Mathf.Min(floorBase + FLOOR_HEIGHT, topY);
                    float floorH    = floorTop - floorBase;
                    bool  isGround  = (floor == 0);

                    // Determine window params for this floor
                    float winW, winH, sillH, spacing;
                    int minWallLenForWindow;
                    GetWindowParams(bType, isGround, out winW, out winH, out sillH, out spacing, out minWallLenForWindow);

                    // Calculate how many windows fit
                    float usable = wallLen - WIN_MARGIN_H;
                    int winCount = 0;
                    if (usable >= spacing && wallLen >= minWallLenForWindow)
                        winCount = Mathf.FloorToInt(usable / spacing);

                    // For industrial: fewer windows
                    if (bType == BuildingType.Industrial && !isGround)
                        winCount = Mathf.Min(winCount, Mathf.Max(1, winCount / 3));

                    if (winCount > 0)
                    {
                        // Generate wall panels around windows
                        GenerateWallWithWindows(
                            bottom0, wallDirN, wallNorm, wallLen,
                            floorBase, floorTop, floorH,
                            winCount, winW, winH, sillH, spacing,
                            isGround, td);
                    }
                    else
                    {
                        // Plain wall quad
                        AddWallQuad(
                            bottom0, bottom1, floorBase, floorTop,
                            wallLen, isGround, td);
                    }
                }
            }
        }

        private static void GetWindowParams(
            BuildingType bType, bool isGround,
            out float winW, out float winH, out float sillH,
            out float spacing, out int minWallLenForWindow)
        {
            if (bType == BuildingType.Commercial && isGround)
            {
                winW = COMMERCIAL_WIN_WIDTH;
                winH = COMMERCIAL_WIN_HEIGHT;
                sillH = COMMERCIAL_SILL_H;
                spacing = winW + WIN_MARGIN_H;
                minWallLenForWindow = 2;
            }
            else if (bType == BuildingType.Industrial)
            {
                winW = INDUSTRIAL_WIN_WIDTH;
                winH = INDUSTRIAL_WIN_HEIGHT;
                sillH = WIN_SILL_H;
                spacing = INDUSTRIAL_SPACING;
                minWallLenForWindow = 3;
            }
            else
            {
                winW = WIN_WIDTH;
                winH = WIN_HEIGHT;
                sillH = WIN_SILL_H;
                spacing = WIN_SPACING;
                minWallLenForWindow = 2;
            }
        }

        /// <summary>
        /// Generates a wall section with recessed window openings.
        /// The wall is subdivided into: left margin, [window + pier]..., right margin.
        /// Each window is a slightly inset quad on the window submesh.
        /// </summary>
        private static void GenerateWallWithWindows(
            Vector3 origin, Vector3 dir, Vector3 norm, float wallLen,
            float floorBase, float floorTop, float floorH,
            int winCount, float winW, float winH, float sillH, float spacing,
            bool isGround, TileBuildingMeshData td)
        {
            // Center the window array on the wall
            float totalWinSpan = winCount * spacing - WIN_MARGIN_H + winW;
            // Actually: N windows each of width winW, with (N-1) gaps of WIN_MARGIN_H, plus margin on each side
            float arrayWidth = winCount * winW + (winCount - 1) * WIN_MARGIN_H;
            float marginLeft = (wallLen - arrayWidth) * 0.5f;

            // Submesh target
            List<int> wallTris = isGround ? td.groundFloorTriangles : td.wallTriangles;
            List<int> winTris  = td.windowTriangles;

            float uScale = wallLen;  // UV: u = distance along wall
            float vScale = floorH;

            // For each section along the wall
            float cursor = 0;
            for (int w = -1; w < winCount; w++)
            {
                // Section before first window or between windows
                float sectionStart = cursor;
                float sectionEnd;

                if (w == -1)
                    sectionEnd = marginLeft;
                else
                    sectionEnd = marginLeft + w * (winW + WIN_MARGIN_H) + winW + (w < winCount - 1 ? 0 : 0);

                // Wall strip left of this window (pier)
                if (w >= 0)
                {
                    float winLeft  = marginLeft + w * (winW + WIN_MARGIN_H);
                    float winRight = winLeft + winW;
                    float winBot   = sillH;
                    float winTop   = sillH + winH;

                    // ── Wall pieces around the window (4 pieces: below, above, left, right of window) ──

                    // Below window (full width of window cell)
                    if (winBot > 0.001f)
                    {
                        AddSubQuad(origin, dir, norm, winLeft, winRight, floorBase, floorBase + winBot,
                                   uScale, vScale, wallTris, td);
                    }

                    // Above window
                    if (winTop < floorH - 0.001f)
                    {
                        AddSubQuad(origin, dir, norm, winLeft, winRight, floorBase + winTop, floorTop,
                                   uScale, vScale, wallTris, td);
                    }

                    // Window recess (inset quad)
                    Vector3 insetOrigin = origin + norm * (-WIN_INSET);
                    AddSubQuad(insetOrigin, dir, norm, winLeft, winRight, floorBase + winBot, floorBase + winTop,
                               uScale, vScale, winTris, td);

                    cursor = winRight;
                }
                else
                {
                    cursor = marginLeft;
                }

                // Pier / margin strip
                if (w == -1 && marginLeft > 0.001f)
                {
                    // Left margin
                    AddSubQuad(origin, dir, norm, 0, marginLeft, floorBase, floorTop,
                               uScale, vScale, wallTris, td);
                }
                else if (w >= 0 && w < winCount - 1)
                {
                    // Pier between windows
                    float pierStart = marginLeft + w * (winW + WIN_MARGIN_H) + winW;
                    float pierEnd   = marginLeft + (w + 1) * (winW + WIN_MARGIN_H);
                    if (pierEnd - pierStart > 0.001f)
                    {
                        AddSubQuad(origin, dir, norm, pierStart, pierEnd, floorBase, floorTop,
                                   uScale, vScale, wallTris, td);
                    }
                }
            }

            // Right margin
            float lastWinRight = marginLeft + winCount * winW + (winCount - 1) * WIN_MARGIN_H;
            if (wallLen - lastWinRight > 0.001f)
            {
                AddSubQuad(origin, dir, norm, lastWinRight, wallLen, floorBase, floorTop,
                           uScale, vScale, wallTris, td);
            }
        }

        /// <summary>
        /// Add a quad on the wall plane. hStart/hEnd are distances along the wall direction from origin.
        /// vBot/vTop are Y world coords.
        /// </summary>
        private static void AddSubQuad(
            Vector3 origin, Vector3 dir, Vector3 norm,
            float hStart, float hEnd, float vBot, float vTop,
            float uScale, float vScale,
            List<int> tris, TileBuildingMeshData td)
        {
            int idx = td.vertices.Count;

            Vector3 bl = origin + dir * hStart + Vector3.up * (vBot - origin.y);
            Vector3 br = origin + dir * hEnd   + Vector3.up * (vBot - origin.y);
            Vector3 tl = origin + dir * hStart + Vector3.up * (vTop - origin.y);
            Vector3 tr = origin + dir * hEnd   + Vector3.up * (vTop - origin.y);

            // Correct: origin.y is the base Y, but these points should be at absolute Y
            // Re-derive: the origin is at (origin.x, origin.y, origin.z). But origin.y = baseY of the wall origin.
            // We want the vertex at world Y = vBot/vTop. Since origin already has y component,
            // and dir is horizontal, we set y explicitly.
            bl.y = vBot;
            br.y = vBot;
            tl.y = vTop;
            tr.y = vTop;

            td.vertices.Add(bl);
            td.vertices.Add(br);
            td.vertices.Add(tl);
            td.vertices.Add(tr);

            // UV: u mapped to horizontal position on wall, v mapped to vertical
            float u0 = hStart / Mathf.Max(uScale, 0.01f);
            float u1 = hEnd   / Mathf.Max(uScale, 0.01f);
            float v0 = 0f;
            float v1 = 1f;

            td.uvs.Add(new Vector2(u0, v0));
            td.uvs.Add(new Vector2(u1, v0));
            td.uvs.Add(new Vector2(u0, v1));
            td.uvs.Add(new Vector2(u1, v1));

            // Two triangles (CCW winding facing outward along norm)
            tris.Add(idx);     tris.Add(idx + 2); tris.Add(idx + 1);
            tris.Add(idx + 1); tris.Add(idx + 2); tris.Add(idx + 3);
        }

        /// <summary>
        /// Simple wall quad with no windows (fallback for narrow walls).
        /// </summary>
        private static void AddWallQuad(
            Vector3 p0, Vector3 p1, float vBot, float vTop,
            float wallLen, bool isGround, TileBuildingMeshData td)
        {
            int idx = td.vertices.Count;

            td.vertices.Add(new Vector3(p0.x, vBot, p0.z));
            td.vertices.Add(new Vector3(p1.x, vBot, p1.z));
            td.vertices.Add(new Vector3(p0.x, vTop, p0.z));
            td.vertices.Add(new Vector3(p1.x, vTop, p1.z));

            td.uvs.Add(new Vector2(0, 0));
            td.uvs.Add(new Vector2(wallLen, 0));
            td.uvs.Add(new Vector2(0, 1));
            td.uvs.Add(new Vector2(wallLen, 1));

            List<int> tris = isGround ? td.groundFloorTriangles : td.wallTriangles;
            tris.Add(idx);     tris.Add(idx + 2); tris.Add(idx + 1);
            tris.Add(idx + 1); tris.Add(idx + 2); tris.Add(idx + 3);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  ROOFS
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Flat roof with a slight parapet edge.
        /// </summary>
        private static void GenerateFlatRoof(List<Vector3> pts, float roofY, TileBuildingMeshData td)
        {
            float parapetTop = roofY + PARAPET_HEIGHT;

            // Parapet walls (small vertical strips around perimeter)
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                Vector3 a = pts[i];
                Vector3 b = pts[(i + 1) % n];

                int idx = td.vertices.Count;
                td.vertices.Add(new Vector3(a.x, roofY,      a.z));
                td.vertices.Add(new Vector3(b.x, roofY,      b.z));
                td.vertices.Add(new Vector3(a.x, parapetTop,  a.z));
                td.vertices.Add(new Vector3(b.x, parapetTop,  b.z));

                td.uvs.Add(new Vector2(0, 0));
                td.uvs.Add(new Vector2(1, 0));
                td.uvs.Add(new Vector2(0, 1));
                td.uvs.Add(new Vector2(1, 1));

                td.roofTriangles.Add(idx);     td.roofTriangles.Add(idx + 2); td.roofTriangles.Add(idx + 1);
                td.roofTriangles.Add(idx + 1); td.roofTriangles.Add(idx + 2); td.roofTriangles.Add(idx + 3);
            }

            // Flat cap via ear-clipping
            TriangulatePolygon(pts, parapetTop, td.roofTriangles, td);
        }

        /// <summary>
        /// Gabled roof (two slopes along the longest axis of the bounding box).
        /// </summary>
        private static void GenerateGabledRoof(List<Vector3> pts, float roofY, TileBuildingMeshData td)
        {
            // Find oriented bounding box principal axis via longest edge
            GetLongestAxis(pts, out Vector3 axisDir, out Vector3 perpDir, out float axisLen, out float perpLen);
            Vector3 center = ComputeCentroid(pts);

            float ridgeHeight = perpLen * ROOF_SLOPE;
            float halfPerp = perpLen * 0.5f;

            // Ridge line: two endpoints along axis from center
            Vector3 ridgeA = center + axisDir * (axisLen * 0.5f + ROOF_OVERHANG);
            Vector3 ridgeB = center - axisDir * (axisLen * 0.5f + ROOF_OVERHANG);
            ridgeA.y = roofY + ridgeHeight;
            ridgeB.y = roofY + ridgeHeight;

            // Eave points (footprint corners projected outward + overhang)
            // Simplified: use 4 corner eave points
            Vector3 eave0 = center + axisDir * (axisLen * 0.5f + ROOF_OVERHANG) + perpDir * (halfPerp + ROOF_OVERHANG);
            Vector3 eave1 = center + axisDir * (axisLen * 0.5f + ROOF_OVERHANG) - perpDir * (halfPerp + ROOF_OVERHANG);
            Vector3 eave2 = center - axisDir * (axisLen * 0.5f + ROOF_OVERHANG) - perpDir * (halfPerp + ROOF_OVERHANG);
            Vector3 eave3 = center - axisDir * (axisLen * 0.5f + ROOF_OVERHANG) + perpDir * (halfPerp + ROOF_OVERHANG);
            eave0.y = roofY; eave1.y = roofY; eave2.y = roofY; eave3.y = roofY;

            // Slope 1: eave0 -> ridgeA -> ridgeB -> eave3
            AddRoofQuad(eave0, ridgeA, ridgeB, eave3, td);
            // Slope 2: eave1 -> eave2 -> ridgeB -> ridgeA
            AddRoofQuad(eave1, eave2, ridgeB, ridgeA, td);

            // Gable triangles (triangular ends)
            AddRoofTriangle(eave0, eave1, ridgeA, td);
            AddRoofTriangle(eave2, eave3, ridgeB, td);

            // Flat cap at roofY to close the footprint (so interior is not visible)
            TriangulatePolygon(pts, roofY, td.roofTriangles, td);
        }

        /// <summary>
        /// Hipped roof (all four edges slope inward to a ridge or point).
        /// </summary>
        private static void GenerateHippedRoof(List<Vector3> pts, float roofY, TileBuildingMeshData td)
        {
            GetLongestAxis(pts, out Vector3 axisDir, out Vector3 perpDir, out float axisLen, out float perpLen);
            Vector3 center = ComputeCentroid(pts);

            float ridgeHeight = perpLen * ROOF_SLOPE;
            float halfPerp = perpLen * 0.5f;
            float ridgeHalfLen = Mathf.Max(0, (axisLen - perpLen) * 0.5f);

            Vector3 ridgeA = center + axisDir * ridgeHalfLen;
            Vector3 ridgeB = center - axisDir * ridgeHalfLen;
            ridgeA.y = roofY + ridgeHeight;
            ridgeB.y = roofY + ridgeHeight;

            // Four eave corners
            Vector3 e0 = center + axisDir * (axisLen * 0.5f + ROOF_OVERHANG) + perpDir * (halfPerp + ROOF_OVERHANG);
            Vector3 e1 = center + axisDir * (axisLen * 0.5f + ROOF_OVERHANG) - perpDir * (halfPerp + ROOF_OVERHANG);
            Vector3 e2 = center - axisDir * (axisLen * 0.5f + ROOF_OVERHANG) - perpDir * (halfPerp + ROOF_OVERHANG);
            Vector3 e3 = center - axisDir * (axisLen * 0.5f + ROOF_OVERHANG) + perpDir * (halfPerp + ROOF_OVERHANG);
            e0.y = roofY; e1.y = roofY; e2.y = roofY; e3.y = roofY;

            if (ridgeHalfLen > 0.01f)
            {
                // Long sides: quads
                AddRoofQuad(e0, ridgeA, ridgeB, e3, td);
                AddRoofQuad(e1, e2, ridgeB, ridgeA, td);
                // Short sides: triangles (hip ends)
                AddRoofTriangle(e0, e1, ridgeA, td);
                AddRoofTriangle(e2, e3, ridgeB, td);
            }
            else
            {
                // Nearly square: 4 triangles to a single peak
                Vector3 peak = center;
                peak.y = roofY + ridgeHeight;
                AddRoofTriangle(e0, e1, peak, td);
                AddRoofTriangle(e1, e2, peak, td);
                AddRoofTriangle(e2, e3, peak, td);
                AddRoofTriangle(e3, e0, peak, td);
            }

            // Close underside
            TriangulatePolygon(pts, roofY, td.roofTriangles, td);
        }

        /// <summary>
        /// Pyramidal roof (single peak at centroid).
        /// </summary>
        private static void GeneratePyramidalRoof(List<Vector3> pts, float roofY, TileBuildingMeshData td)
        {
            Vector3 center = ComputeCentroid(pts);

            // Height based on half the average "radius"
            float avgDist = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 d = pts[i] - center;
                avgDist += new Vector2(d.x, d.z).magnitude;
            }
            avgDist /= pts.Count;

            Vector3 peak = center;
            peak.y = roofY + avgDist * ROOF_SLOPE;

            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                Vector3 a = new Vector3(pts[i].x, roofY, pts[i].z);
                Vector3 b = new Vector3(pts[(i + 1) % n].x, roofY, pts[(i + 1) % n].z);
                AddRoofTriangle(a, b, peak, td);
            }

            // Close underside
            TriangulatePolygon(pts, roofY, td.roofTriangles, td);
        }

        // ── Roof geometry helpers ──────────────────────────────────────────

        private static void AddRoofQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, TileBuildingMeshData td)
        {
            int idx = td.vertices.Count;
            td.vertices.Add(a); td.vertices.Add(b); td.vertices.Add(c); td.vertices.Add(d);

            td.uvs.Add(new Vector2(0, 0));
            td.uvs.Add(new Vector2(1, 0));
            td.uvs.Add(new Vector2(1, 1));
            td.uvs.Add(new Vector2(0, 1));

            td.roofTriangles.Add(idx);     td.roofTriangles.Add(idx + 1); td.roofTriangles.Add(idx + 2);
            td.roofTriangles.Add(idx);     td.roofTriangles.Add(idx + 2); td.roofTriangles.Add(idx + 3);
        }

        private static void AddRoofTriangle(Vector3 a, Vector3 b, Vector3 c, TileBuildingMeshData td)
        {
            int idx = td.vertices.Count;
            td.vertices.Add(a); td.vertices.Add(b); td.vertices.Add(c);

            td.uvs.Add(new Vector2(0, 0));
            td.uvs.Add(new Vector2(1, 0));
            td.uvs.Add(new Vector2(0.5f, 1));

            td.roofTriangles.Add(idx); td.roofTriangles.Add(idx + 1); td.roofTriangles.Add(idx + 2);
        }

        // ── Axis detection for roof orientation ────────────────────────────

        private static void GetLongestAxis(List<Vector3> pts,
            out Vector3 axisDir, out Vector3 perpDir,
            out float axisLen, out float perpLen)
        {
            // Find the two points that are furthest apart on XZ plane (approximate principal axis)
            float maxDist = 0;
            int ai = 0, bi = 1;
            for (int i = 0; i < pts.Count; i++)
            {
                for (int j = i + 1; j < pts.Count; j++)
                {
                    float dx = pts[j].x - pts[i].x;
                    float dz = pts[j].z - pts[i].z;
                    float d2 = dx * dx + dz * dz;
                    if (d2 > maxDist)
                    {
                        maxDist = d2;
                        ai = i; bi = j;
                    }
                }
            }

            Vector3 diff = pts[bi] - pts[ai];
            axisDir = new Vector3(diff.x, 0, diff.z).normalized;
            perpDir = new Vector3(-axisDir.z, 0, axisDir.x); // 90 degree rotation on XZ

            axisLen = Mathf.Sqrt(maxDist);

            // Perpendicular extent
            float minProj = float.MaxValue, maxProj = float.MinValue;
            Vector3 center = ComputeCentroid(pts);
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 rel = pts[i] - center;
                float proj = rel.x * perpDir.x + rel.z * perpDir.z;
                if (proj < minProj) minProj = proj;
                if (proj > maxProj) maxProj = proj;
            }
            perpLen = maxProj - minProj;
        }

        private static Vector3 ComputeCentroid(List<Vector3> pts)
        {
            Vector3 c = Vector3.zero;
            for (int i = 0; i < pts.Count; i++) c += pts[i];
            return c / pts.Count;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  EAR-CLIPPING TRIANGULATION (for flat roof caps / arbitrary polygons)
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Triangulate an arbitrary polygon on the XZ plane at a given Y height.
        /// Adds vertices and triangles to the target submesh list.
        /// </summary>
        private static void TriangulatePolygon(
            List<Vector3> pts, float y,
            List<int> targetTris, TileBuildingMeshData td)
        {
            if (pts.Count < 3) return;

            // Add roof cap vertices
            int baseIdx = td.vertices.Count;
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;

            for (int i = 0; i < pts.Count; i++)
            {
                if (pts[i].x < minX) minX = pts[i].x;
                if (pts[i].x > maxX) maxX = pts[i].x;
                if (pts[i].z < minZ) minZ = pts[i].z;
                if (pts[i].z > maxZ) maxZ = pts[i].z;
            }

            float spanX = Mathf.Max(maxX - minX, 0.01f);
            float spanZ = Mathf.Max(maxZ - minZ, 0.01f);

            for (int i = 0; i < pts.Count; i++)
            {
                td.vertices.Add(new Vector3(pts[i].x, y, pts[i].z));
                td.uvs.Add(new Vector2(
                    (pts[i].x - minX) / spanX,
                    (pts[i].z - minZ) / spanZ));
            }

            // Signed area to determine winding
            float signedArea = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 a = pts[i];
                Vector3 b = pts[(i + 1) % pts.Count];
                signedArea += (b.x - a.x) * (b.z + a.z);
            }
            bool isCW = signedArea > 0;

            // Ear clipping
            List<int> indices = new List<int>(pts.Count);
            for (int i = 0; i < pts.Count; i++) indices.Add(i);

            int safety = pts.Count * pts.Count;
            while (indices.Count > 2 && safety-- > 0)
            {
                bool earFound = false;
                for (int i = 0; i < indices.Count; i++)
                {
                    int iPrev = (i - 1 + indices.Count) % indices.Count;
                    int iNext = (i + 1) % indices.Count;

                    Vector3 a = pts[indices[iPrev]];
                    Vector3 b = pts[indices[i]];
                    Vector3 c = pts[indices[iNext]];

                    float cross = (b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x);
                    bool isConvex = isCW ? cross < 0 : cross > 0;
                    if (!isConvex) continue;

                    bool hasInside = false;
                    for (int j = 0; j < indices.Count; j++)
                    {
                        if (j == iPrev || j == i || j == iNext) continue;
                        if (PointInTriangleXZ(pts[indices[j]], a, b, c))
                        {
                            hasInside = true;
                            break;
                        }
                    }
                    if (hasInside) continue;

                    int vA = baseIdx + indices[iPrev];
                    int vB = baseIdx + indices[i];
                    int vC = baseIdx + indices[iNext];

                    if (isCW)
                    {
                        targetTris.Add(vA); targetTris.Add(vC); targetTris.Add(vB);
                    }
                    else
                    {
                        targetTris.Add(vA); targetTris.Add(vB); targetTris.Add(vC);
                    }

                    indices.RemoveAt(i);
                    earFound = true;
                    break;
                }

                if (!earFound) break;
            }
        }

        private static bool PointInTriangleXZ(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            float d1 = SignXZ(p, a, b);
            float d2 = SignXZ(p, b, c);
            float d3 = SignXZ(p, c, a);
            bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(hasNeg && hasPos);
        }

        private static float SignXZ(Vector3 p1, Vector3 p2, Vector3 p3)
        {
            return (p1.x - p3.x) * (p2.z - p3.z) - (p2.x - p3.x) * (p1.z - p3.z);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  COLOR UTILITIES
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Pick a random Mediterranean wall colour, or parse the OSM building:colour tag.
        /// </summary>
        public static Color ResolveWallColour(string colourTag, System.Random rng)
        {
            if (!string.IsNullOrEmpty(colourTag))
            {
                Color parsed;
                if (TryParseColour(colourTag, out parsed))
                    return parsed;
            }

            // Random warm Mediterranean tone with slight HSV variation
            Color baseC = WarmPalette[rng.Next(WarmPalette.Length)];
            Color.RGBToHSV(baseC, out float h, out float s, out float v);
            h += (float)(rng.NextDouble() * 0.03 - 0.015);
            s += (float)(rng.NextDouble() * 0.1  - 0.05);
            v += (float)(rng.NextDouble() * 0.06 - 0.03);
            s = Mathf.Clamp01(s);
            v = Mathf.Clamp01(v);
            return Color.HSVToRGB(Mathf.Repeat(h, 1f), s, v);
        }

        /// <summary>
        /// Parse colour from hex (#RRGGBB / #RGB) or named CSS colour.
        /// </summary>
        public static bool TryParseColour(string input, out Color result)
        {
            result = Color.white;
            if (string.IsNullOrEmpty(input)) return false;

            input = input.Trim().ToLowerInvariant();

            // Hex
            if (input.StartsWith("#"))
            {
                if (ColorUtility.TryParseHtmlString(input, out result))
                    return true;
            }

            // Named colours (common OSM values)
            switch (input)
            {
                case "white":       result = Color.white; return true;
                case "black":       result = Color.black; return true;
                case "red":         result = new Color(0.8f, 0.2f, 0.15f); return true;
                case "green":       result = new Color(0.3f, 0.6f, 0.3f); return true;
                case "blue":        result = new Color(0.2f, 0.3f, 0.7f); return true;
                case "yellow":      result = new Color(0.95f, 0.85f, 0.4f); return true;
                case "orange":      result = new Color(0.9f, 0.6f, 0.2f); return true;
                case "brown":       result = new Color(0.55f, 0.35f, 0.2f); return true;
                case "grey": case "gray":
                                    result = new Color(0.6f, 0.6f, 0.6f); return true;
                case "beige":       result = new Color(0.96f, 0.90f, 0.72f); return true;
                case "cream":       result = new Color(0.98f, 0.95f, 0.85f); return true;
                case "pink":        result = new Color(0.95f, 0.75f, 0.75f); return true;
                case "terracotta":  result = new Color(0.82f, 0.55f, 0.40f); return true;
            }

            return false;
        }
    }
}
