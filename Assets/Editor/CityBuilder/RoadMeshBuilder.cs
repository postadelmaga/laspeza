using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CityBuilder
{
    /// <summary>
    /// Road data in world coordinates (Vector3), ready for mesh generation.
    /// Converted from OsmData.WorldRoadData (LatLon) by the UI pipeline.
    /// </summary>
    public class WorldRoadData
    {
        public List<Vector3> centerline;
        public string highwayType, surface, name;
        public int lanes;
        public float width;
        public bool oneway;
    }

    /// <summary>
    /// Generates proper mesh-based roads with sidewalks and lane markings,
    /// replacing the old LineRenderer approach.
    /// </summary>
    public static class RoadMeshBuilder
    {
        // ── Default widths per highway type (in real meters) ──
        private static readonly Dictionary<string, float> DefaultWidths = new Dictionary<string, float>
        {
            { "motorway",    14.0f },
            { "trunk",       10.5f },
            { "primary",      7.0f },
            { "secondary",    6.0f },
            { "tertiary",     5.0f },
            { "residential",  4.0f },
            { "service",      3.0f },
            { "footway",      1.5f },
            { "cycleway",     1.5f },
            { "path",         1.0f },
        };

        // ── Sidewalk widths per road class ──
        private static readonly HashSet<string> MainRoadTypes = new HashSet<string>
        {
            "motorway", "trunk", "primary", "secondary", "tertiary"
        };

        // ── Y offsets to prevent z-fighting ──
        private const float ROAD_Y_OFFSET     = 0.25f;
        private const float SIDEWALK_Y_OFFSET  = 0.35f;   // road + 0.10 above road
        private const float MARKING_Y_OFFSET   = 0.27f;   // just above road surface

        // ── Minimum segment distance (skip degenerate points) ──
        private const float MIN_SEGMENT_DIST   = 0.05f;

        // ── Catmull-Rom subdivision ──
        private const int   CATMULL_SUBDIVISIONS = 4;
        private const float CORNER_ANGLE_THRESHOLD = 15f; // degrees

        // ================================================================
        //  PUBLIC ENTRY POINT
        // ================================================================

        /// <summary>
        /// Generates mesh-based roads, sidewalks, and lane markings for all
        /// supplied road data.  Results are batched into a few combined meshes.
        /// </summary>
        public static async Task GenerateRoadsAsync(
            List<WorldRoadData> roads,
            GameObject parent,
            Terrain[,] terrains, int gridCount,
            float tileWidthM, float tileLengthM)
        {
            if (roads == null || roads.Count == 0) return;

            // Accumulators per road type (asphalt / sidewalk / markings)
            TileMeshData asphaltMesh  = new TileMeshData();
            TileMeshData sidewalkMesh = new TileMeshData();
            TileMeshData markingMesh  = new TileMeshData();

            for (int i = 0; i < roads.Count; i++)
            {
                if (i % 80 == 0)
                {
                    EditorUtility.DisplayProgressBar(
                        "RoadMeshBuilder",
                        $"Generazione strade {i}/{roads.Count}...",
                        (float)i / roads.Count);
                    await Task.Yield();
                }

                WorldRoadData road = roads[i];
                if (road.centerline == null || road.centerline.Count < 2) continue;

                // Clean and smooth the centerline
                List<Vector3> cleaned = CleanCenterline(road.centerline);
                if (cleaned.Count < 2) continue;

                List<Vector3> smoothed = SmoothCenterline(cleaned);
                if (smoothed.Count < 2) continue;

                // Proietta Y sui terreni
                if (terrains != null)
                {
                    for (int p = 0; p < smoothed.Count; p++)
                    {
                        Vector3 pt = smoothed[p];
                        int gx = Mathf.Clamp((int)(pt.x / tileWidthM), 0, gridCount - 1);
                        int gz = Mathf.Clamp((int)(pt.z / tileLengthM), 0, gridCount - 1);
                        if (terrains[gx, gz] != null)
                        {
                            pt.y = terrains[gx, gz].SampleHeight(new Vector3(pt.x, 0, pt.z));
                            smoothed[p] = pt;
                        }
                    }
                }

                float halfWidth = ResolveHalfWidth(road);
                bool isMain = MainRoadTypes.Contains(road.highwayType ?? "");
                float sidewalkW = isMain ? 1.5f : 1.0f;

                // 1. Road surface
                ExtrudeStrip(smoothed, halfWidth, ROAD_Y_OFFSET, asphaltMesh);

                // 2. Sidewalks (skip for footway / cycleway / path)
                if (!IsFootpath(road.highwayType))
                {
                    ExtrudeStrip(smoothed, halfWidth + sidewalkW, SIDEWALK_Y_OFFSET, sidewalkMesh,
                                 innerOffset: halfWidth + 0.02f);
                    ExtrudeStrip(smoothed, -(halfWidth + 0.02f), SIDEWALK_Y_OFFSET, sidewalkMesh,
                                 innerOffset: -(halfWidth + sidewalkW));
                }

                // 3. Lane markings
                GenerateMarkings(smoothed, road, halfWidth, markingMesh);
            }

            EditorUtility.DisplayProgressBar("RoadMeshBuilder", "Creazione mesh finali...", 0.95f);
            await Task.Yield();

            // ── Materials ──
            Material asphaltMat  = CreateAsphaltMaterial();
            Material sidewalkMat = CreateSidewalkMaterial();
            Material markingMat  = CreateMarkingMaterial();

            // ── Instantiate combined GameObjects ──
            CreateChunkedMesh("Strade_Asfalto", asphaltMesh, asphaltMat, parent);
            CreateChunkedMesh("Marciapiedi", sidewalkMesh, sidewalkMat, parent);
            CreateChunkedMesh("Segnaletica", markingMesh, markingMat, parent);

            // Libera RAM immediatamente
            asphaltMesh.Free();
            sidewalkMesh.Free();
            markingMesh.Free();

            EditorUtility.ClearProgressBar();
            Debug.Log($"RoadMeshBuilder: {roads.Count} strade generate.");
        }

        // ================================================================
        //  CENTERLINE PROCESSING
        // ================================================================

        /// <summary>
        /// Removes consecutive points that are too close together.
        /// </summary>
        private static List<Vector3> CleanCenterline(List<Vector3> pts)
        {
            List<Vector3> result = new List<Vector3>(pts.Count);
            result.Add(pts[0]);
            for (int i = 1; i < pts.Count; i++)
            {
                if (Vector3.Distance(pts[i], result[result.Count - 1]) >= MIN_SEGMENT_DIST)
                    result.Add(pts[i]);
            }
            return result;
        }

        /// <summary>
        /// Applies Catmull-Rom subdivision at corners where direction changes
        /// exceed the angle threshold, producing smooth curves.
        /// </summary>
        private static List<Vector3> SmoothCenterline(List<Vector3> pts)
        {
            if (pts.Count < 3) return new List<Vector3>(pts);

            List<Vector3> result = new List<Vector3>();
            result.Add(pts[0]);

            for (int i = 1; i < pts.Count - 1; i++)
            {
                Vector3 prev = pts[i - 1];
                Vector3 curr = pts[i];
                Vector3 next = pts[i + 1];

                Vector3 dirIn  = (curr - prev).normalized;
                Vector3 dirOut = (next - curr).normalized;
                float angle = Vector3.Angle(dirIn, dirOut);

                if (angle > CORNER_ANGLE_THRESHOLD)
                {
                    // Use Catmull-Rom through (prev, curr, next) with virtual control points
                    Vector3 p0 = (i >= 2) ? pts[i - 2] : prev - (curr - prev);
                    Vector3 p1 = prev;
                    Vector3 p2 = curr;
                    Vector3 p3 = next;

                    // Subdivide the segment p1->p2 with Catmull-Rom influence
                    for (int s = 1; s <= CATMULL_SUBDIVISIONS; s++)
                    {
                        float t = (float)s / (CATMULL_SUBDIVISIONS + 1);
                        result.Add(CatmullRom(p0, p1, p2, p3, t));
                    }

                    // Also subdivide p2->p3
                    Vector3 p4 = (i + 2 < pts.Count) ? pts[i + 2] : next + (next - curr);
                    for (int s = 1; s <= CATMULL_SUBDIVISIONS; s++)
                    {
                        float t = (float)s / (CATMULL_SUBDIVISIONS + 1);
                        result.Add(CatmullRom(p1, p2, p3, p4, t));
                    }
                }
                else
                {
                    result.Add(curr);
                }
            }

            result.Add(pts[pts.Count - 1]);
            return result;
        }

        /// <summary>
        /// Catmull-Rom spline evaluation at parameter t in [0,1] for segment p1->p2.
        /// </summary>
        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * (
                (2f * p1) +
                (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        // ================================================================
        //  MESH EXTRUSION
        // ================================================================

        /// <summary>
        /// Extrudes a flat quad strip along the centerline.
        /// If innerOffset is non-zero, creates a strip between innerOffset and outerOffset
        /// (used for sidewalks that sit beside the road).
        /// </summary>
        private static void ExtrudeStrip(
            List<Vector3> centerline,
            float halfWidth,
            float yOffset,
            TileMeshData td,
            float innerOffset = 0f)
        {
            bool isSidewalkStrip = (innerOffset != 0f);
            int startV = td.vertices.Count;

            for (int i = 0; i < centerline.Count; i++)
            {
                Vector3 forward;
                if (i == 0)
                    forward = (centerline[1] - centerline[0]).normalized;
                else if (i == centerline.Count - 1)
                    forward = (centerline[i] - centerline[i - 1]).normalized;
                else
                    forward = ((centerline[i + 1] - centerline[i]).normalized +
                               (centerline[i] - centerline[i - 1]).normalized).normalized;

                // Right perpendicular on XZ plane
                Vector3 right = Vector3.Cross(forward, Vector3.up).normalized;
                if (right.sqrMagnitude < 0.001f)
                    right = Vector3.right;

                Vector3 center = centerline[i] + Vector3.up * yOffset;

                if (isSidewalkStrip)
                {
                    // Two edges defined by innerOffset and halfWidth (which acts as outer)
                    float leftDist, rightDist;
                    if (innerOffset > 0f)
                    {
                        // Right-side sidewalk
                        leftDist  = innerOffset;
                        rightDist = halfWidth;
                    }
                    else
                    {
                        // Left-side sidewalk (innerOffset is negative, halfWidth is negative)
                        leftDist  = halfWidth;
                        rightDist = innerOffset;
                    }
                    td.vertices.Add(center + right * leftDist);
                    td.vertices.Add(center + right * rightDist);
                }
                else
                {
                    td.vertices.Add(center - right * Mathf.Abs(halfWidth));
                    td.vertices.Add(center + right * Mathf.Abs(halfWidth));
                }
            }

            // Triangles: two tris per quad
            for (int i = 0; i < centerline.Count - 1; i++)
            {
                int v0 = startV + i * 2;
                int v1 = v0 + 1;
                int v2 = v0 + 2;
                int v3 = v0 + 3;

                td.triangles.Add(v0); td.triangles.Add(v2); td.triangles.Add(v1);
                td.triangles.Add(v1); td.triangles.Add(v2); td.triangles.Add(v3);
            }
        }

        // ================================================================
        //  LANE MARKINGS
        // ================================================================

        /// <summary>
        /// Generates thin mesh strips for lane markings:
        ///  - Center dashed line for 2-lane roads
        ///  - Edge lines on both sides
        /// </summary>
        private static void GenerateMarkings(
            List<Vector3> centerline,
            WorldRoadData road,
            float halfWidth,
            TileMeshData td)
        {
            if (IsFootpath(road.highwayType)) return;
            // Solo strade principali: evita milioni di vertici per stradine secondarie
            string ht = road.highwayType ?? "";
            if (ht != "motorway" && ht != "trunk" && ht != "primary" && ht != "secondary") return;

            const float EDGE_LINE_W  = 0.08f;
            const float CENTER_LINE_W = 0.10f;
            const float DASH_LENGTH  = 3.0f;
            const float GAP_LENGTH   = 3.0f;

            int effectiveLanes = road.lanes > 0 ? road.lanes : GuessByType(road.highwayType);

            // Edge lines (solid, thin strips at the road edges)
            ExtrudeStrip(centerline, halfWidth - 0.05f, MARKING_Y_OFFSET, td,
                         innerOffset: halfWidth - 0.05f - EDGE_LINE_W);
            ExtrudeStrip(centerline, -(halfWidth - 0.05f - EDGE_LINE_W), MARKING_Y_OFFSET, td,
                         innerOffset: -(halfWidth - 0.05f));

            // Center dashed line for 2-lane, non-oneway roads
            if (effectiveLanes == 2 && !road.oneway)
            {
                GenerateDashedStrip(centerline, CENTER_LINE_W, MARKING_Y_OFFSET, td,
                                    DASH_LENGTH, GAP_LENGTH);
            }
            // Multi-lane: solid center line
            else if (effectiveLanes >= 3)
            {
                ExtrudeStrip(centerline, CENTER_LINE_W * 0.5f, MARKING_Y_OFFSET, td);
            }
        }

        /// <summary>
        /// Generates a dashed center line by selecting sub-segments of the centerline.
        /// </summary>
        private static void GenerateDashedStrip(
            List<Vector3> centerline,
            float width,
            float yOffset,
            TileMeshData td,
            float dashLen,
            float gapLen)
        {
            float halfW = width * 0.5f;
            float accumulated = 0f;
            bool drawing = true;
            List<Vector3> segment = new List<Vector3>();
            segment.Add(centerline[0]);

            for (int i = 1; i < centerline.Count; i++)
            {
                float dist = Vector3.Distance(centerline[i], centerline[i - 1]);
                float remaining = dist;
                Vector3 dir = (centerline[i] - centerline[i - 1]).normalized;
                Vector3 pos = centerline[i - 1];

                while (remaining > 0.01f)
                {
                    float target = drawing ? dashLen : gapLen;
                    float needed = target - accumulated;

                    if (needed <= remaining)
                    {
                        pos += dir * needed;
                        remaining -= needed;
                        accumulated = 0f;

                        if (drawing)
                        {
                            segment.Add(pos);
                            if (segment.Count >= 2)
                                ExtrudeStrip(segment, halfW, yOffset, td);
                            segment.Clear();
                        }
                        segment.Add(pos);
                        drawing = !drawing;
                    }
                    else
                    {
                        accumulated += remaining;
                        remaining = 0f;
                    }
                }

                if (drawing)
                    segment.Add(centerline[i]);
            }

            // Flush last dash segment
            if (drawing && segment.Count >= 2)
                ExtrudeStrip(segment, halfW, yOffset, td);
        }

        // ================================================================
        //  MATERIALS
        // ================================================================

        private static Shader FindCompatibleShader()
        {
            Shader s = Shader.Find("Universal Render Pipeline/Lit");
            if (s != null) return s;
            s = Shader.Find("HDRP/Lit");
            if (s != null) return s;
            s = Shader.Find("Standard");
            if (s != null) return s;
            return Shader.Find("Unlit/Color");
        }

        private static Material CreateAsphaltMaterial()
        {
            Shader shader = FindCompatibleShader();
            Material mat = new Material(shader);
            // Asfalto scuro realistico con leggera tonalita' calda (usura)
            mat.color = new Color(0.18f, 0.17f, 0.16f);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.08f);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.08f);
            if (mat.HasProperty("_Metallic"))   mat.SetFloat("_Metallic", 0.0f);
            // Leggero riflesso bagnato per profondita' visiva
            mat.EnableKeyword("_EMISSION");
            if (mat.HasProperty("_EmissionColor"))
                mat.SetColor("_EmissionColor", new Color(0.03f, 0.03f, 0.03f));
            mat.name = "Asfalto_Road";
            return mat;
        }

        private static Material CreateSidewalkMaterial()
        {
            Shader shader = FindCompatibleShader();
            Material mat = new Material(shader);
            // Marciapiede beige/grigio caldo — tipico cemento italiano invecchiato
            mat.color = new Color(0.58f, 0.54f, 0.50f);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.03f);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.03f);
            if (mat.HasProperty("_Metallic"))   mat.SetFloat("_Metallic", 0.0f);
            mat.name = "Marciapiede_Concrete";
            return mat;
        }

        private static Material CreateMarkingMaterial()
        {
            Shader shader = FindCompatibleShader();
            Material mat = new Material(shader);
            // Segnaletica orizzontale: bianco leggermente consumato
            mat.color = new Color(0.92f, 0.90f, 0.86f);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.15f);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.15f);
            if (mat.HasProperty("_Metallic"))   mat.SetFloat("_Metallic", 0.0f);
            mat.EnableKeyword("_EMISSION");
            if (mat.HasProperty("_EmissionColor"))
                mat.SetColor("_EmissionColor", new Color(0.6f, 0.58f, 0.55f) * 0.12f);
            mat.name = "Segnaletica_Bianca";
            return mat;
        }

        // ================================================================
        //  MESH INSTANTIATION
        // ================================================================

        /// <summary>
        /// Splitta mesh grandi in chunk da max 60k vertici (UInt16).
        /// </summary>
        private static void CreateChunkedMesh(string name, TileMeshData td, Material mat, GameObject parent)
        {
            if (td.vertices.Count == 0) return;

            const int MAX_VERTS = 60000;
            if (td.vertices.Count <= MAX_VERTS)
            {
                CreateMeshObject(name, td, mat, parent);
                return;
            }

            // Splitta per gruppi di triangoli
            int chunkIdx = 0;
            int triIdx = 0;
            while (triIdx < td.triangles.Count)
            {
                var chunk = new TileMeshData();
                var vertMap = new Dictionary<int, int>();

                while (triIdx < td.triangles.Count && chunk.vertices.Count < MAX_VERTS - 3)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int oldIdx = td.triangles[triIdx + k];
                        if (!vertMap.TryGetValue(oldIdx, out int newIdx))
                        {
                            newIdx = chunk.vertices.Count;
                            vertMap[oldIdx] = newIdx;
                            chunk.vertices.Add(td.vertices[oldIdx]);
                        }
                        chunk.triangles.Add(newIdx);
                    }
                    triIdx += 3;
                }

                CreateMeshObject($"{name}_{chunkIdx}", chunk, mat, parent);
                chunkIdx++;
            }
        }

        private static void CreateMeshObject(string name, TileMeshData td, Material mat, GameObject parent)
        {
            GameObject go = new GameObject(name);
            go.transform.parent = parent.transform;

            Mesh mesh = new Mesh();
            mesh.indexFormat = td.vertices.Count < 65000
                ? UnityEngine.Rendering.IndexFormat.UInt16
                : UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices  = td.vertices.ToArray();
            mesh.triangles = td.triangles.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        // ================================================================
        //  HELPERS
        // ================================================================

        private static float ResolveHalfWidth(WorldRoadData road)
        {
            if (road.width > 0.1f)
                return road.width * 0.5f;

            if (road.lanes > 0)
                return road.lanes * 3.5f * 0.5f;

            string type = road.highwayType ?? "";
            if (DefaultWidths.TryGetValue(type, out float w))
                return w * 0.5f;

            return 4.0f * 0.5f; // 4m fallback
        }

        private static int GuessByType(string highwayType)
        {
            switch (highwayType ?? "")
            {
                case "motorway":   return 4;
                case "trunk":      return 3;
                case "primary":
                case "secondary":  return 2;
                default:           return 2;
            }
        }

        private static bool IsFootpath(string highwayType)
        {
            switch (highwayType ?? "")
            {
                case "footway":
                case "cycleway":
                case "path":
                case "steps":
                case "pedestrian":
                    return true;
                default:
                    return false;
            }
        }
    }
}
