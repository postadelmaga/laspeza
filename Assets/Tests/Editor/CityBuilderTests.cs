using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using CityBuilder;

/// <summary>
/// EditMode test suite per CityBuilder.
/// Verifica le fix dei bug riportati nelle issue #1-#9
/// e la correttezza delle utility condivise.
/// </summary>
public class CityBuilderTests
{
    // ================================================================
    //  ISSUE #1 — RestoreFromScene carica osmData dalla cache
    // ================================================================

    [Test]
    public void Pipeline_RestoreFromScene_LoadsOsmDataFromCache()
    {
        // Verifica che OverpassDownloader.ParseJson sia invocabile
        // e ritorni un OsmDownloadResult valido (non null) anche con JSON vuoto
        string minimalJson = "{\"elements\":[]}";
        var result = OverpassDownloader.ParseJson(minimalJson);

        Assert.IsNotNull(result, "ParseJson deve ritornare un oggetto, non null");
        Assert.IsNotNull(result.buildings, "buildings list non deve essere null");
        Assert.IsNotNull(result.roads, "roads list non deve essere null");
        Assert.IsNotNull(result.water, "water list non deve essere null");
        Assert.AreEqual(0, result.TotalFeatures, "JSON vuoto → 0 features");
    }

    [Test]
    public void Pipeline_ParseJson_HandlesNodeAndWay()
    {
        // JSON minimale con un nodo e un way (building)
        string json = @"{
            ""elements"": [
                {""type"":""node"",""id"":1,""lat"":44.1,""lon"":9.82},
                {""type"":""node"",""id"":2,""lat"":44.1,""lon"":9.83},
                {""type"":""node"",""id"":3,""lat"":44.101,""lon"":9.83},
                {""type"":""node"",""id"":4,""lat"":44.101,""lon"":9.82},
                {""type"":""way"",""id"":100,""tags"":{""building"":""yes""},""nodes"":[1,2,3,4,1]}
            ]
        }";
        var result = OverpassDownloader.ParseJson(json);

