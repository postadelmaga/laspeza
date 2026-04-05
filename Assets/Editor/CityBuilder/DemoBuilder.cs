using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using System.IO;

namespace CityBuilder
{
    /// <summary>
    /// Prepara e compila il demo BotW.
    ///
    /// Menu: CityBuilder > Prepara Demo
    ///       CityBuilder > Build Demo (Linux)
    ///
    /// CLI:
    ///   unity -batchmode -projectPath /home/fra/laspeza \
    ///         -executeMethod CityBuilder.DemoBuilder.BuildLinux -quit
    /// </summary>
    public static class DemoBuilder
    {
        [MenuItem("CityBuilder/1. Prepara Demo (configura scena)")]
        public static void PrepareDemoScene()
        {
            // Apri la scena
            string scenePath = "Assets/Scenes/SampleScene.unity";
            EditorSceneManager.OpenScene(scenePath);

            // Ottimizza terrain per build size
            OptimizeTerrainsForBuild();

            // Usa la camera esistente (creata da SceneSetup), o creane una
            Camera mainCam = Camera.main;
            if (mainCam == null)
            {
                // Cerca qualsiasi camera nella scena prima di crearne una nuova
                mainCam = Object.FindAnyObjectByType<Camera>();
                if (mainCam != null)
                {
                    mainCam.tag = "MainCamera";
                }
                else
                {
                    var camGo = new GameObject("DemoCamera");
                    mainCam = camGo.AddComponent<Camera>();
                    camGo.AddComponent<AudioListener>();
                    camGo.tag = "MainCamera";
                }
            }

            // Rimuovi vecchi controller se presenti
            RemoveComponent(mainCam.gameObject, "CityExplorer");
            RemoveComponent(mainCam.gameObject, "AtmosphereController");
            RemoveComponent(mainCam.gameObject, "BotWStyleManager");

            // Rimuovi duplicati di BotWAtmosphere/ToonTreePlacer da altri GameObject
            foreach (var obj in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (obj.gameObject == mainCam.gameObject) continue;
                string tn = obj.GetType().Name;
                if (tn == "BotWAtmosphere" || tn == "ToonTreePlacer" || tn == "DemoManager")
                {
                    Debug.Log($"Demo: rimosso {tn} duplicato da {obj.gameObject.name}");
                    Undo.DestroyObjectImmediate(obj);
                }
            }

            // Aggiungi i componenti demo
            AddComponentIfMissing(mainCam.gameObject, "DemoManager");
            AddComponentIfMissing(mainCam.gameObject, "BotWAtmosphere");
            AddComponentIfMissing(mainCam.gameObject, "ToonTreePlacer");

            // Configura camera
            mainCam.farClipPlane = 25000f;
            mainCam.nearClipPlane = 0.5f;
            mainCam.allowHDR = true;
            mainCam.allowMSAA = false;

            // Salva scena
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log("Demo: scena preparata! Ora usa 'CityBuilder > Build Demo' o premi Play.");
        }

        [MenuItem("CityBuilder/2. Build Demo (Linux x64)")]
        public static void BuildLinux()
        {
            PrepareDemoScene();
            Build(BuildTarget.StandaloneLinux64, "Build/LaSpeziaDemo_Linux/LaSpeziaDemo");
        }

        [MenuItem("CityBuilder/3. Build Demo (Windows x64)")]
        public static void BuildWindows()
        {
            PrepareDemoScene();
            Build(BuildTarget.StandaloneWindows64, "Build/LaSpeziaDemo_Win/LaSpeziaDemo.exe");
        }

        [MenuItem("CityBuilder/4. Build Demo (macOS)")]
        public static void BuildMac()
        {
            PrepareDemoScene();
            Build(BuildTarget.StandaloneOSX, "Build/LaSpeziaDemo_Mac/LaSpeziaDemo.app");
        }

        public static void Build(BuildTarget target, string path)
        {
            string fullPath = Path.Combine(
                Path.GetDirectoryName(Application.dataPath), path);
            string dir = Path.GetDirectoryName(fullPath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/SampleScene.unity" },
                locationPathName = fullPath,
                target = target,
                options = BuildOptions.CompressWithLz4HC  // compressione LZ4 High Compression
            };

            var report = BuildPipeline.BuildPlayer(opts);

            if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                long sizeMB = (long)report.summary.totalSize / (1024 * 1024);
                Debug.Log($"BUILD RIUSCITA: {fullPath} ({sizeMB}MB)");
                Debug.Log($"Per eseguire: ./{path}");
            }
            else
            {
                Debug.LogError($"BUILD FALLITA: {report.summary.result}");
            }
        }

