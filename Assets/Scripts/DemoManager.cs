using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

/// <summary>
/// Demo standalone: all'avvio genera scenari suggestivi,
/// volo libero + flythrough cinematico + settings video.
///
/// Controlli:
///   WASD / Frecce = muovi (volo libero)
///   Mouse         = guarda
///   Shift         = veloce
///   Q/E           = su/giu
///   Space         = cambia scenario (flythrough)
///   F1            = toggle menu impostazioni
///   F2            = toggle flythrough / volo libero
///   N             = giorno/notte
///   L             = time-lapse
///   ESC           = esci
/// </summary>
[RequireComponent(typeof(Camera))]
public class DemoManager : MonoBehaviour
{
    [Header("Camera")]
    public float flySpeed = 150f;
    public float fastMult = 4f;
    public float mouseSens = 2f;
    public float smoothing = 8f;

    [Header("Flythrough")]
    public float flythroughSpeed = 30f;
    public float transitionTime = 3f;

    // State
    enum Mode { FreeFly, Flythrough }
    Mode mode = Mode.Flythrough;
    bool showUI = false;
    bool cursorLocked = true;

    float rotX, rotY;
    Bounds worldBounds;
    float terrainHeight;
    float seaLevelY;

    // Flythrough
    List<Viewpoint> viewpoints = new List<Viewpoint>();
    int currentVP = 0;
    float vpTimer;
    float vpDuration = 12f;
    Vector3 vpStartPos, vpEndPos;
    Quaternion vpStartRot, vpEndRot;

    // Video settings
    int qualityIndex;
    bool fullscreen;
    int resIndex;
    string[] resLabels;
    Resolution[] resolutions;

    // Input
    Keyboard kb;
    Mouse mouse;

    // GUI styles (cached per evitare allocazioni ogni frame)
    GUIStyle cachedHudStyle;
    GUIStyle cachedTitleStyle;
    GUIStyle cachedFpsStyle;

    struct Viewpoint
    {
        public Vector3 position;
        public Quaternion rotation;
        public string name;
        public float duration;
    }

    // ================================================================
    //  START
    // ================================================================

    void Start()
    {
        kb = Keyboard.current;
        mouse = Mouse.current;

        // Forza risoluzione nativa fullscreen all'avvio
        Screen.SetResolution(Screen.currentResolution.width, Screen.currentResolution.height, FullScreenMode.FullScreenWindow);
        fullscreen = true;

        FindWorldBounds();
        CollectResolutions();
        qualityIndex = QualitySettings.GetQualityLevel();

        GenerateViewpoints();
        StartFlythrough();
        LockCursor(true);

        // Aggiungi componenti se mancanti
        if (GetComponent<BotWAtmosphere>() == null)
            gameObject.AddComponent<BotWAtmosphere>();
        if (GetComponent<ToonTreePlacer>() == null)
            gameObject.AddComponent<ToonTreePlacer>();

        // Setup camera
        Camera cam = GetComponent<Camera>();
        cam.farClipPlane = 25000f;
        cam.nearClipPlane = 0.5f;
        cam.allowHDR = true;

        // Skybox
        SetupSkybox();

        // Shadow distance
        QualitySettings.shadowDistance = 800f;
    }

    void SetupSkybox()
    {
        Shader sh = Shader.Find("Skybox/Procedural");
        if (sh == null) return;
        Material sky = new Material(sh);
        if (sky.HasProperty("_SunSize")) sky.SetFloat("_SunSize", 0.04f);
        if (sky.HasProperty("_SunSizeConvergence")) sky.SetFloat("_SunSizeConvergence", 5f);
        if (sky.HasProperty("_AtmosphereThickness")) sky.SetFloat("_AtmosphereThickness", 1.15f);
        if (sky.HasProperty("_SkyTint")) sky.SetColor("_SkyTint", new Color(0.25f, 0.45f, 0.90f)); // blu intenso BotW
        if (sky.HasProperty("_GroundColor")) sky.SetColor("_GroundColor", new Color(0.30f, 0.42f, 0.28f));
        if (sky.HasProperty("_Exposure")) sky.SetFloat("_Exposure", 1.35f);
        RenderSettings.skybox = sky;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogStartDistance = 800f;
        RenderSettings.fogEndDistance = 12000f;
        RenderSettings.fogColor = new Color(0.72f, 0.78f, 0.85f);
    }

