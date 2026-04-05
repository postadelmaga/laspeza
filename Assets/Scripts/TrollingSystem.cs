using UnityEngine;
using System.Collections;

/// <summary>
/// Sistema di pesca a traina (trolling) per il Golfo di La Spezia.
/// Simula due canne da pesca (canna) montate a poppa della barca,
/// con lenza (line), esca/rapala (lure), mulinello (reel) e combattimento (fight).
///
/// Si attacca al GameObject della barca da pesca.
///
/// Controlli durante il combattimento:
///   Scroll mouse / R   = mulinello: recupera lenza
///   Scroll mouse / F   = mulinello: lascia filare lenza
///   +/-                = regola frizione (drag)
/// </summary>
public class TrollingSystem : MonoBehaviour
{
    // ── Stato della canna (state machine) ──────────────────────────────
    public enum RodState
    {
        Trolling,       // Traina: esca in acqua, in attesa
        Bite,           // Abboccata: il pesce ha preso l'esca (2s finestra)
        Fighting,       // Combattimento: il pescatore combatte il pesce
        Landing,        // Salpata: pesce vicino, cattura automatica
        Empty           // Vuota: canna senza pesce, reset dopo cattura
    }

    // ── Dati per singola canna ─────────────────────────────────────────
    [System.Serializable]
    public class RodData
    {
        [Header("Stato")]
        public RodState state = RodState.Trolling;
        public float currentTension;        // tensione lenza 0-100%
        public bool isBiting;               // abboccata in corso
        public float lineLength = 40f;      // lenza fuori (m)
        public float fishStrength;          // forza del pesce 0-100

        [HideInInspector] public GameObject rodObject;
        [HideInInspector] public GameObject tipRing;
        [HideInInspector] public GameObject lureObject;
        [HideInInspector] public LineRenderer lineRenderer;
        [HideInInspector] public Transform rodTip;
        [HideInInspector] public ParticleSystem splashParticles;

        // Combattimento (combattimento)
        [HideInInspector] public GameObject hookedFish;
        [HideInInspector] public float biteTimer;
        [HideInInspector] public float breakTimer;      // tempo a tensione > 90%
        [HideInInspector] public float slackTimer;      // tempo a tensione < 5%
        [HideInInspector] public float landingTimer;
        [HideInInspector] public float emptyTimer;

        // Piegatura canna (flessione)
        [HideInInspector] public float currentBend;
        [HideInInspector] public Quaternion baseTipRotation;
    }

    // ── Parametri pubblici ─────────────────────────────────────────────
    [Header("Canne da pesca (2 canne a poppa)")]
    public RodData[] rods = new RodData[2];

    [Header("Frizione / Drag")]
    [Range(1f, 10f)]
    public float dragSetting = 5f;

    [Header("Geometria Traina")]
    public float rodLength = 2.5f;              // lunghezza canna (m)
    public float rodRadius = 0.012f;            // raggio canna
    public float rodOutwardAngle = 45f;         // angolo verso l'esterno
    public float rodBackAngle = 30f;            // angolo all'indietro
    public float defaultLineLength = 40f;       // lenza di default (m)
    public float waterEntryDistance = 15f;       // punto ingresso acqua dietro barca (m)
    public int linePoints = 7;                  // punti LineRenderer per catenaria

    [Header("Esca / Rapala")]
    public float lureLength = 0.08f;            // lunghezza rapala (m)
    public float lureRadius = 0.015f;           // raggio rapala

    [Header("Fisica Lenza")]
    public float baseTensionMin = 10f;          // tensione a riposo minima %
    public float baseTensionMax = 20f;          // tensione a riposo massima %
    public float catenarySag = 2.5f;            // abbassamento catenaria (m)

    [Header("Profondita' Esca")]
    public float minLureDepth = 1f;             // profondita' minima (m) ad alta velocita'
    public float maxLureDepth = 5f;             // profondita' massima (m) a bassa velocita'
    public float speedForMinDepth = 8f;         // velocita' barca (m/s) per profondita' minima
    public float speedForMaxDepth = 2f;         // velocita' barca (m/s) per profondita' massima

    [Header("Combattimento")]
    public float reelSpeed = 3f;                // velocita' recupero mulinello (m/s)
    public float lineBreakTime = 3f;            // secondi a >90% prima di rottura
    public float hookSpitTime = 2f;             // secondi a <5% prima che sputi l'amo
    public float landingDistance = 3f;           // distanza salpata (m)
    public float landingTime = 2f;              // tempo per completare salpata (s)
    public float emptyResetTime = 3f;           // tempo reset dopo cattura (s)
    public float biteWindowDuration = 2f;       // durata finestra abboccata (s)

