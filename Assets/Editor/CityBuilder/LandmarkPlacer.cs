using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace CityBuilder
{
    /// <summary>
    /// Piazza landmark iconici di La Spezia come mesh procedurali.
    /// Chiamato da CityBuilderPipeline.StepScene() o manualmente.
    ///
    /// Landmark:
    ///   - Ciminiera ENEL Vallegrande (cilindro 120m)
    ///   - Faro Isola del Tino (torre bianca 19m)
    ///   - Chiesa San Pietro, Porto Venere (box + campanile)
    ///   - Faro della Palmaria
    ///   - Castello di Lerici
    ///   - Arsenale Marina Militare
    /// </summary>
    public static class LandmarkPlacer
    {
        [System.Serializable]
        struct LandmarkDef
        {
            public string name;
            public double lon, lat;
            public System.Action<GameObject, float> builder;
        }

        // ================================================================
        //  PUBLIC
        // ================================================================

        public static void PlaceLandmarks(GameObject worldParent, TerrainMetaData tMeta,
            float cropWidthM, float cropLengthM, Terrain[,] terrains, int gridCount)
        {
            if (worldParent == null || tMeta == null) return;

            // Rimuovi vecchi landmark
            var old = worldParent.transform.Find("Landmarks");
            if (old != null) Object.DestroyImmediate(old.gameObject);

            GameObject landmarkParent = new GameObject("Landmarks");
            landmarkParent.transform.parent = worldParent.transform;

            var landmarks = GetLandmarks();
            int placed = 0;

            foreach (var lm in landmarks)
            {
                // Converti GPS -> world
                float x = Mathf.InverseLerp(tMeta.minLon, tMeta.maxLon, (float)lm.lon) * cropWidthM;
                float z = Mathf.InverseLerp(tMeta.minLat, tMeta.maxLat, (float)lm.lat) * cropLengthM;

                // Verifica che sia dentro i bounds
                if (x < 0 || x > cropWidthM || z < 0 || z > cropLengthM)
                {
                    Debug.Log($"Landmark '{lm.name}' fuori dai bounds, skip.");
                    continue;
                }

                // Sample altezza terreno
                float y = SampleTerrain(x, z, terrains, gridCount, cropWidthM, cropLengthM);

                GameObject go = new GameObject(lm.name);
                go.transform.parent = landmarkParent.transform;
                go.transform.position = new Vector3(x, y, z);

                lm.builder(go, y);
                go.isStatic = true;
                placed++;
            }

            Debug.Log($"LandmarkPlacer: {placed} landmark piazzati.");
        }

        // ================================================================
        //  LANDMARK DEFINITIONS
        // ================================================================

        static List<LandmarkDef> GetLandmarks()
        {
            return new List<LandmarkDef>
            {
                new LandmarkDef {
                    name = "Ciminiera_ENEL",
                    lon = 9.8520, lat = 44.0990,
                    builder = BuildChimney
                },
                new LandmarkDef {
                    name = "Faro_Tino",
                    lon = 9.8502, lat = 44.0260,
                    builder = BuildLighthouse
                },
                new LandmarkDef {
                    name = "Chiesa_SanPietro_PortoVenere",
                    lon = 9.8358, lat = 44.0503,
                    builder = BuildChurch
                },
                new LandmarkDef {
                    name = "Faro_Palmaria",
                    lon = 9.8380, lat = 44.0370,
                    builder = BuildSmallLighthouse
                },
                new LandmarkDef {
                    name = "Castello_Lerici",
                    lon = 9.9095, lat = 44.0755,
                    builder = BuildCastle
                },
                new LandmarkDef {
                    name = "Arsenale_MarinaM",
                    lon = 9.8190, lat = 44.1010,
                    builder = BuildArsenal
                },
                new LandmarkDef {
                    name = "Castello_SanGiorgio",
                    lon = 9.8230, lat = 44.1085,
                    builder = BuildHillCastle
                },
            };
        }

        // ================================================================
        //  BUILDERS — mesh procedurali per ogni landmark
        // ================================================================

        static Shader FindShader()
        {
            return Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard")
                ?? Shader.Find("Unlit/Color");
        }

        static Material MakeMat(Color c, float smooth = 0.2f)
        {
            var mat = new Material(FindShader()) { color = c };
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smooth);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
            return mat;
        }

        static void AddPrimitive(GameObject parent, PrimitiveType type,
            Vector3 localPos, Vector3 scale, Material mat, string name = "part")
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.parent = parent.transform;
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.isStatic = true;
        }

        // ── Ciminiera ENEL: cilindro alto 120m, diametro ~8m, strisce rosse/bianche ──
        static void BuildChimney(GameObject go, float baseY)
        {
            Material red = MakeMat(new Color(0.8f, 0.15f, 0.1f));
            Material white = MakeMat(new Color(0.9f, 0.88f, 0.85f));

            float totalH = 120f;
            float radius = 4f;

            // Cilindro principale (Unity Cylinder ha altezza 2, raggio 0.5)
            int stripes = 6;
            float stripeH = totalH / stripes;
            for (int i = 0; i < stripes; i++)
            {
                Material m = (i % 2 == 0) ? red : white;
                float y = stripeH * 0.5f + i * stripeH;
                AddPrimitive(go, PrimitiveType.Cylinder,
                    new Vector3(0, y, 0),
                    new Vector3(radius * 2, stripeH * 0.5f, radius * 2),
                    m, $"stripe_{i}");
            }

            // Base più larga
            AddPrimitive(go, PrimitiveType.Cylinder,
                new Vector3(0, 3f, 0),
                new Vector3(radius * 3f, 3f, radius * 3f),
                MakeMat(new Color(0.5f, 0.5f, 0.5f)), "base");
        }

        // ── Faro del Tino: torre bianca cilindrica ~19m con lanterna in cima ──
        static void BuildLighthouse(GameObject go, float baseY)
        {
            Material white = MakeMat(new Color(0.95f, 0.93f, 0.90f));
            Material glass = MakeMat(new Color(0.8f, 0.9f, 0.5f), 0.8f);
            Material dark = MakeMat(new Color(0.3f, 0.3f, 0.3f));

            // Torre
            AddPrimitive(go, PrimitiveType.Cylinder,
                new Vector3(0, 9.5f, 0),
                new Vector3(3.5f, 9.5f, 3.5f),
                white, "torre");

            // Balconata
            AddPrimitive(go, PrimitiveType.Cylinder,
                new Vector3(0, 19f, 0),
                new Vector3(5f, 0.3f, 5f),
                dark, "balconata");

            // Lanterna (vetro)
            AddPrimitive(go, PrimitiveType.Cylinder,
                new Vector3(0, 21f, 0),
                new Vector3(2.5f, 2f, 2.5f),
                glass, "lanterna");

            // Cupola
            AddPrimitive(go, PrimitiveType.Sphere,
                new Vector3(0, 23.5f, 0),
                new Vector3(2.8f, 1.5f, 2.8f),
                dark, "cupola");
        }

        // ── Chiesa San Pietro Porto Venere: navata + campanile ──
        static void BuildChurch(GameObject go, float baseY)
        {
            Material stone = MakeMat(new Color(0.72f, 0.68f, 0.60f));
            Material darkStone = MakeMat(new Color(0.45f, 0.42f, 0.38f));
            Material roof = MakeMat(new Color(0.55f, 0.35f, 0.25f));

            // Navata
            AddPrimitive(go, PrimitiveType.Cube,
                new Vector3(0, 5f, 0),
                new Vector3(12f, 10f, 25f),
                stone, "navata");

            // Tetto navata (box inclinato simulato con box piatto)
            AddPrimitive(go, PrimitiveType.Cube,
                new Vector3(0, 11f, 0),
                new Vector3(13f, 2f, 26f),
                roof, "tetto");

            // Facciata (più alta)
            AddPrimitive(go, PrimitiveType.Cube,
                new Vector3(0, 7f, 13f),
                new Vector3(13f, 14f, 1f),
                darkStone, "facciata");

            // Campanile
            AddPrimitive(go, PrimitiveType.Cube,
                new Vector3(-7f, 10f, -8f),
                new Vector3(5f, 20f, 5f),
                stone, "campanile");

            // Cuspide campanile
            AddPrimitive(go, PrimitiveType.Cube,
                new Vector3(-7f, 22f, -8f),
                new Vector3(3f, 4f, 3f),
                darkStone, "cuspide");

            // Abside (semicerchio simulato)
            AddPrimitive(go, PrimitiveType.Cylinder,
                new Vector3(0, 4f, -14f),
                new Vector3(8f, 4f, 8f),
                stone, "abside");
        }

        // ── Faro piccolo (Palmaria) ──
        static void BuildSmallLighthouse(GameObject go, float baseY)
        {
            Material white = MakeMat(new Color(0.95f, 0.93f, 0.88f));
            Material dark = MakeMat(new Color(0.3f, 0.3f, 0.3f));

            AddPrimitive(go, PrimitiveType.Cylinder,
                new Vector3(0, 6f, 0),
                new Vector3(2f, 6f, 2f),
                white, "torre");

            AddPrimitive(go, PrimitiveType.Sphere,
                new Vector3(0, 13f, 0),
                new Vector3(2.5f, 1.5f, 2.5f),
                dark, "lanterna");
        }

        // ── Castello di Lerici: mura + torre ──
        static void BuildCastle(GameObject go, float baseY)
        {
            Material stone = MakeMat(new Color(0.65f, 0.58f, 0.48f));
            Material darkStone = MakeMat(new Color(0.45f, 0.40f, 0.35f));

            // Mura perimetrali
            AddPrimitive(go, PrimitiveType.Cube,
                new Vector3(0, 5f, 0),
                new Vector3(40f, 10f, 35f),
                stone, "mura");

            // Torre principale
            AddPrimitive(go, PrimitiveType.Cylinder,
                new Vector3(0, 12f, 0),
                new Vector3(8f, 12f, 8f),
                darkStone, "torre");

            // Merlatura (simulata)
            AddPrimitive(go, PrimitiveType.Cube,
                new Vector3(0, 11f, 0),
                new Vector3(42f, 1f, 37f),
                darkStone, "merlatura");
        }

        // ── Arsenale Marina Militare: grandi capannoni ──
        static void BuildArsenal(GameObject go, float baseY)
        {
            Material concrete = MakeMat(new Color(0.6f, 0.58f, 0.55f));
            Material roof = MakeMat(new Color(0.4f, 0.38f, 0.36f));

            // 3 capannoni paralleli
            for (int i = 0; i < 3; i++)
            {
                float offsetX = (i - 1) * 45f;
                AddPrimitive(go, PrimitiveType.Cube,
                    new Vector3(offsetX, 6f, 0),
                    new Vector3(35f, 12f, 80f),
                    concrete, $"capannone_{i}");

                AddPrimitive(go, PrimitiveType.Cube,
                    new Vector3(offsetX, 12.5f, 0),
                    new Vector3(37f, 1f, 82f),
                    roof, $"tetto_{i}");
            }

            // Muro perimetrale
            AddPrimitive(go, PrimitiveType.Cube,
                new Vector3(0, 3f, 50f),
                new Vector3(160f, 6f, 2f),
                concrete, "muro_fronte");
        }

        // ── Castello San Giorgio (collina sopra La Spezia) ──
        static void BuildHillCastle(GameObject go, float baseY)
        {
            Material stone = MakeMat(new Color(0.70f, 0.62f, 0.52f));
            Material darkStone = MakeMat(new Color(0.50f, 0.44f, 0.38f));

            // Corpo principale
            AddPrimitive(go, PrimitiveType.Cube,
                new Vector3(0, 6f, 0),
                new Vector3(30f, 12f, 25f),
                stone, "corpo");

            // Torri angolari
            float[][] torri = { new[]{-12f, 10f}, new[]{12f, 10f}, new[]{-12f, -10f}, new[]{12f, -10f} };
            for (int i = 0; i < torri.Length; i++)
            {
                AddPrimitive(go, PrimitiveType.Cylinder,
                    new Vector3(torri[i][0], 8f, torri[i][1]),
                    new Vector3(5f, 8f, 5f),
                    darkStone, $"torre_{i}");
            }
        }

        // ================================================================
        //  UTILITY
        // ================================================================

        static float SampleTerrain(float x, float z, Terrain[,] terrains, int gridCount,
            float cropW, float cropL)
        {
            if (terrains == null) return 0;
            float tileW = cropW / gridCount;
            float tileL = cropL / gridCount;
            int gx = Mathf.Clamp((int)(x / tileW), 0, gridCount - 1);
            int gz = Mathf.Clamp((int)(z / tileL), 0, gridCount - 1);
            if (terrains[gx, gz] == null) return 0;
            return terrains[gx, gz].SampleHeight(new Vector3(x, 0, z));
        }
    }
}
