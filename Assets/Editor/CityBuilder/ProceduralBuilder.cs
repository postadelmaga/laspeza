using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CityBuilder
{
    public static class ProceduralBuilder
    {
        /// <summary>
        /// Trova uno shader funzionante per il render pipeline corrente.
        /// </summary>
        private static Shader FindCompatibleShader()
        {
            // URP
            Shader s = Shader.Find("Universal Render Pipeline/Lit");
            if (s != null) return s;
            // HDRP
            s = Shader.Find("HDRP/Lit");
            if (s != null) return s;
            // Built-in
            s = Shader.Find("Standard");
            if (s != null) return s;
            // Ultimo fallback
            return Shader.Find("Unlit/Color");
        }

        private static Material CreateMaterial(Color color, float glossiness = 0.5f)
        {
            Material mat = new Material(FindCompatibleShader()) { color = color };
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", glossiness);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", glossiness);
            return mat;
        }

        private static Material CreateEmissiveMaterial(Color emissionColor)
        {
            Material mat = new Material(FindCompatibleShader());
            mat.EnableKeyword("_EMISSION");
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", emissionColor);
            return mat;
        }

        public static async Task Generate3DAsync(List<OsmFeature> features, GameObject cityParent, int gridCount)
        {
            Material asphaltMat = CreateMaterial(new Color(0.15f, 0.15f, 0.15f), 0.1f);
            Material sidewalkMat = CreateMaterial(new Color(0.6f, 0.6f, 0.6f), 0.0f);
            Material buildingMat = CreateMaterial(new Color(0.7f, 0.7f, 0.75f));
            Material lightMat = CreateEmissiveMaterial(new Color(1f, 0.9f, 0.5f) * 2f);

            TileMeshData[,] tileMeshes = new TileMeshData[gridCount, gridCount];
            for (int y = 0; y < gridCount; y++)
                for (int x = 0; x < gridCount; x++)
                    tileMeshes[x, y] = new TileMeshData();

            int builtCount = 0;
            for (int i = 0; i < features.Count; i++)
            {
                if (i % 100 == 0)
                {
                    EditorUtility.DisplayProgressBar("Generazione 3D", $"Costruzione Modelli {i}/{features.Count}...", (float)i / features.Count);
                    await Task.Yield();
                }

                OsmFeature f = features[i];
                if (f.isHighway)
                {
                    bool isMain = f.widthOrHeight > 0.5f;
                    DrawRoadWithDetails(f.points, cityParent.transform, asphaltMat, sidewalkMat, lightMat, f.widthOrHeight, f.originalId, isMain);
                }
                else if (f.isBuilding && f.tileX >= 0 && f.tileZ >= 0)
                {
                    AddBuildingData(f.points, f.widthOrHeight, tileMeshes[f.tileX, f.tileZ]);
                    builtCount++;
                }
            }

            EditorUtility.DisplayProgressBar("CityBuilder", "Fusione Architettonica dei Palazzi...", 0.9f);
            await Task.Yield();

            for (int gy = 0; gy < gridCount; gy++)
            {
                for (int gx = 0; gx < gridCount; gx++)
                {
                    if (tileMeshes[gx, gy].vertices.Count == 0) continue;

                    GameObject combinedObj = new GameObject($"Palazzi_Chunk_{gx}_{gy}");
                    combinedObj.transform.parent = cityParent.transform;

                    Mesh m = new Mesh();
                    m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                    m.vertices = tileMeshes[gx, gy].vertices.ToArray();
                    m.triangles = tileMeshes[gx, gy].triangles.ToArray();
                    m.RecalculateNormals();
                    m.RecalculateBounds();

                    combinedObj.AddComponent<MeshFilter>().sharedMesh = m;
                    combinedObj.AddComponent<MeshRenderer>().sharedMaterial = buildingMat;
                    combinedObj.AddComponent<MeshCollider>();
                }
            }
            UnityEngine.Debug.Log($"Generazione Completata! {builtCount} palazzi ottimizzati e fusi.");
        }

        private static void DrawRoadWithDetails(List<Vector3> pts, Transform parent, Material asph, Material side, Material lightMat, float width, int id, bool addLights)
        {
            GameObject roadGroup = new GameObject("Strada_" + id);
            roadGroup.transform.parent = parent;

            GameObject sideObj = new GameObject("Marciapiede");
            sideObj.transform.parent = roadGroup.transform;
            LineRenderer lrS = sideObj.AddComponent<LineRenderer>();
            lrS.positionCount = pts.Count;
            for (int i = 0; i < pts.Count; i++) lrS.SetPosition(i, pts[i] + Vector3.up * 0.05f);
            lrS.startWidth = width + 0.3f;
            lrS.endWidth = width + 0.3f;
            lrS.sharedMaterial = side;

            GameObject asphObj = new GameObject("Asfalto");
            asphObj.transform.parent = roadGroup.transform;
            LineRenderer lrA = asphObj.AddComponent<LineRenderer>();
            lrA.positionCount = pts.Count;
            for (int i = 0; i < pts.Count; i++) lrA.SetPosition(i, pts[i] + Vector3.up * 0.1f);
            lrA.startWidth = width;
            lrA.endWidth = width;
            lrA.sharedMaterial = asph;

            if (addLights)
            {
                float distCounter = 0;
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    Vector3 dir = pts[i + 1] - pts[i];
                    float segLen = dir.magnitude;
                    distCounter += segLen;

                    if (distCounter > 2.0f)
                    {
                        distCounter = 0;
                        dir.Normalize();
                        Vector3 right = Vector3.Cross(dir, Vector3.up).normalized;
                        Vector3 polePos = pts[i] + right * (width / 2 + 0.2f);

                        GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                        pole.transform.parent = roadGroup.transform;
                        pole.transform.position = polePos + Vector3.up * 0.2f;
                        pole.transform.localScale = new Vector3(0.02f, 0.4f, 0.02f);
                        Object.DestroyImmediate(pole.GetComponent<Collider>());

                        GameObject bulb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        bulb.transform.parent = pole.transform;
                        bulb.transform.position = polePos + Vector3.up * 0.6f;
                        bulb.transform.localScale = new Vector3(0.1f, 0.1f, 0.1f);
                        bulb.GetComponent<MeshRenderer>().sharedMaterial = lightMat;
                        Object.DestroyImmediate(bulb.GetComponent<Collider>());
                    }
                }
            }
        }

        /// <summary>
        /// Genera mesh pareti + tetto per un edificio.
        /// Usa ear-clipping per il tetto (supporta poligoni concavi: L, U, T).
        /// </summary>
        private static void AddBuildingData(List<Vector3> pts, float height, TileMeshData td)
        {
            if (pts.Count < 3) return;

            int startV = td.vertices.Count;
            float baseY = float.MaxValue;
            for (int pi = 0; pi < pts.Count; pi++)
                if (pts[pi].y < baseY) baseY = pts[pi].y;

            // Vertici: coppie (bottom, top) per ogni punto, stride 2
            for (int i = 0; i < pts.Count; i++)
            {
                td.vertices.Add(new Vector3(pts[i].x, baseY, pts[i].z));
                td.vertices.Add(new Vector3(pts[i].x, baseY + height, pts[i].z));
            }

            // Pareti
            for (int i = 0; i < pts.Count - 1; i++)
            {
                int b0 = startV + i * 2;
                int t0 = startV + i * 2 + 1;
                int b1 = startV + (i + 1) * 2;
                int t1 = startV + (i + 1) * 2 + 1;

                td.triangles.Add(b0); td.triangles.Add(t0); td.triangles.Add(b1);
                td.triangles.Add(b1); td.triangles.Add(t0); td.triangles.Add(t1);
            }

            // Chiusura ultimo -> primo
            {
                int bLast = startV + (pts.Count - 1) * 2;
                int tLast = startV + (pts.Count - 1) * 2 + 1;
                td.triangles.Add(bLast); td.triangles.Add(tLast); td.triangles.Add(startV);
                td.triangles.Add(startV); td.triangles.Add(tLast); td.triangles.Add(startV + 1);
            }

            // Tetto con ear-clipping (funziona con poligoni concavi)
            TriangulateRoof(pts, baseY + height, startV, td);
        }

        /// <summary>
        /// Ear-clipping triangulation sul piano XZ per il tetto.
        /// I vertici top nel TileMeshData hanno indice startV + i*2 + 1.
        /// </summary>
        private static void TriangulateRoof(List<Vector3> pts, float roofY, int startV, TileMeshData td)
        {
            // Costruisci lista indici lavorabili
            List<int> indices = new List<int>(pts.Count);
            for (int i = 0; i < pts.Count; i++) indices.Add(i);

            // Determina il winding (CW o CCW) con il signed area sul piano XZ
            float signedArea = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 a = pts[i];
                Vector3 b = pts[(i + 1) % pts.Count];
                signedArea += (b.x - a.x) * (b.z + a.z);
            }
            bool isCW = signedArea > 0;

            int safety = pts.Count * pts.Count; // evita loop infinito su poligoni degenerati
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

                    // Controlla se l'angolo in b e convesso (rispetto al winding)
                    float cross = (b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x);
                    bool isConvex = isCW ? cross < 0 : cross > 0;
                    if (!isConvex) continue;

                    // Controlla che nessun altro vertice cada dentro il triangolo
                    bool hasPointInside = false;
                    for (int j = 0; j < indices.Count; j++)
                    {
                        if (j == iPrev || j == i || j == iNext) continue;
                        if (PointInTriangleXZ(pts[indices[j]], a, b, c))
                        {
                            hasPointInside = true;
                            break;
                        }
                    }
                    if (hasPointInside) continue;

                    // Ear trovata: aggiungi triangolo usando gli indici top nel mesh
                    int topA = startV + indices[iPrev] * 2 + 1;
                    int topB = startV + indices[i] * 2 + 1;
                    int topC = startV + indices[iNext] * 2 + 1;

                    if (isCW)
                    {
                        td.triangles.Add(topA); td.triangles.Add(topC); td.triangles.Add(topB);
                    }
                    else
                    {
                        td.triangles.Add(topA); td.triangles.Add(topB); td.triangles.Add(topC);
                    }

                    indices.RemoveAt(i);
                    earFound = true;
                    break;
                }

                if (!earFound) break; // poligono degenerato, evitiamo loop
            }
        }

        /// <summary>
        /// Test punto-nel-triangolo sul piano XZ usando coordinate baricentriche.
        /// </summary>
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
    }
}
