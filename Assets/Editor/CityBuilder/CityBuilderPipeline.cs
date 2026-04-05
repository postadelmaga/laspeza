using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CityBuilder
{
    /// <summary>
    /// Pipeline modulare per la generazione della citta'.
    /// Mantiene lo stato condiviso tra gli step e permette di
    /// rigenerare singoli aspetti senza rifare tutto da capo.
    ///
    /// Dipendenze tra step:
    ///   Terreno -> Texture, OSM (per bounds)
    ///   OSM -> Conversione Coordinate -> Edifici, Strade, Piazze, Acqua, Vegetazione
    ///   Scena puo' essere eseguito in qualsiasi momento dopo il Terreno
    /// </summary>
    public class CityBuilderPipeline
    {
        // ================================================================
        //  STATO CONDIVISO
        // ================================================================

        // Input
        public string tifFilePath;
        public int gridCount = 4;
        public bool useAutoCrop = true;

        // Terreno
        public TerrainMetaData tMeta;
        public float cropWidthM, cropLengthM;
        public Terrain[,] generatedTerrains;
        public GameObject worldParent;

        // OSM
        public OsmDownloadResult osmData;
        public string osmCachePath;

        // Dati convertiti in coordinate mondo
        public List<WorldBuildingData> buildings;
        public List<WorldRoadData> roads;
        public List<WorldWaterData> water;
        public List<VegetationFeature> vegetation;
        public List<Vector3> benchPositions;
        public List<WorldPedestrianArea> pedestrianAreas;

        // Step parents (per poter distruggere e rigenerare singoli aspetti)
        public GameObject edificiParent;
        public GameObject stradeParent;
        public GameObject piazzeParent;
        public GameObject acquaParent;
        public GameObject vegetazioneParent;

        // Tracking completamento step
        public bool hasTerreno;
        public bool hasTexture;
        public bool hasOsm;
        public bool hasCoordinate;
        public bool hasEdifici;
        public bool hasStrade;
        public bool hasPiazze;
        public bool hasAcqua;
        public bool hasVegetazione;
        public bool hasScena;

        // Tutte le coordinate sono in metri reali (UTM)

        // ================================================================
        //  RIPRISTINO STATO DA SCENA + DISCO (per step separati)
        // ================================================================

        /// <summary>
        /// Ricostruisce lo stato della pipeline dalla scena corrente e dai file
        /// su disco. Permette di eseguire step singoli in processi Unity separati.
        /// </summary>
        public bool RestoreFromScene()
        {
            // Cerca worldParent nella scena
            worldParent = GameObject.Find("CityBuilder_World");
            if (worldParent == null)
            {
                Debug.Log("RestoreFromScene: nessun CityBuilder_World trovato (primo run)");
                return false;
            }

            // Ricostruisci terrain grid
            Terrain[] allTerrains = worldParent.GetComponentsInChildren<Terrain>();
            if (allTerrains.Length > 0)
            {
                // Calcola gridCount dalla disposizione dei terreni
                gridCount = Mathf.RoundToInt(Mathf.Sqrt(allTerrains.Length));
                if (gridCount < 1) gridCount = 1;

                generatedTerrains = new Terrain[gridCount, gridCount];
                foreach (var t in allTerrains)
                {
                    float tileW = t.terrainData.size.x;
                    float tileL = t.terrainData.size.z;
                    int gx = Mathf.Clamp(Mathf.RoundToInt(t.transform.position.x / tileW), 0, gridCount - 1);
                    int gz = Mathf.Clamp(Mathf.RoundToInt(t.transform.position.z / tileL), 0, gridCount - 1);
                    generatedTerrains[gx, gz] = t;
                }

                // Calcola dimensioni dal terrain
                var firstTerrain = allTerrains[0];
                float tileWidth = firstTerrain.terrainData.size.x;
                float tileLength = firstTerrain.terrainData.size.z;
                cropWidthM = tileWidth * gridCount;
                cropLengthM = tileLength * gridCount;

                hasTerreno = true;
                hasTexture = true; // assumiamo applicata
                Debug.Log($"RestoreFromScene: terreno {gridCount}x{gridCount}, {cropWidthM:F0}x{cropLengthM:F0}m");
            }

            // Cerca parent esistenti
            edificiParent = worldParent.transform.Find("Edifici")?.gameObject;
            stradeParent = worldParent.transform.Find("Strade")?.gameObject;
            piazzeParent = worldParent.transform.Find("Piazze")?.gameObject;
            acquaParent = worldParent.transform.Find("Acqua")?.gameObject;
            vegetazioneParent = worldParent.transform.Find("Vegetazione")?.gameObject;

            hasEdifici = edificiParent != null;
            hasStrade = stradeParent != null;
            hasPiazze = piazzeParent != null;
            hasAcqua = acquaParent != null;
            hasVegetazione = vegetazioneParent != null;

            // Ricarica metadata terreno da disco
            if (!string.IsNullOrEmpty(tifFilePath))
            {
                string dataDir = System.IO.Path.GetDirectoryName(tifFilePath);
                string metaPath = System.IO.Path.Combine(dataDir, "terrain_meta_saved.json");
                osmCachePath = System.IO.Path.Combine(dataDir, "osm_cache.json");

                if (System.IO.File.Exists(metaPath))
                {
                    tMeta = JsonUtility.FromJson<TerrainMetaData>(
                        System.IO.File.ReadAllText(metaPath));
                    Debug.Log($"RestoreFromScene: tMeta caricato da {metaPath}");
                }
                else if (hasTerreno && allTerrains.Length > 0)
                {
                    // Ricostruisci tMeta minimo dal terreno
                    tMeta = new TerrainMetaData
                    {
                        heightM = allTerrains[0].terrainData.size.y,
                        widthM = cropWidthM,
                        lengthM = cropLengthM,
                        seaLevelNorm = 0.02f // default
                    };
                }

                // OSM cache: carica osmData se disponibile
                if (System.IO.File.Exists(osmCachePath))
                {
                    try
                    {
                        string osmJson = System.IO.File.ReadAllText(osmCachePath);
                        osmData = OverpassDownloader.ParseJson(osmJson);
                        if (osmData != null && tMeta != null)
                        {
                            osmData.minLon = tMeta.minLon;
                            osmData.minLat = tMeta.minLat;
                            osmData.maxLon = tMeta.maxLon;
                            osmData.maxLat = tMeta.maxLat;
                        }
                        hasOsm = true;
                        Debug.Log($"RestoreFromScene: osmData caricato dalla cache ({osmCachePath})");
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning($"RestoreFromScene: errore caricamento cache OSM: {e.Message}");
                    }
                }
            }

            return hasTerreno;
        }

        // ================================================================
        //  STEP 1: TERRENO
        // ================================================================

        public async Task<bool> StepTerrain()
        {
            if (string.IsNullOrEmpty(tifFilePath))
            {
                EditorUtility.DisplayDialog("CityBuilder", "Seleziona un GeoTIFF!", "OK");
                return false;
            }

            string dataDir = System.IO.Path.GetDirectoryName(tifFilePath);
            string rawPath = System.IO.Path.Combine(dataDir, "processed_terrain.raw");
            string metaSavedPath = System.IO.Path.Combine(dataDir, "terrain_meta_saved.json");
            osmCachePath = System.IO.Path.Combine(dataDir, "osm_cache.json");

            // Se .raw e metadata esistono gia' (pre-processati da prepare_terrain.py), salta Python
            if (System.IO.File.Exists(rawPath) && System.IO.File.Exists(metaSavedPath))
            {
                Debug.Log("Terreno pre-processato trovato, salto Python!");
                tMeta = JsonUtility.FromJson<TerrainMetaData>(
                    System.IO.File.ReadAllText(metaSavedPath));
            }
            else
            {
                EditorUtility.DisplayProgressBar("CityBuilder", "Elaborazione GeoTIFF...", 0.1f);
                tMeta = await GisPythonEngine.CleanTifAsync(tifFilePath, rawPath);
                if (tMeta == null)
                {
                    Debug.LogError("CityBuilder: GeoTIFF processing fallito");
                    EditorUtility.ClearProgressBar();
                    return false;
                }
            }

            if (worldParent != null) Undo.DestroyObjectImmediate(worldParent);
            worldParent = new GameObject("CityBuilder_World");
            Undo.RegisterCreatedObjectUndo(worldParent, "CityBuilder World");

            // Reset dei parent figli
            edificiParent = stradeParent = piazzeParent = acquaParent = vegetazioneParent = null;

            EditorUtility.DisplayProgressBar("CityBuilder", "Costruzione terreno...", 0.3f);
            // Clona tMeta perché TerrainSlicer muta i bounds con il crop
            // (evita crop progressivo se StepTerrain viene chiamato più volte)
            var tMetaForSlicer = tMeta.Clone();
            generatedTerrains = await TerrainSlicer.BuildSmartTerrainAsync(
                rawPath, tMetaForSlicer, gridCount, useAutoCrop, worldParent,
                (w, l) => { cropWidthM = w; cropLengthM = l; });
            // Copia i bounds croppati nel tMeta principale
            tMeta = tMetaForSlicer;

            EditorUtility.ClearProgressBar();

            if (generatedTerrains == null)
            {
                Debug.LogError("CityBuilder: Terrain slicer fallito");
                return false;
            }

            hasTerreno = true;
            // Invalida step dipendenti
            hasTexture = false;
            hasCoordinate = false;
            hasEdifici = hasStrade = hasPiazze = hasAcqua = hasVegetazione = false;
            hasScena = false;

            // Salva tMeta su disco per step separati
            SaveTerrainMeta();

            Debug.Log($"Terreno completato: {gridCount}x{gridCount} tiles, crop {cropWidthM:F0}x{cropLengthM:F0}m, GPS [{tMeta.minLon:F4},{tMeta.minLat:F4}]-[{tMeta.maxLon:F4},{tMeta.maxLat:F4}]");
            Debug.Log($"CityBuilder Step 1: Terreno generato ({gridCount}x{gridCount}, {cropWidthM:F0}x{cropLengthM:F0}m)");
            return true;
        }

        private void SaveTerrainMeta()
        {
            if (tMeta == null || string.IsNullOrEmpty(tifFilePath)) return;
            string dataDir = System.IO.Path.GetDirectoryName(tifFilePath);
            string metaPath = System.IO.Path.Combine(dataDir, "terrain_meta_saved.json");
            // Salva tMeta con i bounds aggiornati dal crop
            var json = JsonUtility.ToJson(tMeta, true);
            System.IO.File.WriteAllText(metaPath, json);
            Debug.Log($"tMeta salvato: {metaPath}");
        }

        // ================================================================
        //  STEP 2: TEXTURE TERRENO
        // ================================================================

        public async Task<bool> StepTextures()
        {
            if (!hasTerreno)
            {
                Debug.LogError("CityBuilder: genera prima il terreno (Step 1)");
                return false;
            }

            try
            {
                await TerrainTextureGenerator.ApplyProceduralTexturesAsync(
                    generatedTerrains, gridCount, tMeta.seaLevelNorm);
                hasTexture = true;
                Debug.Log("CityBuilder Step 2: Texture terreno applicate.");
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("Texture terreno fallita: " + e.Message);
                return false;
            }
        }

        // ================================================================
        //  STEP 3: DOWNLOAD OSM
        // ================================================================

        public async Task<bool> StepDownloadOsm()
        {
            if (!hasTerreno)
            {
                Debug.LogError("CityBuilder: genera prima il terreno (Step 1) per avere i bounds GPS");
                return false;
            }

            EditorUtility.DisplayProgressBar("CityBuilder", "Download dati OSM...", 0.1f);
            osmData = await OverpassDownloader.DownloadAsync(
                tMeta.minLon, tMeta.minLat, tMeta.maxLon, tMeta.maxLat, osmCachePath);
            EditorUtility.ClearProgressBar();

            if (osmData == null)
            {
                Debug.LogError("CityBuilder: Download OSM fallito");
                return false;
            }

            Debug.Log($"CityBuilder Step 3: OSM scaricato — {osmData.buildings.Count} edifici, " +
                      $"{osmData.roads.Count} strade, {osmData.water.Count} acqua, " +
                      $"{osmData.pedestrianAreas.Count} piazze, {osmData.vegetation.Count} vegetazione");

            hasOsm = true;
            // Invalida conversione e step dipendenti
            hasCoordinate = false;
            hasEdifici = hasStrade = hasPiazze = hasAcqua = hasVegetazione = false;

            return true;
        }

        // ================================================================
        //  STEP 3b: CONVERSIONE COORDINATE (automatica, interna)
        // ================================================================

        public bool StepConvertCoordinates()
        {
            if (!hasTerreno || !hasOsm)
            {
                Debug.LogError("CityBuilder: servono terreno e dati OSM prima della conversione");
                return false;
            }

            float tileW = cropWidthM / gridCount;
            float tileL = cropLengthM / gridCount;

            buildings = ConvertBuildings(osmData.buildings, tileW, tileL);
            roads = ConvertRoads(osmData.roads);
            water = ConvertWater(osmData.water);
            vegetation = ConvertVegetation(osmData.vegetation);
            benchPositions = ConvertBenches(osmData.amenities);
            pedestrianAreas = ConvertPedestrianAreas(osmData.pedestrianAreas);

            hasCoordinate = true;
            Debug.Log("CityBuilder: coordinate convertite.");
            return true;
        }

        // ================================================================
        //  STEP 4: EDIFICI
        // ================================================================

        public async Task<bool> StepBuildings()
        {
            if (!EnsureCoordinates()) return false;

            DestroyChild(ref edificiParent, "Edifici");
            edificiParent = CreateChild(worldParent, "Edifici");

            float tileW = cropWidthM / gridCount;
            float tileL = cropLengthM / gridCount;

            EditorUtility.DisplayProgressBar("CityBuilder", $"Generazione {buildings.Count} edifici...", 0.1f);
            await BuildingGenerator.GenerateBuildingsAsync(
                buildings, edificiParent, gridCount, generatedTerrains, tileW, tileL);
            EditorUtility.ClearProgressBar();

            hasEdifici = true;
            Debug.Log($"CityBuilder Step 4: {buildings.Count} edifici generati.");
            return true;
        }

        // ================================================================
        //  STEP 5: STRADE
        // ================================================================

        public async Task<bool> StepRoads()
        {
            if (!EnsureCoordinates()) return false;

            DestroyChild(ref stradeParent, "Strade");
            stradeParent = CreateChild(worldParent, "Strade");

            float tileW = cropWidthM / gridCount;
            float tileL = cropLengthM / gridCount;

            EditorUtility.DisplayProgressBar("CityBuilder", $"Generazione {roads.Count} strade...", 0.1f);
            await RoadMeshBuilder.GenerateRoadsAsync(
                roads, stradeParent, generatedTerrains, gridCount, tileW, tileL);
            EditorUtility.ClearProgressBar();

            hasStrade = true;
            Debug.Log($"CityBuilder Step 5: {roads.Count} strade generate.");
            return true;
        }

        // ================================================================
        //  STEP 6: PIAZZE E AREE PEDONALI
        // ================================================================

        public async Task<bool> StepPedestrianAreas()
        {
            if (!EnsureCoordinates()) return false;

            DestroyChild(ref piazzeParent, "Piazze");
            piazzeParent = CreateChild(worldParent, "Piazze");

            float tileW = cropWidthM / gridCount;
            float tileL = cropLengthM / gridCount;

            EditorUtility.DisplayProgressBar("CityBuilder", $"Generazione {pedestrianAreas.Count} piazze...", 0.1f);
            await PedestrianAreaBuilder.GenerateAreasAsync(
                pedestrianAreas, piazzeParent, generatedTerrains, gridCount, tileW, tileL);
            EditorUtility.ClearProgressBar();

            hasPiazze = true;
            Debug.Log($"CityBuilder Step 6: {pedestrianAreas.Count} piazze generate.");
            return true;
        }

        // ================================================================
        //  STEP 7: ACQUA
        // ================================================================

        public async Task<bool> StepWater()
        {
            if (!EnsureCoordinates()) return false;

            DestroyChild(ref acquaParent, "Acqua");
            acquaParent = CreateChild(worldParent, "Acqua");

            // Piccolo offset positivo (+0.15m) per evitare z-fighting dove costa incontra mare
            float seaY = tMeta.seaLevelNorm * tMeta.heightM + 0.15f;

            EditorUtility.DisplayProgressBar("CityBuilder", $"Generazione {water.Count} corpi d'acqua...", 0.1f);
            await WaterBuilder.GenerateWaterAsync(water, acquaParent, seaY, cropWidthM, cropLengthM);
            EditorUtility.ClearProgressBar();

            hasAcqua = true;
            Debug.Log($"CityBuilder Step 7: acqua generata.");
            return true;
        }

        // ================================================================
        //  STEP 8: VEGETAZIONE + ARREDO URBANO
        // ================================================================

        public async Task<bool> StepVegetation()
        {
            if (!EnsureCoordinates()) return false;

            DestroyChild(ref vegetazioneParent, "Vegetazione");
            vegetazioneParent = CreateChild(worldParent, "Vegetazione");

            float tileW = cropWidthM / gridCount;
            float tileL = cropLengthM / gridCount;

            EditorUtility.DisplayProgressBar("CityBuilder", $"Vegetazione e arredo...", 0.1f);
            await VegetationPlacer.PlaceVegetationAsync(
                vegetation, vegetazioneParent, generatedTerrains, gridCount, tileW, tileL,
                benchPositions, roads);
            EditorUtility.ClearProgressBar();

            hasVegetazione = true;
            Debug.Log($"CityBuilder Step 8: vegetazione e arredo urbano generati.");
            return true;
        }

        // ================================================================
        //  STEP 9: CONFIGURAZIONE SCENA
        // ================================================================

        public bool StepScene()
        {
            if (!hasTerreno || worldParent == null)
            {
                Debug.LogError("CityBuilder: genera prima il terreno");
                return false;
            }

            SceneSetup.SetupScene(worldParent);

            // Piazza landmark iconici
            if (tMeta != null && generatedTerrains != null)
            {
                LandmarkPlacer.PlaceLandmarks(
                    worldParent, tMeta, cropWidthM, cropLengthM,
                    generatedTerrains, gridCount);
            }

            hasScena = true;
            Debug.Log("CityBuilder Step 9: scena configurata + landmark.");
            return true;
        }

        // ================================================================
        //  PIPELINE COMPLETA (tutti gli step in sequenza)
        // ================================================================

        /// <summary>Limite massimo di feature per contenere l'uso di RAM.</summary>
        public int maxBuildings = 30000;
        public int maxRoads = 15000;

        public async Task<bool> RunFullPipeline()
        {
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("CityBuilder: Genera Citta");

            var pipelineStart = System.Diagnostics.Stopwatch.StartNew();
            LogMemory("INIZIO PIPELINE");

            if (!await RunTracked("1. Terreno", StepTerrain)) return false;
            FlushMemory();
            await RunTracked("2. Texture", StepTextures);
            FlushMemory();
            if (!await RunTracked("3. Download OSM", StepDownloadOsm)) return false;

            TrimOsmData();

            LogMemory("PRE-CONVERSIONE");
            StepConvertCoordinates();

            // Libera i dati OSM grezzi — abbiamo gia' le coordinate mondo
            osmData = null;
            FlushMemory();
            LogMemory("POST-CONVERSIONE");

            if (!await RunTracked("4. Edifici", StepBuildings)) return false;
            buildings = null;
            FlushMemory();
            LogMemory("POST-FREE Edifici");

            if (!await RunTracked("5. Strade", StepRoads)) return false;
            // Nota: roads serve ancora per VegetationPlacer (sentieri parchi)
            FlushMemory();

            await RunTracked("6. Piazze", StepPedestrianAreas);
            pedestrianAreas = null;
            FlushMemory();

            if (!await RunTracked("7. Acqua", StepWater)) return false;
            water = null;
            FlushMemory();

            if (!await RunTracked("8. Vegetazione", StepVegetation)) return false;
            vegetation = null;
            benchPositions = null;
            roads = null;
            FlushMemory();

            LogMemory("PRE-SCENA");
            StepScene();
            LogMemory("POST-SCENA");

            RegisterChildrenUndo(worldParent.transform);
            Undo.CollapseUndoOperations(undoGroup);

            pipelineStart.Stop();
            LogMemory("FINE PIPELINE");
            Debug.Log($"========================================");
            Debug.Log($"CityBuilder: Citta' completa in {pipelineStart.Elapsed.TotalSeconds:F1}s");
            Debug.Log($"========================================");
            return true;
        }

        private async Task<bool> RunTracked(string name, System.Func<Task<bool>> step)
        {
            LogMemory($"PRE  {name}");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            bool ok = await step();
            sw.Stop();

            string status = ok ? "OK" : "FAIL";
            LogMemory($"POST {name} [{status}] {sw.Elapsed.TotalSeconds:F1}s");
            return ok;
        }

        private static void LogMemory(string label)
        {
            long managedMB = System.GC.GetTotalMemory(false) / (1024 * 1024);
            long processMB = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
            long virtualMB = System.Diagnostics.Process.GetCurrentProcess().VirtualMemorySize64 / (1024 * 1024);
            Debug.Log($"[MEM] {label} | Managed: {managedMB}MB | RSS: {processMB}MB | Virtual: {virtualMB}MB");
        }

        private void TrimOsmData()
        {
            if (osmData == null) return;

            if (osmData.buildings.Count > maxBuildings)
            {
                Debug.Log($"Limitato edifici: {osmData.buildings.Count} -> {maxBuildings} (risparmio RAM)");
                osmData.buildings.RemoveRange(maxBuildings, osmData.buildings.Count - maxBuildings);
            }
            if (osmData.roads.Count > maxRoads)
            {
                Debug.Log($"Limitato strade: {osmData.roads.Count} -> {maxRoads} (risparmio RAM)");
                osmData.roads.RemoveRange(maxRoads, osmData.roads.Count - maxRoads);
            }
        }

        private static void FlushMemory()
        {
            // Doppio GC per raccogliere oggetti con finalizer
            System.GC.Collect(System.GC.MaxGeneration, System.GCCollectionMode.Forced);
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect(System.GC.MaxGeneration, System.GCCollectionMode.Forced);
            // Chiedi a Unity di scaricare asset inutilizzati
            Resources.UnloadUnusedAssets();
        }

        // ================================================================
        //  CONVERSIONE LatLon -> Vector3 World
        // ================================================================

        private Vector3 LatLonToWorld(LatLon ll)
        {
            float x = Mathf.InverseLerp(tMeta.minLon, tMeta.maxLon, (float)ll.lon) * cropWidthM;
            float z = Mathf.InverseLerp(tMeta.minLat, tMeta.maxLat, (float)ll.lat) * cropLengthM;
            return new Vector3(x, 0, z);
        }

        private float SampleTerrainHeight(float worldX, float worldZ)
        {
            float tileW = cropWidthM / gridCount;
            float tileL = cropLengthM / gridCount;
            int gx = Mathf.Clamp((int)(worldX / tileW), 0, gridCount - 1);
            int gz = Mathf.Clamp((int)(worldZ / tileL), 0, gridCount - 1);
            if (generatedTerrains == null || generatedTerrains[gx, gz] == null) return 0;
            return generatedTerrains[gx, gz].SampleHeight(new Vector3(worldX, 0, worldZ));
        }

        private List<WorldBuildingData> ConvertBuildings(List<OsmBuildingData> src, float tileW, float tileL)
        {
            var result = new List<WorldBuildingData>(src.Count);
            foreach (var b in src)
            {
                if (b.footprint.Count < 3) continue;
                var wb = new WorldBuildingData
                {
                    height = b.height > 0 ? b.height : 6f,
                    minHeight = b.minHeight,
                    levels = b.levels > 0 ? b.levels : (b.height > 0 ? Mathf.RoundToInt(b.height / 3f) : 2),
                    material = b.material,
                    colour = b.colour,
                    roofShape = b.roofShape,
                    roofMaterial = b.roofMaterial
                };

                bool tileSet = false;
                foreach (var ll in b.footprint)
                {
                    Vector3 p = LatLonToWorld(ll);
                    p.y = SampleTerrainHeight(p.x, p.z);
                    wb.footprint.Add(p);

                    if (!tileSet)
                    {
                        wb.tileX = Mathf.Clamp((int)(p.x / tileW), 0, gridCount - 1);
                        wb.tileZ = Mathf.Clamp((int)(p.z / tileL), 0, gridCount - 1);
                        tileSet = true;
                    }
                }
                result.Add(wb);
            }
            return result;
        }

        private List<WorldRoadData> ConvertRoads(List<OsmRoadData> src)
        {
            var result = new List<WorldRoadData>(src.Count);
            for (int i = 0; i < src.Count; i++)
            {
                var r = src[i];
                if (r.centerline.Count < 2) continue;
                var wr = new WorldRoadData
                {
                    centerline = new List<Vector3>(r.centerline.Count),
                    highwayType = r.highwayType,
                    lanes = r.lanes,
                    width = r.width > 0 ? r.width : 0,
                    surface = r.surface,
                    name = r.name,
                    oneway = r.oneway
                };
                foreach (var ll in r.centerline)
                    wr.centerline.Add(LatLonToWorld(ll));
                result.Add(wr);
            }
            return result;
        }

        private List<WorldWaterData> ConvertWater(List<OsmWaterData> src)
        {
            var result = new List<WorldWaterData>(src.Count);
            foreach (var w in src)
            {
                if (w.polygon.Count < 3) continue;
                var ww = new WorldWaterData
                {
                    polygon = new List<Vector3>(w.polygon.Count),
                    waterType = w.waterType
                };
                foreach (var ll in w.polygon)
                    ww.polygon.Add(LatLonToWorld(ll));
                result.Add(ww);
            }
            return result;
        }

        private List<VegetationFeature> ConvertVegetation(List<OsmVegetationData> src)
        {
            var result = new List<VegetationFeature>(src.Count);
            foreach (var v in src)
            {
                var vf = new VegetationFeature { vegType = v.vegType, isPoint = v.isPoint };
                if (v.isPoint && v.point != null)
                {
                    vf.position = LatLonToWorld(v.point);
                }
                else if (v.polygon != null && v.polygon.Count >= 3)
                {
                    vf.polygon = new List<Vector3>(v.polygon.Count);
                    foreach (var ll in v.polygon)
                        vf.polygon.Add(LatLonToWorld(ll));
                }
                else continue;
                result.Add(vf);
            }
            return result;
        }

        private List<Vector3> ConvertBenches(List<OsmAmenityData> amenities)
        {
            var result = new List<Vector3>();
            if (amenities == null) return result;
            foreach (var a in amenities)
            {
                if (a.amenityType == "bench" && a.point != null)
                    result.Add(LatLonToWorld(a.point));
            }
            return result;
        }

        private List<WorldPedestrianArea> ConvertPedestrianAreas(List<OsmPedestrianAreaData> src)
        {
            var result = new List<WorldPedestrianArea>(src.Count);
            foreach (var pa in src)
            {
                if (pa.polygon.Count < 3) continue;
                var wpa = new WorldPedestrianArea
                {
                    polygon = new List<Vector3>(pa.polygon.Count),
                    areaType = pa.areaType,
                    surface = pa.surface,
                    name = pa.name
                };
                foreach (var ll in pa.polygon)
                    wpa.polygon.Add(LatLonToWorld(ll));
                result.Add(wpa);
            }
            return result;
        }

        // ================================================================
        //  UTILITY
        // ================================================================

        private bool EnsureCoordinates()
        {
            if (!hasTerreno)
            {
                Debug.LogError("CityBuilder: genera prima il terreno (Step 1)");
                return false;
            }
            if (!hasOsm)
            {
                Debug.LogError("CityBuilder: scarica prima i dati OSM (Step 3)");
                return false;
            }
            if (!hasCoordinate)
                StepConvertCoordinates();
            return true;
        }

        private void DestroyChild(ref GameObject child, string name)
        {
            if (child != null)
            {
                Undo.DestroyObjectImmediate(child);
                child = null;
            }
            else if (worldParent != null)
            {
                // Cerca per nome nel caso sia stato ricaricato
                var existing = worldParent.transform.Find(name);
                if (existing != null)
                    Undo.DestroyObjectImmediate(existing.gameObject);
            }
        }

        private static GameObject CreateChild(GameObject parent, string name)
        {
            var go = new GameObject(name);
            go.transform.parent = parent.transform;
            return go;
        }

        private static void RegisterChildrenUndo(Transform parent)
        {
            foreach (Transform child in parent)
            {
                Undo.RegisterCreatedObjectUndo(child.gameObject, "CityBuilder");
                RegisterChildrenUndo(child);
            }
        }
    }
}
