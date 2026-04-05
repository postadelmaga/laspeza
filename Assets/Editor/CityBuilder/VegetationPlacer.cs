using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CityBuilder
{
    /// <summary>
    /// Dati vegetazione con coordinate mondo (Vector3) gia convertite da LatLon.
    /// Usato internamente dal VegetationPlacer dopo la conversione coordinate.
    /// </summary>
    public class VegetationFeature
    {
        public Vector3 position;              // per alberi singoli
        public List<Vector3> polygon;         // per parchi/foreste (null se albero singolo)
        public string vegType;                // "tree", "park", "forest", "grass", "garden"
        public bool isPoint;                  // true = singolo albero, false = area
    }

    public static class VegetationPlacer
    {
        // --- Limite massimo alberi ---
        public static int maxTotalTrees = 50000;

        // --- Palette mediterranea ---
        private static readonly Color TrunkColor = new Color(0.35f, 0.2f, 0.1f);
        private static readonly Color ParkGrassColor = new Color(0.2f, 0.6f, 0.1f);
        private static readonly Color ForestGroundColor = new Color(0.15f, 0.4f, 0.08f);
        private static readonly Color GrassColor = new Color(0.25f, 0.65f, 0.15f);
        private static readonly Color BenchWoodColor = new Color(0.45f, 0.28f, 0.12f);
        private static readonly Color BenchMetalColor = new Color(0.25f, 0.25f, 0.25f);

        // Palette chiome — specie dominanti colline
        private static readonly Color PineCrownColor = new Color(0.10f, 0.30f, 0.08f);
        private static readonly Color ChestnutCrownColor = new Color(0.20f, 0.48f, 0.12f);

        // Palette chiome — specie ornamentali parchi
        private static readonly Color BougainvilleaColor = new Color(0.75f, 0.15f, 0.55f);  // fucsia
        private static readonly Color GinkgoColor = new Color(0.55f, 0.70f, 0.15f);          // giallo-verde
        private static readonly Color GinkgoAutumnColor = new Color(0.85f, 0.75f, 0.10f);    // giallo oro
        private static readonly Color MagnoliaColor = new Color(0.18f, 0.42f, 0.16f);        // verde scuro lucido
        private static readonly Color OleandroColor = new Color(0.85f, 0.35f, 0.45f);        // rosa
        private static readonly Color PalmaColor = new Color(0.15f, 0.45f, 0.10f);           // verde tropicale
        private static readonly Color CedroColor = new Color(0.08f, 0.28f, 0.12f);           // verde blu scuro
        private static readonly Color TiglioColor = new Color(0.25f, 0.52f, 0.18f);          // verde chiaro

        private static readonly Color[] CrownColors =
        {
            PineCrownColor, ChestnutCrownColor,
            new Color(0.15f, 0.35f, 0.10f),
            new Color(0.12f, 0.32f, 0.08f),
            new Color(0.22f, 0.45f, 0.14f),
        };

        /// <summary>Tipo di albero ornamentale per i parchi.</summary>
        private enum ParkTreeType
        {
            PinoParasol,     // pino domestico (forma a ombrello)
            Ginkgo,          // chioma a ventaglio, giallo in autunno
            Bouganvillea,    // arbusto/rampicante fucsia
            Magnolia,        // chioma densa ovale
            Oleandro,        // arbusto fiorito rosa
            Palma,           // tronco slanciato + ciuffo
            Cedro,           // grande, conico, blu-verde
            Tiglio,          // chioma tonda classica
        }

        private static readonly ParkTreeType[] ParkTreeTypes = (ParkTreeType[])System.Enum.GetValues(typeof(ParkTreeType));

        // Parametri mesh procedurale
        private const int CylinderSegments = 6;
        private const int SphereRings = 4;
        private const int SphereSegments = 6;

        // Footpath types per identificare vialetti nei parchi
        private static readonly HashSet<string> FootpathTypes = new HashSet<string>
        {
            "footway", "path", "pedestrian", "cycleway", "steps"
        };

        /// <summary>
        /// Trova uno shader funzionante per il render pipeline corrente.
        /// </summary>
        private static Material CreateMaterial(Color color, float smoothness = 0.2f)
        {
            Material mat = new Material(MeshUtils.FindLitShader()) { color = color };
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", smoothness);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            return mat;
        }

        // ============================================================
        //  ENTRY POINT
        // ============================================================

        public static async Task PlaceVegetationAsync(
            List<VegetationFeature> vegetation,
            GameObject parent,
            Terrain[,] terrains, int gridCount,
            float tileWidthM, float tileLengthM,
            List<Vector3> benchPositions = null,
            List<WorldRoadData> roads = null)
        {
            if (vegetation == null || vegetation.Count == 0) return;

            // Materiali condivisi
            Material trunkMat = CreateMaterial(TrunkColor, 0.1f);
            Material parkMat = CreateMaterial(ParkGrassColor, 0.05f);
            Material grassMat = CreateMaterial(GrassColor, 0.05f);
            Material forestGroundMat = CreateMaterial(ForestGroundColor, 0.05f);
            Material benchWoodMat = CreateMaterial(BenchWoodColor, 0.15f);
            Material benchMetalMat = CreateMaterial(BenchMetalColor, 0.3f);
            Material[] crownMats = new Material[CrownColors.Length];
            for (int i = 0; i < CrownColors.Length; i++)
                crownMats[i] = CreateMaterial(CrownColors[i], 0.15f);

            // Materiali ornamentali parchi (colori vivaci cartoon)
            Dictionary<ParkTreeType, Material> parkCrownMats = new Dictionary<ParkTreeType, Material>
            {
                { ParkTreeType.PinoParasol, CreateMaterial(PineCrownColor, 0.15f) },
                { ParkTreeType.Ginkgo,      CreateMaterial(GinkgoColor, 0.1f) },
                { ParkTreeType.Bouganvillea,CreateMaterial(BougainvilleaColor, 0.1f) },
                { ParkTreeType.Magnolia,    CreateMaterial(MagnoliaColor, 0.2f) },
                { ParkTreeType.Oleandro,    CreateMaterial(OleandroColor, 0.1f) },
                { ParkTreeType.Palma,       CreateMaterial(PalmaColor, 0.15f) },
                { ParkTreeType.Cedro,       CreateMaterial(CedroColor, 0.15f) },
                { ParkTreeType.Tiglio,      CreateMaterial(TiglioColor, 0.1f) },
            };

            // Mesh ornamentali raggruppate per tipo e tile
            Dictionary<ParkTreeType, Dictionary<(int,int), TileMeshData>> parkCrownTiles =
                new Dictionary<ParkTreeType, Dictionary<(int,int), TileMeshData>>();
            foreach (var pt in ParkTreeTypes)
                parkCrownTiles[pt] = new Dictionary<(int,int), TileMeshData>();

            // Mesh di alberi raggruppate per tile
            Dictionary<(int, int), TileMeshData> trunkTiles = new Dictionary<(int, int), TileMeshData>();
            Dictionary<(int, int), TileMeshData> crownTiles = new Dictionary<(int, int), TileMeshData>();

            // Mesh overlay (parchi, prati) raggruppate per tile
            Dictionary<(int, int), TileMeshData> overlayTiles = new Dictionary<(int, int), TileMeshData>();

            // Mesh panchine
            Dictionary<(int, int), TileMeshData> benchWoodTiles = new Dictionary<(int, int), TileMeshData>();
            Dictionary<(int, int), TileMeshData> benchMetalTiles = new Dictionary<(int, int), TileMeshData>();

            // Estrai sentieri (footway/path) per piazzamento panchine nei parchi
            List<WorldRoadData> footpaths = new List<WorldRoadData>();
            if (roads != null)
            {
                foreach (var r in roads)
                {
                    if (r.centerline != null && r.centerline.Count >= 2 &&
                        FootpathTypes.Contains(r.highwayType ?? ""))
                        footpaths.Add(r);
                }
            }

            System.Random rng = new System.Random(42);
            int benchCount = 0;
            int treeCount = 0;
            bool treeLimitReached = false;

            for (int i = 0; i < vegetation.Count; i++)
            {
                if (i % 50 == 0)
                {
                    float progress = (float)i / vegetation.Count;
                    EditorUtility.DisplayProgressBar("CityBuilder", $"Vegetazione {i}/{vegetation.Count}...", progress);
                    await Task.Yield();
                }

                VegetationFeature veg = vegetation[i];
                string vt = (veg.vegType ?? "tree").ToLowerInvariant();

                switch (vt)
                {
                    case "tree":
                        if (treeCount < maxTotalTrees)
                        {
                            // Alberi singoli OSM: specie ornamentale casuale
                            PlaceParkTree(veg.position, terrains, gridCount, tileWidthM, tileLengthM,
                                trunkTiles, parkCrownTiles, rng, ref treeCount);
                        }
                        else if (!treeLimitReached)
                        {
                            treeLimitReached = true;
                            UnityEngine.Debug.LogWarning($"VegetationPlacer: raggiunto limite massimo di {maxTotalTrees} alberi. Gli alberi rimanenti verranno ignorati.");
                        }
                        break;

                    case "park":
                    case "garden":
                        if (veg.polygon != null && veg.polygon.Count >= 3)
                        {
                            AddPolygonOverlay(veg.polygon, terrains, gridCount, tileWidthM, tileLengthM,
                                overlayTiles, 0.02f);

                            // Alberi ornamentali nei parchi: varieta' di specie
                            ScatterParkTrees(veg.polygon, terrains, gridCount, tileWidthM, tileLengthM,
                                trunkTiles, parkCrownTiles, rng,
                                densityPerSqM: 1f / 400f,
                                ref treeCount, ref treeLimitReached);

                            // Panchine lungo i vialetti che attraversano il parco
                            benchCount += PlaceBenchesAlongParkPaths(
                                veg.polygon, footpaths, terrains, gridCount,
                                tileWidthM, tileLengthM, benchWoodTiles, benchMetalTiles, rng);
                        }
                        break;

                    case "wood":
                    case "forest":
                        if (veg.polygon != null && veg.polygon.Count >= 3)
                        {
                            AddPolygonOverlay(veg.polygon, terrains, gridCount, tileWidthM, tileLengthM,
                                overlayTiles, 0.01f);
                            // Boschi: mix castagni (60%) e pini marittimi (40%)
                            // Densita' ridotta per performance
                            ScatterTreesInPolygon(veg.polygon, terrains, gridCount, tileWidthM, tileLengthM,
                                trunkTiles, crownTiles, rng,
                                densityPerSqM: 1f / 200f, trunkH: 5f, crownR: 2.5f, isMedPine: false,
                                ref treeCount, ref treeLimitReached);
                        }
                        break;

                    case "grass":
                        if (veg.polygon != null && veg.polygon.Count >= 3)
                        {
                            AddPolygonOverlay(veg.polygon, terrains, gridCount, tileWidthM, tileLengthM,
                                overlayTiles, 0.02f);
                        }
                        break;
                }
            }

            // Panchine esplicite da OSM (non dentro parchi)
            if (benchPositions != null)
            {
                foreach (var bp in benchPositions)
                {
                    PlaceBench(bp, 0f, terrains, gridCount, tileWidthM, tileLengthM,
                        benchWoodTiles, benchMetalTiles);
                    benchCount++;
                }
            }

            // Costruisci GameObjects combinati
            EditorUtility.DisplayProgressBar("CityBuilder", "Fusione mesh vegetazione...", 0.9f);
            await Task.Yield();

            BuildAndFreeMeshes(trunkTiles, parent, trunkMat, "Tronchi");
            BuildAndFreeMeshes(crownTiles, parent, crownMats[0], "Chiome", crownMats);
            BuildAndFreeMeshes(overlayTiles, parent, parkMat, "Vegetazione_Suolo");
            BuildAndFreeMeshes(benchWoodTiles, parent, benchWoodMat, "Panchine_Legno");
            BuildAndFreeMeshes(benchMetalTiles, parent, benchMetalMat, "Panchine_Metallo");

            // Chiome ornamentali parchi (un colore per specie)
            foreach (var kv in parkCrownTiles)
            {
                if (parkCrownMats.TryGetValue(kv.Key, out Material mat))
                    BuildAndFreeMeshes(kv.Value, parent, mat, $"Parco_{kv.Key}");
            }
            parkCrownTiles.Clear();

            // Forza raccolta rifiuti dopo vegetazione (step pesante)
            System.GC.Collect();

            UnityEngine.Debug.Log($"Vegetazione: {vegetation.Count} elementi OSM, {treeCount} alberi (limite: {maxTotalTrees}), {benchCount} panchine.");
        }

        // ============================================================
        //  PANCHINE LUNGO VIALETTI NEI PARCHI
        // ============================================================

        /// <summary>
        /// Trova i sentieri (footway/path) che passano dentro il poligono del parco
        /// e piazza panchine lungo di essi a intervalli regolari.
        /// </summary>
        private static int PlaceBenchesAlongParkPaths(
            List<Vector3> parkPolygon,
            List<WorldRoadData> footpaths,
            Terrain[,] terrains, int gridCount,
            float tileWidthM, float tileLengthM,
            Dictionary<(int, int), TileMeshData> benchWoodTiles,
            Dictionary<(int, int), TileMeshData> benchMetalTiles,
            System.Random rng)
        {
            int placed = 0;
            float benchInterval = 25f; // una panchina ogni ~25m
            float benchOffset = 1.2f;  // distanza dal centro del sentiero

            foreach (var path in footpaths)
            {
                // Raccogli i segmenti del sentiero che sono dentro il parco
                List<Vector3> insidePoints = new List<Vector3>();
                foreach (var pt in path.centerline)
                {
                    if (PointInPolygonXZ(pt, parkPolygon))
                        insidePoints.Add(pt);
                }
                if (insidePoints.Count < 2) continue;

                // Percorri il sentiero e piazza panchine a intervalli
                float accumulated = benchInterval * 0.5f; // offset iniziale
                for (int i = 1; i < insidePoints.Count; i++)
                {
                    Vector3 prev = insidePoints[i - 1];
                    Vector3 curr = insidePoints[i];
                    Vector3 segDir = curr - prev;
                    float segLen = segDir.magnitude;
                    if (segLen < 0.01f) continue;
                    segDir /= segLen;

                    float pos = 0f;
                    while (pos < segLen)
                    {
                        float remaining = benchInterval - accumulated;
                        if (remaining <= segLen - pos)
                        {
                            pos += remaining;
                            accumulated = 0f;

                            // Punto sulla linea del sentiero
                            Vector3 benchPos = prev + segDir * pos;

                            // Offset laterale (alterno destra/sinistra)
                            Vector3 right = Vector3.Cross(segDir, Vector3.up).normalized;
                            float side = (placed % 2 == 0) ? benchOffset : -benchOffset;
                            benchPos += right * side;

                            // Angolo della panchina = perpendicolare al sentiero (guarda il sentiero)
                            float angle = Mathf.Atan2(segDir.x, segDir.z) * Mathf.Rad2Deg;

                            // Piccola variazione casuale
                            angle += (float)(rng.NextDouble() * 10.0 - 5.0);

                            PlaceBench(benchPos, angle, terrains, gridCount,
                                tileWidthM, tileLengthM, benchWoodTiles, benchMetalTiles);
                            placed++;
                        }
                        else
                        {
                            accumulated += segLen - pos;
                            break;
                        }
                    }
                }
            }

            // Se non ci sono sentieri dentro il parco, piazza qualche panchina sparsa
            if (placed == 0)
            {
                float area = PolygonAreaXZ(parkPolygon);
                int numBenches = Mathf.Clamp(Mathf.RoundToInt(area / 500f), 1, 8);

                float minX = float.MaxValue, maxX = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (var p in parkPolygon)
                {
                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.z < minZ) minZ = p.z;
                    if (p.z > maxZ) maxZ = p.z;
                }

                int maxAttempts = numBenches * 15;
                for (int a = 0; a < maxAttempts && placed < numBenches; a++)
                {
                    float rx = minX + (float)rng.NextDouble() * (maxX - minX);
                    float rz = minZ + (float)rng.NextDouble() * (maxZ - minZ);
                    Vector3 testPt = new Vector3(rx, 0, rz);

                    if (!PointInPolygonXZ(testPt, parkPolygon)) continue;

                    float angle = (float)(rng.NextDouble() * 360.0);
                    PlaceBench(testPt, angle, terrains, gridCount,
                        tileWidthM, tileLengthM, benchWoodTiles, benchMetalTiles);
                    placed++;
                }
            }

            return placed;
        }

        // ============================================================
        //  MESH PROCEDURALE: PANCHINA
        // ============================================================

        /// <summary>
        /// Genera una panchina procedurale: seduta in legno, struttura in metallo.
        /// Dimensioni reali: ~1.5m larga, ~0.45m profonda, ~0.45m altezza seduta, ~0.8m schienale.
        /// </summary>
        private static void PlaceBench(
            Vector3 worldPos, float angleY,
            Terrain[,] terrains, int gridCount,
            float tileWidthM, float tileLengthM,
            Dictionary<(int, int), TileMeshData> woodTiles,
            Dictionary<(int, int), TileMeshData> metalTiles)
        {
            float groundY = SampleTerrainHeight(worldPos, terrains, gridCount, tileWidthM, tileLengthM);
            Vector3 basePos = new Vector3(worldPos.x, groundY, worldPos.z);

            int gx = Mathf.Clamp((int)(worldPos.x / tileWidthM), 0, gridCount - 1);
            int gz = Mathf.Clamp((int)(worldPos.z / tileLengthM), 0, gridCount - 1);
            var tileKey = (gx, gz);

            if (!woodTiles.ContainsKey(tileKey))
            {
                woodTiles[tileKey] = new TileMeshData();
                metalTiles[tileKey] = new TileMeshData();
            }

            Quaternion rot = Quaternion.Euler(0, angleY, 0);

            // Dimensioni panchina
            float seatW = 1.5f;    // larghezza
            float seatD = 0.4f;    // profondita'
            float seatH = 0.06f;   // spessore seduta
            float seatY = 0.45f;   // altezza da terra
            float backH = 0.35f;   // altezza schienale
            float backT = 0.05f;   // spessore schienale
            float legW = 0.06f;    // spessore gambe
            float legD = 0.06f;

            // Seduta (legno) - box
            AddOrientedBox(woodTiles[tileKey], basePos, rot,
                new Vector3(0, seatY, 0), new Vector3(seatW, seatH, seatD));

            // Schienale (legno) - box dietro la seduta
            AddOrientedBox(woodTiles[tileKey], basePos, rot,
                new Vector3(0, seatY + seatH * 0.5f + backH * 0.5f, -seatD * 0.5f + backT * 0.5f),
                new Vector3(seatW, backH, backT));

            // 4 gambe (metallo) - thin boxes
            float legOffsetX = seatW * 0.4f;
            float legOffsetZ = seatD * 0.35f;
            float legH = seatY;

            AddOrientedBox(metalTiles[tileKey], basePos, rot,
                new Vector3(-legOffsetX, legH * 0.5f, legOffsetZ), new Vector3(legW, legH, legD));
            AddOrientedBox(metalTiles[tileKey], basePos, rot,
                new Vector3(legOffsetX, legH * 0.5f, legOffsetZ), new Vector3(legW, legH, legD));
            AddOrientedBox(metalTiles[tileKey], basePos, rot,
                new Vector3(-legOffsetX, legH * 0.5f, -legOffsetZ), new Vector3(legW, legH, legD));
            AddOrientedBox(metalTiles[tileKey], basePos, rot,
                new Vector3(legOffsetX, legH * 0.5f, -legOffsetZ), new Vector3(legW, legH, legD));

            // Braccioli (metallo) - ai lati
            float armY = seatY + seatH + 0.15f;
            float armH = 0.04f;
            float armD = seatD * 0.8f;
            AddOrientedBox(metalTiles[tileKey], basePos, rot,
                new Vector3(-seatW * 0.5f + legW * 0.5f, armY, 0), new Vector3(legW, armH, armD));
            AddOrientedBox(metalTiles[tileKey], basePos, rot,
                new Vector3(seatW * 0.5f - legW * 0.5f, armY, 0), new Vector3(legW, armH, armD));
        }

        /// <summary>
        /// Aggiunge un box orientato (ruotato) alla mesh. Center e' relativo a basePos.
        /// </summary>
        private static void AddOrientedBox(TileMeshData td, Vector3 basePos, Quaternion rot,
            Vector3 localCenter, Vector3 size)
        {
            int startV = td.vertices.Count;
            float hx = size.x * 0.5f, hy = size.y * 0.5f, hz = size.z * 0.5f;

            // 8 vertici del box in coordinate locali, poi ruotati e traslati
            Vector3[] corners = new Vector3[8]
            {
                new Vector3(-hx, -hy, -hz), new Vector3( hx, -hy, -hz),
                new Vector3( hx,  hy, -hz), new Vector3(-hx,  hy, -hz),
                new Vector3(-hx, -hy,  hz), new Vector3( hx, -hy,  hz),
                new Vector3( hx,  hy,  hz), new Vector3(-hx,  hy,  hz),
            };

            for (int i = 0; i < 8; i++)
                td.vertices.Add(basePos + rot * (localCenter + corners[i]));

            // 6 facce (12 triangoli)
            int[] faces = {
                0,2,1, 0,3,2,  // back
                4,5,6, 4,6,7,  // front
                0,1,5, 0,5,4,  // bottom
                2,3,7, 2,7,6,  // top
                0,4,7, 0,7,3,  // left
                1,2,6, 1,6,5,  // right
            };

            for (int i = 0; i < faces.Length; i++)
                td.triangles.Add(startV + faces[i]);
        }

        // ============================================================
        //  ALBERI SINGOLI
        // ============================================================

        private static void PlaceSingleTree(
            Vector3 worldPos,
            Terrain[,] terrains, int gridCount,
            float tileWidthM, float tileLengthM,
            Dictionary<(int, int), TileMeshData> trunkTiles,
            Dictionary<(int, int), TileMeshData> crownTiles,
            System.Random rng,
            float baseTrunkH, float baseCrownR, bool isMedPine)
        {
            float worldY = SampleTerrainHeight(worldPos, terrains, gridCount, tileWidthM, tileLengthM);
            Vector3 basePos = new Vector3(worldPos.x, worldY, worldPos.z);

            int gx = Mathf.Clamp((int)(worldPos.x / tileWidthM), 0, gridCount - 1);
            int gz = Mathf.Clamp((int)(worldPos.z / tileLengthM), 0, gridCount - 1);
            var tileKey = (gx, gz);

            if (!trunkTiles.ContainsKey(tileKey))
                trunkTiles[tileKey] = new TileMeshData();
            if (!crownTiles.ContainsKey(tileKey))
                crownTiles[tileKey] = new TileMeshData();

            // Variazione casuale +/- 20%
            float scale = 0.8f + (float)rng.NextDouble() * 0.4f;
            float trunkH = baseTrunkH * scale;
            float trunkR = 0.15f * scale;
            float crownR = baseCrownR * scale;

            if (isMedPine)
            {
                // Pino marittimo mediterraneo: tronco alto e sottile, chioma a ombrello
                trunkH = (5f + (float)rng.NextDouble() * 3f) * scale;
                trunkR = 0.12f * scale;
                crownR = (2f + (float)rng.NextDouble() * 1.5f) * scale;
                // Chioma appiattita: posta in cima al tronco
                AddCylinderToMesh(trunkTiles[tileKey], basePos, trunkR, trunkH);
                Vector3 crownPos = basePos + Vector3.up * (trunkH - crownR * 0.3f);
                AddFlattenedSphereToMesh(crownTiles[tileKey], crownPos, crownR, crownR * 0.4f);
            }
            else
            {
                // Albero generico: tronco + chioma sferica
                AddCylinderToMesh(trunkTiles[tileKey], basePos, trunkR, trunkH);
                Vector3 crownPos = basePos + Vector3.up * (trunkH + crownR * 0.5f);
                AddSphereToMesh(crownTiles[tileKey], crownPos, crownR);
            }
        }

        // ============================================================
        //  ALBERI ORNAMENTALI PARCHI
        // ============================================================

        /// <summary>
        /// Piazza un singolo albero ornamentale (specie casuale da parco).
        /// Ogni specie ha forma e dimensioni proprie.
        /// </summary>
        private static void PlaceParkTree(
            Vector3 worldPos,
            Terrain[,] terrains, int gridCount,
            float tileWidthM, float tileLengthM,
            Dictionary<(int, int), TileMeshData> trunkTiles,
            Dictionary<ParkTreeType, Dictionary<(int,int), TileMeshData>> parkCrownTiles,
            System.Random rng, ref int treeCount)
        {
            float worldY = SampleTerrainHeight(worldPos, terrains, gridCount, tileWidthM, tileLengthM);
            Vector3 basePos = new Vector3(worldPos.x, worldY, worldPos.z);

            int gx = Mathf.Clamp((int)(worldPos.x / tileWidthM), 0, gridCount - 1);
            int gz = Mathf.Clamp((int)(worldPos.z / tileLengthM), 0, gridCount - 1);
            var tileKey = (gx, gz);

            if (!trunkTiles.ContainsKey(tileKey))
                trunkTiles[tileKey] = new TileMeshData();

            ParkTreeType type = ParkTreeTypes[rng.Next(ParkTreeTypes.Length)];

            if (!parkCrownTiles[type].ContainsKey(tileKey))
                parkCrownTiles[type][tileKey] = new TileMeshData();

            TileMeshData trunkTD = trunkTiles[tileKey];
            TileMeshData crownTD = parkCrownTiles[type][tileKey];

            float scale = 0.8f + (float)rng.NextDouble() * 0.4f;

            switch (type)
            {
                case ParkTreeType.PinoParasol:
                {
                    // Pino domestico: tronco alto, chioma a ombrello
                    float h = (5f + (float)rng.NextDouble() * 3f) * scale;
                    AddCylinderToMesh(trunkTD, basePos, 0.12f * scale, h);
                    Vector3 cp = basePos + Vector3.up * (h - 0.5f);
                    AddFlattenedSphereToMesh(crownTD, cp, 2.5f * scale, 0.8f * scale);
                    break;
                }
                case ParkTreeType.Ginkgo:
                {
                    // Ginkgo biloba: tronco medio, chioma a ventaglio (conico-ovale)
                    float h = 3.5f * scale;
                    AddCylinderToMesh(trunkTD, basePos, 0.1f * scale, h);
                    Vector3 cp = basePos + Vector3.up * (h + 1.5f * scale);
                    // Chioma ovale verticale (piu' alta che larga)
                    AddFlattenedSphereToMesh(crownTD, cp, 1.5f * scale, 2.0f * scale);
                    break;
                }
                case ParkTreeType.Bouganvillea:
                {
                    // Bouganvillea: arbusto/rampicante basso, coloratissimo
                    float h = 0.8f * scale;
                    AddCylinderToMesh(trunkTD, basePos, 0.04f * scale, h);
                    // Massa irregolare di fiori
                    Vector3 cp = basePos + Vector3.up * (h + 0.6f * scale);
                    AddSphereToMesh(crownTD, cp, 1.2f * scale);
                    // Secondo lobo
                    Vector3 cp2 = cp + new Vector3(0.5f * scale, 0.2f * scale, 0.3f * scale);
                    AddSphereToMesh(crownTD, cp2, 0.8f * scale);
                    break;
                }
                case ParkTreeType.Magnolia:
                {
                    // Magnolia: chioma ovale densa e lucida
                    float h = 2.5f * scale;
                    AddCylinderToMesh(trunkTD, basePos, 0.12f * scale, h);
                    Vector3 cp = basePos + Vector3.up * (h + 1.8f * scale);
                    AddFlattenedSphereToMesh(crownTD, cp, 1.8f * scale, 2.2f * scale);
                    break;
                }
                case ParkTreeType.Oleandro:
                {
                    // Oleandro: arbusto fiorito, multi-stelo
                    float h = 1.5f * scale;
                    // 2-3 steli
                    for (int s = 0; s < 3; s++)
                    {
                        float ox = (s - 1) * 0.2f * scale;
                        AddCylinderToMesh(trunkTD, basePos + new Vector3(ox, 0, 0), 0.03f * scale, h);
                    }
                    Vector3 cp = basePos + Vector3.up * (h + 0.8f * scale);
                    AddSphereToMesh(crownTD, cp, 1.0f * scale);
                    break;
                }
                case ParkTreeType.Palma:
                {
                    // Palma: tronco alto slanciato + ciuffo in cima
                    float h = (4f + (float)rng.NextDouble() * 3f) * scale;
                    AddCylinderToMesh(trunkTD, basePos, 0.15f * scale, h);
                    // Ciuffo = ellissoide piatto in cima
                    Vector3 cp = basePos + Vector3.up * (h + 0.3f);
                    AddFlattenedSphereToMesh(crownTD, cp, 2.0f * scale, 0.5f * scale);
                    break;
                }
                case ParkTreeType.Cedro:
                {
                    // Cedro del Libano: grande, forma conica larga
                    float h = 3f * scale;
                    AddCylinderToMesh(trunkTD, basePos, 0.18f * scale, h);
                    // Chioma conica larga (piu' larga in basso)
                    Vector3 cp = basePos + Vector3.up * (h + 2.5f * scale);
                    AddFlattenedSphereToMesh(crownTD, cp, 3.0f * scale, 2.5f * scale);
                    break;
                }
                case ParkTreeType.Tiglio:
                {
                    // Tiglio: classico albero da viale, chioma tonda
                    float h = 3.5f * scale;
                    AddCylinderToMesh(trunkTD, basePos, 0.14f * scale, h);
                    Vector3 cp = basePos + Vector3.up * (h + 1.8f * scale);
                    AddSphereToMesh(crownTD, cp, 2.0f * scale);
                    break;
                }
            }

            treeCount++;
        }

        /// <summary>
        /// Spargi alberi ornamentali in un poligono parco.
        /// </summary>
        private static void ScatterParkTrees(
            List<Vector3> polygon,
            Terrain[,] terrains, int gridCount,
            float tileWidthM, float tileLengthM,
            Dictionary<(int, int), TileMeshData> trunkTiles,
            Dictionary<ParkTreeType, Dictionary<(int,int), TileMeshData>> parkCrownTiles,
            System.Random rng,
            float densityPerSqM,
            ref int treeCount, ref bool treeLimitReached)
        {
            float area = PolygonAreaXZ(polygon);
            int numTrees = Mathf.Max(1, Mathf.RoundToInt(area * densityPerSqM));

            int remaining = maxTotalTrees - treeCount;
            if (remaining <= 0) { treeLimitReached = true; return; }
            if (numTrees > remaining) numTrees = remaining;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var p in polygon)
            {
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.z < minZ) minZ = p.z;
                if (p.z > maxZ) maxZ = p.z;
            }

            int placed = 0;
            int maxAttempts = numTrees * 10;
            for (int a = 0; a < maxAttempts && placed < numTrees; a++)
            {
                float rx = minX + (float)rng.NextDouble() * (maxX - minX);
                float rz = minZ + (float)rng.NextDouble() * (maxZ - minZ);
                if (!PointInPolygonXZ(new Vector3(rx, 0, rz), polygon)) continue;

                PlaceParkTree(new Vector3(rx, 0, rz), terrains, gridCount,
                    tileWidthM, tileLengthM, trunkTiles, parkCrownTiles, rng, ref treeCount);
                placed++;
            }
        }

        // ============================================================
        //  SCATTER ALBERI IN POLIGONO (boschi: castagni + pini)
        // ============================================================

        private static void ScatterTreesInPolygon(
            List<Vector3> polygon,
            Terrain[,] terrains, int gridCount,
            float tileWidthM, float tileLengthM,
            Dictionary<(int, int), TileMeshData> trunkTiles,
            Dictionary<(int, int), TileMeshData> crownTiles,
            System.Random rng,
            float densityPerSqM, float trunkH, float crownR, bool isMedPine,
            ref int treeCount, ref bool treeLimitReached)
        {
            float area = PolygonAreaXZ(polygon);
            int numTrees = Mathf.Max(1, Mathf.RoundToInt(area * densityPerSqM));

            // Cap numTrees so total doesn't exceed maxTotalTrees
            int remaining = maxTotalTrees - treeCount;
            if (remaining <= 0)
            {
                if (!treeLimitReached)
                {
                    treeLimitReached = true;
                    UnityEngine.Debug.LogWarning($"VegetationPlacer: raggiunto limite massimo di {maxTotalTrees} alberi. Gli alberi rimanenti verranno ignorati.");
                }
                return;
            }
            if (numTrees > remaining)
                numTrees = remaining;

            // Bounding box del poligono
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var p in polygon)
            {
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.z < minZ) minZ = p.z;
                if (p.z > maxZ) maxZ = p.z;
            }

            int placed = 0;
            int maxAttempts = numTrees * 10;
            for (int a = 0; a < maxAttempts && placed < numTrees; a++)
            {
                float rx = minX + (float)rng.NextDouble() * (maxX - minX);
                float rz = minZ + (float)rng.NextDouble() * (maxZ - minZ);

                if (!PointInPolygonXZ(new Vector3(rx, 0, rz), polygon)) continue;

                PlaceSingleTree(new Vector3(rx, 0, rz), terrains, gridCount,
                    tileWidthM, tileLengthM, trunkTiles, crownTiles, rng,
                    trunkH, crownR, isMedPine);
                placed++;
                treeCount++;
            }
        }

        // ============================================================
        //  OVERLAY POLIGONALI (parchi, prati)
        // ============================================================

        private static void AddPolygonOverlay(
            List<Vector3> polygon,
            Terrain[,] terrains, int gridCount,
            float tileWidthM, float tileLengthM,
            Dictionary<(int, int), TileMeshData> overlayTiles,
            float yOffset)
        {
            if (polygon.Count < 3) return;

            // Usa il tile del centroide
            Vector3 centroid = Vector3.zero;
            foreach (var p in polygon) centroid += p;
            centroid /= polygon.Count;

            int gx = Mathf.Clamp((int)(centroid.x / tileWidthM), 0, gridCount - 1);
            int gz = Mathf.Clamp((int)(centroid.z / tileLengthM), 0, gridCount - 1);
            var tileKey = (gx, gz);

            if (!overlayTiles.ContainsKey(tileKey))
                overlayTiles[tileKey] = new TileMeshData();

            TileMeshData td = overlayTiles[tileKey];
            int startV = td.vertices.Count;

            // Vertici del poligono proiettati sul terreno + offset
            for (int i = 0; i < polygon.Count; i++)
            {
                float y = SampleTerrainHeight(polygon[i], terrains, gridCount, tileWidthM, tileLengthM);
                td.vertices.Add(new Vector3(polygon[i].x, y + yOffset, polygon[i].z));
            }

            // Triangolazione ear-clipping
            TriangulatePolygonXZ(polygon, startV, td);
        }

        // ============================================================
        //  MESH PROCEDURALE: CILINDRO (tronco)
        // ============================================================

        private static void AddCylinderToMesh(TileMeshData td, Vector3 basePos, float radius, float height)
        {
            int startV = td.vertices.Count;
            int seg = CylinderSegments;

            // Vertici anello inferiore e superiore
            for (int i = 0; i < seg; i++)
            {
                float angle = (float)i / seg * Mathf.PI * 2f;
                float x = Mathf.Cos(angle) * radius;
                float z = Mathf.Sin(angle) * radius;
                td.vertices.Add(basePos + new Vector3(x, 0, z));
                td.vertices.Add(basePos + new Vector3(x, height, z));
            }

            // Facce laterali
            for (int i = 0; i < seg; i++)
            {
                int curr = startV + i * 2;
                int next = startV + ((i + 1) % seg) * 2;
                td.triangles.Add(curr);
                td.triangles.Add(curr + 1);
                td.triangles.Add(next);
                td.triangles.Add(next);
                td.triangles.Add(curr + 1);
                td.triangles.Add(next + 1);
            }

            // Cap superiore
            int topCenter = td.vertices.Count;
            td.vertices.Add(basePos + Vector3.up * height);
            for (int i = 0; i < seg; i++)
            {
                int curr = startV + i * 2 + 1;
                int next = startV + ((i + 1) % seg) * 2 + 1;
                td.triangles.Add(topCenter);
                td.triangles.Add(next);
                td.triangles.Add(curr);
            }

            // Cap inferiore
            int bottomCenter = td.vertices.Count;
            td.vertices.Add(basePos);
            for (int i = 0; i < seg; i++)
            {
                int curr = startV + i * 2;
                int next = startV + ((i + 1) % seg) * 2;
                td.triangles.Add(bottomCenter);
                td.triangles.Add(curr);
                td.triangles.Add(next);
            }
        }

        // ============================================================
        //  MESH PROCEDURALE: SFERA (chioma generica)
        // ============================================================

        private static void AddSphereToMesh(TileMeshData td, Vector3 center, float radius)
        {
            int startV = td.vertices.Count;
            int rings = SphereRings;
            int segs = SphereSegments;

            td.vertices.Add(center + Vector3.up * radius);

            for (int r = 1; r < rings; r++)
            {
                float phi = Mathf.PI * r / rings;
                float y = Mathf.Cos(phi) * radius;
                float ringR = Mathf.Sin(phi) * radius;
                for (int s = 0; s < segs; s++)
                {
                    float theta = 2f * Mathf.PI * s / segs;
                    td.vertices.Add(center + new Vector3(
                        Mathf.Cos(theta) * ringR, y, Mathf.Sin(theta) * ringR));
                }
            }

            td.vertices.Add(center - Vector3.up * radius);

            int northPole = startV;
            int southPole = startV + 1 + (rings - 1) * segs;

            for (int s = 0; s < segs; s++)
            {
                int curr = startV + 1 + s;
                int next = startV + 1 + (s + 1) % segs;
                td.triangles.Add(northPole);
                td.triangles.Add(next);
                td.triangles.Add(curr);
            }

            for (int r = 0; r < rings - 2; r++)
            {
                for (int s = 0; s < segs; s++)
                {
                    int curr = startV + 1 + r * segs + s;
                    int next = startV + 1 + r * segs + (s + 1) % segs;
                    int currBelow = startV + 1 + (r + 1) * segs + s;
                    int nextBelow = startV + 1 + (r + 1) * segs + (s + 1) % segs;

                    td.triangles.Add(curr);
                    td.triangles.Add(next);
                    td.triangles.Add(currBelow);

                    td.triangles.Add(next);
                    td.triangles.Add(nextBelow);
                    td.triangles.Add(currBelow);
                }
            }

            int lastRingStart = startV + 1 + (rings - 2) * segs;
            for (int s = 0; s < segs; s++)
            {
                int curr = lastRingStart + s;
                int next = lastRingStart + (s + 1) % segs;
                td.triangles.Add(curr);
                td.triangles.Add(next);
                td.triangles.Add(southPole);
            }
        }

        // ============================================================
        //  MESH PROCEDURALE: SFERA APPIATTITA (chioma pino mediterraneo)
        // ============================================================

        private static void AddFlattenedSphereToMesh(TileMeshData td, Vector3 center, float radiusXZ, float radiusY)
        {
            int startV = td.vertices.Count;
            int rings = SphereRings;
            int segs = SphereSegments;

            td.vertices.Add(center + Vector3.up * radiusY);

            for (int r = 1; r < rings; r++)
            {
                float phi = Mathf.PI * r / rings;
                float y = Mathf.Cos(phi) * radiusY;
                float ringR = Mathf.Sin(phi) * radiusXZ;
                for (int s = 0; s < segs; s++)
                {
                    float theta = 2f * Mathf.PI * s / segs;
                    td.vertices.Add(center + new Vector3(
                        Mathf.Cos(theta) * ringR, y, Mathf.Sin(theta) * ringR));
                }
            }

            td.vertices.Add(center - Vector3.up * radiusY);

            int northPole = startV;
            int southPole = startV + 1 + (rings - 1) * segs;

            for (int s = 0; s < segs; s++)
            {
                int curr = startV + 1 + s;
                int next = startV + 1 + (s + 1) % segs;
                td.triangles.Add(northPole);
                td.triangles.Add(next);
                td.triangles.Add(curr);
            }

            for (int r = 0; r < rings - 2; r++)
            {
                for (int s = 0; s < segs; s++)
                {
                    int curr = startV + 1 + r * segs + s;
                    int next = startV + 1 + r * segs + (s + 1) % segs;
                    int currBelow = startV + 1 + (r + 1) * segs + s;
                    int nextBelow = startV + 1 + (r + 1) * segs + (s + 1) % segs;

                    td.triangles.Add(curr);
                    td.triangles.Add(next);
                    td.triangles.Add(currBelow);

                    td.triangles.Add(next);
                    td.triangles.Add(nextBelow);
                    td.triangles.Add(currBelow);
                }
            }

            int lastRingStart = startV + 1 + (rings - 2) * segs;
            for (int s = 0; s < segs; s++)
            {
                int curr = lastRingStart + s;
                int next = lastRingStart + (s + 1) % segs;
                td.triangles.Add(curr);
                td.triangles.Add(next);
                td.triangles.Add(southPole);
            }
        }

        // ============================================================
        //  COSTRUZIONE GAMEOBJECTS COMBINATI
        // ============================================================

        /// <summary>
        /// Costruisce mesh combinate e libera subito le liste intermedie.
        /// </summary>
        private static void BuildAndFreeMeshes(
            Dictionary<(int, int), TileMeshData> tiles,
            GameObject parent, Material defaultMat, string namePrefix,
            Material[] randomMats = null)
        {
            foreach (var kv in tiles)
            {
                TileMeshData td = kv.Value;
                if (td.vertices.Count == 0) { td.Free(); continue; }

                GameObject go = new GameObject($"{namePrefix}_Chunk_{kv.Key.Item1}_{kv.Key.Item2}");
                go.transform.parent = parent.transform;

                Mesh m = new Mesh();
                m.indexFormat = td.vertices.Count < 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt16
                    : UnityEngine.Rendering.IndexFormat.UInt32;
                m.vertices = td.vertices.ToArray();
                m.triangles = td.triangles.ToArray();
                m.RecalculateNormals();
                m.RecalculateBounds();

                go.AddComponent<MeshFilter>().sharedMesh = m;
                Material mat = (randomMats != null && randomMats.Length > 0) ? randomMats[0] : defaultMat;
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;

                // Libera subito le liste intermedie
                td.Free();
            }
            tiles.Clear();
        }

        // ============================================================
        //  UTILITA GEOMETRICHE
        // ============================================================

        private static float SampleTerrainHeight(Vector3 worldPos, Terrain[,] terrains, int gridCount,
            float tileWidthM, float tileLengthM)
        {
            int gx = Mathf.Clamp((int)(worldPos.x / tileWidthM), 0, gridCount - 1);
            int gz = Mathf.Clamp((int)(worldPos.z / tileLengthM), 0, gridCount - 1);

            if (terrains == null || terrains[gx, gz] == null) return 0f;

            return terrains[gx, gz].SampleHeight(new Vector3(worldPos.x, 0, worldPos.z));
        }

        private static float PolygonAreaXZ(List<Vector3> poly)
        {
            float area = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                Vector3 a = poly[i];
                Vector3 b = poly[(i + 1) % poly.Count];
                area += (b.x - a.x) * (b.z + a.z);
            }
            return Mathf.Abs(area) * 0.5f;
        }

        private static bool PointInPolygonXZ(Vector3 point, List<Vector3> polygon)
        {
            bool inside = false;
            int j = polygon.Count - 1;
            for (int i = 0; i < polygon.Count; j = i++)
            {
                if ((polygon[i].z > point.z) != (polygon[j].z > point.z) &&
                    point.x < (polygon[j].x - polygon[i].x) * (point.z - polygon[i].z)
                             / (polygon[j].z - polygon[i].z) + polygon[i].x)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        private static void TriangulatePolygonXZ(List<Vector3> pts, int startV, TileMeshData td)
        {
            List<int> indices = new List<int>(pts.Count);
            for (int i = 0; i < pts.Count; i++) indices.Add(i);

            float signedArea = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 a = pts[i];
                Vector3 b = pts[(i + 1) % pts.Count];
                signedArea += (b.x - a.x) * (b.z + a.z);
            }
            bool isCW = signedArea > 0;

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

                    int vA = startV + indices[iPrev];
                    int vB = startV + indices[i];
                    int vC = startV + indices[iNext];

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
    }
}
