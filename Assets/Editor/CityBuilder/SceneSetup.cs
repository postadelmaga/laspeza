using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;

namespace CityBuilder
{
    /// <summary>
    /// Configurazione scena con illuminazione realistica mediterranea,
    /// atmosfera cinematografica e ottimizzazioni performance.
    /// </summary>
    public static class SceneSetup
    {
        public static void SetupScene(GameObject worldParent)
        {
            SetupDirectionalLight();
            SetupFillLight();
            SetupSkybox();
            SetupAmbientLighting();
            SetupFog();
            OptimizeForPerformance(worldParent);
            SetupExplorerCamera(worldParent);

            Debug.Log("SceneSetup: scena configurata (illuminazione realistica + ottimizzazioni).");
        }

        // ============================================================
        //  PERFORMANCE
        // ============================================================

        private static void OptimizeForPerformance(GameObject worldParent)
        {
            if (worldParent == null) return;

            SetStaticRecursive(worldParent.transform);

            Camera mainCam = Camera.main;
            if (mainCam != null)
            {
                mainCam.farClipPlane = 25000f;
                mainCam.nearClipPlane = 0.5f;
                mainCam.allowHDR = true;
                mainCam.allowMSAA = true;
            }

            // StaticBatching disabilitato: con 100k+ mesh consuma troppa RAM
            // Il flag isStatic abilita gia' GPU instancing e culling
            // StaticBatchingUtility.Combine(worldParent);

            if (worldParent.GetComponent("DistanceCuller") == null)
            {
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    var cullerType = asm.GetType("DistanceCuller");
                    if (cullerType != null)
                    {
                        worldParent.AddComponent(cullerType);
                        break;
                    }
                }
            }
        }

        private static void SetStaticRecursive(Transform t)
        {
            // Non marcare come static oggetti con animazione shader (acqua, vegetazione con vento)
            string name = t.gameObject.name.ToLowerInvariant();
            bool isDynamic = name.Contains("acqua") || name.Contains("water")
                          || name.Contains("mare") || name.Contains("sole")
                          || name.Contains("stell") || name.Contains("luna")
                          || name.Contains("nuvol") || name.Contains("cloud");
            t.gameObject.isStatic = !isDynamic;
            foreach (Transform child in t)
                SetStaticRecursive(child);
        }

        // ============================================================
        //  CAMERA EXPLORER
        // ============================================================

        private static void SetupExplorerCamera(GameObject worldParent)
        {
            Camera mainCam = Camera.main;
            if (mainCam == null)
            {
                var camGo = new GameObject("CityExplorer_Camera");
                mainCam = camGo.AddComponent<Camera>();
                camGo.AddComponent<AudioListener>();
                camGo.tag = "MainCamera";
            }

            if (mainCam.GetComponent("CityExplorer") == null)
            {
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    var explorerType = asm.GetType("CityExplorer");
                    if (explorerType != null)
                    {
                        mainCam.gameObject.AddComponent(explorerType);
                        break;
                    }
                }
            }

            // BotWAtmosphere: atmosfera completa stile BotW (sostituisce AtmosphereController + BotWStyleManager)
            AddRuntimeComponent(mainCam.gameObject, "BotWAtmosphere");

