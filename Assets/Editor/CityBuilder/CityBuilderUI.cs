using UnityEngine;
using UnityEditor;
using System.Threading.Tasks;

namespace CityBuilder
{
    public class CityBuilderUI : EditorWindow
    {
        [MenuItem("Tools/CityBuilder Pro")]
        public static void ShowWindow() { GetWindow<CityBuilderUI>("CityBuilder Pro"); }

        // Pipeline
        private CityBuilderPipeline pipeline = new CityBuilderPipeline();

        // Download DEM
        private bool showDownload = false;
        private float dlMinLon = 9.75f, dlMinLat = 44.05f;
        private float dlMaxLon = 9.90f, dlMaxLat = 44.15f;

        // State
        private enum State { Idle, Working, Done, Failed }
        private State currentState = State.Idle;
        private string statusMessage = "";
        private string currentStep = "";

        private Vector2 scrollPos;
        private bool showSteps = true;

        // ================================================================
        //  GUI
        // ================================================================

        void OnGUI()
        {
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

            // --- Header ---
            GUILayout.Label("CityBuilder Pro", new GUIStyle(EditorStyles.boldLabel) { fontSize = 16 });
            EditorGUILayout.Space(5);

            // --- Download DEM ---
            showDownload = EditorGUILayout.Foldout(showDownload, "0. Scarica DEM (Copernicus GLO-30)", true, EditorStyles.foldoutHeader);
            if (showDownload)
            {
                EditorGUILayout.HelpBox(
                    "Scarica DEM Copernicus da AWS (gratis, ~30m).\n" +
                    "Per 10m TINITALY o 1m LiDAR usa file locale.", MessageType.Info);

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Min Lon/Lat:", GUILayout.Width(80));
                dlMinLon = EditorGUILayout.FloatField(dlMinLon);
                dlMinLat = EditorGUILayout.FloatField(dlMinLat);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Max Lon/Lat:", GUILayout.Width(80));
                dlMaxLon = EditorGUILayout.FloatField(dlMaxLon);
                dlMaxLat = EditorGUILayout.FloatField(dlMaxLat);
                EditorGUILayout.EndHorizontal();

                GUI.enabled = currentState != State.Working;
                if (GUILayout.Button("Scarica DEM"))
                    RunDownloadDem();
                GUI.enabled = true;
            }

            EditorGUILayout.Space(10);

            // --- Input ---
            GUILayout.Label("File GeoTIFF Sorgente", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            pipeline.tifFilePath = EditorGUILayout.TextField("GeoTIFF (.tif):", pipeline.tifFilePath ?? "");
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string path = EditorUtility.OpenFilePanel("Seleziona GeoTIFF", "", "tif");
                if (!string.IsNullOrEmpty(path)) pipeline.tifFilePath = path;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);
            GUILayout.Label("Impostazioni", EditorStyles.boldLabel);
            pipeline.gridCount = EditorGUILayout.IntSlider("Griglia terreno:", pipeline.gridCount, 2, 8);
            pipeline.useAutoCrop = EditorGUILayout.ToggleLeft("Auto-crop oceano/NoData", pipeline.useAutoCrop);

            EditorGUILayout.Space(10);

            // --- Bottone pipeline completa ---
            GUI.enabled = currentState != State.Working;
            GUIStyle bigButton = new GUIStyle(GUI.skin.button) { fontSize = 14, fixedHeight = 40 };
            if (GUILayout.Button("GENERA CITTA' COMPLETA", bigButton))
                RunFullPipeline();
            GUI.enabled = true;

            EditorGUILayout.Space(10);

            // --- Step individuali ---
            showSteps = EditorGUILayout.Foldout(showSteps, "Step Individuali (rigenera singoli aspetti)", true, EditorStyles.foldoutHeader);
            if (showSteps)
            {
                DrawStepButtons();
            }

            // --- Status ---
            EditorGUILayout.Space(5);
            DrawStatus();

            // --- Stato pipeline ---
            EditorGUILayout.Space(5);
            DrawPipelineState();

            EditorGUILayout.EndScrollView();
        }

        // ================================================================
        //  STEP BUTTONS
        // ================================================================