        // CLI entry points
        public static void BuildLinuxCLI() => BuildLinux();
        public static void BuildWindowsCLI() => BuildWindows();
        public static void BuildMacCLI() => BuildMac();

        // ================================================================
        //  OTTIMIZZAZIONE TERRAIN PER BUILD
        // ================================================================

        /// <summary>
        /// Riduce la dimensione dei TerrainData nella scena per contenere
        /// la dimensione della build. Il grosso dei 9.5GB è causato da:
        /// - basemap cache (texture composita a bassa res) a risoluzione troppo alta
        /// - alphamap/splatmap a risoluzione troppo alta
        /// - detail resolution inutile (non usiamo detail meshes/grass)
        /// </summary>
        static void OptimizeTerrainsForBuild()
        {
            var world = GameObject.Find("CityBuilder_World");
            if (world == null) return;

            Terrain[] terrains = world.GetComponentsInChildren<Terrain>();
            int optimized = 0;

            foreach (var terrain in terrains)
            {
                TerrainData td = terrain.terrainData;
                if (td == null) continue;

                bool changed = false;

                // Riduci alphamap (splatmap) — 256 è sufficiente per texture procedurali
                if (td.alphamapResolution > 256)
                {
                    td.alphamapResolution = 256;
                    changed = true;
                }

                // Riduci basemap — è la texture composita vista da lontano
                if (td.baseMapResolution > 128)
                {
                    td.baseMapResolution = 128;
                    changed = true;
                }

                // Detail resolution: non usiamo grass/detail, minimizza
                if (td.detailResolution > 32)
                {
                    td.SetDetailResolution(32, 8);
                    changed = true;
                }

                // Disabilita draw instanced se non serve
                terrain.drawInstanced = true;

                // Pixel error più alto = meno triangoli a distanza
                terrain.heightmapPixelError = 8f;

                // Basemap distance: distanza oltre la quale usa la basemap
                terrain.basemapDistance = 2000f;

                if (changed) optimized++;
            }

            // Ridimensiona heightmap se troppo grande
            foreach (var terrain in terrains)
            {
                TerrainData td = terrain.terrainData;
                if (td == null) continue;

                // 1025 per buon dettaglio territorio e fondale
                if (td.heightmapResolution > 1025)
                {
                    int oldRes = td.heightmapResolution;
                    float[,] oldHeights = td.GetHeights(0, 0, oldRes, oldRes);

                    td.heightmapResolution = 1025;
                    int newRes = 1025;

                    // Ricampiona con interpolazione bilineare
                    float[,] newHeights = new float[newRes, newRes];
                    for (int y = 0; y < newRes; y++)
                    {
                        for (int x = 0; x < newRes; x++)
                        {
                            float u = (float)x / (newRes - 1) * (oldRes - 1);
                            float v = (float)y / (newRes - 1) * (oldRes - 1);
                            int x0 = Mathf.Min((int)u, oldRes - 2);
                            int y0 = Mathf.Min((int)v, oldRes - 2);
                            float fx = u - x0;
                            float fy = v - y0;
                            newHeights[y, x] =
                                oldHeights[y0, x0] * (1 - fx) * (1 - fy) +
                                oldHeights[y0, x0 + 1] * fx * (1 - fy) +
                                oldHeights[y0 + 1, x0] * (1 - fx) * fy +
                                oldHeights[y0 + 1, x0 + 1] * fx * fy;
                        }
                    }
                    td.SetHeights(0, 0, newHeights);
                    Debug.Log($"Demo: terrain heightmap ridotto {oldRes} -> {newRes}");
                    optimized++;
                }
            }

            if (optimized > 0)
                Debug.Log($"Demo: ottimizzati {optimized} terrain per build (heightmap=513, alphamap=256, basemap=128, detail=32)");
        }

        // Utility
        static void AddComponentIfMissing(GameObject go, string typeName)
        {
            if (go.GetComponent(typeName) != null) return;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = asm.GetType(typeName);
                if (type != null)
                {
                    go.AddComponent(type);
                    Debug.Log($"Demo: aggiunto {typeName}");
                    return;
                }
            }
            Debug.LogWarning($"Demo: tipo {typeName} non trovato!");
        }

        static void RemoveComponent(GameObject go, string typeName)
        {
            var comp = go.GetComponent(typeName);
            if (comp != null)
            {
                Undo.DestroyObjectImmediate(comp);
                Debug.Log($"Demo: rimosso {typeName}");
            }
        }
    }
}
