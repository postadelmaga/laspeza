using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Configura lo stile visivo Breath of the Wild:
///   - Post-processing: bloom morbido, split toning caldo/freddo, vignette leggera
///   - Fog atmosferico: transizione calda -> blu con la distanza
///   - Skybox: gradiente painterly
///   - Illuminazione: sole caldo, ombra blu-viola, rim light
///   - Color grading: saturazione leggera, toni dorati
///
/// Attaccare alla camera o a un GameObject qualsiasi.
/// Crea automaticamente un Volume se non ne trova uno.
/// </summary>
public class BotWStyleManager : MonoBehaviour
{
    [Header("Style")]
    [Tooltip("0 = realistico, 1 = full BotW")]
    [Range(0f, 1f)]
    public float styleIntensity = 1f;

    [Header("Atmosphere")]
    public Color fogNear = new Color(0.85f, 0.90f, 0.82f);   // foschia calda vicina
    public Color fogFar = new Color(0.55f, 0.65f, 0.82f);     // foschia blu lontana
    public float fogStart = 800f;
    public float fogEnd = 12000f;

    [Header("Lighting")]
    public Color sunColor = new Color(1.0f, 0.95f, 0.85f);
    public float sunIntensity = 1.3f;
    public Color ambientSky = new Color(0.60f, 0.72f, 0.90f);
    public Color ambientEquator = new Color(0.80f, 0.78f, 0.68f);
    public Color ambientGround = new Color(0.35f, 0.32f, 0.28f);

    [Header("Post Processing")]
    public float bloomIntensity = 0.35f;
    public float bloomThreshold = 0.85f;
    public float bloomScatter = 0.65f;
    public float vignetteIntensity = 0.18f;
    public float saturation = 12f;
    public float contrast = 8f;
    public Color splitShadows = new Color(0.35f, 0.30f, 0.55f); // viola nelle ombre
    public Color splitHighlights = new Color(0.58f, 0.52f, 0.42f); // dorato nelle luci

    private Volume volume;
    private VolumeProfile profile;
    private bool initialized;

    void Start()
    {
        SetupPostProcessing();
        SetupAtmosphere();
        SetupLighting();
        SetupSkybox();
        initialized = true;
    }

    void Update()
    {
        if (!initialized) return;

        // Fog colore che cambia leggermente col tempo (simulazione scattering)
        float t = Mathf.Sin(Time.time * 0.03f) * 0.5f + 0.5f;
        Color currentFog = Color.Lerp(fogNear, fogFar, 0.3f + t * 0.1f);
        RenderSettings.fogColor = currentFog;
    }

    // ================================================================
    //  POST PROCESSING
    // ================================================================

    void SetupPostProcessing()
    {
        // Cerca Volume esistente o creane uno
        volume = FindAnyObjectByType<Volume>();
        if (volume == null)
        {
            var go = new GameObject("BotW_PostProcess");
            volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
        }

        // Crea un nuovo profilo runtime (non modifica l'asset)
        profile = ScriptableObject.CreateInstance<VolumeProfile>();
        volume.profile = profile;

        // Bloom — il cuore del look BotW: glow morbido sulle luci
        var bloom = profile.Add<Bloom>(true);
        bloom.intensity.Override(bloomIntensity);
        bloom.threshold.Override(bloomThreshold);
        bloom.scatter.Override(bloomScatter);
        bloom.tint.Override(new Color(1f, 0.98f, 0.92f)); // tint caldo
        bloom.highQualityFiltering.Override(false); // performance

        // Color Adjustments — saturazione e contrasto leggeri
        var colorAdj = profile.Add<ColorAdjustments>(true);
        colorAdj.saturation.Override(saturation);
        colorAdj.contrast.Override(contrast);
        colorAdj.postExposure.Override(0.15f); // leggermente piu' luminoso
        colorAdj.colorFilter.Override(new Color(1f, 0.98f, 0.95f)); // warmth

        // Split Toning — ombre blu/viola, luci dorate (signature BotW)
        var split = profile.Add<SplitToning>(true);
        split.shadows.Override(splitShadows);
        split.highlights.Override(splitHighlights);
        split.balance.Override(-15f); // shadows leggermente dominanti

        // Vignette — leggera, centra l'attenzione
        var vignette = profile.Add<Vignette>(true);
        vignette.intensity.Override(vignetteIntensity);
        vignette.smoothness.Override(0.4f);
        vignette.color.Override(new Color(0.15f, 0.10f, 0.20f)); // vignette viola scuro

        // Tonemapping — ACES per look cinematico
        var tonemap = profile.Add<Tonemapping>(true);
        tonemap.mode.Override(TonemappingMode.ACES);

        // Lift Gamma Gain — fine tuning
        var lgg = profile.Add<LiftGammaGain>(true);
        // Gamma leggermente caldo
        lgg.gamma.Override(new Vector4(1.02f, 1.0f, 0.97f, 0f));

        Debug.Log("BotW: post-processing configurato (bloom, split toning, ACES)");
    }