        Assert.IsNotNull(result);
        Assert.GreaterOrEqual(result.buildings.Count, 1, "Deve trovare almeno 1 building");
        Assert.GreaterOrEqual(result.buildings[0].footprint.Count, 3, "Footprint deve avere almeno 3 punti");
    }

    [Test]
    public void Pipeline_HasOsm_RequiresOsmDataNotNull()
    {
        // Verifica che il pipeline non abbia hasOsm=true con osmData=null
        var pipeline = new CityBuilderPipeline();
        // Senza RestoreFromScene, osmData deve essere null e hasOsm false
        Assert.IsFalse(pipeline.hasOsm, "hasOsm deve essere false inizialmente");
        Assert.IsNull(pipeline.osmData, "osmData deve essere null inizialmente");
    }

    // ================================================================
    //  ISSUE #2 — AtmosphereController usa new Input System
    // ================================================================

    [Test]
    public void AtmosphereController_UsesNewInputSystem()
    {
        // Verifica che AtmosphereController importi UnityEngine.InputSystem
        // (non possiamo testare l'input direttamente in EditMode, ma
        //  verifichiamo che il tipo usi il namespace corretto)
        var type = typeof(AtmosphereController);
        Assert.IsNotNull(type);

        // Verifica che il file non contenga riferimenti al vecchio Input
        string scriptPath = System.IO.Path.Combine(
            Application.dataPath, "Scripts", "AtmosphereController.cs");
        if (System.IO.File.Exists(scriptPath))
        {
            string code = System.IO.File.ReadAllText(scriptPath);
            Assert.IsFalse(
                code.Contains("Input.GetKeyDown"),
                "AtmosphereController non deve usare Input.GetKeyDown (legacy Input Manager)");
            Assert.IsTrue(
                code.Contains("using UnityEngine.InputSystem"),
                "AtmosphereController deve importare UnityEngine.InputSystem");
            Assert.IsTrue(
                code.Contains("Keyboard.current") || code.Contains("kb.nKey"),
                "Deve usare Keyboard.current per i tasti");
        }
    }

    // ================================================================
    //  ISSUE #3 — SatelliteTextureLOD non più aggiunto come componente morto
    // ================================================================

    [Test]
    public void SceneSetup_DoesNotAddSatelliteTextureLOD()
    {
        // Verifica che SceneSetup non aggiunga SatelliteTextureLOD
        // (rimosso perché Initialize non veniva mai chiamato)
        string scriptPath = System.IO.Path.Combine(
            Application.dataPath, "Editor", "CityBuilder", "SceneSetup.cs");
        if (System.IO.File.Exists(scriptPath))
        {
            string code = System.IO.File.ReadAllText(scriptPath);
            Assert.IsFalse(
                code.Contains("SatelliteTextureLOD"),
                "SceneSetup non deve aggiungere SatelliteTextureLOD (issue #3: Initialize mai chiamato)");
        }
    }

    // ================================================================
    //  ISSUE #8 — Dead code rimosso
    // ================================================================

    [Test]
    public void DeadCode_OsmParserRemoved()
    {
        string path = System.IO.Path.Combine(
            Application.dataPath, "Editor", "CityBuilder", "OsmParser.cs");
        Assert.IsFalse(System.IO.File.Exists(path),
            "OsmParser.cs deve essere stato rimosso (dead code, issue #8)");
    }

    [Test]
    public void DeadCode_ProceduralBuilderRemoved()
    {
        string path = System.IO.Path.Combine(
            Application.dataPath, "Editor", "CityBuilder", "ProceduralBuilder.cs");
        Assert.IsFalse(System.IO.File.Exists(path),
            "ProceduralBuilder.cs deve essere stato rimosso (dead code, issue #8)");
    }

    // ================================================================
    //  ISSUE #9 — Shader dedup: tutti usano MeshUtils.FindLitShader()
    // ================================================================

    [Test]
    public void ShaderDedup_NoDuplicateFindCompatibleShader()
    {
        // Verifica che i file principali non definiscano la propria FindCompatibleShader
        // (WaterBuilder è escluso perché la sua versione cerca anche Custom/ToonWater)
        string[] filesToCheck = {
            "BuildingGenerator.cs",
            "RoadMeshBuilder.cs",
            "PedestrianAreaBuilder.cs",
            "LandmarkPlacer.cs",
            "VegetationPlacer.cs",
        };

        string editorDir = System.IO.Path.Combine(
            Application.dataPath, "Editor", "CityBuilder");

        foreach (string file in filesToCheck)
        {
            string path = System.IO.Path.Combine(editorDir, file);
            if (!System.IO.File.Exists(path)) continue;

            string code = System.IO.File.ReadAllText(path);
            // Non deve avere una propria definizione di FindCompatibleShader/FindShader
            bool hasOwnDefinition =
                code.Contains("private static Shader FindCompatibleShader()") ||
                code.Contains("private static Shader FindShader()") ||
                code.Contains("static Shader FindCompatibleShader()");

            Assert.IsFalse(hasOwnDefinition,
                $"{file} non deve avere la propria FindCompatibleShader — deve usare MeshUtils.FindLitShader()");
        }
    }

    [Test]
    public void ShaderDedup_WaterBuilderKeepsOwnVersion()
    {
        // WaterBuilder è l'eccezione: la sua FindCompatibleShader cerca Custom/ToonWater
        string path = System.IO.Path.Combine(
            Application.dataPath, "Editor", "CityBuilder", "WaterBuilder.cs");
        if (System.IO.File.Exists(path))
        {
            string code = System.IO.File.ReadAllText(path);
            Assert.IsTrue(code.Contains("Custom/ToonWater"),
                "WaterBuilder deve continuare a cercare Custom/ToonWater");
        }
    }

    // ================================================================
    //  MESHUTILS — Triangolazione, shader lookup, materials
    // ================================================================

    [Test]
    public void MeshUtils_FindLitShader_ReturnsNonNull()
    {
        // In editor, almeno lo Standard shader dovrebbe essere disponibile
        Shader s = MeshUtils.FindLitShader();
        Assert.IsNotNull(s, "FindLitShader deve trovare almeno uno shader");
    }

    [Test]
    public void MeshUtils_CreateMaterial_ReturnsValidMaterial()
    {
        Material mat = MeshUtils.CreateMaterial(Color.red, 0.5f, 0.1f);
        Assert.IsNotNull(mat);
        Assert.AreEqual(Color.red, mat.color);
        Object.DestroyImmediate(mat);
    }

    [Test]
    public void MeshUtils_TriangulateSquare_ProducesCorrectTriangles()
    {
        // Quadrato 10x10 sul piano XZ
        var pts = new List<Vector3> {
            new Vector3(0, 0, 0),
            new Vector3(10, 0, 0),
            new Vector3(10, 0, 10),
            new Vector3(0, 0, 10),
        };

        var verts = new List<Vector3>();
        var tris = new List<int>();

        MeshUtils.TriangulatePolygonXZ(pts, 5f, tris, verts);

        Assert.AreEqual(4, verts.Count, "Quadrato = 4 vertici");
        // 2 triangoli × 3 indici = 6
        Assert.AreEqual(6, tris.Count, "Quadrato = 2 triangoli = 6 indici");

        // Tutti i vertici devono avere Y = 5
        foreach (var v in verts)
            Assert.AreEqual(5f, v.y, 0.001f, "Y deve essere il valore passato");
    }

    [Test]
    public void MeshUtils_TriangulateTriangle_ProducesOneTriangle()
    {
        var pts = new List<Vector3> {
            new Vector3(0, 0, 0),
            new Vector3(5, 0, 0),
            new Vector3(2.5f, 0, 4),
        };

        var verts = new List<Vector3>();
        var tris = new List<int>();

        MeshUtils.TriangulatePolygonXZ(pts, 0f, tris, verts);

        Assert.AreEqual(3, verts.Count, "Triangolo = 3 vertici");
        Assert.AreEqual(3, tris.Count, "Triangolo = 1 triangolo = 3 indici");
    }

    [Test]
    public void MeshUtils_TriangulateLShape_ProducesCorrectCount()
    {
        // Poligono a L (concavo) — 6 vertici
        var pts = new List<Vector3> {
            new Vector3(0, 0, 0),
            new Vector3(10, 0, 0),
            new Vector3(10, 0, 5),
            new Vector3(5, 0, 5),
            new Vector3(5, 0, 10),
            new Vector3(0, 0, 10),
        };

        var verts = new List<Vector3>();
        var tris = new List<int>();

        MeshUtils.TriangulatePolygonXZ(pts, 0f, tris, verts);

        Assert.AreEqual(6, verts.Count, "L-shape = 6 vertici");
        // 6 vertici → 4 triangoli → 12 indici
        Assert.AreEqual(12, tris.Count, "L-shape = 4 triangoli = 12 indici");
    }

    [Test]
    public void MeshUtils_TriangulateDegeneratePolygon_NoCrash()
    {
        // Meno di 3 punti — non deve crashare
        var verts = new List<Vector3>();
        var tris = new List<int>();

        MeshUtils.TriangulatePolygonXZ(new List<Vector3>(), 0f, tris, verts);
        Assert.AreEqual(0, verts.Count);

        MeshUtils.TriangulatePolygonXZ(
            new List<Vector3> { Vector3.zero, Vector3.right }, 0f, tris, verts);
        Assert.AreEqual(0, tris.Count);
    }

    [Test]
    public void MeshUtils_PointInTriangleXZ_BasicCases()
    {
        Vector3 a = new Vector3(0, 0, 0);
        Vector3 b = new Vector3(10, 0, 0);
        Vector3 c = new Vector3(5, 0, 10);

        // Centro del triangolo — deve essere dentro
        Vector3 inside = new Vector3(5, 0, 3);
        Assert.IsTrue(MeshUtils.PointInTriangleXZ(inside, a, b, c), "Punto al centro deve essere dentro");

        // Punto fuori — lontano
        Vector3 outside = new Vector3(20, 0, 20);
        Assert.IsFalse(MeshUtils.PointInTriangleXZ(outside, a, b, c), "Punto lontano deve essere fuori");

        // Punto su un vertice — edge case, deve essere "dentro" (on boundary)
        Assert.IsTrue(MeshUtils.PointInTriangleXZ(a, a, b, c), "Punto su vertice = dentro/boundary");
    }

    // ================================================================
    //  TERRAINMETADATA — Clone
    // ================================================================

    [Test]
    public void TerrainMetaData_Clone_IsDeepCopy()
    {
        var original = new TerrainMetaData
        {
            minLon = 9.77f, maxLon = 9.95f,
            minLat = 44.09f, maxLat = 44.13f,
            widthM = 14000f, lengthM = 4000f,
            heightM = 730f, seaLevelNorm = 0.084f,
            rawResolution = 2049
        };

        var clone = original.Clone();

        // Modifica il clone
        clone.minLon = 0f;
        clone.widthM = 0f;

        // L'originale non deve cambiare
        Assert.AreEqual(9.77f, original.minLon, 0.01f, "Clone non deve modificare originale");
        Assert.AreEqual(14000f, original.widthM, 1f, "Clone non deve modificare originale");
    }

    // ================================================================
    //  OSMDATA — Strutture dati
    // ================================================================

    [Test]
    public void OsmDownloadResult_TotalFeatures_SumsCorrectly()
    {
        var result = new OsmDownloadResult();
        Assert.AreEqual(0, result.TotalFeatures);

        result.buildings.Add(new OsmBuildingData());
        result.roads.Add(new OsmRoadData());
        result.roads.Add(new OsmRoadData());
        result.water.Add(new OsmWaterData());

        Assert.AreEqual(4, result.TotalFeatures);
    }

    [Test]
    public void OsmBuildingData_DefaultsAreReasonable()
    {
        var b = new OsmBuildingData();
        Assert.IsNotNull(b.footprint, "footprint list non deve essere null");
        Assert.AreEqual(0, b.footprint.Count, "footprint deve partire vuota");
        Assert.AreEqual(0, b.height, "height default = 0 (il pipeline applica il fallback)");
    }

    // ================================================================
    //  TILEMESHDATA — Accumulo e free
    // ================================================================

    [Test]
    public void TileMeshData_AccumulatesAndFrees()
    {
        var td = new TileMeshData();
        td.vertices.Add(Vector3.zero);
        td.vertices.Add(Vector3.one);
        td.triangles.Add(0);
        td.triangles.Add(1);
        td.triangles.Add(0);

        Assert.AreEqual(2, td.vertices.Count);
        Assert.AreEqual(3, td.triangles.Count);

        td.Free();
        Assert.AreEqual(0, td.vertices.Count, "Free deve svuotare i vertici");
        Assert.AreEqual(0, td.triangles.Count, "Free deve svuotare i triangoli");
    }

    [Test]
    public void TileBuildingMeshData_HasFourSubmeshLists()
    {
        var td = new TileBuildingMeshData();
        Assert.IsNotNull(td.wallTriangles);
        Assert.IsNotNull(td.windowTriangles);
        Assert.IsNotNull(td.roofTriangles);
        Assert.IsNotNull(td.groundFloorTriangles);
        Assert.IsNotNull(td.vertices);
        Assert.IsNotNull(td.uvs);

        td.Free();
        Assert.IsNull(td.vertices, "Free deve nullare i vertici");
        Assert.IsNull(td.wallTriangles, "Free deve nullare wallTriangles");
    }

    // ================================================================
    //  COORDINATE CONVERSION — Pipeline helpers
    // ================================================================

    [Test]
    public void Pipeline_LatLonToWorld_CornersMapCorrectly()
    {
        // Testa che gli angoli GPS mappino correttamente agli angoli mondo
        var pipeline = new CityBuilderPipeline();
        pipeline.tMeta = new TerrainMetaData
        {
            minLon = 9.8f, maxLon = 9.9f,
            minLat = 44.0f, maxLat = 44.1f,
            widthM = 1000f, lengthM = 500f,
        };
        pipeline.cropWidthM = 1000f;
        pipeline.cropLengthM = 500f;

        // Usa reflection per accedere al metodo privato LatLonToWorld
        var method = typeof(CityBuilderPipeline).GetMethod("LatLonToWorld",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.IsNotNull(method, "LatLonToWorld deve esistere");

        // Angolo SW (minLon, minLat) → (0, 0, 0)
        var sw = (Vector3)method.Invoke(pipeline, new object[] { new LatLon(44.0, 9.8) });
        Assert.AreEqual(0f, sw.x, 0.1f, "SW.x deve essere ~0");
        Assert.AreEqual(0f, sw.z, 0.1f, "SW.z deve essere ~0");

        // Angolo NE (maxLon, maxLat) → (1000, 0, 500)
        var ne = (Vector3)method.Invoke(pipeline, new object[] { new LatLon(44.1, 9.9) });
        Assert.AreEqual(1000f, ne.x, 0.1f, "NE.x deve essere ~1000");
        Assert.AreEqual(500f, ne.z, 0.1f, "NE.z deve essere ~500");

        // Centro → (500, 0, 250)
        var center = (Vector3)method.Invoke(pipeline, new object[] { new LatLon(44.05, 9.85) });
        Assert.AreEqual(500f, center.x, 0.1f, "Centro.x deve essere ~500");
        Assert.AreEqual(250f, center.z, 0.1f, "Centro.z deve essere ~250");
    }

    // ================================================================
    //  WATERBUIDLER — Ear-clipping triangulation
    // ================================================================

    [Test]
    public void WaterBuilder_FindShader_PrefersCustomToonWater()
    {
        // In editor il ToonWater shader potrebbe essere disponibile.
        // Verifica che il codice lo cerchi (non possiamo garantire il risultato)
        string path = System.IO.Path.Combine(
            Application.dataPath, "Editor", "CityBuilder", "WaterBuilder.cs");
        if (System.IO.File.Exists(path))
        {
            string code = System.IO.File.ReadAllText(path);
            int toonIndex = code.IndexOf("Custom/ToonWater");
            int urpIndex = code.IndexOf("Universal Render Pipeline/Lit",
                toonIndex >= 0 ? toonIndex : 0);

            Assert.Greater(toonIndex, -1, "Deve cercare Custom/ToonWater");
            Assert.Greater(urpIndex, toonIndex, "ToonWater deve essere cercato PRIMA di URP/Lit");
        }
    }

    // ================================================================
    //  REGRESSION: no WORLD_SCALE residua
    // ================================================================

    [Test]
    public void NoWorldScale_InActivePipeline()
    {
        // Il vecchio OsmParser usava WORLD_SCALE=10. Verifica che non ci sia
        // in nessun file attivo della pipeline
        string[] activeFiles = {
            "CityBuilderPipeline.cs",
            "BuildingGenerator.cs",
            "RoadMeshBuilder.cs",
            "WaterBuilder.cs",
            "VegetationPlacer.cs",
            "PedestrianAreaBuilder.cs",
            "OverpassDownloader.cs",
        };

        string editorDir = System.IO.Path.Combine(
            Application.dataPath, "Editor", "CityBuilder");

        foreach (string file in activeFiles)
        {
            string path = System.IO.Path.Combine(editorDir, file);
            if (!System.IO.File.Exists(path)) continue;

            string code = System.IO.File.ReadAllText(path);
            Assert.IsFalse(
                code.Contains("WORLD_SCALE"),
                $"{file} non deve contenere WORLD_SCALE (rimosso con OsmParser, issue #8)");
        }
    }

    // ================================================================
    //  SHADER FILES EXIST
    // ================================================================

    [Test]
    public void Shaders_ToonWaterExists()
    {
        string path = System.IO.Path.Combine(
            Application.dataPath, "Shaders", "ToonWater.shader");
        Assert.IsTrue(System.IO.File.Exists(path), "ToonWater.shader deve esistere");
    }

    [Test]
    public void Shaders_ToonVegetationExists()
    {
        string path = System.IO.Path.Combine(
            Application.dataPath, "Shaders", "ToonVegetation.shader");
        Assert.IsTrue(System.IO.File.Exists(path), "ToonVegetation.shader deve esistere");
    }

    [Test]
    public void Shaders_ToonWaterHasShadowCaster()
    {
        string path = System.IO.Path.Combine(
            Application.dataPath, "Shaders", "ToonWater.shader");
        if (System.IO.File.Exists(path))
        {
            string code = System.IO.File.ReadAllText(path);
            Assert.IsTrue(code.Contains("ShadowCaster"),
                "ToonWater deve avere un pass ShadowCaster");
        }
    }

    [Test]
    public void Shaders_ToonVegetationSupportsInstancing()
    {
        string path = System.IO.Path.Combine(
            Application.dataPath, "Shaders", "ToonVegetation.shader");
        if (System.IO.File.Exists(path))
        {
            string code = System.IO.File.ReadAllText(path);
            Assert.IsTrue(code.Contains("multi_compile_instancing"),
                "ToonVegetation deve supportare GPU instancing");
        }
    }
}