    void FindWorldBounds()
    {
        var world = GameObject.Find("CityBuilder_World");
        if (world == null) { worldBounds = new Bounds(Vector3.zero, Vector3.one * 5000f); return; }

        Terrain[] terrains = world.GetComponentsInChildren<Terrain>();
        if (terrains.Length == 0) { worldBounds = new Bounds(Vector3.zero, Vector3.one * 5000f); return; }

        worldBounds = new Bounds(terrains[0].transform.position, Vector3.zero);
        foreach (var t in terrains)
        {
            worldBounds.Encapsulate(t.transform.position);
            worldBounds.Encapsulate(t.transform.position + t.terrainData.size);
            terrainHeight = Mathf.Max(terrainHeight, t.terrainData.size.y);
        }

        // Stima sea level dal terrain
        if (terrains[0].terrainData.size.y > 0)
        {
            float[,] h = terrains[0].terrainData.GetHeights(0, 0, 2, 2);
            seaLevelY = h[0, 0] * terrains[0].terrainData.size.y;
        }
    }

    // ================================================================
    //  VIEWPOINTS SUGGESTIVI
    // ================================================================

    void GenerateViewpoints()
    {
        Vector3 center = worldBounds.center;
        float w = worldBounds.size.x;
        float l = worldBounds.size.z;
        float h = terrainHeight;

        // 1. Panoramica dall'alto — vista mappa
        viewpoints.Add(new Viewpoint
        {
            position = center + new Vector3(0, h * 0.8f, -l * 0.3f),
            rotation = Quaternion.Euler(35f, 0, 0),
            name = "Panoramica",
            duration = 15f
        });

        // 2. Volo radente sulla costa
        viewpoints.Add(new Viewpoint
        {
            position = new Vector3(worldBounds.min.x + w * 0.2f, seaLevelY + 20f, center.z),
            rotation = Quaternion.Euler(8f, 45f, 0),
            name = "Costa",
            duration = 14f
        });

        // 3. Alba sulle colline (da est)
        viewpoints.Add(new Viewpoint
        {
            position = new Vector3(worldBounds.max.x - w * 0.1f, h * 0.4f, center.z - l * 0.2f),
            rotation = Quaternion.Euler(15f, -120f, 0),
            name = "Colline all'alba",
            duration = 14f
        });

        // 4. Volo tra le valli
        viewpoints.Add(new Viewpoint
        {
            position = center + new Vector3(-w * 0.2f, h * 0.15f, -l * 0.15f),
            rotation = Quaternion.Euler(10f, 30f, 0),
            name = "Valli",
            duration = 13f
        });

        // 5. Vista mare dall'alto
        viewpoints.Add(new Viewpoint
        {
            position = new Vector3(center.x, h * 0.6f, worldBounds.min.z + l * 0.1f),
            rotation = Quaternion.Euler(45f, 0, 0),
            name = "Vista mare",
            duration = 14f
        });

        // 6. Orbita attorno al centro
        viewpoints.Add(new Viewpoint
        {
            position = center + new Vector3(w * 0.25f, h * 0.3f, l * 0.25f),
            rotation = Quaternion.Euler(25f, -135f, 0),
            name = "Orbita",
            duration = 16f
        });

        // 7. Volo notturno basso
        viewpoints.Add(new Viewpoint
        {
            position = center + new Vector3(0, h * 0.08f, l * 0.2f),
            rotation = Quaternion.Euler(5f, 180f, 0),
            name = "Volo notturno",
            duration = 12f
        });

        // 8. Picchiata vertiginosa
        viewpoints.Add(new Viewpoint
        {
            position = center + new Vector3(0, h * 1.2f, 0),
            rotation = Quaternion.Euler(75f, 45f, 0),
            name = "Vista zenitale",
            duration = 10f
        });
    }