    [Header("Velocita' Barca")]
    public float boatSpeed;                     // velocita' corrente, aggiornata ogni frame

    // ── Colori lenza per feedback visivo ───────────────────────────────
    private Color lineColorSafe = new Color(0.1f, 0.8f, 0.2f, 0.7f);      // verde = sicuro
    private Color lineColorCaution = new Color(0.9f, 0.85f, 0.1f, 0.8f);  // giallo = attenzione
    private Color lineColorDanger = new Color(0.95f, 0.15f, 0.1f, 0.9f);  // rosso = pericolo

    // Colori esche: argento/blu (babordo), rosso/oro (tribordo)
    private Color[] lureColorPrimary = { new Color(0.75f, 0.8f, 0.85f), new Color(0.85f, 0.15f, 0.1f) };
    private Color[] lureColorSecondary = { new Color(0.3f, 0.45f, 0.75f), new Color(0.9f, 0.75f, 0.2f) };

    private Vector3 previousPosition;
    private Material rodMaterial;
    private Material[] lureMaterials;

    // ================================================================
    //  INIZIALIZZAZIONE
    // ================================================================

    void Awake()
    {
        if (rods == null || rods.Length < 2)
            rods = new RodData[] { new RodData(), new RodData() };

        previousPosition = transform.position;
        CreateMaterials();
    }

    void Start()
    {
        // Crea le due canne: indice 0 = babordo (port/sinistra), indice 1 = tribordo (starboard/destra)
        for (int i = 0; i < 2; i++)
        {
            CreateRod(i);
            CreateLure(i);
            CreateLine(i);
            CreateSplashParticles(i);

            rods[i].lineLength = defaultLineLength;
            rods[i].state = RodState.Trolling;
        }
    }

    // ── Materiali base ─────────────────────────────────────────────────
    void CreateMaterials()
    {
        // Materiale canna (fibra di carbonio scura)
        rodMaterial = new Material(Shader.Find("Standard"));
        rodMaterial.color = new Color(0.15f, 0.15f, 0.18f);
        rodMaterial.SetFloat("_Metallic", 0.6f);
        rodMaterial.SetFloat("_Glossiness", 0.7f);

        // Materiali esche (rapala)
        lureMaterials = new Material[2];
        for (int i = 0; i < 2; i++)
        {
            lureMaterials[i] = new Material(Shader.Find("Standard"));
            lureMaterials[i].color = lureColorPrimary[i];
            lureMaterials[i].SetFloat("_Metallic", 0.8f);
            lureMaterials[i].SetFloat("_Glossiness", 0.85f);
        }
    }

    // ── Creazione canna procedurale ────────────────────────────────────
    // Canna = cilindro sottile (mesh procedurale) con anello di punta (tip ring)
    void CreateRod(int index)
    {
        // Direzione: babordo = -1, tribordo = +1
        float side = (index == 0) ? -1f : 1f;

        GameObject rod = new GameObject($"Canna_{(index == 0 ? "Babordo" : "Tribordo")}");
        rod.transform.SetParent(transform);

        // Posizione: poppa della barca, leggermente ai lati
        rod.transform.localPosition = new Vector3(side * 1.2f, 1.0f, -2.5f);

        // Rotazione: angolata verso l'esterno e all'indietro
        rod.transform.localRotation = Quaternion.Euler(-rodBackAngle, side * rodOutwardAngle, 0f);

        // Mesh procedurale: cilindro rastremato (piu' sottile in punta)
        MeshFilter mf = rod.AddComponent<MeshFilter>();
        MeshRenderer mr = rod.AddComponent<MeshRenderer>();
        mr.material = rodMaterial;
        mf.mesh = CreateTaperedCylinder(rodLength, rodRadius, rodRadius * 0.3f, 8, 6);

        // Anello di punta (tip ring) — piccolo toro approssimato con un anello
        GameObject tipRing = new GameObject("AnelloPunta");
        tipRing.transform.SetParent(rod.transform);
        tipRing.transform.localPosition = new Vector3(0f, rodLength, 0f);
        tipRing.transform.localRotation = Quaternion.identity;

        MeshFilter tipMf = tipRing.AddComponent<MeshFilter>();
        MeshRenderer tipMr = tipRing.AddComponent<MeshRenderer>();
        Material tipMat = new Material(Shader.Find("Standard"));
        tipMat.color = new Color(0.6f, 0.6f, 0.65f);
        tipMat.SetFloat("_Metallic", 0.9f);
        tipMr.material = tipMat;
        tipMf.mesh = CreateRing(0.02f, 0.004f, 12);

        rods[index].rodObject = rod;
        rods[index].tipRing = tipRing;
        rods[index].rodTip = tipRing.transform;
        rods[index].baseTipRotation = rod.transform.localRotation;
    }

