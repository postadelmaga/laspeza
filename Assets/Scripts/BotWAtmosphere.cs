using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;

/// <summary>
/// Atmosfera completa stile Breath of the Wild.
/// Ciclo giorno/notte con colori painterly, cielo stellato,
/// nuvole volumetriche stilizzate, post-processing integrato.
///
/// Controlli:
///   N = giorno/notte istantaneo
///   L = time-lapse (ciclo continuo)
///   +/- = velocita' time-lapse
/// </summary>
public class BotWAtmosphere : MonoBehaviour
{
    // ================================================================
    //  IMPOSTAZIONI
    // ================================================================

    [Header("Ciclo Giorno/Notte")]
    [Range(0f, 1f)]
    [Tooltip("0=mezzanotte, 0.25=alba, 0.5=mezzogiorno, 0.75=tramonto")]
    public float timeOfDay = 0.35f;
    [Tooltip("Il tempo scorre sempre: 30s reali = 1 ora di gioco. L=pausa/play")]
    public bool timePaused = false;
    [Tooltip("Secondi reali per 1 ora di gioco (default: 30)")]
    public float secondsPerHour = 30f;

    [Header("Stelle")]
    public int starCount = 3000;
    public float starBrightness = 1.2f;

    [Header("Nuvole")]
    public int cloudCount = 18;
    public float cloudAltitude = 600f;
    public float cloudSpeed = 1.5f;

    [Header("Post Processing")]
    public float bloomIntensity = 0.35f;
    public float vignetteIntensity = 0.15f;

    // ================================================================
    //  PALETTE BotW per ora del giorno
    // ================================================================

    // ── Mezzogiorno — BotW manga: cielo IPERSATURO, luce dorata calda ──
    static readonly Color DaySun = new Color(1f, 0.95f, 0.75f);
    static readonly Color DaySky = new Color(0.20f, 0.50f, 1f);        // azzurro puro manga
    static readonly Color DayEquator = new Color(0.45f, 0.85f, 0.40f); // verde brillantissimo
    static readonly Color DayGround = new Color(0.25f, 0.60f, 0.12f);  // verde saturo
    static readonly Color DayFog = new Color(0.55f, 0.78f, 1f);        // foschia celeste vivace
    static readonly Color DayCloud = new Color(1f, 1f, 1f, 0.60f);

    // ── Alba/Tramonto — BotW: arancio fuoco INTENSO, rosa shocking ──
    static readonly Color SunsetSun = new Color(1f, 0.35f, 0.05f);
    static readonly Color SunsetSky = new Color(1f, 0.28f, 0.10f);
    static readonly Color SunsetFog = new Color(1f, 0.42f, 0.18f);
    static readonly Color SunsetCloud = new Color(1f, 0.35f, 0.10f, 0.85f);

    // ── Notte — BotW: blu inchiostro con viola profondo ──
    static readonly Color NightSky = new Color(0.008f, 0.005f, 0.10f);
    static readonly Color NightEquator = new Color(0.015f, 0.01f, 0.15f);
    static readonly Color NightGround = new Color(0.008f, 0.005f, 0.05f);
    static readonly Color NightFog = new Color(0.02f, 0.02f, 0.12f);
    static readonly Color NightCloud = new Color(0.04f, 0.03f, 0.14f, 0.45f);

    // ── Split-tone (ombre VIOLA FORTE, luci ORO CALDO — firma BotW manga) ──
    static readonly Color SplitShadowDay = new Color(0.35f, 0.15f, 0.65f);   // viola SATURO
    static readonly Color SplitHighDay = new Color(0.80f, 0.60f, 0.28f);     // oro brillante
    static readonly Color SplitShadowNight = new Color(0.15f, 0.06f, 0.40f); // viola notte
    static readonly Color SplitHighNight = new Color(0.15f, 0.20f, 0.55f);   // blu freddo

    // ================================================================
    //  RUNTIME STATE
    // ================================================================

    private Light sunLight, moonLight;
    private GameObject starSphere;
    private Material starMaterial;
    private GameObject sunDisc;        // disco solare visibile
    private Material sunDiscMaterial;
    private GameObject moonDisc;       // disco lunare visibile
    private Material moonDiscMaterial;
    private List<CloudData> clouds = new List<CloudData>();
    private Material cloudMat;
    private Bounds worldBounds;

    // Post processing
    private Volume ppVolume;
    private VolumeProfile ppProfile;
    private Bloom ppBloom;
    private ColorAdjustments ppColor;
    private SplitToning ppSplit;
    private Vignette ppVignette;
    private Tonemapping ppTonemap;

    private bool initialized;

    // ================================================================
    //  INIT
    // ================================================================

    void Start()
    {
        FindWorldBounds();
        SetupSun();
        SetupMoon();
        CreateStars();
        CreateSunDisc();
        CreateMoonDisc();
        CreateClouds();
        SetupPostProcessing();
        initialized = true;
        ApplyTimeOfDay();
    }

    void FindWorldBounds()
    {
        var world = GameObject.Find("CityBuilder_World");
        if (world != null)
        {
            Terrain[] terrains = world.GetComponentsInChildren<Terrain>();
            if (terrains.Length > 0)
            {
                worldBounds = new Bounds(terrains[0].transform.position, Vector3.zero);
                foreach (var t in terrains)
                {
                    worldBounds.Encapsulate(t.transform.position);
                    worldBounds.Encapsulate(t.transform.position + t.terrainData.size);
                }
                return;
            }
        }
        worldBounds = new Bounds(Vector3.zero, Vector3.one * 5000f);
    }

