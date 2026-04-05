using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System;
using System.Threading.Tasks;

namespace CityBuilder
{
    /// <summary>
    /// Entry point per esecuzione da CLI (batch mode).
    ///
    /// Uso:
    ///   unity -batchmode -projectPath /home/fra/laspeza -executeMethod CityBuilder.CityBuilderCLI.Generate -logFile - -quit \
    ///         -- --tif /path/to/dem.tif --grid 4 --no-crop
    ///
    ///   unity -batchmode -projectPath /home/fra/laspeza -executeMethod CityBuilder.CityBuilderCLI.Generate -logFile - -quit \
    ///         -- --download --minlon 9.75 --minlat 44.05 --maxlon 9.90 --maxlat 44.15
    ///
    /// Argomenti (dopo --):
    ///   --tif PATH          File GeoTIFF sorgente
    ///   --grid N            Griglia terreno (default: 4)
    ///   --no-crop           Disabilita auto-crop oceano
    ///   --download          Scarica DEM Copernicus invece di usare file locale
    ///   --minlon/--minlat   Bounding box per download
    ///   --maxlon/--maxlat
    ///   --step NOME         Esegui solo uno step (terrain,textures,osm,buildings,roads,squares,water,vegetation,scene)
    ///   --scene PATH        Scena da aprire (default: Assets/Scenes/SampleScene.unity)
    ///   --save              Salva la scena dopo la generazione
    /// </summary>
    public static class CityBuilderCLI
    {
        public static void Generate()
        {
            Debug.Log("=== CityBuilder CLI ===");

            var args = ParseArgs();

            // Apri scena
            string scenePath = args.GetOr("scene", "Assets/Scenes/SampleScene.unity");
            if (System.IO.File.Exists(System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(Application.dataPath), scenePath)))
            {
                EditorSceneManager.OpenScene(scenePath);
                Debug.Log($"Scena aperta: {scenePath}");
            }

            // Esegui async — Unity batch mode mantiene il processo in vita
            // finché EditorApplication.Exit() viene chiamato da RunAsync al completamento.
            // Non possiamo bloccare il thread principale (deadlock con Task.Yield).
            RunAsync(args).ContinueWith(t =>
            {
                if (t.Exception != null)
                {
                    Debug.LogError($"CLI FALLITO: {t.Exception.InnerException?.Message ?? t.Exception.Message}");
                    EditorApplication.Exit(1);
                }
            });
        }