    void StartFlythrough()
    {
        if (viewpoints.Count == 0) return;

        var vp = viewpoints[currentVP];
        vpDuration = vp.duration;
        vpStartPos = transform.position;
        vpStartRot = transform.rotation;

        // End pos: avanza nella direzione dello sguardo
        Vector3 fwd = vp.rotation * Vector3.forward;
        vpEndPos = vp.position + fwd * flythroughSpeed * vpDuration;
        vpEndRot = vp.rotation;

        // Start da una posizione leggermente diversa
        vpStartPos = vp.position;
        vpEndPos = vpStartPos + fwd * flythroughSpeed * vpDuration;

        vpTimer = 0;
    }

    void NextViewpoint()
    {
        currentVP = (currentVP + 1) % viewpoints.Count;

        // Triggera cambio ora per varieta'
        var atmos = GetComponent<BotWAtmosphere>();
        if (atmos != null)
        {
            // Scenari suggestivi: alba, giorno, tramonto, notte
            float[] times = { 0.28f, 0.40f, 0.73f, 0.88f, 0.35f, 0.50f, 0.05f, 0.45f };
            atmos.timeOfDay = times[currentVP % times.Length];
        }

        StartFlythrough();
    }

    // ================================================================
    //  UPDATE
    // ================================================================

    void Update()
    {
        if (kb == null) kb = Keyboard.current;
        if (mouse == null) mouse = Mouse.current;
        if (kb == null) return;

        // Toggle UI
        if (kb.f1Key.wasPressedThisFrame)
        {
            showUI = !showUI;
            LockCursor(!showUI);
        }

        // Toggle mode
        if (kb.f2Key.wasPressedThisFrame)
        {
            mode = mode == Mode.FreeFly ? Mode.Flythrough : Mode.FreeFly;
            if (mode == Mode.Flythrough) StartFlythrough();
            LockCursor(true);
        }

        // Next scenario
        if (kb.spaceKey.wasPressedThisFrame && mode == Mode.Flythrough)
            NextViewpoint();

        // Cursor
        if (!showUI)
        {
            if (mouse != null && mouse.leftButton.wasPressedThisFrame) LockCursor(true);
            if (kb.escapeKey.wasPressedThisFrame)
            {
                if (cursorLocked) LockCursor(false);
                else Application.Quit();
            }
        }

        if (showUI) return;

        if (mode == Mode.Flythrough)
            UpdateFlythrough();
        else
            UpdateFreeFly();
    }

    void UpdateFlythrough()
    {
        vpTimer += Time.deltaTime;
        float t = Mathf.Clamp01(vpTimer / vpDuration);

        // Smooth ease in/out
        float smooth = t * t * (3f - 2f * t);

        transform.position = Vector3.Lerp(vpStartPos, vpEndPos, smooth);
        transform.rotation = Quaternion.Slerp(vpStartRot, vpEndRot, Mathf.Min(smooth * 2f, 1f));

        if (vpTimer >= vpDuration)
            NextViewpoint();
    }