    // ── Creazione esca/rapala ──────────────────────────────────────────
    // Esca = ellissoide allungato (rapala) con colori distintivi
    void CreateLure(int index)
    {
        GameObject lure = new GameObject($"Rapala_{index}");

        // Mesh: ellissoide allungato (sfera schiacciata/allungata)
        MeshFilter mf = lure.AddComponent<MeshFilter>();
        MeshRenderer mr = lure.AddComponent<MeshRenderer>();
        mr.material = lureMaterials[index];

        // Uso una sfera Unity di default scalata per ottenere l'ellissoide
        // Creiamo mesh procedurale ellissoide per maggior controllo
        mf.mesh = CreateEllipsoidMesh(lureLength, lureRadius, lureRadius * 0.8f, 8, 6);

        // Aggiungi dettaglio colore secondario (striscia laterale)
        GameObject stripe = new GameObject("Striscia");
        stripe.transform.SetParent(lure.transform);
        stripe.transform.localPosition = Vector3.zero;
        stripe.transform.localScale = new Vector3(1.02f, 0.4f, 1.02f);
        MeshFilter sf = stripe.AddComponent<MeshFilter>();
        MeshRenderer sr = stripe.AddComponent<MeshRenderer>();
        Material stripeMat = new Material(Shader.Find("Standard"));
        stripeMat.color = lureColorSecondary[index];
        stripeMat.SetFloat("_Metallic", 0.7f);
        sr.material = stripeMat;
        sf.mesh = CreateEllipsoidMesh(lureLength * 0.95f, lureRadius * 0.6f, lureRadius * 0.5f, 8, 6);

        rods[index].lureObject = lure;
    }

    // ── LineRenderer per la lenza ──────────────────────────────────────
    void CreateLine(int index)
    {
        GameObject lineObj = new GameObject($"Lenza_{index}");
        lineObj.transform.SetParent(transform);
        lineObj.transform.localPosition = Vector3.zero;

        LineRenderer lr = lineObj.AddComponent<LineRenderer>();
        lr.positionCount = linePoints;
        lr.startWidth = 0.004f;     // lenza sottile
        lr.endWidth = 0.003f;
        lr.numCapVertices = 2;
        lr.numCornerVertices = 2;

        // Materiale semi-trasparente
        Material lineMat = new Material(Shader.Find("Sprites/Default"));
        lineMat.color = lineColorSafe;
        lr.material = lineMat;
        lr.useWorldSpace = true;

        rods[index].lineRenderer = lr;
    }

    // ── Particelle splash per ingresso/uscita acqua dell'esca ──────────
    void CreateSplashParticles(int index)
    {
        GameObject splashObj = new GameObject($"Splash_{index}");
        splashObj.transform.SetParent(transform);

        ParticleSystem ps = splashObj.AddComponent<ParticleSystem>();
        var main = ps.main;
        main.startLifetime = 0.6f;
        main.startSpeed = 2f;
        main.startSize = 0.08f;
        main.maxParticles = 30;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startColor = new Color(0.7f, 0.85f, 0.95f, 0.6f);
        main.loop = false;
        main.playOnAwake = false;

        var emission = ps.emission;
        emission.rateOverTime = 0;
        emission.SetBursts(new ParticleSystem.Burst[] {
            new ParticleSystem.Burst(0f, 15)
        });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Hemisphere;
        shape.radius = 0.15f;

        // Materiale particelle
        ParticleSystemRenderer psr = splashObj.GetComponent<ParticleSystemRenderer>();
        Material splashMat = new Material(Shader.Find("Particles/Standard Unlit"));
        splashMat.color = new Color(0.8f, 0.9f, 1f, 0.5f);
        psr.material = splashMat;

        ps.Stop();

        rods[index].splashParticles = ps;
    }

    // ================================================================
    //  UPDATE PRINCIPALE
    // ================================================================

    void Update()
    {
        UpdateBoatSpeed();
        HandleDragInput();

        for (int i = 0; i < 2; i++)
        {
            switch (rods[i].state)
            {
                case RodState.Trolling:
                    UpdateTrolling(i);
                    break;
                case RodState.Bite:
                    UpdateBite(i);
                    break;
                case RodState.Fighting:
                    UpdateFighting(i);
                    HandleReelInput(i);
                    break;
                case RodState.Landing:
                    UpdateLanding(i);
                    break;
                case RodState.Empty:
                    UpdateEmpty(i);
                    break;
            }

            UpdateLineVisual(i);
            UpdateRodBend(i);
            UpdateLurePosition(i);
        }
    }