        static async Task RunAsync(CLIArgs args)
        {
            var pipeline = new CityBuilderPipeline();
            pipeline.gridCount = args.GetInt("grid", 4);
            pipeline.useAutoCrop = !args.Has("no-crop");

            // Download DEM se richiesto
            if (args.Has("download"))
            {
                float minLon = args.GetFloat("minlon", 9.75f);
                float minLat = args.GetFloat("minlat", 44.05f);
                float maxLon = args.GetFloat("maxlon", 9.90f);
                float maxLat = args.GetFloat("maxlat", 44.15f);

                Debug.Log($"Download DEM Copernicus [{minLon},{minLat}]-[{maxLon},{maxLat}]...");

                string dataDir = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(Application.dataPath), "DATA");
                System.IO.Directory.CreateDirectory(dataDir);

                string path = await GisPythonEngine.DownloadCopernicusDemAsync(minLon, minLat, maxLon, maxLat, dataDir);
                if (path == null)
                {
                    Debug.LogError("Download DEM fallito!");
                    EditorApplication.Exit(1);
                    return;
                }
                pipeline.tifFilePath = path;
            }
            else
            {
                pipeline.tifFilePath = args.GetOr("tif", "");
                if (string.IsNullOrEmpty(pipeline.tifFilePath))
                {
                    // Cerca un .tif nella cartella DATA
                    string dataDir = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(Application.dataPath), "DATA");
                    if (System.IO.Directory.Exists(dataDir))
                    {
                        var tifs = System.IO.Directory.GetFiles(dataDir, "*.tif");
                        if (tifs.Length > 0)
                        {
                            pipeline.tifFilePath = tifs[0];
                            Debug.Log($"Auto-trovato GeoTIFF: {pipeline.tifFilePath}");
                        }
                    }

                    if (string.IsNullOrEmpty(pipeline.tifFilePath))
                    {
                        Debug.LogError("Nessun GeoTIFF specificato! Usa --tif PATH o --download");
                        EditorApplication.Exit(1);
                        return;
                    }
                }
            }

            pipeline.maxBuildings = args.GetInt("max-buildings", 30000);
            pipeline.maxRoads = args.GetInt("max-roads", 15000);

            Debug.Log($"GeoTIFF: {pipeline.tifFilePath}");
            Debug.Log($"Griglia: {pipeline.gridCount}x{pipeline.gridCount}, AutoCrop: {pipeline.useAutoCrop}");
            Debug.Log($"Limiti: max {pipeline.maxBuildings} edifici, {pipeline.maxRoads} strade");

            // Step singolo o pipeline completa?
            string step = args.GetOr("step", "");

            bool ok;
            if (!string.IsNullOrEmpty(step))
            {
                // Step singolo: ricostruisci stato dalla scena salvata
                int userGrid = pipeline.gridCount; // preserva il valore utente
                Debug.Log($"Step singolo: {step} — ripristino stato dalla scena...");
                pipeline.RestoreFromScene();
                pipeline.gridCount = userGrid; // RestoreFromScene potrebbe averlo sovrascritto

                // Per step che richiedono OSM, caricalo dalla cache
                if (step != "terrain" && step != "terreno" && step != "textures" && step != "texture" && step != "scene" && step != "scena")
                {
                    if (pipeline.hasOsm && !pipeline.hasCoordinate && pipeline.osmCachePath != null)
                    {
                        // Ricarica OSM dalla cache e converti coordinate
                        pipeline.osmData = await OverpassDownloader.DownloadAsync(
                            pipeline.tMeta.minLon, pipeline.tMeta.minLat,
                            pipeline.tMeta.maxLon, pipeline.tMeta.maxLat,
                            pipeline.osmCachePath);
                        if (pipeline.osmData != null)
                        {
                            pipeline.hasOsm = true;
                            pipeline.StepConvertCoordinates();
                        }
                    }
                }

                ok = await RunStep(pipeline, step);
            }
            else
            {
                Debug.Log("Esecuzione pipeline completa...");
                ok = await pipeline.RunFullPipeline();
            }

            if (!ok)
            {
                Debug.LogError("Pipeline fallita!");
                EditorApplication.Exit(1);
                return;
            }

            // Salva scena
            if (args.Has("save"))
            {
                string scenePath = args.GetOr("scene", "Assets/Scenes/SampleScene.unity");
                EditorSceneManager.SaveScene(
                    EditorSceneManager.GetActiveScene(), scenePath);
                Debug.Log($"Scena salvata: {scenePath}");
            }

            Debug.Log("=== CityBuilder CLI: COMPLETATO ===");

            if (Application.isBatchMode)
                EditorApplication.Exit(0);
        }

        static async Task<bool> RunStep(CityBuilderPipeline pipeline, string step)
        {
            Debug.Log($"Esecuzione step: {step}");

            switch (step.ToLowerInvariant())
            {
                case "terrain":
                case "terreno":
                    return await pipeline.StepTerrain();

                case "textures":
                case "texture":
                    return await pipeline.StepTextures();

                case "osm":
                    return await pipeline.StepDownloadOsm();

                case "buildings":
                case "edifici":
                    return await pipeline.StepBuildings();

                case "roads":
                case "strade":
                    return await pipeline.StepRoads();

                case "squares":
                case "piazze":
                    return await pipeline.StepPedestrianAreas();

                case "water":
                case "acqua":
                    return await pipeline.StepWater();

                case "vegetation":
                case "vegetazione":
                    return await pipeline.StepVegetation();

                case "scene":
                case "scena":
                    return pipeline.StepScene();

                case "demo":
                    // Pipeline leggera: solo terreno + texture + acqua + scena
                    Debug.Log("=== DEMO MODE: terreno + acqua + scena (no OSM) ===");
                    if (!await pipeline.StepTerrain()) return false;
                    FlushMem();
                    await pipeline.StepTextures();
                    FlushMem();
                    // Acqua: sea plane diretto (senza passare per WaterBuilder)
                    if (pipeline.worldParent != null)
                    {
                        float seaY = pipeline.tMeta.seaLevelNorm * pipeline.tMeta.heightM + 0.15f;
                        float w = pipeline.cropWidthM;
                        float l = pipeline.cropLengthM;
                        Debug.Log($"Demo: creazione mare a Y={seaY:F2}m, area {w:F0}x{l:F0}m");

                        var acquaGO = new UnityEngine.GameObject("Acqua");
                        acquaGO.transform.parent = pipeline.worldParent.transform;
                        pipeline.acquaParent = acquaGO;

                        // Crea sea plane mesh direttamente
                        var seaGO = new UnityEngine.GameObject("Acqua_Mare");
                        seaGO.transform.parent = acquaGO.transform;

                        var mesh = new UnityEngine.Mesh();
                        mesh.vertices = new UnityEngine.Vector3[] {
                            new UnityEngine.Vector3(-w * 0.3f, seaY, -l * 0.3f),
                            new UnityEngine.Vector3(w * 1.3f, seaY, -l * 0.3f),
                            new UnityEngine.Vector3(w * 1.3f, seaY, l * 1.3f),
                            new UnityEngine.Vector3(-w * 0.3f, seaY, l * 1.3f)
                        };
                        mesh.triangles = new int[] { 0, 2, 1, 0, 3, 2 };
                        mesh.RecalculateNormals();
                        mesh.RecalculateBounds();

                        seaGO.AddComponent<UnityEngine.MeshFilter>().sharedMesh = mesh;
                        var mr = seaGO.AddComponent<UnityEngine.MeshRenderer>();

                        // Materiale: prova ToonWater, fallback URP/Lit
                        Shader waterShader = Shader.Find("Custom/ToonWater");
                        if (waterShader != null)
                        {
                            var mat = new UnityEngine.Material(waterShader);
                            mr.sharedMaterial = mat;
                            Debug.Log("Demo: mare con ToonWater shader");
                        }
                        else
                        {
                            Shader litShader = Shader.Find("Universal Render Pipeline/Lit")
                                           ?? Shader.Find("Standard");
                            if (litShader != null)
                            {
                                var mat = new UnityEngine.Material(litShader);
                                mat.color = new UnityEngine.Color(0.05f, 0.15f, 0.30f, 0.85f);
                                mr.sharedMaterial = mat;
                            }
                            Debug.LogWarning("Demo: ToonWater non trovato, fallback URP/Lit");
                        }
                    }
                    FlushMem();
                    pipeline.StepScene();
                    return true;

                default:
                    Debug.LogError($"Step sconosciuto: {step}");
                    Debug.Log("Step validi: terrain, textures, osm, buildings, roads, squares, water, vegetation, scene");
                    return false;
            }
        }

        static void FlushMem()
        {
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();
        }

        // ================================================================
        //  ARGUMENT PARSER
        // ================================================================

        class CLIArgs
        {
            private System.Collections.Generic.Dictionary<string, string> map
                = new System.Collections.Generic.Dictionary<string, string>();

            public void Set(string key, string value) => map[key] = value;
            public bool Has(string key) => map.ContainsKey(key);
            public string GetOr(string key, string fallback) =>
                map.TryGetValue(key, out string v) ? v : fallback;
            public int GetInt(string key, int fallback) =>
                map.TryGetValue(key, out string v) && int.TryParse(v, out int i) ? i : fallback;
            public float GetFloat(string key, float fallback) =>
                map.TryGetValue(key, out string v) && float.TryParse(v,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : fallback;
        }

        static CLIArgs ParseArgs()
        {
            var args = new CLIArgs();
            string[] raw = Environment.GetCommandLineArgs();

            // Trova "--" separator
            int start = -1;
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] == "--") { start = i + 1; break; }
            }
            if (start < 0) return args;

            for (int i = start; i < raw.Length; i++)
            {
                string arg = raw[i];
                if (arg.StartsWith("--"))
                {
                    string key = arg.Substring(2);
                    // Peek at next arg: if it exists and doesn't start with --, it's the value
                    if (i + 1 < raw.Length && !raw[i + 1].StartsWith("--"))
                    {
                        args.Set(key, raw[i + 1]);
                        i++;
                    }
                    else
                    {
                        args.Set(key, "true");
                    }
                }
            }

            return args;
        }
    }
}