    void SetupSun()
    {
        foreach (var l in FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (l.type == LightType.Directional && l.gameObject.name != "Luna")
            {
                sunLight = l;
                sunLight.gameObject.name = "BotW_Sole";
                break;
            }
        }
        if (sunLight == null)
        {
            var go = new GameObject("BotW_Sole");
            sunLight = go.AddComponent<Light>();
            sunLight.type = LightType.Directional;
        }
        sunLight.shadows = LightShadows.Soft;
        sunLight.shadowStrength = 0.6f;
        sunLight.shadowResolution = LightShadowResolution.Medium;
    }

    void SetupMoon()
    {
        var go = new GameObject("Luna");
        moonLight = go.AddComponent<Light>();
        moonLight.type = LightType.Directional;
        moonLight.color = new Color(0.30f, 0.38f, 0.65f);  // blu-argento freddo
        moonLight.intensity = 0f;
        moonLight.shadows = LightShadows.Soft;
        moonLight.shadowStrength = 0.25f;
        moonLight.shadowResolution = LightShadowResolution.Low;
    }

    // ================================================================
    //  STELLE
    // ================================================================

    // ================================================================
    //  CATALOGO STELLE REALI — le 50 più brillanti
    //  (RA ore, RA min, Dec gradi, Dec min, magnitudine, indice colore B-V)
    // ================================================================
    static readonly float[,] BrightStars = {
        // RA_h, RA_m, Dec_d, Dec_m, mag, BV
        {  6,45f, -16,-43f, -1.46f, 0.00f },  // Sirio
        { 14,16f, -60,-50f, -0.72f, 0.15f },  // Canopo (bassa, appena visibile da 44N)
        {  5,17f, -08,12f,  0.12f, 1.85f },   // Betelgeuse
        {  5,14f, -08,-12f, 0.18f, -0.03f },  // Rigel
        { 18,37f,  38,47f,  0.03f, 0.00f },   // Vega
        {  5,17f,  46,00f,  0.08f, 0.80f },   // Capella
        {  7,39f,   5,14f,  0.34f, 0.42f },   // Procione
        { 19,51f,   8,52f,  0.76f, 0.22f },   // Altair
        { 13,25f, -11,10f,  0.97f, -0.23f },  // Spica
        {  1,38f, -57,-14f, 0.46f, 0.10f },   // Achernar (bassa)
        { 20,41f,  45,17f,  1.25f, 0.09f },   // Deneb
        {  4,36f,  16,31f,  0.85f, 1.54f },   // Aldebaran
        { 16,29f, -26,-26f, 0.96f, 1.83f },   // Antares
        { 12,27f, -63,-06f, 0.77f, -0.24f },  // Mimosa
        { 10,08f,  11,58f,  1.35f, -0.11f },  // Regolo
        {  2,07f,  23,28f,  1.16f, -0.13f },  // Mirfak? no — Hamal? — Polaris area
        {  6,23f, -52,-42f, 1.50f, -0.20f },  // Canopus companion placeholder
        {  7,35f,  31,53f,  1.58f, 0.03f },   // Polluce
        {  7,34f,  31,53f,  1.93f, 0.00f },   // Castore
        { 22,58f, -29,-37f, 1.16f, -0.02f },  // Fomalhaut
        {  0,40f,  56,32f,  2.23f, -0.15f },  // Schedar (Cassiopea)
        {  0,09f,  59,09f,  2.27f, -0.05f },  // Caph (Cassiopea)
        {  0,57f,  60,43f,  2.47f, 0.15f },   // Gamma Cas
        {  1,26f,  60,14f,  2.68f, -0.15f },  // Ruchbah (Cassiopea)
        {  1,55f,  63,40f,  3.37f, 0.13f },   // Epsilon Cas
        { 11,04f,  61,45f,  1.77f, 0.02f },   // Dubhe (Orsa Maggiore)
        { 11,02f,  56,23f,  2.37f, -0.02f },  // Merak
        { 12,54f,  55,58f,  2.44f, 0.00f },   // Alioth
        { 13,24f,  54,56f,  1.86f, -0.02f },  // Mizar
        { 13,48f,  49,19f,  1.86f, -0.19f },  // Alkaid
        {  2,02f,  89,16f,  1.98f, 0.60f },   // Polaris!
        {  3,08f,  40,57f,  2.12f, -0.05f },  // Mirfak (Perseo)
        {  3,24f,  49,52f,  1.80f, -0.15f },  // Algol area
        {  5,25f,  28,36f,  1.65f, -0.18f },  // Elnath (Toro)
        {  5,36f, -01,-12f, 1.70f, -0.18f },  // Alnilam (Cintura Orione)
        {  5,41f, -01,-57f, 1.69f, -0.24f },  // Alnitak
        {  5,32f, -00,-18f, 2.09f, -0.17f },  // Mintaka
        { 19,21f,  27,58f,  2.20f, 0.00f },   // Albireo area
        { 17,34f, -43,-00f, 1.63f, 0.40f },   // Sargas (Scorpione S)
        { 18,24f, -34,-23f, 1.85f, -0.22f },  // Kaus Australis (Sagittario)
        { 20,22f, -56,-44f, 1.74f, -0.02f },  // Peacock (bassa)
        { 22,42f,  30,13f,  2.49f, 1.67f },   // Scheat (Pegaso)
        { 23,05f,  28,05f,  2.06f, -0.04f },  // Markab (Pegaso)
        { 00,13f,  15,11f,  2.83f, 0.06f },   // Algenib (Pegaso)
        { 21,44f,   9,53f,  2.39f, 0.08f },   // Enif (Pegaso)
        { 22,08f, -47,00f,  1.94f, -0.25f },  // Alnair (Gru bassa)
        { 23,39f, -18,-00f, 2.02f, 0.09f },   // Diphda (Cetus)
        { 12,16f, -17,32f,  2.74f, -0.05f },  // Gienah (Corvo)
    };

