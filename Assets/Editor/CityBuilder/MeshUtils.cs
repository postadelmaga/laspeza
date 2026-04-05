using UnityEngine;
using System.Collections.Generic;

namespace CityBuilder
{
    /// <summary>
    /// Utility geometriche condivise: triangolazione, shader lookup, ecc.
    /// Evita duplicazione di codice tra BuildingGenerator, WaterBuilder, PedestrianAreaBuilder.
    /// </summary>
    public static class MeshUtils
    {
        /// <summary>
        /// Trova uno shader compatibile con il render pipeline corrente.
        /// Prova URP -> HDRP -> Standard -> Unlit/Color.
        /// </summary>
        public static Shader FindLitShader()
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
        /// Crea un Material con colore, smoothness e metallic.
        /// </summary>
        public static Material CreateMaterial(Color color, float smoothness = 0.3f, float metallic = 0f)
        {
            Shader shader = FindLitShader();
            if (shader == null) return new Material(Shader.Find("Sprites/Default")) { color = color };
            Material mat = new Material(shader) { color = color };
            if (mat.HasProperty("_Glossiness"))  mat.SetFloat("_Glossiness", smoothness);
            if (mat.HasProperty("_Smoothness"))  mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_Metallic"))    mat.SetFloat("_Metallic", metallic);
            return mat;
        }

        /// <summary>
        /// Triangolazione ear-clipping di un poligono sul piano XZ a una data altezza Y.
        /// Aggiunge vertici e triangoli al TileMeshData.
        /// </summary>
        public static void TriangulatePolygonXZ(
            List<Vector3> pts, float y, List<int> targetTris, 
            List<Vector3> targetVerts, List<Vector2> targetUVs = null)
        {
            if (pts.Count < 3) return;

            int baseIdx = targetVerts.Count;
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
                targetVerts.Add(new Vector3(pts[i].x, y, pts[i].z));
                if (targetUVs != null)
                    targetUVs.Add(new Vector2(
                        (pts[i].x - minX) / spanX,
                        (pts[i].z - minZ) / spanZ));
            }

            // Signed area per determinare il winding
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

        public static bool PointInTriangleXZ(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
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
