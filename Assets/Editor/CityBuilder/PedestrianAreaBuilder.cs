using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CityBuilder
{
    /// <summary>
    /// Dati area pedonale in coordinate mondo (Vector3).
    /// Convertita da OsmPedestrianAreaData (LatLon) dal pipeline.
    /// </summary>
    public class WorldPedestrianArea
    {
        public List<Vector3> polygon;
        public string areaType;   // square, pedestrian, marketplace, playground
        public string surface;    // paving_stones, cobblestone, sett, asphalt
        public string name;
    }

    /// <summary>
    /// Genera mesh per piazze e aree pedonali urbane con pavimentazione distintiva.
    /// Stile tipico italiano: sampietrini, lastricato, bordi rialzati.
    /// </summary>
    public static class PedestrianAreaBuilder
    {
        // Altezze per evitare z-fighting
        private const float PAVING_Y_OFFSET = 0.20f;
        private const float BORDER_Y_OFFSET = 0.30f;
        private const float BORDER_WIDTH = 0.3f;

        // ================================================================
        //  PUBLIC ENTRY POINT
        // ================================================================

        public static async Task GenerateAreasAsync(
            List<WorldPedestrianArea> areas,
            GameObject parent,
            Terrain[,] terrains, int gridCount,
            float tileWidthM, float tileLengthM)
        {
            if (areas == null || areas.Count == 0) return;

            TileMeshData pavingMesh = new TileMeshData();
            TileMeshData borderMesh = new TileMeshData();
            TileMeshData accentMesh = new TileMeshData(); // dettagli decorativi

            for (int i = 0; i < areas.Count; i++)
            {
                if (i % 20 == 0)
                {
                    EditorUtility.DisplayProgressBar("PedestrianAreaBuilder",
                        $"Piazze e aree pedonali {i}/{areas.Count}...",
                        (float)i / areas.Count);
                    await Task.Yield();
                }

                var area = areas[i];
                if (area.polygon == null || area.polygon.Count < 3) continue;

                // Proietta vertici sul terreno
                List<Vector3> projectedPoly = ProjectOnTerrain(
                    area.polygon, terrains, gridCount, tileWidthM, tileLengthM);

                // Pavimentazione principale
                TriangulateOnTerrain(projectedPoly, PAVING_Y_OFFSET, pavingMesh);

                // Bordo rialzato (cordolo) attorno all'area
                GenerateBorder(projectedPoly, BORDER_Y_OFFSET, BORDER_WIDTH, borderMesh);

                // Pattern decorativo al centro per piazze importanti
                if (area.areaType == "square" && !string.IsNullOrEmpty(area.name))
                {
                    GenerateCenterAccent(projectedPoly, PAVING_Y_OFFSET + 0.02f, accentMesh);
                }
            }

            // Materiali
            Material pavingMat = CreatePavingMaterial();
            Material borderMat = CreateBorderMaterial();
            Material accentMat = CreateAccentMaterial();

            if (pavingMesh.vertices.Count > 0)
                CreateMeshObject("Piazze_Pavimentazione", pavingMesh, pavingMat, parent);
            pavingMesh.Free();

            if (borderMesh.vertices.Count > 0)
                CreateMeshObject("Piazze_Cordoli", borderMesh, borderMat, parent);
            borderMesh.Free();

            if (accentMesh.vertices.Count > 0)
                CreateMeshObject("Piazze_Decorazioni", accentMesh, accentMat, parent);
            accentMesh.Free();

            EditorUtility.ClearProgressBar();
            Debug.Log($"PedestrianAreaBuilder: {areas.Count} aree pedonali generate.");
        }

        // ================================================================
        //  PROIEZIONE TERRENO
        // ================================================================

        private static List<Vector3> ProjectOnTerrain(
            List<Vector3> polygon,
            Terrain[,] terrains, int gridCount,
            float tileWidthM, float tileLengthM)
        {
            var result = new List<Vector3>(polygon.Count);
            foreach (var p in polygon)
            {
                float y = SampleTerrainHeight(p, terrains, gridCount, tileWidthM, tileLengthM);
                result.Add(new Vector3(p.x, y, p.z));
            }
            return result;
        }

        private static float SampleTerrainHeight(Vector3 pos, Terrain[,] terrains,
            int gridCount, float tileWidthM, float tileLengthM)
        {
            int gx = Mathf.Clamp((int)(pos.x / tileWidthM), 0, gridCount - 1);
            int gz = Mathf.Clamp((int)(pos.z / tileLengthM), 0, gridCount - 1);
            if (terrains == null || terrains[gx, gz] == null) return 0f;
            return terrains[gx, gz].SampleHeight(new Vector3(pos.x, 0, pos.z));
        }

        // ================================================================
        //  PAVIMENTAZIONE (triangolazione poligono)
        // ================================================================

        private static void TriangulateOnTerrain(List<Vector3> polygon, float yOffset, TileMeshData td)
        {
            if (polygon.Count < 3) return;

            int startV = td.vertices.Count;

            for (int i = 0; i < polygon.Count; i++)
                td.vertices.Add(new Vector3(polygon[i].x, polygon[i].y + yOffset, polygon[i].z));

            // Ear-clipping su XZ
            List<int> indices = new List<int>(polygon.Count);
            for (int i = 0; i < polygon.Count; i++) indices.Add(i);

            float signedArea = 0;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector3 a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                signedArea += (b.x - a.x) * (b.z + a.z);
            }
            bool isCW = signedArea > 0;

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

                    bool inside = false;
                    for (int j = 0; j < indices.Count; j++)
                    {
                        if (j == iPrev || j == i || j == iNext) continue;
                        if (PointInTriXZ(polygon[indices[j]], a, b, c)) { inside = true; break; }
                    }
                    if (inside) continue;

                    int vA = startV + indices[iPrev];
                    int vB = startV + indices[i];
                    int vC = startV + indices[iNext];

                    if (isCW) { td.triangles.Add(vA); td.triangles.Add(vC); td.triangles.Add(vB); }
                    else { td.triangles.Add(vA); td.triangles.Add(vB); td.triangles.Add(vC); }

                    indices.RemoveAt(i);
                    earFound = true;
                    break;
                }
                if (!earFound) break;
            }
        }

        // ================================================================
        //  BORDO RIALZATO (cordolo perimetrale)
        // ================================================================

        private static void GenerateBorder(List<Vector3> polygon, float yOffset, float width, TileMeshData td)
        {
            if (polygon.Count < 3) return;

            float borderH = 0.12f; // altezza cordolo

            for (int i = 0; i < polygon.Count; i++)
            {
                int next = (i + 1) % polygon.Count;
                Vector3 a = polygon[i];
                Vector3 b = polygon[next];

                Vector3 dir = (b - a).normalized;
                Vector3 outward = Vector3.Cross(Vector3.up, dir).normalized;

                // 4 punti: interno basso, esterno basso, esterno alto, interno alto
                float yA = a.y + yOffset;
                float yB = b.y + yOffset;

                Vector3 a_in  = new Vector3(a.x, yA, a.z);
                Vector3 a_out = new Vector3(a.x + outward.x * width, yA, a.z + outward.z * width);
                Vector3 b_in  = new Vector3(b.x, yB, b.z);
                Vector3 b_out = new Vector3(b.x + outward.x * width, yB, b.z + outward.z * width);

                int s = td.vertices.Count;

                // Top face
                td.vertices.Add(a_in);
                td.vertices.Add(a_out);
                td.vertices.Add(b_out);
                td.vertices.Add(b_in);
                td.triangles.Add(s); td.triangles.Add(s + 2); td.triangles.Add(s + 1);
                td.triangles.Add(s); td.triangles.Add(s + 3); td.triangles.Add(s + 2);

                // Outer face (vertical side visible from outside)
                Vector3 a_out_lo = a_out - Vector3.up * borderH;
                Vector3 b_out_lo = b_out - Vector3.up * borderH;

                int s2 = td.vertices.Count;
                td.vertices.Add(a_out);
                td.vertices.Add(b_out);
                td.vertices.Add(b_out_lo);
                td.vertices.Add(a_out_lo);
                td.triangles.Add(s2); td.triangles.Add(s2 + 1); td.triangles.Add(s2 + 2);
                td.triangles.Add(s2); td.triangles.Add(s2 + 2); td.triangles.Add(s2 + 3);
            }
        }

        // ================================================================
        //  DECORAZIONE CENTRALE (rosone/pattern per piazze importanti)
        // ================================================================

        private static void GenerateCenterAccent(List<Vector3> polygon, float yOffset, TileMeshData td)
        {
            // Calcola centroide
            Vector3 center = Vector3.zero;
            foreach (var p in polygon) center += p;
            center /= polygon.Count;
            center.y += yOffset;

            // Calcola raggio come meta' della distanza minima dal centroide ai lati
            float minDist = float.MaxValue;
            foreach (var p in polygon)
            {
                float d = Vector3.Distance(
                    new Vector3(p.x, 0, p.z),
                    new Vector3(center.x, 0, center.z));
                if (d < minDist) minDist = d;
            }
            float radius = minDist * 0.3f;
            if (radius < 1f) return;

            // Genera un pattern a stella/rosone
            int points = 8;
            float innerR = radius * 0.4f;

            int startV = td.vertices.Count;
            td.vertices.Add(center); // centro

            for (int i = 0; i < points * 2; i++)
            {
                float angle = Mathf.PI * 2f * i / (points * 2);
                float r = (i % 2 == 0) ? radius : innerR;
                td.vertices.Add(center + new Vector3(Mathf.Cos(angle) * r, 0, Mathf.Sin(angle) * r));
            }

            // Fan dal centro
            for (int i = 0; i < points * 2; i++)
            {
                int curr = startV + 1 + i;
                int next = startV + 1 + (i + 1) % (points * 2);
                td.triangles.Add(startV);
                td.triangles.Add(next);
                td.triangles.Add(curr);
            }
        }

        // ================================================================
        //  MATERIALI - Stile piazza italiana
        // ================================================================

        private static Shader FindShader()
        {
            Shader s = Shader.Find("Universal Render Pipeline/Lit");
            if (s != null) return s;
            s = Shader.Find("HDRP/Lit");
            if (s != null) return s;
            s = Shader.Find("Standard");
            if (s != null) return s;
            return Shader.Find("Unlit/Color");
        }

        /// <summary>
        /// Sampietrini / lastricato: grigio caldo con leggera texture.
        /// Piu' chiaro dell'asfalto, tono leggermente rosato (porfido).
        /// </summary>
        private static Material CreatePavingMaterial()
        {
            Material mat = new Material(FindShader());
            // Porfido romano: grigio con sfumatura calda
            mat.color = new Color(0.45f, 0.40f, 0.38f);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.15f);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.15f);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0.0f);
            mat.name = "Pavimentazione_Piazza";
            return mat;
        }

        /// <summary>
        /// Cordolo: pietra chiara tipo travertino.
        /// </summary>
        private static Material CreateBorderMaterial()
        {
            Material mat = new Material(FindShader());
            mat.color = new Color(0.65f, 0.62f, 0.58f);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.05f);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.05f);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0.0f);
            mat.name = "Cordolo_Pietra";
            return mat;
        }

        /// <summary>
        /// Decorazione centrale: marmo/pietra piu' chiara.
        /// </summary>
        private static Material CreateAccentMaterial()
        {
            Material mat = new Material(FindShader());
            mat.color = new Color(0.72f, 0.68f, 0.62f);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.25f);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.25f);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0.05f);
            mat.name = "Decorazione_Piazza";
            return mat;
        }

        // ================================================================
        //  MESH OBJECT
        // ================================================================

        private static void CreateMeshObject(string name, TileMeshData td, Material mat, GameObject parent)
        {
            GameObject go = new GameObject(name);
            go.transform.parent = parent.transform;

            Mesh mesh = new Mesh();
            mesh.indexFormat = td.vertices.Count < 65000
                ? UnityEngine.Rendering.IndexFormat.UInt16
                : UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = td.vertices.ToArray();
            mesh.triangles = td.triangles.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        // ================================================================
        //  GEOMETRY UTILS
        // ================================================================

        private static bool PointInTriXZ(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            float d1 = (p.x - b.x) * (a.z - b.z) - (a.x - b.x) * (p.z - b.z);
            float d2 = (p.x - c.x) * (b.z - c.z) - (b.x - c.x) * (p.z - c.z);
            float d3 = (p.x - a.x) * (c.z - a.z) - (c.x - a.x) * (p.z - a.z);
            bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(hasNeg && hasPos);
        }
    }
}