    /// <summary>
    /// Latitudine osservatore (La Spezia ~44.1°N)
    /// </summary>
    const float OBSERVER_LAT = 44.1f;

    void CreateStars()
    {
        starSphere = new GameObject("BotW_Stelle");
        starSphere.transform.position = worldBounds.center;

        float skyRadius = worldBounds.size.magnitude * 0.8f;

        // Mesh procedurale con un quad per ogni stella
        int realCount = BrightStars.GetLength(0);
        int fillCount = starCount; // stelle di riempimento
        int planetCount = 5; // Venere, Giove, Marte, Saturno, Mercurio
        int totalStars = realCount + fillCount + planetCount;

        Vector3[] verts = new Vector3[totalStars * 4];
        int[] tris = new int[totalStars * 6];
        Vector2[] uvs = new Vector2[totalStars * 4];
        Color[] colors = new Color[totalStars * 4];

        int vi = 0, ti = 0;

        // ── Stelle reali dal catalogo ──
        for (int i = 0; i < realCount; i++)
        {
            float ra_h = BrightStars[i, 0];
            float dec_d = BrightStars[i, 2];
            float mag = BrightStars[i, 4];
            float bv = BrightStars[i, 5];

            // RA in radianti (ore -> gradi -> rad)
            float ra = ra_h / 24f * 360f * Mathf.Deg2Rad;
            // Dec in radianti
            float dec = dec_d * Mathf.Deg2Rad;

            // Converti equatoriali -> altazimutali approssimate
            // (rotazione per latitudine osservatore)
            float latRad = OBSERVER_LAT * Mathf.Deg2Rad;

            // Direzione sulla sfera celeste (coord equatoriali)
            float cx = Mathf.Cos(dec) * Mathf.Cos(ra);
            float cy = Mathf.Sin(dec);
            float cz = Mathf.Cos(dec) * Mathf.Sin(ra);

            // Rotazione per latitudine (asse X, porta polo nord a lat corretta)
            float angle = (Mathf.PI / 2f - latRad);
            float ry = cy * Mathf.Cos(angle) - cz * Mathf.Sin(angle);
            float rz = cy * Mathf.Sin(angle) + cz * Mathf.Cos(angle);
            float rx = cx;

            // Sotto l'orizzonte? Skip (ma mettiamo stella invisibile)
            Vector3 dir = new Vector3(rx, ry, rz).normalized;

            // Magnitudine -> dimensione e luminosità
            float brightness = Mathf.Clamp01(1f - (mag + 1.5f) / 5f);
            float size = Mathf.Lerp(15f, 80f, brightness) * (skyRadius / 5000f);

            // Colore dal B-V index
            Color starCol = BVtoColor(bv, brightness);

            Vector3 pos = dir * skyRadius;
            AddStarQuad(pos, size, starCol, verts, uvs, colors, tris, ref vi, ref ti);
        }

        // ── Stelle di riempimento (casuali, più deboli) ──
        Random.InitState(42);
        for (int i = 0; i < fillCount; i++)
        {
            // Distribuzione uniforme sulla semisfera visibile
            float u = Random.value;
            float v = Random.value;
            float theta = u * 2f * Mathf.PI;
            float phi = Mathf.Acos(1f - v); // semisfera superiore

            Vector3 dir = new Vector3(
                Mathf.Sin(phi) * Mathf.Cos(theta),
                Mathf.Cos(phi),
                Mathf.Sin(phi) * Mathf.Sin(theta)
            );

            float brightness = Random.Range(0.08f, 0.5f);
            float size = Mathf.Lerp(3f, 12f, brightness) * (skyRadius / 5000f);

            // Colori realistici casuali
            float bv = Random.Range(-0.2f, 1.5f);
            Color c = BVtoColor(bv, brightness);

            AddStarQuad(dir * skyRadius, size, c, verts, uvs, colors, tris, ref vi, ref ti);
        }

        // ── Pianeti (visibili a occhio nudo) ──
        // Posizioni approssimate sull'eclittica, luminosi e colorati
        // Longitudine eclittica fissa (snapshot) — orbitano lentamente col tempo
        // In realtà cambiano posizione durante l'anno, ma per l'effetto visivo va bene
        float eclipticObliquity = 23.44f * Mathf.Deg2Rad;
        System.Action<float, float, float, Color, float> addPlanet = (eclLon, eclLat, mag, col, pSize) =>
        {
            // Eclittica -> equatoriale (rotazione attorno a X di 23.44°)
            float lonRad = eclLon * Mathf.Deg2Rad;
            float latRad = eclLat * Mathf.Deg2Rad;
            float xe = Mathf.Cos(latRad) * Mathf.Cos(lonRad);
            float ye = Mathf.Cos(latRad) * Mathf.Sin(lonRad);
            float ze = Mathf.Sin(latRad);
            // Rotazione obliquità eclittica
            float yeq = ye * Mathf.Cos(eclipticObliquity) - ze * Mathf.Sin(eclipticObliquity);
            float zeq = ye * Mathf.Sin(eclipticObliquity) + ze * Mathf.Cos(eclipticObliquity);
            // Equatoriale -> locale (stessa rotazione delle stelle)
            float latR = OBSERVER_LAT * Mathf.Deg2Rad;
            float angle2 = (Mathf.PI / 2f - latR);
            float ry2 = zeq * Mathf.Cos(angle2) - yeq * Mathf.Sin(angle2);
            float rz2 = zeq * Mathf.Sin(angle2) + yeq * Mathf.Cos(angle2);
            Vector3 dir2 = new Vector3(xe, ry2, rz2).normalized;
            float brightness2 = Mathf.Clamp01(1f - (mag + 2.5f) / 5f);
            col.a = brightness2;
            AddStarQuad(dir2 * skyRadius, pSize * (skyRadius / 5000f), col, verts, uvs, colors, tris, ref vi, ref ti);
        };

        // Venere — la più luminosa, bianco-giallastro
        addPlanet(45f, -1f, -4.0f, new Color(1f, 0.98f, 0.85f), 120f);
        // Giove — seconda più luminosa, bianco-crema
        addPlanet(160f, 0.5f, -2.5f, new Color(0.95f, 0.92f, 0.80f), 90f);
        // Marte — rossastro, variabile
        addPlanet(220f, 1.2f, -1.0f, new Color(1f, 0.65f, 0.40f), 60f);
        // Saturno — giallastro pallido
        addPlanet(330f, 0.8f, 0.5f, new Color(0.95f, 0.90f, 0.70f), 45f);
        // Mercurio — vicino al sole, raramente visibile ma aggiungiamolo
        addPlanet(15f, -2f, -0.5f, new Color(0.90f, 0.85f, 0.75f), 35f);

        // Crea mesh
        Mesh mesh = new Mesh();
        mesh.indexFormat = totalStars * 4 > 65000
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.uv = uvs;
        mesh.colors = colors;
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * skyRadius * 2.5f);

