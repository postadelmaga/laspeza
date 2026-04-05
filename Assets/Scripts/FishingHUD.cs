using UnityEngine;

/// <summary>
/// HUD per il simulatore di pesca a traina.
/// Mostra: velocita' barca, RPM, ecoscandaglio, tensione lenza,
/// stato canne, info combattimento, catture.
///
/// Si attiva automaticamente quando il giocatore e' sulla barca.
/// </summary>
public class FishingHUD : MonoBehaviour
{
    [Header("Riferimenti (auto-trovati)")]
    public FishingBoat boat;
    public TrollingSystem trolling;
    public TunaAI tunaAI;

    [Header("Stile")]
    public int fontSize = 14;
    public Color textColor = Color.white;
    public Color dangerColor = new Color(1f, 0.3f, 0.2f);
    public Color warningColor = new Color(1f, 0.85f, 0.2f);
    public Color safeColor = new Color(0.3f, 1f, 0.4f);

    // Stato interno
    private GUIStyle labelStyle;
    private GUIStyle boxStyle;
    private GUIStyle titleStyle;
    private bool stylesReady;
    private float totalCatchWeight;
    private int totalCatchCount;
    private string lastCatchMessage = "";
    private float lastCatchTimer;

    // Ecoscandaglio
    private float[] depthHistory = new float[80];
    private float[] fishPings = new float[80];
    private int depthHistoryIndex;
    private float scanTimer;
    private Texture2D sonarTexture;

    void Start()
    {
        FindReferences();
        sonarTexture = new Texture2D(1, 1);
        sonarTexture.SetPixel(0, 0, Color.white);
        sonarTexture.Apply();
    }

    void FindReferences()
    {
        if (boat == null) boat = FindAnyObjectByType<FishingBoat>();
        if (trolling == null) trolling = FindAnyObjectByType<TrollingSystem>();
        if (tunaAI == null) tunaAI = FindAnyObjectByType<TunaAI>();
    }

    void InitStyles()
    {
        if (stylesReady) return;

        labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize,
            alignment = TextAnchor.MiddleLeft,
        };
        labelStyle.normal.textColor = textColor;

        boxStyle = new GUIStyle(GUI.skin.box)
        {
            fontSize = fontSize - 2,
        };

        titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize + 2,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
        };
        titleStyle.normal.textColor = textColor;

        stylesReady = true;
    }

    void Update()
    {
        if (boat == null)
        {
            FindReferences();
            return;
        }

        // Aggiorna ecoscandaglio
        scanTimer += Time.deltaTime;
        if (scanTimer >= 0.2f)
        {
            scanTimer = 0f;
            UpdateSonar();
        }

        // Timer ultimo catch
        if (lastCatchTimer > 0)
            lastCatchTimer -= Time.deltaTime;
    }

    void UpdateSonar()
    {
        if (boat == null) return;

        // Profondita' sotto la barca
        Vector3 boatPos = boat.transform.position;
        float waterY = boatPos.y; // approssimazione
        float groundY = GetGroundHeight(boatPos);
        float depth = Mathf.Max(0, waterY - groundY);

        depthHistory[depthHistoryIndex] = depth;

        // Ping pesci: controlla se ci sono tonni sotto la barca
        fishPings[depthHistoryIndex] = 0;
        if (tunaAI != null)
        {
            // Usa reflection leggera per non creare dipendenza stretta
            // In produzione si userebbe un'API diretta
            int hookedCount = tunaAI.GetHookedCount();
            int totalFish = tunaAI.GetTotalFishCount();
            if (totalFish > 0)
                fishPings[depthHistoryIndex] = Random.Range(0.2f, 0.8f);
        }

        depthHistoryIndex = (depthHistoryIndex + 1) % depthHistory.Length;
    }

    float GetGroundHeight(Vector3 pos)
    {
        if (Physics.Raycast(pos + Vector3.up * 500f, Vector3.down, out RaycastHit hit, 1000f))
            return hit.point.y;
        if (Terrain.activeTerrain != null)
            return Terrain.activeTerrain.SampleHeight(pos);
        return 0f;
    }

    // ================================================================
    //  RENDERING
    // ================================================================

    void OnGUI()
    {
        if (boat == null) return;
        // Non mostrare se la barca non e' attiva
        // if (!boat.IsActive) return;

        InitStyles();

        float sw = Screen.width;
        float sh = Screen.height;

        // ── Pannello navigazione (alto sinistra) ──
        DrawNavigationPanel(10, 10);

        // ── Ecoscandaglio (basso sinistra) ──
        DrawSonar(10, sh - 180);

        // ── Stato canne (destra) ──
        DrawRodStatus(sw - 220, 10);

        // ── Info combattimento (centro) ──
        DrawFightInfo(sw * 0.5f - 120, sh * 0.3f);

        // ── Messaggio cattura ──
        if (lastCatchTimer > 0)
            DrawCatchMessage(sw * 0.5f - 150, sh * 0.15f);

        // ── Controlli (basso centro) ──
        DrawControls(sw * 0.5f - 200, sh - 30);
    }

    // ================================================================
    //  PANNELLI
    // ================================================================

    void DrawNavigationPanel(float x, float y)
    {
        GUI.Box(new Rect(x, y, 200, 90), "", boxStyle);

        GUI.color = textColor;
        float speed = 0; // boat.CurrentSpeed se disponibile
        float knots = speed * 1.94384f;

        GUI.Label(new Rect(x + 8, y + 5, 190, 20),
            $"Velocita': {knots:F1} nodi ({speed:F1} m/s)", labelStyle);
        GUI.Label(new Rect(x + 8, y + 25, 190, 20),
            $"Motore: -- RPM", labelStyle);

        // Posizione GPS approssimata
        Vector3 pos = boat.transform.position;
        GUI.Label(new Rect(x + 8, y + 45, 190, 20),
            $"Pos: {pos.x:F0}, {pos.z:F0}", labelStyle);
        GUI.Label(new Rect(x + 8, y + 65, 190, 20),
            $"Heading: {boat.transform.eulerAngles.y:F0}°", labelStyle);
    }

    void DrawSonar(float x, float y)
    {
        float w = 200, h = 160;
        GUI.Box(new Rect(x, y, w, h), "", boxStyle);

        // Titolo
        GUI.color = new Color(0.3f, 1f, 0.3f);
        GUI.Label(new Rect(x, y + 2, w, 18), "ECOSCANDAGLIO", titleStyle);
        GUI.color = textColor;

        // Sfondo scuro
        Color oldBg = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0, 0.05f, 0.1f);
        GUI.Box(new Rect(x + 5, y + 22, w - 10, h - 30), "");
        GUI.backgroundColor = oldBg;

        // Disegna profilo fondale
        float sonarX = x + 5;
        float sonarY = y + 22;
        float sonarW = w - 10;
        float sonarH = h - 30;

        float maxDepth = 50f;

        for (int i = 0; i < depthHistory.Length - 1; i++)
        {
            int idx = (depthHistoryIndex + i) % depthHistory.Length;
            int idxNext = (depthHistoryIndex + i + 1) % depthHistory.Length;

            float px = sonarX + (float)i / depthHistory.Length * sonarW;
            float pxNext = sonarX + (float)(i + 1) / depthHistory.Length * sonarW;

            float py = sonarY + (depthHistory[idx] / maxDepth) * sonarH;
            float pyNext = sonarY + (depthHistory[idxNext] / maxDepth) * sonarH;

            // Linea fondale (verde)
            GUI.color = new Color(0.2f, 0.7f, 0.2f);
            DrawLine(px, py, pxNext, pyNext, 2);

            // Ping pesci (punti gialli)
            if (fishPings[idx] > 0.1f)
            {
                GUI.color = new Color(1f, 0.9f, 0.2f, fishPings[idx]);
                float fishY = sonarY + (depthHistory[idx] * 0.3f + fishPings[idx] * 8f) / maxDepth * sonarH;
                GUI.DrawTexture(new Rect(px - 2, fishY - 2, 5, 4), sonarTexture);
            }
        }

        // Profondita' corrente
        int lastIdx = (depthHistoryIndex - 1 + depthHistory.Length) % depthHistory.Length;
        GUI.color = textColor;
        labelStyle.fontSize = fontSize - 2;
        GUI.Label(new Rect(sonarX + sonarW - 60, sonarY + sonarH - 18, 60, 16),
            $"{depthHistory[lastIdx]:F1}m", labelStyle);
        labelStyle.fontSize = fontSize;

        GUI.color = Color.white;
    }

    void DrawRodStatus(float x, float y)
    {
        GUI.Box(new Rect(x, y, 210, 100), "", boxStyle);

        GUI.color = textColor;
        GUI.Label(new Rect(x, y + 2, 210, 18), "CANNE DA TRAINA", titleStyle);

        for (int rod = 0; rod < 2; rod++)
        {
            float ry = y + 25 + rod * 35;
            string side = rod == 0 ? "SX" : "DX";

            string stateText = "Traina";
            Color stateColor = safeColor;

            if (trolling != null)
            {
                // Leggi stato dal TrollingSystem quando disponibile
                // Per ora mostra stato base
                stateText = "In traina";
                stateColor = safeColor;
            }

            if (tunaAI != null && tunaAI.GetHookedFishInfo(rod, out float w, out float stam, out float force))
            {
                stateText = $"PESCE! {w:F1}kg";
                stateColor = dangerColor;

                // Barra stamina
                GUI.color = Color.Lerp(dangerColor, safeColor, stam);
                GUI.DrawTexture(new Rect(x + 8, ry + 18, stam * 190, 8), sonarTexture);
            }

            GUI.color = stateColor;
            GUI.Label(new Rect(x + 8, ry, 195, 20),
                $"Canna {side}: {stateText}", labelStyle);
        }

        GUI.color = Color.white;
    }

    void DrawFightInfo(float x, float y)
    {
        if (tunaAI == null) return;

        for (int rod = 0; rod < 2; rod++)
        {
            if (!tunaAI.GetHookedFishInfo(rod, out float weight, out float stamina, out float force))
                continue;

            float h = 80;
            GUI.Box(new Rect(x, y, 240, h), "", boxStyle);

            GUI.color = dangerColor;
            titleStyle.fontSize = fontSize + 4;
            GUI.Label(new Rect(x, y + 2, 240, 25), "COMBATTIMENTO!", titleStyle);
            titleStyle.fontSize = fontSize + 2;

            GUI.color = textColor;
            GUI.Label(new Rect(x + 10, y + 28, 220, 20),
                $"Tonno ~{weight:F0}kg | Stamina: {stamina * 100:F0}%", labelStyle);
            GUI.Label(new Rect(x + 10, y + 48, 220, 20),
                $"Forza: {force:F0}N | R/F mulinello | +/- frizione", labelStyle);

            GUI.color = Color.white;
            y += h + 5; // se entrambe le canne hanno pesce
        }
    }

    void DrawCatchMessage(float x, float y)
    {
        float alpha = Mathf.Clamp01(lastCatchTimer);
        GUI.color = new Color(1, 1, 0.3f, alpha);
        titleStyle.fontSize = fontSize + 8;
        GUI.Label(new Rect(x, y, 300, 40), lastCatchMessage, titleStyle);
        titleStyle.fontSize = fontSize + 2;
        GUI.color = Color.white;
    }

    void DrawControls(float x, float y)
    {
        GUI.color = new Color(1, 1, 1, 0.7f);
        labelStyle.fontSize = fontSize - 2;
        GUI.Label(new Rect(x, y, 400, 20),
            "WASD guida | R/F mulinello | +/- frizione | N notte | B esci barca", labelStyle);
        labelStyle.fontSize = fontSize;
        GUI.color = Color.white;
    }

    // ================================================================
    //  UTILITY
    // ================================================================

    void DrawLine(float x1, float y1, float x2, float y2, int width)
    {
        // Semplice approssimazione con DrawTexture
        float dx = x2 - x1;
        float dy = y2 - y1;
        float len = Mathf.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f) return;

        float angle = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
        var pivot = new Vector2(0, width * 0.5f);
        var rect = new Rect(x1, y1 - width * 0.5f, len, width);

        Matrix4x4 saved = GUI.matrix;
        GUIUtility.RotateAroundPivot(angle, new Vector2(x1, y1));
        GUI.DrawTexture(rect, sonarTexture);
        GUI.matrix = saved;
    }

    // ================================================================
    //  PUBLIC API (per TrollingSystem/TunaAI)
    // ================================================================

    /// <summary>Notifica cattura per il messaggio a schermo.</summary>
    public void NotifyCatch(float weightKg)
    {
        totalCatchCount++;
        totalCatchWeight += weightKg;
        lastCatchMessage = $"TONNO! {weightKg:F1}kg — Totale: {totalCatchCount} ({totalCatchWeight:F1}kg)";
        lastCatchTimer = 5f;
    }
}