            // ToonTreePlacer: alberi cartoon sulle colline (GPU instanced)
            AddRuntimeComponent(mainCam.gameObject, "ToonTreePlacer");
        }

        // ============================================================
        //  LUCE PRINCIPALE (Sole)
        // ============================================================

        private static void SetupDirectionalLight()
        {
            Light sun = FindOrCreateLight("Sole_Mediterraneo", LightType.Directional);

            // Luce mediterranea tardo-pomeriggio: calda, dorata, angolo basso
            sun.color = new Color(1.0f, 0.92f, 0.82f);
            sun.colorTemperature = 5800f;
            sun.intensity = 1.4f;

            // Angolo: sole di pomeriggio mediterraneo (ombre lunghe ma non troppo)
            sun.transform.rotation = Quaternion.Euler(42f, -35f, 0f);

            // Ombre di qualita' con buone performance
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.75f;
            sun.shadowResolution = LightShadowResolution.High;
            sun.shadowBias = 0.02f;
            sun.shadowNormalBias = 0.3f;

            // Shadow distance (URP: questo potrebbe essere in URP asset, ma settiamo comunque)
            QualitySettings.shadowDistance = 500f;
            QualitySettings.shadowCascades = 4;
            QualitySettings.shadowResolution = ShadowResolution.High;
        }

        // ============================================================
        //  LUCE DI RIEMPIMENTO (simulazione bounce/rimbalzo)
        // ============================================================

        private static void SetupFillLight()
        {
            Light fill = FindOrCreateLight("Luce_Riempimento", LightType.Directional);

            // Luce morbida dal cielo: simula il rimbalzo della luce sull'ambiente
            fill.color = new Color(0.7f, 0.8f, 0.95f); // blu cielo tenue
            fill.intensity = 0.25f;
            fill.transform.rotation = Quaternion.Euler(75f, 160f, 0f); // quasi dall'alto, opposta al sole
            fill.shadows = LightShadows.None; // niente ombre per performance
        }

        // ============================================================
        //  SKYBOX
        // ============================================================

        private static void SetupSkybox()
        {
            Shader skyboxShader = Shader.Find("Skybox/Procedural");
            if (skyboxShader == null) return;

            Material skyMat = new Material(skyboxShader);

            // Cielo mediterraneo: azzurro profondo, sole medio, atmosfera densa
            if (skyMat.HasProperty("_SunSize")) skyMat.SetFloat("_SunSize", 0.05f);
            if (skyMat.HasProperty("_SunSizeConvergence")) skyMat.SetFloat("_SunSizeConvergence", 8f);
            if (skyMat.HasProperty("_AtmosphereThickness")) skyMat.SetFloat("_AtmosphereThickness", 1.2f);
            if (skyMat.HasProperty("_SkyTint")) skyMat.SetColor("_SkyTint", new Color(0.45f, 0.58f, 0.82f));
            if (skyMat.HasProperty("_GroundColor")) skyMat.SetColor("_GroundColor", new Color(0.42f, 0.40f, 0.36f));
            if (skyMat.HasProperty("_Exposure")) skyMat.SetFloat("_Exposure", 1.2f);

            RenderSettings.skybox = skyMat;
            DynamicGI.UpdateEnvironment();
        }

        // ============================================================
        //  AMBIENT LIGHTING
        // ============================================================

        private static void SetupAmbientLighting()
        {
            // Trilight per ambient piu' ricco (cielo, orizzonte, terra)
            RenderSettings.ambientMode = AmbientMode.Trilight;

            // Cielo: azzurro luminoso
            RenderSettings.ambientSkyColor = new Color(0.55f, 0.68f, 0.88f);
            // Orizzonte: caldo, leggermente nebbioso
            RenderSettings.ambientEquatorColor = new Color(0.75f, 0.70f, 0.62f);
            // Terra: toni caldi della macchia mediterranea
            RenderSettings.ambientGroundColor = new Color(0.35f, 0.30f, 0.25f);

            RenderSettings.ambientIntensity = 1.1f;
            RenderSettings.reflectionIntensity = 0.8f;
            RenderSettings.defaultReflectionResolution = 256;
        }

        // ============================================================
        //  FOG ATMOSFERICO
        // ============================================================

        private static void SetupFog()
        {
            RenderSettings.fog = true;

            // Foschia mediterranea: linear con distanze ragionevoli per performance
            RenderSettings.fogMode = FogMode.Linear;

            // Colore: grigio-azzurro caldo (foschia marina)
            RenderSettings.fogColor = new Color(0.68f, 0.72f, 0.78f);

            // Distanze: visibilita' buona ma con profondita' atmosferica
            RenderSettings.fogStartDistance = 2000f;
            RenderSettings.fogEndDistance = 15000f;
        }

        // ============================================================
        //  UTILITY
        // ============================================================

        private static void AddRuntimeComponent(GameObject go, string typeName)
        {
            if (go.GetComponent(typeName) != null) return;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(typeName);
                if (t != null)
                {
                    go.AddComponent(t);
                    Debug.Log($"SceneSetup: {typeName} aggiunto.");
                    return;
                }
            }
        }

        private static Light FindOrCreateLight(string name, LightType type)
        {
            // Cerca luce esistente con questo nome
            Light[] lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var l in lights)
            {
                if (l.gameObject.name == name && l.type == type)
                    return l;
            }

            // Se non c'e', cerca una directional generica (solo per la principale)
            if (name == "Sole_Mediterraneo")
            {
                foreach (var l in lights)
                {
                    if (l.type == LightType.Directional && l.gameObject.name != "Luce_Riempimento")
                    {
                        l.gameObject.name = name;
                        return l;
                    }
                }
            }

            // Crea nuova
            GameObject go = new GameObject(name);
            Light light = go.AddComponent<Light>();
            light.type = type;
            return light;
        }
    }
}