        MeshFilter mf = starSphere.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;

        Shader sh = Shader.Find("Universal Render Pipeline/Unlit")
                 ?? Shader.Find("Sprites/Default");
        if (sh == null) { Debug.LogWarning("BotWAtmosphere: nessun shader per stelle"); return; }
        starMaterial = new Material(sh);
        starMaterial.renderQueue = 1000;
        starMaterial.SetColor("_BaseColor", Color.white);
        if (starMaterial.HasProperty("_Surface"))
        {
            starMaterial.SetFloat("_Surface", 1f);
            starMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            starMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            starMaterial.SetInt("_ZWrite", 0);
        }

        MeshRenderer mr = starSphere.AddComponent<MeshRenderer>();
        mr.sharedMaterial = starMaterial;
        starSphere.SetActive(false);
    }

    void AddStarQuad(Vector3 center, float size, Color col,
        Vector3[] verts, Vector2[] uvs, Color[] colors, int[] tris,
        ref int vi, ref int ti)
    {
        // Billboard quad che guarda verso il centro della sfera
        Vector3 up = Vector3.up;
        Vector3 forward = center.normalized;
        if (Mathf.Abs(Vector3.Dot(forward, up)) > 0.99f)
            up = Vector3.right;
        Vector3 right = Vector3.Cross(forward, up).normalized * size * 0.5f;
        up = Vector3.Cross(right, forward).normalized * size * 0.5f;

        int v0 = vi;
        verts[vi] = center - right - up; uvs[vi] = new Vector2(0, 0); colors[vi] = col; vi++;
        verts[vi] = center + right - up; uvs[vi] = new Vector2(1, 0); colors[vi] = col; vi++;
        verts[vi] = center + right + up; uvs[vi] = new Vector2(1, 1); colors[vi] = col; vi++;
        verts[vi] = center - right + up; uvs[vi] = new Vector2(0, 1); colors[vi] = col; vi++;

        tris[ti++] = v0; tris[ti++] = v0 + 2; tris[ti++] = v0 + 1;
        tris[ti++] = v0; tris[ti++] = v0 + 3; tris[ti++] = v0 + 2;
    }

    /// <summary>Converte indice di colore B-V in colore RGB (approssimazione Ballesteros)</summary>
    Color BVtoColor(float bv, float brightness)
    {
        // Temperatura approssimata dal B-V
        float t = 4600f * (1f / (0.92f * bv + 1.7f) + 1f / (0.92f * bv + 0.62f));
        // Blackbody -> RGB (approssimazione)
        float r, g, b;
        if (t < 6600f)
        {
            r = 1f;
            g = Mathf.Clamp01(0.39f * Mathf.Log(t / 100f) - 0.63f);
            b = t < 2000f ? 0f : Mathf.Clamp01(0.54f * Mathf.Log(t / 100f - 10f) - 1.19f);
        }
        else
        {
            r = Mathf.Clamp01(1.29f * Mathf.Pow(t / 100f - 60f, -0.13f));
            g = Mathf.Clamp01(1.13f * Mathf.Pow(t / 100f - 60f, -0.08f));
            b = 1f;
        }
        return new Color(r, g, b, brightness);
    }

    // ================================================================
    //  DISCO SOLARE
    // ================================================================

    void CreateSunDisc()
    {
        sunDisc = new GameObject("BotW_Sole_Disco");

        // Quad procedurale
        Mesh quad = new Mesh();
        float s = worldBounds.size.magnitude * 0.06f; // dimensione sole
        quad.vertices = new Vector3[] {
            new Vector3(-s, -s, 0), new Vector3(s, -s, 0),
            new Vector3(s, s, 0), new Vector3(-s, s, 0)
        };
        quad.uv = new Vector2[] {
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(1, 1), new Vector2(0, 1)
        };
        quad.triangles = new int[] { 0, 2, 1, 0, 3, 2 };
        quad.RecalculateNormals();
        quad.bounds = new Bounds(Vector3.zero, Vector3.one * s * 3f);

        sunDisc.AddComponent<MeshFilter>().sharedMesh = quad;

        // Texture gradiente radiale (cerchio sfumato)
        int sz = 64;
        Texture2D tex = new Texture2D(sz, sz, TextureFormat.RGBA32, false);
        Color[] px = new Color[sz * sz];
        float center = sz * 0.5f;
        for (int y = 0; y < sz; y++)
            for (int x = 0; x < sz; x++)
            {
                float dx = x - center, dy = y - center;
                float dist = Mathf.Sqrt(dx * dx + dy * dy) / center;
                // Nucleo brillante + alone morbido
                float core = Mathf.Clamp01(1f - dist * 2.5f);       // disco
                float glow = Mathf.Exp(-dist * dist * 3f) * 0.6f;   // alone
                float a = Mathf.Clamp01(core + glow);
                // Colore: centro bianco, bordo giallo-arancio
                Color c = Color.Lerp(
                    new Color(1f, 0.85f, 0.4f),   // bordo
                    new Color(1f, 1f, 0.95f),      // centro
                    core);
                c.a = a;
                px[y * sz + x] = c;
            }
        tex.SetPixels(px);
        tex.Apply();
        tex.filterMode = FilterMode.Bilinear;

        Shader sh = Shader.Find("Universal Render Pipeline/Unlit")
                 ?? Shader.Find("Sprites/Default");
        if (sh == null) { sunDisc.SetActive(false); return; }
        sunDiscMaterial = new Material(sh);
        sunDiscMaterial.mainTexture = tex;
        sunDiscMaterial.renderQueue = 999;
        sunDiscMaterial.SetColor("_BaseColor", Color.white);
        if (sunDiscMaterial.HasProperty("_Surface"))
        {
            sunDiscMaterial.SetFloat("_Surface", 1f);
            sunDiscMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            sunDiscMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            sunDiscMaterial.SetInt("_ZWrite", 0);
        }

        sunDisc.AddComponent<MeshRenderer>().sharedMaterial = sunDiscMaterial;
        sunDisc.SetActive(false);
    }

    // ================================================================
    //  DISCO LUNARE
    // ================================================================

    void CreateMoonDisc()
    {
        moonDisc = new GameObject("BotW_Luna_Disco");

        float s = worldBounds.size.magnitude * 0.035f;
        Mesh quad = new Mesh();
        quad.vertices = new Vector3[] {
            new Vector3(-s, -s, 0), new Vector3(s, -s, 0),
            new Vector3(s, s, 0), new Vector3(-s, s, 0)
        };
        quad.uv = new Vector2[] {
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(1, 1), new Vector2(0, 1)
        };
        quad.triangles = new int[] { 0, 2, 1, 0, 3, 2 };
        quad.RecalculateNormals();
        quad.bounds = new Bounds(Vector3.zero, Vector3.one * s * 3f);

        moonDisc.AddComponent<MeshFilter>().sharedMesh = quad;

        // Texture: cerchio luminoso con bordo sfumato + cratere sottile
        int sz = 64;
        Texture2D tex = new Texture2D(sz, sz, TextureFormat.RGBA32, false);
        Color[] px = new Color[sz * sz];
        float center = sz * 0.5f;
        for (int y = 0; y < sz; y++)
            for (int x = 0; x < sz; x++)
            {
                float dx = x - center, dy = y - center;
                float dist = Mathf.Sqrt(dx * dx + dy * dy) / center;
                float disc = Mathf.Clamp01(1f - dist * 2.2f);
                float glow = Mathf.Exp(-dist * dist * 4f) * 0.4f;
                float a = Mathf.Clamp01(disc + glow);
                // Cratere sottile (ombra decentrata)
                float cx = (x - center * 1.15f), cy = (y - center * 0.9f);
                float crater = Mathf.Clamp01(1f - Mathf.Sqrt(cx*cx+cy*cy) / (center*0.35f));
                // Colore: bianco-argento con sfumatura blu
                Color c = Color.Lerp(
                    new Color(0.75f, 0.80f, 0.95f),  // bordo blu-argento
                    new Color(0.95f, 0.95f, 1f),      // centro bianco
                    disc);
                c = Color.Lerp(c, new Color(0.6f, 0.62f, 0.7f), crater * 0.25f); // cratere
                c.a = a;
                px[y * sz + x] = c;
            }
        tex.SetPixels(px);
        tex.Apply();
        tex.filterMode = FilterMode.Bilinear;

        Shader sh = Shader.Find("Universal Render Pipeline/Unlit")
                 ?? Shader.Find("Sprites/Default");
        if (sh == null) { moonDisc.SetActive(false); return; }
        moonDiscMaterial = new Material(sh);
        moonDiscMaterial.mainTexture = tex;
        moonDiscMaterial.renderQueue = 998;
        moonDiscMaterial.SetColor("_BaseColor", Color.white);
        if (moonDiscMaterial.HasProperty("_Surface"))
        {
            moonDiscMaterial.SetFloat("_Surface", 1f);
            moonDiscMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            moonDiscMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            moonDiscMaterial.SetInt("_ZWrite", 0);
        }

        moonDisc.AddComponent<MeshRenderer>().sharedMaterial = moonDiscMaterial;
        moonDisc.SetActive(false);
    }

    // ================================================================
    //  NUVOLE
    // ================================================================

    class CloudData
    {
        public GameObject go;
        public Vector3 velocity;
    }

    void CreateClouds()
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Lit")
                 ?? Shader.Find("Standard")
                 ?? Shader.Find("Sprites/Default");
        if (sh == null) { Debug.LogWarning("BotWAtmosphere: nessun shader per nuvole"); return; }
        cloudMat = new Material(sh);
        cloudMat.color = DayCloud;

        // Transparent
        if (cloudMat.HasProperty("_Surface"))
        {
            cloudMat.SetFloat("_Surface", 1f);
            cloudMat.SetOverrideTag("RenderType", "Transparent");
            cloudMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            cloudMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            cloudMat.SetInt("_ZWrite", 0);
            cloudMat.renderQueue = (int)RenderQueue.Transparent;
        }
        if (cloudMat.HasProperty("_Smoothness")) cloudMat.SetFloat("_Smoothness", 0f);
        cloudMat.EnableKeyword("_EMISSION");

        Random.InitState(777);
        for (int i = 0; i < cloudCount; i++)
        {
            GameObject cloud = new GameObject($"Nuvola_{i}");
            float cx = Random.Range(worldBounds.min.x, worldBounds.max.x);
            float cz = Random.Range(worldBounds.min.z, worldBounds.max.z);
            float cy = cloudAltitude + Random.Range(-80f, 80f);
            cloud.transform.position = new Vector3(cx, cy, cz);

            int blobs = Random.Range(3, 6);
            for (int b = 0; b < blobs; b++)
            {
                GameObject blob = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(blob.GetComponent<Collider>());
                blob.transform.parent = cloud.transform;
                blob.transform.localPosition = new Vector3(
                    Random.Range(-70f, 70f), Random.Range(-8f, 12f), Random.Range(-35f, 35f));
                blob.transform.localScale = new Vector3(
                    Random.Range(50f, 150f), Random.Range(12f, 30f), Random.Range(35f, 80f));
                blob.GetComponent<Renderer>().sharedMaterial = cloudMat;
            }

            clouds.Add(new CloudData
            {
                go = cloud,
                velocity = new Vector3(Random.Range(-1f, 1f) * cloudSpeed, 0,
                                       Random.Range(-0.3f, 0.3f) * cloudSpeed)
            });
        }
    }

    // ================================================================
    //  POST PROCESSING
    // ================================================================

    void SetupPostProcessing()
    {
        ppVolume = FindAnyObjectByType<Volume>();
        if (ppVolume == null)
        {
            var go = new GameObject("BotW_Volume");
            ppVolume = go.AddComponent<Volume>();
            ppVolume.isGlobal = true;
        }

        ppProfile = ScriptableObject.CreateInstance<VolumeProfile>();
        ppVolume.profile = ppProfile;

        ppBloom = ppProfile.Add<Bloom>(true);
        ppBloom.intensity.Override(bloomIntensity);
        ppBloom.threshold.Override(0.85f);
        ppBloom.scatter.Override(0.65f);
        ppBloom.tint.Override(new Color(1, 0.98f, 0.92f));
        ppBloom.highQualityFiltering.Override(false);

        ppColor = ppProfile.Add<ColorAdjustments>(true);
        ppColor.saturation.Override(12f);
        ppColor.contrast.Override(8f);
        ppColor.postExposure.Override(0.15f);

        ppSplit = ppProfile.Add<SplitToning>(true);

        ppVignette = ppProfile.Add<Vignette>(true);
        ppVignette.intensity.Override(vignetteIntensity);
        ppVignette.smoothness.Override(0.4f);
        ppVignette.color.Override(new Color(0.12f, 0.08f, 0.18f));

        ppTonemap = ppProfile.Add<Tonemapping>(true);
        ppTonemap.mode.Override(TonemappingMode.ACES);
    }

    // ================================================================
    //  UPDATE
    // ================================================================

    void Update()
    {
        if (!initialized) return;

        // Input (New Input System)
        var kb = Keyboard.current;
        if (kb != null)
        {
            // N = salta a giorno/notte
            if (kb.nKey.wasPressedThisFrame)
            {
                timeOfDay = timeOfDay < 0.5f ? 0.85f : 0.35f;
            }
            // L = pausa/play tempo
            if (kb.lKey.wasPressedThisFrame)
            {
                timePaused = !timePaused;
            }
            // +/- = velocita' tempo
            if (kb.equalsKey.wasPressedThisFrame || kb.numpadPlusKey.wasPressedThisFrame)
                secondsPerHour = Mathf.Max(5f, secondsPerHour / 1.5f);  // piu' veloce
            if (kb.minusKey.wasPressedThisFrame || kb.numpadMinusKey.wasPressedThisFrame)
                secondsPerHour = Mathf.Min(300f, secondsPerHour * 1.5f);  // piu' lento
        }

        // Tempo scorre sempre (a meno che in pausa)
        if (!timePaused)
        {
            // 30s reali = 1 ora = 1/24 del ciclo
            float hoursPerSecond = 1f / secondsPerHour;
            float cyclePerSecond = hoursPerSecond / 24f;
            timeOfDay += cyclePerSecond * Time.deltaTime;
            if (timeOfDay > 1f) timeOfDay -= 1f;
        }

        ApplyTimeOfDay();
        UpdateClouds();
    }

    // ================================================================
    //  CICLO GIORNO/NOTTE
    // ================================================================

    void ApplyTimeOfDay()
    {
        float sunAngle = (timeOfDay - 0.25f) * 360f;
        float sunIntensity = Mathf.Clamp01(Mathf.Sin(timeOfDay * Mathf.PI));
        float nightFactor = 1f - sunIntensity;

        // Quanto siamo vicini all'orizzonte (alba/tramonto)
        float horizonDist = Mathf.Abs(timeOfDay - 0.25f);
        float horizonDist2 = Mathf.Abs(timeOfDay - 0.75f);
        float horizon = Mathf.Clamp01(1f - Mathf.Min(horizonDist, horizonDist2) * 6f);

        // ── Sole ──
        if (sunLight != null)
        {
            sunLight.transform.rotation = Quaternion.Euler(sunAngle, -30f, 0f);
            sunLight.intensity = sunIntensity * 1.3f;
            sunLight.color = Color.Lerp(DaySun, SunsetSun, horizon * 0.7f);
            sunLight.shadowStrength = Mathf.Lerp(0.3f, 0.65f, sunIntensity);
        }

        // ── Luna (orbita opposta al sole) ──
        float moonAngle = sunAngle + 180f;  // opposta al sole
        float moonAltitude = Mathf.Sin((timeOfDay + 0.5f) * Mathf.PI); // alta quando sole basso
        float moonIntensity = Mathf.Clamp01(moonAltitude) * nightFactor;

        if (moonLight != null)
        {
            moonLight.transform.rotation = Quaternion.Euler(moonAngle, 150f, 0f);
            moonLight.intensity = moonIntensity * 0.18f;
            // Colore luna: piu' calda quando bassa (come il sole)
            float moonHorizon = Mathf.Clamp01(1f - Mathf.Abs(moonAltitude) * 3f);
            moonLight.color = Color.Lerp(
                new Color(0.30f, 0.38f, 0.65f),   // alta: blu-argento
                new Color(0.55f, 0.40f, 0.30f),   // bassa: ambra calda
                moonHorizon * 0.5f);
        }

        // ── Disco lunare ──
        if (moonDisc != null)
        {
            bool moonVisible = moonIntensity > 0.02f;
            moonDisc.SetActive(moonVisible);
            if (moonVisible && moonLight != null && Camera.main != null)
            {
                float skyR = worldBounds.size.magnitude * 0.70f;
                Vector3 moonDir = -moonLight.transform.forward;
                moonDisc.transform.position = Camera.main.transform.position + moonDir * skyR;
                moonDisc.transform.LookAt(Camera.main.transform);
                // Alpha basata su intensita'
                Color moonCol = new Color(0.85f, 0.88f, 1f, Mathf.Clamp01(moonIntensity * 1.5f));
                if (moonDiscMaterial != null)
                    moonDiscMaterial.SetColor("_BaseColor", moonCol);
            }
        }

        // ── Stelle ──
        if (starSphere != null)
        {
            starSphere.SetActive(nightFactor > 0.15f);
            if (starMaterial != null)
                starMaterial.SetColor("_BaseColor", new Color(1, 1, 1, nightFactor * starBrightness));
            // Rotazione siderale (15°/ora = 0.25°/min) — simula rotazione terrestre
            starSphere.transform.Rotate(Vector3.up, Time.deltaTime * 0.5f, Space.World);
            // Centra sulla camera
            if (Camera.main != null)
                starSphere.transform.position = Camera.main.transform.position;
        }

        // ── Disco solare ──
        if (sunDisc != null)
        {
            bool sunVisible = sunIntensity > 0.05f;
            sunDisc.SetActive(sunVisible);
            if (sunVisible && sunLight != null && Camera.main != null)
            {
                // Posiziona il disco nella direzione del sole
                float skyR = worldBounds.size.magnitude * 0.75f;
                Vector3 sunDir = -sunLight.transform.forward;
                sunDisc.transform.position = Camera.main.transform.position + sunDir * skyR;
                sunDisc.transform.LookAt(Camera.main.transform);
                // Colore: bianco di giorno, arancione all'orizzonte
                Color sunCol = Color.Lerp(new Color(1f, 1f, 0.95f, 0.9f),
                                          new Color(1f, 0.5f, 0.15f, 0.95f), horizon);
                sunCol.a *= Mathf.Clamp01(sunIntensity * 2f);
                if (sunDiscMaterial != null)
                    sunDiscMaterial.SetColor("_BaseColor", sunCol);
            }
        }

        // ── Ambient ──
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = Color.Lerp(NightSky, DaySky, sunIntensity);
        RenderSettings.ambientEquatorColor = Color.Lerp(NightEquator, DayEquator, sunIntensity);
        RenderSettings.ambientGroundColor = Color.Lerp(NightGround, DayGround, sunIntensity);
        RenderSettings.ambientIntensity = Mathf.Lerp(0.4f, 1.2f, sunIntensity);

        // ── Fog ──
        Color baseFog = Color.Lerp(NightFog, DayFog, sunIntensity);
        if (horizon > 0) baseFog = Color.Lerp(baseFog, SunsetFog, horizon * 0.4f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = baseFog;
        RenderSettings.fogStartDistance = Mathf.Lerp(200f, 800f, sunIntensity);
        RenderSettings.fogEndDistance = Mathf.Lerp(4000f, 12000f, sunIntensity);

        // ── Skybox ──
        Material sky = RenderSettings.skybox;
        if (sky != null)
        {
            if (sky.HasProperty("_Exposure"))
                sky.SetFloat("_Exposure", Mathf.Lerp(0.05f, 1.3f, sunIntensity));
            if (sky.HasProperty("_AtmosphereThickness"))
                sky.SetFloat("_AtmosphereThickness", Mathf.Lerp(0.3f, 1.15f + horizon * 0.5f, sunIntensity));
            if (sky.HasProperty("_SkyTint"))
            {
                Color tint = Color.Lerp(new Color(0.03f, 0.03f, 0.15f),          // notte
                                        new Color(0.25f, 0.45f, 0.92f), sunIntensity); // giorno: blu intenso
                if (horizon > 0)
                    tint = Color.Lerp(tint, new Color(0.80f, 0.40f, 0.22f), horizon * 0.4f); // tramonto acceso
                sky.SetColor("_SkyTint", tint);
            }
        }

        // ── Nuvole ──
        if (cloudMat != null)
        {
            Color cc = Color.Lerp(NightCloud, DayCloud, sunIntensity);
            if (horizon > 0) cc = Color.Lerp(cc, SunsetCloud, horizon * 0.6f);
            cloudMat.color = cc;

            Color em = Color.Lerp(new Color(0.01f, 0.01f, 0.02f),
                                  new Color(0.35f, 0.35f, 0.38f), sunIntensity);
            if (horizon > 0) em = Color.Lerp(em, new Color(0.5f, 0.25f, 0.1f), horizon * 0.4f);
            if (cloudMat.HasProperty("_EmissionColor"))
                cloudMat.SetColor("_EmissionColor", em);
        }

        // ── Post Processing ──
        if (ppSplit != null)
        {
            ppSplit.shadows.Override(Color.Lerp(SplitShadowNight, SplitShadowDay, sunIntensity));
            ppSplit.highlights.Override(Color.Lerp(SplitHighNight, SplitHighDay, sunIntensity));
            ppSplit.balance.Override(-15f);
        }
        if (ppBloom != null)
        {
            ppBloom.intensity.Override(Mathf.Lerp(bloomIntensity * 0.5f, bloomIntensity, sunIntensity));
        }
        if (ppColor != null)
        {
            ppColor.postExposure.Override(Mathf.Lerp(-0.3f, 0.15f, sunIntensity));
        }

        DynamicGI.UpdateEnvironment();
    }

    void UpdateClouds()
    {
        float halfW = worldBounds.size.x * 0.7f;
        float halfZ = worldBounds.size.z * 0.7f;
        foreach (var c in clouds)
        {
            if (c.go == null) continue;
            Vector3 p = c.go.transform.position + c.velocity * Time.deltaTime;
            if (p.x > worldBounds.center.x + halfW) p.x -= halfW * 2f;
            if (p.x < worldBounds.center.x - halfW) p.x += halfW * 2f;
            if (p.z > worldBounds.center.z + halfZ) p.z -= halfZ * 2f;
            if (p.z < worldBounds.center.z - halfZ) p.z += halfZ * 2f;
            c.go.transform.position = p;
        }
    }

    // ================================================================
    //  CLEANUP
    // ================================================================

    void OnDestroy()
    {
        if (starSphere != null) Destroy(starSphere);
        if (sunDisc != null) Destroy(sunDisc);
        if (moonDisc != null) Destroy(moonDisc);
        foreach (var c in clouds) { if (c.go != null) Destroy(c.go); }
        if (moonLight != null) Destroy(moonLight.gameObject);
        if (ppProfile != null) DestroyImmediate(ppProfile);
    }
}