        private void DrawStepButtons()
        {
            bool working = currentState == State.Working;
            GUI.enabled = !working;

            EditorGUILayout.BeginVertical("box");

            // Step 1: Terreno
            EditorGUILayout.BeginHorizontal();
            DrawStepIndicator(pipeline.hasTerreno);
            if (GUILayout.Button("1. Terreno", GUILayout.Height(25)))
                RunStep("Terreno", pipeline.StepTerrain);
            EditorGUILayout.EndHorizontal();

            // Step 2: Texture
            EditorGUILayout.BeginHorizontal();
            DrawStepIndicator(pipeline.hasTexture);
            GUI.enabled = !working && pipeline.hasTerreno;
            if (GUILayout.Button("2. Texture Terreno", GUILayout.Height(25)))
                RunStep("Texture", pipeline.StepTextures);
            GUI.enabled = !working;
            EditorGUILayout.EndHorizontal();

            // Step 3: OSM
            EditorGUILayout.BeginHorizontal();
            DrawStepIndicator(pipeline.hasOsm);
            GUI.enabled = !working && pipeline.hasTerreno;
            if (GUILayout.Button("3. Download OSM", GUILayout.Height(25)))
                RunStep("Download OSM", pipeline.StepDownloadOsm);
            GUI.enabled = !working;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(3);
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

            bool canBuild = pipeline.hasTerreno && pipeline.hasOsm;
            string req = canBuild ? "" : " (serve Terreno + OSM)";

            // Step 4: Edifici
            EditorGUILayout.BeginHorizontal();
            DrawStepIndicator(pipeline.hasEdifici);
            GUI.enabled = !working && canBuild;
            if (GUILayout.Button("4. Edifici" + req, GUILayout.Height(25)))
                RunStep("Edifici", pipeline.StepBuildings);
            GUI.enabled = !working;
            EditorGUILayout.EndHorizontal();

            // Step 5: Strade
            EditorGUILayout.BeginHorizontal();
            DrawStepIndicator(pipeline.hasStrade);
            GUI.enabled = !working && canBuild;
            if (GUILayout.Button("5. Strade" + req, GUILayout.Height(25)))
                RunStep("Strade", pipeline.StepRoads);
            GUI.enabled = !working;
            EditorGUILayout.EndHorizontal();

            // Step 6: Piazze
            EditorGUILayout.BeginHorizontal();
            DrawStepIndicator(pipeline.hasPiazze);
            GUI.enabled = !working && canBuild;
            if (GUILayout.Button("6. Piazze e Aree Pedonali" + req, GUILayout.Height(25)))
                RunStep("Piazze", pipeline.StepPedestrianAreas);
            GUI.enabled = !working;
            EditorGUILayout.EndHorizontal();

            // Step 7: Acqua
            EditorGUILayout.BeginHorizontal();
            DrawStepIndicator(pipeline.hasAcqua);
            GUI.enabled = !working && canBuild;
            if (GUILayout.Button("7. Acqua" + req, GUILayout.Height(25)))
                RunStep("Acqua", pipeline.StepWater);
            GUI.enabled = !working;
            EditorGUILayout.EndHorizontal();

            // Step 8: Vegetazione
            EditorGUILayout.BeginHorizontal();
            DrawStepIndicator(pipeline.hasVegetazione);
            GUI.enabled = !working && canBuild;
            if (GUILayout.Button("8. Vegetazione e Arredo" + req, GUILayout.Height(25)))
                RunStep("Vegetazione", pipeline.StepVegetation);
            GUI.enabled = !working;
            EditorGUILayout.EndHorizontal();

            // Step 9: Scena
            EditorGUILayout.BeginHorizontal();
            DrawStepIndicator(pipeline.hasScena);
            GUI.enabled = !working && pipeline.hasTerreno;
            if (GUILayout.Button("9. Setup Scena (luce, fog, camera)", GUILayout.Height(25)))
            {
                pipeline.StepScene();
                currentState = State.Done;
                statusMessage = "Scena configurata";
                Repaint();
            }
            GUI.enabled = !working;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
            GUI.enabled = true;
        }

