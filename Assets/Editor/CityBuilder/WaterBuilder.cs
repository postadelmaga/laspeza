using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CityBuilder
{
    /// <summary>
    /// Water data in world coordinates (Vector3), ready for mesh generation.
    /// Converted from OsmData.WorldWaterData (LatLon) by the UI pipeline.
    /// </summary>
    public class WorldWaterData
    {
        public List<Vector3> polygon;
        public string waterType; // "sea", "river", "lake", "harbour"
    }

    /// <summary>
    /// Generates water surface meshes for sea, rivers, lakes, and harbour areas.
    /// Handles both closed polygons (lakes) and open coastlines (sea).
    /// </summary>
    public static class WaterBuilder
    {
        // ================================================================
        //  PUBLIC ENTRY POINT
        // ================================================================

        /// <summary>
        /// Generates water surface meshes for all supplied water features.
        /// Always generates a base sea plane at sea level covering the entire
        /// terrain, so that any area below sea level is visually covered by water.
        /// </summary>
        public static async Task GenerateWaterAsync(
            List<WorldWaterData> waterFeatures,
            GameObject parent,
            float seaLevelY,
            float terrainWidth, float terrainLength)
        {
            if (waterFeatures == null) waterFeatures = new List<WorldWaterData>();

            // Separate features by type
            List<WorldWaterData> seaFeatures     = new List<WorldWaterData>();
            List<WorldWaterData> harbourFeatures  = new List<WorldWaterData>();
            List<WorldWaterData> inlandFeatures   = new List<WorldWaterData>(); // rivers, lakes

            foreach (var wf in waterFeatures)
            {
                string type = (wf.waterType ?? "").ToLowerInvariant();
                if (type == "sea" || type == "coastline")
                    seaFeatures.Add(wf);
                else if (type == "harbour" || type == "marina")
                    harbourFeatures.Add(wf);
                else
                    inlandFeatures.Add(wf);
            }

            int total = waterFeatures.Count;
            int done = 0;

            // ── Sea plane: always generate a base ocean quad at sea level ──
            // This ensures that any terrain below sea level is covered by water,
            // regardless of whether OSM coastline data is available.
            {
                EditorUtility.DisplayProgressBar("WaterBuilder", "Generazione mare...", 0.1f);
                await Task.Yield();

                TileMeshData seaMesh = new TileMeshData();

                // Always create a full-terrain sea plane at sea level
                GenerateSeaQuad(seaMesh, seaLevelY, terrainWidth, terrainLength, null);

                // Also add any OSM sea/coastline polygons on top
                foreach (var sea in seaFeatures)
                {
                    if (sea.polygon != null && sea.polygon.Count >= 3)
                        TriangulatePolygon(sea.polygon, seaLevelY, seaMesh);
                    done++;
                }

                if (seaMesh.vertices.Count > 0)
                {
                    // Mare Ligure: blu profondo con riflessi turchesi
                    Material seaMat = CreateWaterMaterial(
                        new Color(0.04f, 0.12f, 0.25f, 0.88f),
                        new Color(0.02f, 0.08f, 0.18f) * 0.4f);
                    CreateMeshObject("Acqua_Mare", seaMesh, seaMat, parent);
                }
            }

            // ── Harbour / marina ──
            if (harbourFeatures.Count > 0)
            {
                EditorUtility.DisplayProgressBar("WaterBuilder", "Generazione porto...", 0.4f);
                await Task.Yield();

                TileMeshData harbourMesh = new TileMeshData();
                foreach (var hf in harbourFeatures)
                {
                    if (hf.polygon == null || hf.polygon.Count < 3) continue;
                    TriangulatePolygon(hf.polygon, seaLevelY, harbourMesh);
                    done++;
                }

                if (harbourMesh.vertices.Count > 0)
                {
                    // Slightly different tint for harbour water (murkier, greener)
                    Material harbourMat = CreateWaterMaterial(
                        new Color(0.08f, 0.18f, 0.25f, 0.88f),
                        new Color(0.03f, 0.08f, 0.12f) * 0.25f);
                    CreateMeshObject("Acqua_Porto", harbourMesh, harbourMat, parent);
                }
            }

            // ── Rivers, lakes, and other inland water ──
            if (inlandFeatures.Count > 0)
            {
                EditorUtility.DisplayProgressBar("WaterBuilder", "Generazione fiumi e laghi...", 0.7f);
                await Task.Yield();

                TileMeshData inlandMesh = new TileMeshData();
                foreach (var wf in inlandFeatures)
                {
                    if (wf.polygon == null || wf.polygon.Count < 3) continue;

                    if (done % 50 == 0)
                    {
                        EditorUtility.DisplayProgressBar("WaterBuilder",
                            $"Acqua interna {done}/{total}...",
                            0.7f + 0.25f * ((float)done / Mathf.Max(total, 1)));
                        await Task.Yield();
                    }

                    TriangulatePolygon(wf.polygon, seaLevelY, inlandMesh);
                    done++;
                }

                if (inlandMesh.vertices.Count > 0)
                {
                    // Freshwater: slightly lighter, bluer
                    Material inlandMat = CreateWaterMaterial(
                        new Color(0.06f, 0.20f, 0.35f, 0.80f),
                        new Color(0.02f, 0.08f, 0.18f) * 0.2f);
                    CreateMeshObject("Acqua_Fiumi_Laghi", inlandMesh, inlandMat, parent);
                }
            }

            EditorUtility.ClearProgressBar();
            Debug.Log($"WaterBuilder: {waterFeatures.Count} feature acquatiche generate " +
                      $"(mare: {seaFeatures.Count}, porto: {harbourFeatures.Count}, " +
                      $"interno: {inlandFeatures.Count})");
        }

        // ================================================================
        //  SEA QUAD (for open coastlines)
        // ================================================================

        /// <summary>
        /// Generates a large quad covering the sea area within terrain bounds.
        /// If a coastline polyline is provided, the quad extends from the
        /// coastline outward to the terrain edge.  Otherwise, covers the
        /// entire terrain footprint at sea level.
        /// </summary>
        private static void GenerateSeaQuad(
            TileMeshData td,
            float seaLevelY,
            float terrainWidth,
            float terrainLength,
            List<Vector3> coastline)
        {
            int startV = td.vertices.Count;

            if (coastline != null && coastline.Count >= 2)
            {
                // Build a polygon from the coastline extended to terrain edges.
                // Strategy: take the coastline points, then close the polygon
                // by walking along the terrain boundary on the sea side.

                // Determine which side is "sea" using the average normal direction.
                Vector3 avgRight = Vector3.zero;
                for (int i = 0; i < coastline.Count - 1; i++)
                {
                    Vector3 dir = (coastline[i + 1] - coastline[i]).normalized;
                    avgRight += Vector3.Cross(dir, Vector3.up);
                }
                avgRight.Normalize();

                // Test: a point offset to the right of the coastline midpoint.
                // If it's nearer to the terrain edge, sea is on the right.
                Vector3 mid = coastline[coastline.Count / 2];
                Vector3 testRight = mid + avgRight * 50f;
                Vector3 testLeft  = mid - avgRight * 50f;
                bool seaOnRight = DistToEdge(testRight, terrainWidth, terrainLength)
                                < DistToEdge(testLeft, terrainWidth, terrainLength);

                // Build the sea polygon: coastline + boundary walk
                List<Vector3> seaPoly = new List<Vector3>();

                // Add coastline in order
                foreach (var pt in coastline)
                    seaPoly.Add(new Vector3(pt.x, seaLevelY, pt.z));

                // From the last coastline point, project to the nearest terrain edge
                // on the sea side, walk corners, return to first coastline point.
                Vector3 last  = coastline[coastline.Count - 1];
                Vector3 first = coastline[0];

                // Get terrain corners sorted by proximity to "sea side"
                List<Vector3> corners = GetTerrainCorners(terrainWidth, terrainLength, seaLevelY);

                // Project last and first onto terrain boundary
                Vector3 lastEdge  = ClampToTerrainBounds(last, terrainWidth, terrainLength, seaLevelY);
                Vector3 firstEdge = ClampToTerrainBounds(first, terrainWidth, terrainLength, seaLevelY);

                seaPoly.Add(lastEdge);

                // Add any terrain corners that are on the sea side
                Vector3 seaDir = seaOnRight ? avgRight : -avgRight;
                foreach (var corner in corners)
                {
                    Vector3 toCorner = (corner - mid);
                    toCorner.y = 0;
                    if (Vector3.Dot(toCorner.normalized, seaDir) > -0.3f)
                        seaPoly.Add(corner);
                }

                seaPoly.Add(firstEdge);

                // Triangulate the resulting polygon
                if (seaPoly.Count >= 3)
                    TriangulatePolygon(seaPoly, seaLevelY, td);
            }
            else
            {
                // No coastline data: cover entire terrain with a simple quad
                td.vertices.Add(new Vector3(0, seaLevelY, 0));
                td.vertices.Add(new Vector3(terrainWidth, seaLevelY, 0));
                td.vertices.Add(new Vector3(terrainWidth, seaLevelY, terrainLength));
                td.vertices.Add(new Vector3(0, seaLevelY, terrainLength));

                td.triangles.Add(startV);     td.triangles.Add(startV + 2); td.triangles.Add(startV + 1);
                td.triangles.Add(startV);     td.triangles.Add(startV + 3); td.triangles.Add(startV + 2);
            }
        }

        private static float DistToEdge(Vector3 p, float w, float l)
        {
            float dx = Mathf.Min(p.x, w - p.x);
            float dz = Mathf.Min(p.z, l - p.z);
            return Mathf.Min(Mathf.Abs(dx), Mathf.Abs(dz));
        }

        private static List<Vector3> GetTerrainCorners(float w, float l, float y)
        {
            return new List<Vector3>
            {
                new Vector3(0, y, 0),
                new Vector3(w, y, 0),
                new Vector3(w, y, l),
                new Vector3(0, y, l),
            };
        }

        private static Vector3 ClampToTerrainBounds(Vector3 p, float w, float l, float y)
        {
            return new Vector3(
                Mathf.Clamp(p.x, 0, w),
                y,
                Mathf.Clamp(p.z, 0, l));
        }

        // ================================================================
        //  POLYGON TRIANGULATION (Ear-Clipping)
        // ================================================================

        /// <summary>
        /// Triangulates a polygon on the XZ plane using ear-clipping and adds
        /// the resulting geometry to the TileMeshData at the specified Y level.
        /// </summary>
        private static void TriangulatePolygon(List<Vector3> polygon, float yLevel, TileMeshData td)
        {
            if (polygon.Count < 3) return;

            int startV = td.vertices.Count;

            // Add vertices at the target Y level
            for (int i = 0; i < polygon.Count; i++)
                td.vertices.Add(new Vector3(polygon[i].x, yLevel, polygon[i].z));

            // Build working index list
            List<int> indices = new List<int>(polygon.Count);
            for (int i = 0; i < polygon.Count; i++)
                indices.Add(i);

            // Determine winding (signed area on XZ)
            float signedArea = 0f;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector3 a = polygon[i];
                Vector3 b = polygon[(i + 1) % polygon.Count];
                signedArea += (b.x - a.x) * (b.z + a.z);
            }
            bool isCW = signedArea > 0f;

            int safety = polygon.Count * polygon.Count;
            while (indices.Count > 2 && safety-- > 0)
            {
                bool earFound = false;
                for (int i = 0; i < indices.Count; i++)
                {
                    int iPrev = (i - 1 + indices.Count) % indices.Count;
                    int iNext = (i + 1) % indices.Count;

                    Vector3 a = polygon[indices[iPrev]];
                    Vector3 b = polygon[indices[i]];
                    Vector3 c = polygon[indices[iNext]];

                    float cross = (b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x);
                    bool isConvex = isCW ? cross < 0 : cross > 0;
                    if (!isConvex) continue;

                    bool hasInside = false;
                    for (int j = 0; j < indices.Count; j++)
                    {
                        if (j == iPrev || j == i || j == iNext) continue;
                        if (PointInTriangleXZ(polygon[indices[j]], a, b, c))
                        {
                            hasInside = true;
                            break;
                        }
                    }
                    if (hasInside) continue;

                    int vA = startV + indices[iPrev];
                    int vB = startV + indices[i];
                    int vC = startV + indices[iNext];

                    // Water faces upward: ensure correct winding for top-down view
                    if (isCW)
                    {
                        td.triangles.Add(vA); td.triangles.Add(vC); td.triangles.Add(vB);
                    }
                    else
                    {
                        td.triangles.Add(vA); td.triangles.Add(vB); td.triangles.Add(vC);
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
            float d1 = Sign(p, a, b);
            float d2 = Sign(p, b, c);
            float d3 = Sign(p, c, a);
            bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(hasNeg && hasPos);
        }

        private static float Sign(Vector3 p1, Vector3 p2, Vector3 p3)
        {
            return (p1.x - p3.x) * (p2.z - p3.z) - (p2.x - p3.x) * (p1.z - p3.z);
        }

        // ================================================================
        //  MATERIALS
        // ================================================================

        private static Shader FindCompatibleShader()
        {
            // Preferisci lo shader toon BotW per l'acqua
            Shader s = Shader.Find("Custom/ToonWater");
            if (s != null) return s;
            s = Shader.Find("Universal Render Pipeline/Lit");
            if (s != null) return s;
            s = Shader.Find("HDRP/Lit");
            if (s != null) return s;
            s = Shader.Find("Standard");
            if (s != null) return s;
            return Shader.Find("Unlit/Color");
        }

        /// <summary>
        /// Creates a semi-transparent water material with the given base color
        /// and emission tint.
        /// </summary>
        private static Material CreateWaterMaterial(Color baseColor, Color emissionColor)
        {
            Shader shader = FindCompatibleShader();
            Material mat = new Material(shader);
            string shaderName = shader.name;

            // Se usiamo lo shader ToonWater, configura i parametri depth-based
            if (shaderName == "Custom/ToonWater")
            {
                // Colori vivaci manga/BotW
                if (mat.HasProperty("_ShallowColor"))
                    mat.SetColor("_ShallowColor", new Color(0.15f, 0.78f, 0.82f));
                if (mat.HasProperty("_DeepColor"))
                    mat.SetColor("_DeepColor", new Color(0.02f, 0.10f, 0.48f));
                if (mat.HasProperty("_FoamColor"))
                    mat.SetColor("_FoamColor", new Color(0.92f, 0.97f, 1f));
                if (mat.HasProperty("_HorizonColor"))
                    mat.SetColor("_HorizonColor", new Color(0.45f, 0.70f, 0.95f));
                if (mat.HasProperty("_ShadowColor"))
                    mat.SetColor("_ShadowColor", new Color(0.08f, 0.06f, 0.30f));
                // Depth settings
                if (mat.HasProperty("_ShallowDepth"))
                    mat.SetFloat("_ShallowDepth", 3f);   // primi 3m = turchese
                if (mat.HasProperty("_DeepDepth"))
                    mat.SetFloat("_DeepDepth", 25f);     // oltre 25m = blu scuro
                if (mat.HasProperty("_ShoreWidth"))
                    mat.SetFloat("_ShoreWidth", 2f);     // 2m di schiuma costa
                return mat;
            }

            // Fallback: URP/Lit o Standard con transparency
            if (shaderName.Contains("Universal Render Pipeline"))
            {
                mat.SetFloat("_Surface", 1f);
                mat.SetFloat("_Blend", 0f);
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            else if (shaderName == "Standard")
            {
                mat.SetFloat("_Mode", 3f);
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }

            mat.color = baseColor;
            if (mat.HasProperty("_Metallic"))   mat.SetFloat("_Metallic", 0.15f);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.85f);
            mat.EnableKeyword("_EMISSION");
            if (mat.HasProperty("_EmissionColor"))
                mat.SetColor("_EmissionColor", emissionColor);

            return mat;
        }

        // ================================================================
        //  MESH INSTANTIATION
        // ================================================================

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
    }
}