    void UpdateFreeFly()
    {
        if (!cursorLocked) return;

        // Mouse look
        if (mouse != null)
        {
            Vector2 d = mouse.delta.ReadValue();
            rotX += d.x * mouseSens * 0.1f;
            rotY -= d.y * mouseSens * 0.1f;
            rotY = Mathf.Clamp(rotY, -89f, 89f);
        }

        Quaternion targetRot = Quaternion.Euler(rotY, rotX, 0);
        transform.rotation = Quaternion.Lerp(transform.rotation, targetRot, Time.deltaTime * smoothing);

        // Movement
        float speed = flySpeed * (kb.leftShiftKey.isPressed ? fastMult : 1f);
        Vector3 move = Vector3.zero;
        if (kb.wKey.isPressed || kb.upArrowKey.isPressed) move += transform.forward;
        if (kb.sKey.isPressed || kb.downArrowKey.isPressed) move -= transform.forward;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) move -= transform.right;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) move += transform.right;
        if (kb.eKey.isPressed) move += Vector3.up;
        if (kb.qKey.isPressed) move -= Vector3.up;

        if (mouse != null)
        {
            float scroll = mouse.scroll.ReadValue().y;
            move += Vector3.up * scroll * 0.3f;
        }

        if (move.sqrMagnitude > 0.001f)
            transform.position += move.normalized * speed * Time.deltaTime;
    }

    void LockCursor(bool locked)
    {
        cursorLocked = locked;
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    // ================================================================
    //  RESOLUTIONS
    // ================================================================

    void CollectResolutions()
    {
        // Risoluzioni comuni + quelle del sistema
        var labels = new List<string>();

        // Aggiungi risoluzioni standard
        string[] common = {
            "854x480", "1280x720", "1366x768", "1600x900",
            "1920x1080", "2560x1440", "3840x2160"
        };
        foreach (var c in common)
            if (!labels.Contains(c)) labels.Add(c);

        // Aggiungi quelle del sistema
        resolutions = Screen.resolutions;
        for (int i = 0; i < resolutions.Length; i++)
        {
            string l = $"{resolutions[i].width}x{resolutions[i].height}";
            if (!labels.Contains(l)) labels.Add(l);
        }

        // Ordina per larghezza
        labels.Sort((a, b) => {
            int wa = int.Parse(a.Split('x')[0]);
            int wb = int.Parse(b.Split('x')[0]);
            return wa.CompareTo(wb);
        });

        resLabels = labels.ToArray();

        // Trova indice corrente
        string current = $"{Screen.width}x{Screen.height}";
        resIndex = System.Array.IndexOf(resLabels, current);
        if (resIndex < 0)
        {
            // Trova la più vicina
            resIndex = resLabels.Length - 1;
            for (int i = 0; i < resLabels.Length; i++)
            {
                int w = int.Parse(resLabels[i].Split('x')[0]);
                if (w >= Screen.width) { resIndex = i; break; }
            }
        }
    }

    // ================================================================
    //  GUI
    // ================================================================

    void OnGUI()
    {
        // HUD always visible
        DrawHUD();

        if (showUI)
            DrawSettingsMenu();
    }

    void DrawHUD()
    {
        if (cachedHudStyle == null)
            cachedHudStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
        GUIStyle style = cachedHudStyle;

        string modeStr = mode == Mode.Flythrough ? "FLYTHROUGH" : "VOLO LIBERO";
        string vpName = mode == Mode.Flythrough && currentVP < viewpoints.Count
            ? viewpoints[currentVP].name : "";

        string info = mode == Mode.Flythrough
            ? $"{modeStr}: {vpName} | Spazio=prossimo | F2=volo libero | F1=impostazioni"
            : $"{modeStr} | WASD+Mouse | Shift=veloce | F2=flythrough | F1=impostazioni";

        // Shadow + text
        GUI.color = new Color(0, 0, 0, 0.7f);
        GUI.Label(new Rect(11, Screen.height - 34, 900, 30), info, style);
        GUI.color = Color.white;
        GUI.Label(new Rect(10, Screen.height - 35, 900, 30), info, style);

        // Title
        if (mode == Mode.Flythrough && !string.IsNullOrEmpty(vpName))
        {
            if (cachedTitleStyle == null)
                cachedTitleStyle = new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter };
            GUIStyle titleStyle = cachedTitleStyle;

            float alpha = Mathf.Clamp01(1f - (vpTimer - 3f) / 2f) * Mathf.Clamp01(vpTimer / 1f);
            GUI.color = new Color(0, 0, 0, alpha * 0.6f);
            GUI.Label(new Rect(1, 41, Screen.width, 40), vpName, titleStyle);
            GUI.color = new Color(1, 1, 1, alpha);
            GUI.Label(new Rect(0, 40, Screen.width, 40), vpName, titleStyle);
            GUI.color = Color.white;
        }

        // FPS
        if (cachedFpsStyle == null)
            cachedFpsStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
        GUIStyle fpsStyle = cachedFpsStyle;
        float fps = 1f / Time.unscaledDeltaTime;
        GUI.color = fps < 30 ? Color.red : fps < 60 ? Color.yellow : Color.green;
        GUI.Label(new Rect(Screen.width - 80, 10, 70, 20), $"{fps:F0} FPS", fpsStyle);
        GUI.color = Color.white;
    }

    void DrawSettingsMenu()
    {
        // Forza il cursore visibile e sbloccato per interazione GUI
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        float menuW = 420, menuH = 400;
        float mx = (Screen.width - menuW) * 0.5f;
        float my = (Screen.height - menuH) * 0.5f;

        // Sfondo semi-trasparente
        GUI.Box(new Rect(mx - 5, my - 5, menuW + 10, menuH + 10), "");
        GUI.Box(new Rect(mx, my, menuW, menuH), "");

        float y = my + 10;
        float lw = 130, cw = 220, pad = 15;

        // Titolo
        GUI.Label(new Rect(mx + pad, y, menuW - pad * 2, 25), "<b>IMPOSTAZIONI VIDEO</b>");
        y += 30;

        // Quality
        string[] qualityNames = QualitySettings.names;
        GUI.Label(new Rect(mx + pad, y, lw, 25), "Qualità:");
        if (GUI.Button(new Rect(mx + lw, y, cw, 28), qualityNames[qualityIndex]))
        {
            qualityIndex = (qualityIndex + 1) % qualityNames.Length;
            QualitySettings.SetQualityLevel(qualityIndex, true);
        }
        y += 38;

        // Resolution — frecce < > per navigare
        GUI.Label(new Rect(mx + pad, y, lw, 25), "Risoluzione:");
        if (resLabels.Length > 0)
        {
            if (GUI.Button(new Rect(mx + lw, y, 35, 28), "<"))
            {
                resIndex = (resIndex - 1 + resLabels.Length) % resLabels.Length;
                ApplyResolution();
            }
            GUI.Label(new Rect(mx + lw + 40, y + 4, cw - 80, 25), resLabels[resIndex]);
            if (GUI.Button(new Rect(mx + lw + cw - 35, y, 35, 28), ">"))
            {
                resIndex = (resIndex + 1) % resLabels.Length;
                ApplyResolution();
            }
        }
        y += 38;

        // Fullscreen
        GUI.Label(new Rect(mx + pad, y, lw, 25), "Schermo intero:");
        if (GUI.Button(new Rect(mx + lw, y, cw, 28), fullscreen ? "SI" : "NO"))
        {
            fullscreen = !fullscreen;
            Screen.fullScreen = fullscreen;
        }
        y += 38;

        // Shadow distance — bottoni step
        GUI.Label(new Rect(mx + pad, y, lw, 25), "Ombre:");
        float shadowDist = QualitySettings.shadowDistance;
        if (GUI.Button(new Rect(mx + lw, y, 35, 28), "-"))
            QualitySettings.shadowDistance = Mathf.Max(50, shadowDist - 100);
        GUI.Label(new Rect(mx + lw + 40, y + 4, 80, 25), $"{shadowDist:F0}m");
        if (GUI.Button(new Rect(mx + lw + 125, y, 35, 28), "+"))
            QualitySettings.shadowDistance = Mathf.Min(2000, shadowDist + 100);
        y += 38;

        // Fog distance — bottoni step
        GUI.Label(new Rect(mx + pad, y, lw, 25), "Visibilità:");
        float fogEnd = RenderSettings.fogEndDistance;
        if (GUI.Button(new Rect(mx + lw, y, 35, 28), "-"))
            RenderSettings.fogEndDistance = Mathf.Max(1000, fogEnd - 1000);
        GUI.Label(new Rect(mx + lw + 40, y + 4, 80, 25), $"{fogEnd / 1000f:F1}km");
        if (GUI.Button(new Rect(mx + lw + 125, y, 35, 28), "+"))
            RenderSettings.fogEndDistance = Mathf.Min(25000, fogEnd + 1000);
        y += 38;

        // VSync
        GUI.Label(new Rect(mx + pad, y, lw, 25), "VSync:");
        if (GUI.Button(new Rect(mx + lw, y, cw, 28), QualitySettings.vSyncCount > 0 ? "ON" : "OFF"))
            QualitySettings.vSyncCount = QualitySettings.vSyncCount > 0 ? 0 : 1;
        y += 45;

        // Close
        if (GUI.Button(new Rect(mx + menuW * 0.5f - 60, y, 120, 35), "CHIUDI (F1)"))
        {
            showUI = false;
            LockCursor(true);
        }
    }

    void ApplyResolution()
    {
        if (resIndex < 0 || resIndex >= resLabels.Length) return;
        string[] parts = resLabels[resIndex].Split('x');
        if (parts.Length == 2)
        {
            int w = int.Parse(parts[0]);
            int h = int.Parse(parts[1]);
            Screen.SetResolution(w, h, fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed);
            Debug.Log($"Risoluzione: {w}x{h} fullscreen={fullscreen}");
        }
    }
}