        private void DrawStepIndicator(bool done)
        {
            GUIStyle indicator = new GUIStyle(EditorStyles.label)
            {
                fixedWidth = 20, fixedHeight = 25,
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14
            };
            GUILayout.Label(done ? "V" : "-", indicator);
        }

        // ================================================================
        //  STATUS DISPLAY
        // ================================================================

        private void DrawStatus()
        {
            switch (currentState)
            {
                case State.Idle:
                    EditorGUILayout.HelpBox("Pronto. Seleziona un GeoTIFF e genera la citta' completa, oppure esegui gli step singolarmente.", MessageType.Info);
                    break;
                case State.Working:
                    EditorGUILayout.HelpBox($"Elaborazione: {currentStep} — {statusMessage}", MessageType.Info);
                    break;
                case State.Failed:
                    EditorGUILayout.HelpBox($"Errore in {currentStep}: {statusMessage}", MessageType.Error);
                    break;
                case State.Done:
                    EditorGUILayout.HelpBox($"Completato: {statusMessage}", MessageType.Info);
                    break;
            }
        }

        private void DrawPipelineState()
        {
            EditorGUILayout.BeginVertical("box");
            GUILayout.Label("Stato Pipeline", EditorStyles.miniLabel);

            string state = "";
            if (pipeline.hasTerreno) state += $"Terreno {pipeline.gridCount}x{pipeline.gridCount} ";
            if (pipeline.hasOsm && pipeline.osmData != null)
                state += $"| OSM ({pipeline.osmData.TotalFeatures} features) ";
            if (pipeline.hasEdifici) state += "| Edifici ";
            if (pipeline.hasStrade) state += "| Strade ";
            if (pipeline.hasPiazze) state += "| Piazze ";
            if (pipeline.hasAcqua) state += "| Acqua ";
            if (pipeline.hasVegetazione) state += "| Vegetazione ";
            if (pipeline.hasScena) state += "| Scena ";

            if (string.IsNullOrEmpty(state)) state = "Nessuno step completato";

            EditorGUILayout.LabelField(state, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();
        }

        // ================================================================
        //  RUNNERS
        // ================================================================

        private async void RunStep(string stepName, System.Func<Task<bool>> stepFunc)
        {
            currentState = State.Working;
            currentStep = stepName;
            statusMessage = "In corso...";
            Repaint();

            try
            {
                bool ok = await stepFunc();
                currentState = ok ? State.Done : State.Failed;
                statusMessage = ok ? $"{stepName} completato" : $"{stepName} fallito";
            }
            catch (System.Exception e)
            {
                currentState = State.Failed;
                statusMessage = e.Message;
                Debug.LogError($"CityBuilder [{stepName}]: {e}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        private async void RunFullPipeline()
        {
            currentState = State.Working;
            currentStep = "Pipeline completa";
            statusMessage = "Generazione citta'...";
            Repaint();

            try
            {
                bool ok = await pipeline.RunFullPipeline();
                currentState = ok ? State.Done : State.Failed;
                statusMessage = ok ? "Citta' generata con successo!" : "Pipeline fallita";
            }
            catch (System.Exception e)
            {
                currentState = State.Failed;
                statusMessage = e.Message;
                Debug.LogError($"CityBuilder: {e}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        private async void RunDownloadDem()
        {
            if (dlMinLon >= dlMaxLon || dlMinLat >= dlMaxLat)
            {
                EditorUtility.DisplayDialog("CityBuilder", "Bounding box non valido!", "OK");
                return;
            }

            string dataDir = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Application.dataPath), "DATA");
            System.IO.Directory.CreateDirectory(dataDir);

            currentState = State.Working;
            currentStep = "Download DEM";
            statusMessage = "Scaricamento...";
            Repaint();

            try
            {
                string path = await GisPythonEngine.DownloadCopernicusDemAsync(
                    dlMinLon, dlMinLat, dlMaxLon, dlMaxLat, dataDir);
                if (path != null) pipeline.tifFilePath = path;
                currentState = State.Done;
                statusMessage = "DEM scaricato";
            }
            catch (System.Exception e)
            {
                currentState = State.Failed;
                statusMessage = e.Message;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }
    }
}