    // ================================================================
    //  ATMOSFERA / FOG
    // ================================================================

    void SetupAtmosphere()
    {
        // Fog lineare con colori BotW
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = Color.Lerp(fogNear, fogFar, 0.4f);
        RenderSettings.fogStartDistance = fogStart;
        RenderSettings.fogEndDistance = fogEnd;

        // Ambient trilight (BotW usa ambient molto colorato)
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = ambientSky;
        RenderSettings.ambientEquatorColor = ambientEquator;
        RenderSettings.ambientGroundColor = ambientGround;
        RenderSettings.ambientIntensity = 1.2f;
        RenderSettings.reflectionIntensity = 0.6f;

        Debug.Log("BotW: atmosfera configurata");
    }

    // ================================================================
    //  ILLUMINAZIONE
    // ================================================================

    void SetupLighting()
    {
        // Trova o crea sole
        Light sun = null;
        foreach (var l in FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (l.type == LightType.Directional)
            {
                sun = l;
                break;
            }
        }

        if (sun == null)
        {
            var go = new GameObject("BotW_Sun");
            sun = go.AddComponent<Light>();
            sun.type = LightType.Directional;
        }

        // BotW sun: angolo medio-alto, luce calda dorata
        sun.color = sunColor;
        sun.intensity = sunIntensity;
        sun.transform.rotation = Quaternion.Euler(45f, -30f, 0f);
        sun.shadows = LightShadows.Soft;
        sun.shadowStrength = 0.6f; // ombre morbide, non troppo scure
        sun.shadowResolution = UnityEngine.Rendering.LightShadowResolution.Medium;

        // Shadow distance generosa per il paesaggio
        QualitySettings.shadowDistance = 800f;
        QualitySettings.shadowCascades = 4;

        Debug.Log("BotW: illuminazione configurata");
    }

    // ================================================================
    //  SKYBOX
    // ================================================================

    void SetupSkybox()
    {
        Shader skyShader = Shader.Find("Skybox/Procedural");
        if (skyShader == null) return;

        Material sky = new Material(skyShader);

        // BotW sky: azzurro chiaro e luminoso, sole non troppo grande
        if (sky.HasProperty("_SunSize")) sky.SetFloat("_SunSize", 0.04f);
        if (sky.HasProperty("_SunSizeConvergence")) sky.SetFloat("_SunSizeConvergence", 5f);
        if (sky.HasProperty("_AtmosphereThickness")) sky.SetFloat("_AtmosphereThickness", 1.0f);
        if (sky.HasProperty("_SkyTint")) sky.SetColor("_SkyTint", new Color(0.45f, 0.55f, 0.78f));
        if (sky.HasProperty("_GroundColor")) sky.SetColor("_GroundColor", new Color(0.50f, 0.48f, 0.42f));
        if (sky.HasProperty("_Exposure")) sky.SetFloat("_Exposure", 1.3f);

        RenderSettings.skybox = sky;
        DynamicGI.UpdateEnvironment();

        Debug.Log("BotW: skybox configurata");
    }

    // ================================================================
    //  CLEANUP
    // ================================================================

    void OnDestroy()
    {
        if (profile != null)
            DestroyImmediate(profile);
    }
}