    // ── Calcolo velocita' barca ────────────────────────────────────────
    void UpdateBoatSpeed()
    {
        Vector3 currentPos = transform.position;
        boatSpeed = (currentPos - previousPosition).magnitude / Time.deltaTime;
        previousPosition = currentPos;
    }

    // ── Input frizione (+/- keys) ──────────────────────────────────────
    // Frizione (drag): regola la resistenza del mulinello
    void HandleDragInput()
    {
        if (Input.GetKeyDown(KeyCode.Plus) || Input.GetKeyDown(KeyCode.KeypadPlus) ||
            Input.GetKeyDown(KeyCode.Equals))
        {
            dragSetting = Mathf.Clamp(dragSetting + 1f, 1f, 10f);
        }
        if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus))
        {
            dragSetting = Mathf.Clamp(dragSetting - 1f, 1f, 10f);
        }
    }

    // ── Input mulinello (reel) durante combattimento ───────────────────
    // Mulinello: R = recupera, F = fila, scroll = recupera/fila
    void HandleReelInput(int index)
    {
        RodData rod = rods[index];
        float reelAmount = 0f;

        // Scroll del mouse: recupero/rilascio lenza
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (scroll != 0f)
        {
            reelAmount = -scroll * reelSpeed * 10f * Time.deltaTime;
        }

        // Tasti R/F: recupero/rilascio mulinello
        if (Input.GetKey(KeyCode.R))
        {
            reelAmount -= reelSpeed * (dragSetting / 5f) * Time.deltaTime;
        }
        if (Input.GetKey(KeyCode.F))
        {
            reelAmount += reelSpeed * Time.deltaTime;
        }

        // La forza del pesce tira fuori lenza (proporzionale alla forza e inversamente alla frizione)
        float fishPull = rod.fishStrength * 0.02f * (11f - dragSetting) / 10f * Time.deltaTime;
        reelAmount += fishPull;

        rod.lineLength = Mathf.Clamp(rod.lineLength + reelAmount, 0f, 150f);
    }

    // ================================================================
    //  STATI DELLA CANNA
    // ================================================================

    // ── Traina: esca in acqua, in attesa di abboccata ──────────────────
    void UpdateTrolling(int index)
    {
        RodData rod = rods[index];

        // Tensione base dalla resistenza dell'acqua (trascino)
        float speedFactor = Mathf.Clamp01(boatSpeed / speedForMinDepth);
        rod.currentTension = Mathf.Lerp(baseTensionMin, baseTensionMax, speedFactor);
        rod.isBiting = false;
    }

    // ── Abboccata: il pesce ha morso, finestra di 2 secondi ────────────
    // Ferrata automatica: dopo la finestra si passa a combattimento
    void UpdateBite(int index)
    {
        RodData rod = rods[index];
        rod.isBiting = true;
        rod.biteTimer += Time.deltaTime;

        // Tensione che sale rapidamente (colpo dell'abboccata)
        rod.currentTension = Mathf.Lerp(rod.fishStrength * 0.5f, rod.fishStrength, rod.biteTimer / biteWindowDuration);

        // Dopo 2 secondi: ferrata automatica → combattimento
        if (rod.biteTimer >= biteWindowDuration)
        {
            rod.state = RodState.Fighting;
            rod.biteTimer = 0f;
            rod.breakTimer = 0f;
            rod.slackTimer = 0f;
        }
    }

    // ── Combattimento: il pescatore lotta con il pesce ─────────────────
    void UpdateFighting(int index)
    {
        RodData rod = rods[index];
        rod.isBiting = false;

        // Tensione dinamica durante il combattimento
        // Base: forza pesce modulata dalla frizione e dalla lenza fuori
        float dragFactor = dragSetting / 10f;
        float lineFactor = Mathf.Clamp01(rod.lineLength / defaultLineLength);

        // Il pesce tira con forza variabile (simulazione semplice con sinusoide)
        float fishPull = rod.fishStrength * (0.5f + 0.5f * Mathf.Sin(Time.time * 1.5f + index * Mathf.PI));

        // Tensione = forza pesce * fattore frizione, ridotta con lenza lunga (ammortizza)
        rod.currentTension = fishPull * dragFactor / Mathf.Max(0.3f, lineFactor);
        rod.currentTension = Mathf.Clamp(rod.currentTension, 0f, 100f);

        // Rottura lenza: tensione > 90% per troppo tempo
        if (rod.currentTension > 90f)
        {
            rod.breakTimer += Time.deltaTime;
            if (rod.breakTimer >= lineBreakTime)
            {
                LineBreak(index);
                return;
            }
        }
        else
        {
            rod.breakTimer = Mathf.Max(0f, rod.breakTimer - Time.deltaTime * 0.5f);
        }

        // Pesce sputa l'amo: tensione troppo bassa per troppo tempo
        if (rod.currentTension < 5f)
        {
            rod.slackTimer += Time.deltaTime;
            if (rod.slackTimer >= hookSpitTime)
            {
                FishEscapes(index);
                return;
            }
        }
        else
        {
            rod.slackTimer = Mathf.Max(0f, rod.slackTimer - Time.deltaTime * 0.5f);
        }

        // Salpata: pesce abbastanza vicino
        if (rod.lineLength < landingDistance)
        {
            rod.state = RodState.Landing;
            rod.landingTimer = 0f;
        }
    }

    // ── Salpata: pesce entro 3m, cattura automatica dopo 2 secondi ─────
    void UpdateLanding(int index)
    {
        RodData rod = rods[index];
        rod.landingTimer += Time.deltaTime;
        rod.currentTension = Mathf.Lerp(rod.currentTension, 20f, Time.deltaTime * 2f);

        if (rod.landingTimer >= landingTime)
        {
            LandFish(index);
        }
    }

    // ── Vuota: canna senza pesce, reset automatico ─────────────────────
    void UpdateEmpty(int index)
    {
        RodData rod = rods[index];
        rod.emptyTimer += Time.deltaTime;
        rod.currentTension = 0f;

        if (rod.emptyTimer >= emptyResetTime)
        {
            // Reset per nuova traina
            rod.state = RodState.Trolling;
            rod.lineLength = defaultLineLength;
            rod.emptyTimer = 0f;
            rod.hookedFish = null;
            rod.isBiting = false;

            // Riattiva esca (rapala)
            if (rod.lureObject != null)
                rod.lureObject.SetActive(true);
        }
    }

    // ================================================================
    //  EVENTI DI FINE COMBATTIMENTO
    // ================================================================

    // Rottura lenza (lenza spezzata!)
    void LineBreak(int index)
    {
        RodData rod = rods[index];
        Debug.Log($"[Traina] Lenza spezzata sulla canna {index}! Tensione troppo alta.");

        if (rod.hookedFish != null)
        {
            // Il pesce scappa con l'esca — disattiva il riferimento
            rod.hookedFish = null;
        }

        rod.state = RodState.Empty;
        rod.emptyTimer = 0f;
        rod.currentTension = 0f;
        rod.isBiting = false;

        // Nascondi esca (persa)
        if (rod.lureObject != null)
            rod.lureObject.SetActive(false);
    }

    // Pesce sputa l'amo (sgancio)
    void FishEscapes(int index)
    {
        RodData rod = rods[index];
        Debug.Log($"[Traina] Pesce sganciato dalla canna {index}! Lenza troppo lenta.");

        rod.hookedFish = null;
        rod.state = RodState.Empty;
        rod.emptyTimer = 0f;
        rod.currentTension = 0f;
        rod.isBiting = false;
    }

    // Pesce salpato con successo (cattura!)
    void LandFish(int index)
    {
        RodData rod = rods[index];
        Debug.Log($"[Traina] Pesce salpato dalla canna {index}! Cattura riuscita!");

        // Il pesce e' a bordo — disattivalo dalla scena
        if (rod.hookedFish != null)
        {
            rod.hookedFish.SetActive(false);
            rod.hookedFish = null;
        }

        rod.state = RodState.Empty;
        rod.emptyTimer = 0f;
        rod.currentTension = 0f;
        rod.isBiting = false;
    }

    // ================================================================
    //  AGGIORNAMENTO VISUALE
    // ================================================================

    // ── Aggiornamento posizione esca ───────────────────────────────────
    void UpdateLurePosition(int index)
    {
        RodData rod = rods[index];
        if (rod.lureObject == null || !rod.lureObject.activeSelf) return;

        Vector3 lurePos = CalculateLurePosition(index);
        rod.lureObject.transform.position = lurePos;

        // Orientamento: esca punta nella direzione della barca
        if (boatSpeed > 0.1f)
        {
            rod.lureObject.transform.rotation = Quaternion.LookRotation(transform.forward, Vector3.up);
        }
    }

    // ── Calcolo posizione esca nel mondo ───────────────────────────────
    // Profondita' esca (rapala): dipende dalla velocita' della barca
    // Piu' veloce = piu' superficiale, piu' lento = piu' profondo
    Vector3 CalculateLurePosition(int index)
    {
        RodData rod = rods[index];
        float side = (index == 0) ? -1f : 1f;

        // Posizione base: dietro la barca alla distanza della lenza
        Vector3 behind = transform.position - transform.forward * rod.lineLength;
        behind += transform.right * side * 5f; // leggero offset laterale

        // Profondita': inversamente proporzionale alla velocita'
        float speedT = Mathf.InverseLerp(speedForMaxDepth, speedForMinDepth, boatSpeed);
        float depth = Mathf.Lerp(maxLureDepth, minLureDepth, speedT);

        behind.y = -depth; // sotto la superficie (assumiamo y=0 = superficie)

        return behind;
    }

    // ── Aggiornamento linea/lenza con catenaria ────────────────────────
    // Lenza: dal tip ring della canna, attraverso l'aria con catenaria,
    // punto di ingresso nell'acqua, fino all'esca sott'acqua
    void UpdateLineVisual(int index)
    {
        RodData rod = rods[index];
        if (rod.lineRenderer == null || rod.rodTip == null) return;

        Vector3 tipPos = rod.rodTip.position;
        Vector3 lurePos = rod.lureObject != null && rod.lureObject.activeSelf
            ? rod.lureObject.transform.position
            : CalculateLurePosition(index);

        // Punto di ingresso nell'acqua (~15m dietro la barca)
        float side = (index == 0) ? -1f : 1f;
        Vector3 waterEntry = transform.position - transform.forward * waterEntryDistance;
        waterEntry += transform.right * side * 3f;
        waterEntry.y = 0f; // superficie dell'acqua

        // Calcolo punti della lenza con approssimazione catenaria
        Vector3[] points = new Vector3[linePoints];

        // Abbassamento catenaria: ridotto quando la lenza e' tesa (combattimento)
        float sagFactor = Mathf.Lerp(catenarySag, 0f, Mathf.Clamp01(rod.currentTension / 60f));

        // Distribuzione punti:
        // 0 = tip ring
        // 1..midIdx = tratto aereo con catenaria
        // midIdx = ingresso acqua
        // midIdx+1..end = tratto subacqueo fino all'esca
        int midIdx = linePoints / 2;

        for (int p = 0; p < linePoints; p++)
        {
            float t;
            if (p <= midIdx)
            {
                // Tratto aereo: dal tip ring al punto di ingresso acqua
                t = (float)p / midIdx;
                Vector3 lerped = Vector3.Lerp(tipPos, waterEntry, t);

                // Catenaria: abbassamento parabolico massimo a meta' del tratto aereo
                float sagAmount = sagFactor * 4f * t * (1f - t);
                lerped.y -= sagAmount;

                points[p] = lerped;
            }
            else
            {
                // Tratto subacqueo: dal punto di ingresso acqua all'esca
                t = (float)(p - midIdx) / (linePoints - 1 - midIdx);
                points[p] = Vector3.Lerp(waterEntry, lurePos, t);
            }
        }

        rod.lineRenderer.positionCount = linePoints;
        rod.lineRenderer.SetPositions(points);

        // Colore lenza basato sulla tensione
        Color lineColor;
        if (rod.currentTension < 30f)
            lineColor = lineColorSafe;
        else if (rod.currentTension < 70f)
            lineColor = Color.Lerp(lineColorSafe, lineColorCaution, (rod.currentTension - 30f) / 40f);
        else
            lineColor = Color.Lerp(lineColorCaution, lineColorDanger, (rod.currentTension - 70f) / 30f);

        rod.lineRenderer.material.color = lineColor;
    }

    // ── Piegatura della canna proporzionale alla tensione ──────────────
    // Flessione: la punta della canna si piega verso il basso
    void UpdateRodBend(int index)
    {
        RodData rod = rods[index];
        if (rod.rodObject == null) return;

        // Angolo di piegatura: 0° a riposo, fino a 45° a tensione massima
        float targetBend = Mathf.Lerp(0f, 45f, rod.currentTension / 100f);
        rod.currentBend = Mathf.Lerp(rod.currentBend, targetBend, Time.deltaTime * 5f);

        // Applica rotazione aggiuntiva sulla punta (asse X locale = piegatura verso il basso)
        rod.rodObject.transform.localRotation = rod.baseTipRotation *
            Quaternion.Euler(rod.currentBend, 0f, 0f);
    }

    // ================================================================
    //  API PUBBLICA PER FISH AI
    // ================================================================

    /// <summary>
    /// Restituisce la posizione nel mondo dell'esca (rapala) per la canna indicata.
    /// Usato dall'AI dei pesci per determinare se un pesce e' vicino all'esca.
    /// </summary>
    public Vector3 GetLureWorldPosition(int rodIndex)
    {
        if (rodIndex < 0 || rodIndex >= 2) return Vector3.zero;
        if (rods[rodIndex].lureObject != null && rods[rodIndex].lureObject.activeSelf)
            return rods[rodIndex].lureObject.transform.position;
        return CalculateLurePosition(rodIndex);
    }

    /// <summary>
    /// Restituisce la profondita' dell'esca (in metri sotto la superficie).
    /// </summary>
    public float GetLureDepth(int rodIndex)
    {
        if (rodIndex < 0 || rodIndex >= 2) return 0f;
        return -GetLureWorldPosition(rodIndex).y;
    }

    /// <summary>
    /// Restituisce la velocita' corrente della barca in m/s.
    /// </summary>
    public float GetBoatSpeed()
    {
        return boatSpeed;
    }

    /// <summary>
    /// Verifica se la canna e' disponibile (in traina, pronta per un'abboccata).
    /// </summary>
    public bool IsRodAvailable(int rodIndex)
    {
        if (rodIndex < 0 || rodIndex >= 2) return false;
        return rods[rodIndex].state == RodState.Trolling;
    }

    /// <summary>
    /// Chiamato dall'AI dei pesci quando un pesce morde l'esca.
    /// Innesca la sequenza abboccata → ferrata → combattimento.
    /// </summary>
    /// <param name="rodIndex">Indice della canna (0 = babordo, 1 = tribordo)</param>
    /// <param name="strength">Forza del pesce (0-100)</param>
    /// <param name="fish">GameObject del pesce agganciato</param>
    public void OnFishBite(int rodIndex, float strength, GameObject fish)
    {
        if (rodIndex < 0 || rodIndex >= 2) return;
        if (rods[rodIndex].state != RodState.Trolling) return;

        RodData rod = rods[rodIndex];
        rod.state = RodState.Bite;
        rod.fishStrength = Mathf.Clamp(strength, 10f, 100f);
        rod.hookedFish = fish;
        rod.biteTimer = 0f;
        rod.isBiting = true;
        rod.currentTension = strength * 0.6f; // colpo iniziale dell'abboccata

        Debug.Log($"[Traina] Abboccata sulla canna {rodIndex}! Forza pesce: {strength:F0}%");

        // Splash all'abboccata
        if (rod.splashParticles != null)
        {
            rod.splashParticles.transform.position = GetLureWorldPosition(rodIndex);
            rod.splashParticles.Play();
        }
    }

    // ================================================================
    //  GENERAZIONE MESH PROCEDURALI
    // ================================================================

    /// <summary>
    /// Crea un cilindro rastremato (canna da pesca): piu' spesso alla base, sottile in punta.
    /// </summary>
    Mesh CreateTaperedCylinder(float height, float radiusBottom, float radiusTop, int segments, int rings)
    {
        Mesh mesh = new Mesh();
        mesh.name = "CannaDaPesca";

        int vertCount = (rings + 1) * (segments + 1);
        Vector3[] vertices = new Vector3[vertCount];
        Vector3[] normals = new Vector3[vertCount];
        Vector2[] uvs = new Vector2[vertCount];
        int[] triangles = new int[rings * segments * 6];

        int vi = 0;
        for (int r = 0; r <= rings; r++)
        {
            float t = (float)r / rings;
            float y = t * height;
            float radius = Mathf.Lerp(radiusBottom, radiusTop, t);

            for (int s = 0; s <= segments; s++)
            {
                float angle = (float)s / segments * Mathf.PI * 2f;
                float x = Mathf.Cos(angle) * radius;
                float z = Mathf.Sin(angle) * radius;

                vertices[vi] = new Vector3(x, y, z);
                normals[vi] = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)).normalized;
                uvs[vi] = new Vector2((float)s / segments, t);
                vi++;
            }
        }

        int ti = 0;
        for (int r = 0; r < rings; r++)
        {
            for (int s = 0; s < segments; s++)
            {
                int current = r * (segments + 1) + s;
                int next = current + segments + 1;

                triangles[ti++] = current;
                triangles[ti++] = next;
                triangles[ti++] = current + 1;

                triangles[ti++] = current + 1;
                triangles[ti++] = next;
                triangles[ti++] = next + 1;
            }
        }

        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Crea un anello (toro semplificato) per il tip ring della canna.
    /// </summary>
    Mesh CreateRing(float ringRadius, float tubeRadius, int segments)
    {
        Mesh mesh = new Mesh();
        mesh.name = "AnelloPunta";

        int tubeSegments = 6;
        int vertCount = (segments + 1) * (tubeSegments + 1);
        Vector3[] vertices = new Vector3[vertCount];
        Vector3[] normals = new Vector3[vertCount];
        Vector2[] uvs = new Vector2[vertCount];
        int[] triangles = new int[segments * tubeSegments * 6];

        int vi = 0;
        for (int i = 0; i <= segments; i++)
        {
            float u = (float)i / segments;
            float angle = u * Mathf.PI * 2f;
            Vector3 center = new Vector3(Mathf.Cos(angle) * ringRadius, 0f, Mathf.Sin(angle) * ringRadius);
            Vector3 radialDir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));

            for (int j = 0; j <= tubeSegments; j++)
            {
                float v = (float)j / tubeSegments;
                float tubeAngle = v * Mathf.PI * 2f;

                Vector3 normal = radialDir * Mathf.Cos(tubeAngle) + Vector3.up * Mathf.Sin(tubeAngle);
                vertices[vi] = center + normal * tubeRadius;
                normals[vi] = normal;
                uvs[vi] = new Vector2(u, v);
                vi++;
            }
        }

        int ti = 0;
        for (int i = 0; i < segments; i++)
        {
            for (int j = 0; j < tubeSegments; j++)
            {
                int current = i * (tubeSegments + 1) + j;
                int next = current + tubeSegments + 1;

                triangles[ti++] = current;
                triangles[ti++] = next;
                triangles[ti++] = current + 1;

                triangles[ti++] = current + 1;
                triangles[ti++] = next;
                triangles[ti++] = next + 1;
            }
        }

        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Crea un ellissoide allungato per l'esca/rapala.
    /// </summary>
    Mesh CreateEllipsoidMesh(float length, float radiusX, float radiusZ, int lonSegments, int latSegments)
    {
        Mesh mesh = new Mesh();
        mesh.name = "Rapala";

        int vertCount = (lonSegments + 1) * (latSegments + 1);
        Vector3[] vertices = new Vector3[vertCount];
        Vector3[] normals = new Vector3[vertCount];
        Vector2[] uvs = new Vector2[vertCount];

        int vi = 0;
        for (int lat = 0; lat <= latSegments; lat++)
        {
            float phi = Mathf.PI * lat / latSegments;
            float sinPhi = Mathf.Sin(phi);
            float cosPhi = Mathf.Cos(phi);

            for (int lon = 0; lon <= lonSegments; lon++)
            {
                float theta = 2f * Mathf.PI * lon / lonSegments;

                float x = Mathf.Cos(theta) * sinPhi * radiusX;
                float y = cosPhi * length * 0.5f;
                float z = Mathf.Sin(theta) * sinPhi * radiusZ;

                vertices[vi] = new Vector3(x, y, z);
                normals[vi] = new Vector3(x / (radiusX * radiusX), y / (length * length * 0.25f), z / (radiusZ * radiusZ)).normalized;
                uvs[vi] = new Vector2((float)lon / lonSegments, (float)lat / latSegments);
                vi++;
            }
        }

        int triCount = latSegments * lonSegments * 6;
        int[] triangles = new int[triCount];
        int ti = 0;
        for (int lat = 0; lat < latSegments; lat++)
        {
            for (int lon = 0; lon < lonSegments; lon++)
            {
                int current = lat * (lonSegments + 1) + lon;
                int next = current + lonSegments + 1;

                triangles[ti++] = current;
                triangles[ti++] = next;
                triangles[ti++] = current + 1;

                triangles[ti++] = current + 1;
                triangles[ti++] = next;
                triangles[ti++] = next + 1;
            }
        }

        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
        return mesh;
    }

    // ================================================================
    //  GIZMOS PER DEBUG IN EDITOR
    // ================================================================

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;

        for (int i = 0; i < 2; i++)
        {
            if (rods[i] == null) continue;

            // Posizione esca
            Vector3 lurePos = GetLureWorldPosition(i);
            Gizmos.color = (rods[i].state == RodState.Fighting) ? Color.red : Color.cyan;
            Gizmos.DrawWireSphere(lurePos, 0.5f);

            // Etichetta stato
            UnityEditor.Handles.Label(lurePos + Vector3.up, $"Canna {i}: {rods[i].state}\nTensione: {rods[i].currentTension:F0}%\nLenza: {rods[i].lineLength:F1}m");
        }
    }
#endif
}
