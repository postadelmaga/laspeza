using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Barca da pesca procedurale per il simulatore di La Spezia.
/// Gozzo mediterraneo ~8m con motore fuoribordo, galleggiamento manuale,
/// controlli realistici e camera in terza persona.
///
/// Controlli (quando attiva):
///   W/S o Frecce su/giu  = gas avanti/retromarcia
///   A/D o Frecce sx/dx   = timone
///   Shift                = turbo (planata)
///   C                    = cambia vista (dietro, console, dall'alto)
///   B                    = esci dalla barca
/// </summary>
public class FishingBoat : MonoBehaviour
{
    // ================================================================
    //  PARAMETRI ISPEZIONABILI
    // ================================================================

    [Header("Livello del mare")]
    [Tooltip("Se non trova 'Acqua_Mare' usa questo valore")]
    public float seaLevel = 0f;

    [Header("Dimensioni scafo — gozzo ligure")]
    public float hullLength = 8f;     // lunghezza totale in metri
    public float hullWidth  = 2.5f;   // larghezza al baglio massimo
    public float hullDepth  = 1.2f;   // pescaggio (profondita dello scafo)
    public float freeboardHeight = 0.6f; // altezza dal galleggiamento al bordo

    [Header("Velocita e manovrabilita")]
    [Tooltip("Velocita massima in m/s (~15 nodi)")]
    public float maxSpeed = 8f;
    [Tooltip("Velocita di traina in m/s (~5 nodi)")]
    public float trollingSpeed = 2.5f;
    [Tooltip("Accelerazione in m/s^2")]
    public float acceleration = 2f;
    [Tooltip("Decelerazione naturale (attrito acqua)")]
    public float waterDrag = 1.2f;
    [Tooltip("Velocita massima di virata in gradi/s")]
    public float maxTurnRate = 45f;
    [Tooltip("Moltiplicatore turbo (shift)")]
    public float boostMultiplier = 1.6f;

    [Header("Galleggiamento — punti di campionamento")]
    [Tooltip("Velocita di interpolazione altezza")]
    public float buoyancySmoothing = 4f;
    [Tooltip("Angolo massimo di rollio/beccheggio in gradi")]
    public float maxTiltAngle = 8f;
    [Tooltip("Velocita interpolazione rotazione")]
    public float tiltSmoothing = 3f;

    [Header("Camera")]
    public float cameraDistance = 12f;
    public float cameraHeight  = 5f;
    public float cameraFollowSpeed = 4f;
    public float cameraLookSpeed   = 6f;

    [Header("Scia — effetto particellare")]
    public float wakeMinSpeed = 1f;
    [Tooltip("Larghezza massima scia in metri")]
    public float wakeMaxWidth = 2.5f;

    // ================================================================
    //  STATO RUNTIME (esposti per debug / telemetria)
    // ================================================================

    [Header("Telemetria (sola lettura)")]
    [SerializeField] private float currentSpeed;   // m/s
    [SerializeField] private float currentRPM;     // 0-1 normalizzato — per futuro audio
    [SerializeField] private float throttleInput;
    [SerializeField] private float rudderInput;
    [SerializeField] private bool  isActive;

    /// <summary>RPM normalizzato 0-1, pronto per pitch audio motore.</summary>
    public float EngineRPM => currentRPM;
    /// <summary>Velocita corrente in m/s.</summary>
    public float Speed => currentSpeed;
    /// <summary>Velocita corrente in nodi.</summary>
    public float SpeedKnots => currentSpeed * 1.94384f;

    // ================================================================
    //  INTERNALS
    // ================================================================

    // Vista camera: 0 = dietro, 1 = console (prima persona), 2 = dall'alto
    private int cameraViewIndex;
    private Camera boatCamera;
    private CityExplorer cityExplorer;

    // Galleggiamento
    private float targetY;
    private float targetPitch;
    private float targetRoll;
    private float smoothY;
    private float smoothPitch;
    private float smoothRoll;
    private float yaw; // direzione prua in gradi

    // Posizione mondo
    private Vector3 boatPosition;

    // Riferimento alla superficie acqua
    private GameObject waterObject;
    private Renderer waterRenderer;

    // Mesh procedurale
    private GameObject boatRoot;
    private GameObject wakeTrailLeft;
    private GameObject wakeTrailRight;
    private ParticleSystem wakeParticles;

    // Input
    private Keyboard kb;
    private Mouse mouse;
    private GUIStyle cachedGuiStyle;

    // ================================================================
    //  LIFECYCLE
    // ================================================================

    void Start()
    {
        kb = Keyboard.current;
        mouse = Mouse.current;

        // Cerca la superficie dell'acqua nella scena
        TrovaSuperficieAcqua();

        // Costruisci la barca procedurale
        CostruisciBarca();

        // Crea effetto scia a poppa
        CreaEffettoScia();

        // Posiziona la barca al centro dell'acqua
        PosizionaIniziale();

        // Camera
        SetupCamera();

        // Inizia disattivata — si attiva con EnterBoat()
        isActive = false;
        boatRoot.SetActive(false);
        if (boatCamera != null) boatCamera.enabled = false;
    }

    void Update()
    {
        if (!isActive) return;

        if (kb == null) kb = Keyboard.current;
        if (mouse == null) mouse = Mouse.current;
        if (kb == null) return;

        // Cambio vista
        if (kb.cKey.wasPressedThisFrame)
            cameraViewIndex = (cameraViewIndex + 1) % 3;

        // Uscita dalla barca
        if (kb.bKey.wasPressedThisFrame)
        {
            ExitBoat();
            return;
        }

        LeggiInput();
        SimulaMovimento();
        SimulaGalleggiamento();
        ApplicaTransform();
        AggiornaScia();
        AggiornaCamera();
    }

    void OnGUI()
    {
        if (!isActive) return;

        if (cachedGuiStyle == null)
        {
            cachedGuiStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            cachedGuiStyle.normal.textColor = Color.white;
        }
        GUIStyle style = cachedGuiStyle;

        float knots = SpeedKnots;
        string vistaStr = cameraViewIndex == 0 ? "Poppa" : cameraViewIndex == 1 ? "Console" : "Alto";
        string info = $"BARCA | {knots:F1} nodi | Gas: {throttleInput:F1} | " +
                      $"Vista: {vistaStr} | WASD naviga | Shift planata | C vista | B esci";

        // Ombra testo
        GUI.color = Color.black;
        GUI.Label(new Rect(11, Screen.height - 29, 900, 25), info, style);
        GUI.color = Color.white;
        GUI.Label(new Rect(10, Screen.height - 30, 900, 25), info, style);
    }

    void OnDestroy()
    {
        if (boatRoot != null) Destroy(boatRoot);
    }

    // ================================================================
    //  API PUBBLICA — ingresso/uscita barca
    // ================================================================

    /// <summary>
    /// Attiva la barca e la camera. Chiamare da CityExplorer (tasto B).
    /// </summary>
    public void EnterBoat()
    {
        isActive = true;
        boatRoot.SetActive(true);

        // Disattiva CityExplorer
        cityExplorer = FindAnyObjectByType<CityExplorer>();
        if (cityExplorer != null)
            cityExplorer.enabled = false;

        // Attiva camera barca
        if (boatCamera != null)
        {
            boatCamera.enabled = true;

            // Disattiva la camera di CityExplorer
            if (cityExplorer != null)
            {
                var explorerCam = cityExplorer.GetComponent<Camera>();
                if (explorerCam != null) explorerCam.enabled = false;
            }
        }

        cameraViewIndex = 0;
        currentSpeed = 0f;
        throttleInput = 0f;
        rudderInput = 0f;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        Debug.Log("FishingBoat: Salito a bordo — buona pesca!");
    }

    /// <summary>
    /// Disattiva la barca e restituisce il controllo a CityExplorer.
    /// </summary>
    public void ExitBoat()
    {
        isActive = false;
        currentSpeed = 0f;

        if (boatCamera != null)
            boatCamera.enabled = false;

        // Riattiva CityExplorer
        if (cityExplorer != null)
        {
            cityExplorer.enabled = true;

            var explorerCam = cityExplorer.GetComponent<Camera>();
            if (explorerCam != null)
            {
                explorerCam.enabled = true;
                // Posiziona l'explorer sopra la barca
                cityExplorer.transform.position = boatPosition + Vector3.up * 10f;
                cityExplorer.transform.LookAt(boatPosition);
            }
        }

        Debug.Log("FishingBoat: Sceso dalla barca");
    }

    // ================================================================
    //  ACQUA — ricerca superficie e campionamento altezza
    // ================================================================

    /// <summary>
    /// Cerca il GameObject "Acqua_Mare" nella scena per determinare il livello del mare.
    /// </summary>
    void TrovaSuperficieAcqua()
    {
        waterObject = GameObject.Find("Acqua_Mare");
        if (waterObject != null)
        {
            waterRenderer = waterObject.GetComponent<Renderer>();
            if (waterRenderer != null)
            {
                // Il livello del mare e la Y della mesh dell'acqua
                seaLevel = waterRenderer.bounds.center.y;
                Debug.Log($"FishingBoat: Superficie acqua trovata a Y={seaLevel:F2}");
            }
        }
        else
        {
            Debug.LogWarning("FishingBoat: 'Acqua_Mare' non trovato — uso seaLevel=" + seaLevel);
        }
    }

    /// <summary>
    /// Restituisce l'altezza dell'acqua in un punto del mondo.
    /// Per ora ritorna seaLevel fisso; sara sostituita dal sistema onde.
    /// </summary>
    public float GetWaterHeight(Vector3 worldPos)
    {
        // TODO: sostituire con campionamento onde (Gerstner waves, FFT, ecc.)
        // Per ora: leggero ondeggiamento procedurale per dare vita alla barca
        float wave = Mathf.Sin(worldPos.x * 0.3f + Time.time * 0.8f) * 0.08f
                   + Mathf.Sin(worldPos.z * 0.2f + Time.time * 0.6f) * 0.06f
                   + Mathf.Sin((worldPos.x + worldPos.z) * 0.15f + Time.time * 1.1f) * 0.04f;
        return seaLevel + wave;
    }

    // ================================================================
    //  INPUT
    // ================================================================

    void LeggiInput()
    {
        // Gas: W/Su = avanti, S/Giu = retromarcia
        float rawThrottle = 0f;
        if (kb.wKey.isPressed || kb.upArrowKey.isPressed)    rawThrottle += 1f;
        if (kb.sKey.isPressed || kb.downArrowKey.isPressed)  rawThrottle -= 1f;
        throttleInput = Mathf.MoveTowards(throttleInput, rawThrottle, Time.deltaTime * 2f);

        // Timone: A/Sx = babordo (sinistra), D/Dx = tribordo (destra)
        float rawRudder = 0f;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)  rawRudder -= 1f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) rawRudder += 1f;
        rudderInput = Mathf.MoveTowards(rudderInput, rawRudder, Time.deltaTime * 3f);
    }

    // ================================================================
    //  SIMULAZIONE MOVIMENTO
    // ================================================================

    void SimulaMovimento()
    {
        bool boosting = kb.leftShiftKey.isPressed;
        float effectiveMaxSpeed = boosting ? maxSpeed * boostMultiplier : maxSpeed;

        // Accelerazione proporzionale al gas
        float targetSpeed = throttleInput * effectiveMaxSpeed;

        if (Mathf.Abs(throttleInput) > 0.01f)
        {
            // Accelera verso la velocita target
            currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, acceleration * Time.deltaTime);
        }
        else
        {
            // Attrito dell'acqua — decelerazione naturale
            currentSpeed = Mathf.MoveTowards(currentSpeed, 0f, waterDrag * Time.deltaTime);
        }

        // RPM normalizzato (per futuro audio motore)
        currentRPM = Mathf.Abs(currentSpeed) / (maxSpeed * boostMultiplier);

        // Virata — il timone e piu efficace ad alta velocita
        // A bassa velocita la barca risponde poco (realistico)
        float speedFactor = Mathf.Clamp01(Mathf.Abs(currentSpeed) / (maxSpeed * 0.3f));
        float turnRate = rudderInput * maxTurnRate * speedFactor;

        // A retromarcia il timone si inverte
        if (currentSpeed < -0.1f)
            turnRate = -turnRate;

        yaw += turnRate * Time.deltaTime;

        // Muovi la barca nella direzione della prua
        Vector3 forward = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
        boatPosition += forward * currentSpeed * Time.deltaTime;
    }

    // ================================================================
    //  GALLEGGIAMENTO — 4 punti di campionamento
    // ================================================================

    void SimulaGalleggiamento()
    {
        // Quattro punti di campionamento: prua, poppa, babordo, tribordo
        float halfLen = hullLength * 0.45f;
        float halfWid = hullWidth * 0.4f;
        Quaternion rot = Quaternion.Euler(0, yaw, 0);

        // Punti nel sistema mondo
        Vector3 bowPoint      = boatPosition + rot * new Vector3(0, 0, halfLen);
        Vector3 sternPoint    = boatPosition + rot * new Vector3(0, 0, -halfLen);
        Vector3 portPoint     = boatPosition + rot * new Vector3(-halfWid, 0, 0);
        Vector3 starboardPoint = boatPosition + rot * new Vector3(halfWid, 0, 0);

        // Altezze acqua nei 4 punti
        float hBow       = GetWaterHeight(bowPoint);
        float hStern     = GetWaterHeight(sternPoint);
        float hPort      = GetWaterHeight(portPoint);
        float hStarboard = GetWaterHeight(starboardPoint);

        // Altezza media = posizione Y della barca
        float avgHeight = (hBow + hStern + hPort + hStarboard) * 0.25f;
        targetY = avgHeight + freeboardHeight * 0.5f;

        // Beccheggio (pitch) — differenza prua/poppa
        float pitchDelta = hBow - hStern;
        targetPitch = Mathf.Clamp(
            Mathf.Atan2(pitchDelta, hullLength) * Mathf.Rad2Deg,
            -maxTiltAngle, maxTiltAngle);

        // Aggiunge beccheggio dinamico in accelerazione
        targetPitch -= currentSpeed * 0.3f;
        targetPitch = Mathf.Clamp(targetPitch, -maxTiltAngle * 1.5f, maxTiltAngle * 1.5f);

        // Rollio (roll) — differenza babordo/tribordo
        float rollDelta = hPort - hStarboard;
        targetRoll = Mathf.Clamp(
            Mathf.Atan2(rollDelta, hullWidth) * Mathf.Rad2Deg,
            -maxTiltAngle, maxTiltAngle);

        // Aggiunge sbandamento in virata
        targetRoll += rudderInput * Mathf.Abs(currentSpeed) * 0.5f;
        targetRoll = Mathf.Clamp(targetRoll, -maxTiltAngle * 2f, maxTiltAngle * 2f);

        // Interpolazione morbida
        smoothY     = Mathf.Lerp(smoothY, targetY, Time.deltaTime * buoyancySmoothing);
        smoothPitch = Mathf.Lerp(smoothPitch, targetPitch, Time.deltaTime * tiltSmoothing);
        smoothRoll  = Mathf.Lerp(smoothRoll, targetRoll, Time.deltaTime * tiltSmoothing);
    }

    void ApplicaTransform()
    {
        boatPosition.y = smoothY;
        boatRoot.transform.position = boatPosition;
        boatRoot.transform.rotation = Quaternion.Euler(smoothPitch, yaw, smoothRoll);
    }

    // ================================================================
    //  SCIA A POPPA — trail renderer + particelle
    // ================================================================

    void CreaEffettoScia()
    {
        // Trail renderer sinistro
        wakeTrailLeft = new GameObject("Scia_Sinistra");
        wakeTrailLeft.transform.parent = boatRoot.transform;
        wakeTrailLeft.transform.localPosition = new Vector3(-hullWidth * 0.35f, -0.1f, -hullLength * 0.45f);
        var trLeft = wakeTrailLeft.AddComponent<TrailRenderer>();
        ConfiguraTrail(trLeft);

        // Trail renderer destro
        wakeTrailRight = new GameObject("Scia_Destra");
        wakeTrailRight.transform.parent = boatRoot.transform;
        wakeTrailRight.transform.localPosition = new Vector3(hullWidth * 0.35f, -0.1f, -hullLength * 0.45f);
        var trRight = wakeTrailRight.AddComponent<TrailRenderer>();
        ConfiguraTrail(trRight);

        // Particelle schiuma a poppa
        GameObject wakeGO = new GameObject("Schiuma_Poppa");
        wakeGO.transform.parent = boatRoot.transform;
        wakeGO.transform.localPosition = new Vector3(0, 0.05f, -hullLength * 0.48f);
        wakeParticles = wakeGO.AddComponent<ParticleSystem>();

        var main = wakeParticles.main;
        main.startLifetime = 2f;
        main.startSpeed = 1f;
        main.startSize = 0.4f;
        main.startColor = new Color(0.9f, 0.95f, 1f, 0.5f);
        main.maxParticles = 300;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = -0.05f;

        var emission = wakeParticles.emission;
        emission.rateOverTime = 0f; // controllato da codice

        var shape = wakeParticles.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(hullWidth * 0.5f, 0.1f, 0.3f);

        // Materiale particelle — usa il default particle shader
        var pr = wakeGO.GetComponent<ParticleSystemRenderer>();
        Material particleMat = new Material(Shader.Find("Particles/Standard Unlit")
                                         ?? Shader.Find("Universal Render Pipeline/Particles/Unlit")
                                         ?? Shader.Find("Unlit/Color"));
        particleMat.color = new Color(0.85f, 0.92f, 1f, 0.4f);
        pr.material = particleMat;

        wakeParticles.Stop();
    }

    void ConfiguraTrail(TrailRenderer tr)
    {
        tr.time = 3f;
        tr.startWidth = 0.1f;
        tr.endWidth = 0f;
        tr.startColor = new Color(0.8f, 0.9f, 1f, 0.6f);
        tr.endColor = new Color(0.8f, 0.9f, 1f, 0f);
        tr.minVertexDistance = 0.5f;
        tr.numCornerVertices = 3;
        tr.numCapVertices = 3;

        Material trailMat = new Material(Shader.Find("Particles/Standard Unlit")
                                      ?? Shader.Find("Universal Render Pipeline/Particles/Unlit")
                                      ?? Shader.Find("Unlit/Color"));
        trailMat.color = new Color(0.85f, 0.92f, 1f, 0.5f);
        tr.material = trailMat;
    }

    void AggiornaScia()
    {
        float absSpeed = Mathf.Abs(currentSpeed);
        float speedRatio = Mathf.Clamp01(absSpeed / maxSpeed);

        // Trail renderer — larghezza proporzionale alla velocita
        float trailWidth = Mathf.Lerp(0.05f, wakeMaxWidth, speedRatio);
        if (absSpeed < wakeMinSpeed) trailWidth = 0f;

        if (wakeTrailLeft != null)
        {
            var tr = wakeTrailLeft.GetComponent<TrailRenderer>();
            if (tr != null) tr.startWidth = trailWidth;
        }
        if (wakeTrailRight != null)
        {
            var tr = wakeTrailRight.GetComponent<TrailRenderer>();
            if (tr != null) tr.startWidth = trailWidth;
        }

        // Particelle schiuma
        if (wakeParticles != null)
        {
            var emission = wakeParticles.emission;
            if (absSpeed > wakeMinSpeed)
            {
                if (!wakeParticles.isPlaying) wakeParticles.Play();
                emission.rateOverTime = Mathf.Lerp(5f, 150f, speedRatio);

                var main = wakeParticles.main;
                main.startSpeed = Mathf.Lerp(0.5f, 3f, speedRatio);
                main.startSize = Mathf.Lerp(0.2f, 0.8f, speedRatio);
            }
            else
            {
                emission.rateOverTime = 0f;
                if (wakeParticles.isPlaying && wakeParticles.particleCount == 0)
                    wakeParticles.Stop();
            }
        }
    }

    // ================================================================
    //  CAMERA
    // ================================================================

    void SetupCamera()
    {
        GameObject camGO = new GameObject("FishingBoat_Camera");
        boatCamera = camGO.AddComponent<Camera>();
        boatCamera.nearClipPlane = 0.3f;
        boatCamera.farClipPlane = 15000f;
        boatCamera.fieldOfView = 65f;
        boatCamera.depth = 10; // priorita sopra la camera di CityExplorer

        // Aggiungi AudioListener se non ce n'e uno
        if (FindAnyObjectByType<AudioListener>() == null)
            camGO.AddComponent<AudioListener>();
    }

    void AggiornaCamera()
    {
        if (boatCamera == null) return;

        Vector3 forward = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
        Vector3 right   = Quaternion.Euler(0, yaw, 0) * Vector3.right;
        Transform cam   = boatCamera.transform;

        switch (cameraViewIndex)
        {
            case 0: // Dietro la barca — terza persona
            {
                Vector3 targetPos = boatPosition
                    - forward * cameraDistance
                    + Vector3.up * cameraHeight;
                cam.position = Vector3.Lerp(cam.position, targetPos, Time.deltaTime * cameraFollowSpeed);

                Vector3 lookTarget = boatPosition + forward * hullLength * 0.3f + Vector3.up * 1f;
                Quaternion lookRot = Quaternion.LookRotation(lookTarget - cam.position);
                cam.rotation = Quaternion.Slerp(cam.rotation, lookRot, Time.deltaTime * cameraLookSpeed);
                break;
            }

            case 1: // Prima persona dalla console di comando
            {
                // La console e a circa 1/3 dalla prua, altezza uomo seduto
                Vector3 consolePos = boatPosition
                    + forward * hullLength * 0.1f
                    + Vector3.up * (freeboardHeight + 1.5f);
                cam.position = Vector3.Lerp(cam.position, consolePos, Time.deltaTime * 10f);

                // Guarda nella direzione della prua con leggero mouse look
                Vector3 lookAhead = consolePos + forward * 20f;
                Quaternion lookRot = Quaternion.LookRotation(lookAhead - cam.position);
                cam.rotation = Quaternion.Slerp(cam.rotation, lookRot, Time.deltaTime * 8f);
                break;
            }

            case 2: // Vista dall'alto
            {
                Vector3 topPos = boatPosition + Vector3.up * 25f - forward * 3f;
                cam.position = Vector3.Lerp(cam.position, topPos, Time.deltaTime * cameraFollowSpeed);

                Quaternion topRot = Quaternion.LookRotation(boatPosition - cam.position);
                cam.rotation = Quaternion.Slerp(cam.rotation, topRot, Time.deltaTime * cameraLookSpeed);
                break;
            }
        }
    }

    // ================================================================
    //  POSIZIONAMENTO INIZIALE
    // ================================================================

    void PosizionaIniziale()
    {
        if (waterRenderer != null)
        {
            // Centro della superficie dell'acqua
            Bounds wb = waterRenderer.bounds;
            boatPosition = new Vector3(wb.center.x, seaLevel + freeboardHeight * 0.5f, wb.center.z);
        }
        else
        {
            boatPosition = new Vector3(0, seaLevel + freeboardHeight * 0.5f, 0);
        }

        yaw = 0f;
        smoothY = boatPosition.y;
        smoothPitch = 0f;
        smoothRoll = 0f;

        boatRoot.transform.position = boatPosition;
    }

    // ================================================================
    //  COSTRUZIONE PROCEDURALE — gozzo da pesca mediterraneo
    // ================================================================

    /// <summary>
    /// Costruisce l'intera barca da pesca con primitive Unity.
    /// Gozzo ligure: scafo bianco con riga blu al galleggiamento,
    /// coperta in legno, cabina di comando, 2 portacanne a poppa,
    /// battagliola di prua, motore fuoribordo.
    /// </summary>
    void CostruisciBarca()
    {
        boatRoot = new GameObject("Barca_Pesca");

        // Materiali
        Shader shader = FindShader();
        Material matScafoBianco  = CreaMateriale(shader, new Color(0.95f, 0.95f, 0.93f)); // scafo bianco
        Material matRigaBlu      = CreaMateriale(shader, new Color(0.15f, 0.3f, 0.65f));   // riga al galleggiamento
        Material matCoperta      = CreaMateriale(shader, new Color(0.65f, 0.5f, 0.32f));   // legno teak
        Material matCabina       = CreaMateriale(shader, new Color(0.85f, 0.85f, 0.82f));  // cabina bianca
        Material matVetro        = CreaMaterialeTrasparente(shader, new Color(0.5f, 0.7f, 0.9f, 0.4f));
        Material matMetallo      = CreaMateriale(shader, new Color(0.6f, 0.6f, 0.62f));   // acciaio inox
        Material matMotore       = CreaMateriale(shader, new Color(0.2f, 0.2f, 0.22f));   // motore nero
        Material matRosso        = CreaMateriale(shader, new Color(0.7f, 0.1f, 0.1f));    // antivegetativa

        // ── SCAFO PRINCIPALE ──
        // Corpo dello scafo — forma approssimata con cubo allungato
        // Lo scafo vero ha carena a V, qui approssimiamo con piu pezzi

        // Parte centrale scafo (opera morta — sopra il galleggiamento)
        CreaComponente("Scafo_Centrale", boatRoot,
            new Vector3(0, 0.15f, 0),
            new Vector3(hullWidth, hullDepth * 0.5f, hullLength * 0.85f),
            matScafoBianco);

        // Prua — rastremata (cubo scalato e ruotato)
        CreaComponente("Scafo_Prua", boatRoot,
            new Vector3(0, 0.1f, hullLength * 0.42f),
            new Vector3(hullWidth * 0.4f, hullDepth * 0.55f, hullLength * 0.2f),
            matScafoBianco);

        // Poppa — leggermente piu larga (specchio di poppa)
        CreaComponente("Scafo_Poppa", boatRoot,
            new Vector3(0, 0.15f, -hullLength * 0.42f),
            new Vector3(hullWidth * 0.95f, hullDepth * 0.5f, hullLength * 0.12f),
            matScafoBianco);

        // ── CARENA A V (opera viva — sotto il galleggiamento) ──
        // Simulata con due piani inclinati
        CreaComponenteRuotato("Carena_Sinistra", boatRoot,
            new Vector3(-hullWidth * 0.18f, -hullDepth * 0.25f, 0),
            new Vector3(hullWidth * 0.5f, 0.08f, hullLength * 0.8f),
            new Vector3(0, 0, 20f), // inclinazione V
            matRosso); // antivegetativa rossa sotto

        CreaComponenteRuotato("Carena_Destra", boatRoot,
            new Vector3(hullWidth * 0.18f, -hullDepth * 0.25f, 0),
            new Vector3(hullWidth * 0.5f, 0.08f, hullLength * 0.8f),
            new Vector3(0, 0, -20f),
            matRosso);

        // Chiglia centrale
        CreaComponente("Chiglia", boatRoot,
            new Vector3(0, -hullDepth * 0.35f, 0),
            new Vector3(0.12f, 0.15f, hullLength * 0.7f),
            matRosso);

        // ── RIGA BLU AL GALLEGGIAMENTO ──
        CreaComponente("Riga_Galleggiamento_Sx", boatRoot,
            new Vector3(-hullWidth * 0.5f + 0.02f, -0.05f, 0),
            new Vector3(0.06f, 0.15f, hullLength * 0.85f),
            matRigaBlu);

        CreaComponente("Riga_Galleggiamento_Dx", boatRoot,
            new Vector3(hullWidth * 0.5f - 0.02f, -0.05f, 0),
            new Vector3(0.06f, 0.15f, hullLength * 0.85f),
            matRigaBlu);

        // Riga blu alla prua
        CreaComponente("Riga_Prua", boatRoot,
            new Vector3(0, -0.05f, hullLength * 0.42f),
            new Vector3(hullWidth * 0.4f, 0.15f, 0.06f),
            matRigaBlu);

        // ── COPERTA (ponte) ──
        CreaComponente("Coperta", boatRoot,
            new Vector3(0, hullDepth * 0.25f + 0.02f, -hullLength * 0.05f),
            new Vector3(hullWidth * 0.9f, 0.06f, hullLength * 0.75f),
            matCoperta);

        // ── CABINA DI COMANDO (console center) ──
        // Posizionata a circa 1/3 dalla prua, tipica dei gozzi da pesca
        float cabinZ = hullLength * 0.1f;
        float cabinW = hullWidth * 0.45f;
        float cabinH = 1.4f;
        float cabinD = 1.2f;

        // Struttura cabina
        CreaComponente("Cabina_Corpo", boatRoot,
            new Vector3(0, hullDepth * 0.25f + cabinH * 0.5f + 0.02f, cabinZ),
            new Vector3(cabinW, cabinH, cabinD),
            matCabina);

        // Tettino cabina
        CreaComponente("Cabina_Tetto", boatRoot,
            new Vector3(0, hullDepth * 0.25f + cabinH + 0.06f, cabinZ),
            new Vector3(cabinW + 0.3f, 0.08f, cabinD + 0.3f),
            matCabina);

        // Parabrezza (vetro)
        CreaComponenteRuotato("Parabrezza", boatRoot,
            new Vector3(0, hullDepth * 0.25f + cabinH * 0.65f, cabinZ + cabinD * 0.5f + 0.04f),
            new Vector3(cabinW * 0.85f, cabinH * 0.5f, 0.04f),
            new Vector3(-10f, 0, 0), // leggera inclinazione
            matVetro);

        // ── CONSOLE DI COMANDO ──
        // Cruscotto dentro la cabina
        CreaComponente("Console_Cruscotto", boatRoot,
            new Vector3(0, hullDepth * 0.25f + 0.5f, cabinZ + cabinD * 0.35f),
            new Vector3(cabinW * 0.7f, 0.4f, 0.25f),
            matMetallo);

        // Volante (cilindro piccolo)
        CreaComponenteRuotato("Volante", boatRoot,
            new Vector3(0, hullDepth * 0.25f + 0.85f, cabinZ + cabinD * 0.38f),
            new Vector3(0.3f, 0.02f, 0.3f),
            new Vector3(-30f, 0, 0),
            matMetallo);

        // ── PORTACANNE A POPPA (2 canne per la traina) ──
        float sternZ = -hullLength * 0.38f;
        for (int side = -1; side <= 1; side += 2)
        {
            float px = side * hullWidth * 0.35f;

            // Base portacanna (tubo in acciaio inox)
            GameObject holder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            holder.name = side < 0 ? "Portacanna_Babordo" : "Portacanna_Tribordo";
            holder.transform.parent = boatRoot.transform;
            holder.transform.localPosition = new Vector3(px, hullDepth * 0.25f + 0.4f, sternZ);
            holder.transform.localScale = new Vector3(0.08f, 0.4f, 0.08f);
            holder.GetComponent<Renderer>().sharedMaterial = matMetallo;
            Object.Destroy(holder.GetComponent<Collider>());

            // Tubo superiore inclinato verso l'esterno
            // Simula l'angolo del portacanna per la traina
            GameObject tube = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            tube.name = side < 0 ? "Canna_Babordo" : "Canna_Tribordo";
            tube.transform.parent = boatRoot.transform;
            tube.transform.localPosition = new Vector3(
                px + side * 0.25f,
                hullDepth * 0.25f + 0.9f,
                sternZ - 0.1f);
            tube.transform.localScale = new Vector3(0.04f, 0.5f, 0.04f);
            tube.transform.localRotation = Quaternion.Euler(0, 0, side * -25f);
            tube.GetComponent<Renderer>().sharedMaterial = matMetallo;
            Object.Destroy(tube.GetComponent<Collider>());
        }

        // ── BATTAGLIOLA DI PRUA (bow rail) ──
        // Corrimano in acciaio inox intorno alla prua
        float railH = hullDepth * 0.25f + 0.6f;
        float railZ = hullLength * 0.35f;

        // Montanti verticali
        for (int i = 0; i < 3; i++)
        {
            float z = railZ - i * 0.6f;
            float narrowing = 1f - (float)i * 0.0f; // la prua si restringe
            for (int side = -1; side <= 1; side += 2)
            {
                float x = side * hullWidth * (0.42f - i * 0.05f);
                GameObject post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                post.name = "Battagliola_Montante";
                post.transform.parent = boatRoot.transform;
                post.transform.localPosition = new Vector3(x, railH - 0.15f, z);
                post.transform.localScale = new Vector3(0.03f, 0.3f, 0.03f);
                post.GetComponent<Renderer>().sharedMaterial = matMetallo;
                Object.Destroy(post.GetComponent<Collider>());
            }
        }

        // Corrimano orizzontale (semplificato — 2 tubi per lato)
        for (int side = -1; side <= 1; side += 2)
        {
            GameObject rail = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            rail.name = side < 0 ? "Corrimano_Babordo" : "Corrimano_Tribordo";
            rail.transform.parent = boatRoot.transform;
            float x = side * hullWidth * 0.37f;
            rail.transform.localPosition = new Vector3(x, railH, railZ - 0.6f);
            rail.transform.localScale = new Vector3(0.025f, 0.65f, 0.025f);
            rail.transform.localRotation = Quaternion.Euler(90f, 0, 0);
            rail.GetComponent<Renderer>().sharedMaterial = matMetallo;
            Object.Destroy(rail.GetComponent<Collider>());
        }

        // ── MOTORE FUORIBORDO A POPPA ──
        float motorZ = -hullLength * 0.48f;

        // Corpo motore (blocco principale)
        CreaComponente("Motore_Corpo", boatRoot,
            new Vector3(0, 0f, motorZ),
            new Vector3(0.35f, 0.6f, 0.3f),
            matMotore);

        // Calandra / coperchio motore
        CreaComponente("Motore_Calandra", boatRoot,
            new Vector3(0, 0.35f, motorZ),
            new Vector3(0.3f, 0.15f, 0.28f),
            matMetallo);

        // Gambo motore (colonna verticale verso l'elica)
        GameObject shaft = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        shaft.name = "Motore_Gambo";
        shaft.transform.parent = boatRoot.transform;
        shaft.transform.localPosition = new Vector3(0, -hullDepth * 0.3f, motorZ);
        shaft.transform.localScale = new Vector3(0.08f, hullDepth * 0.4f, 0.08f);
        shaft.GetComponent<Renderer>().sharedMaterial = matMetallo;
        Object.Destroy(shaft.GetComponent<Collider>());

        // Elica (cilindro orizzontale piccolo)
        GameObject prop = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        prop.name = "Motore_Elica";
        prop.transform.parent = boatRoot.transform;
        prop.transform.localPosition = new Vector3(0, -hullDepth * 0.55f, motorZ - 0.1f);
        prop.transform.localScale = new Vector3(0.25f, 0.03f, 0.25f);
        prop.transform.localRotation = Quaternion.Euler(90f, 0, 0);
        prop.GetComponent<Renderer>().sharedMaterial = matMetallo;
        Object.Destroy(prop.GetComponent<Collider>());

        // Barra del timone / leva gas (sulla console)
        CreaComponente("Leva_Gas", boatRoot,
            new Vector3(0.15f, hullDepth * 0.25f + 0.6f, cabinZ + cabinD * 0.2f),
            new Vector3(0.04f, 0.25f, 0.04f),
            matMetallo);

        // ── PARABORDI (piccole sfere sui lati) ──
        Material matParabordo = CreaMateriale(shader, new Color(0.2f, 0.35f, 0.6f));
        for (int i = 0; i < 3; i++)
        {
            float z = hullLength * 0.1f - i * hullLength * 0.2f;
            for (int side = -1; side <= 1; side += 2)
            {
                GameObject fender = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                fender.name = "Parabordo";
                fender.transform.parent = boatRoot.transform;
                fender.transform.localPosition = new Vector3(
                    side * (hullWidth * 0.5f + 0.08f),
                    hullDepth * 0.1f,
                    z);
                fender.transform.localScale = new Vector3(0.15f, 0.25f, 0.15f);
                fender.GetComponent<Renderer>().sharedMaterial = matParabordo;
                Object.Destroy(fender.GetComponent<Collider>());
            }
        }

        // ── BITTA DI PRUA (per l'ormeggio) ──
        CreaComponente("Bitta_Prua", boatRoot,
            new Vector3(0, hullDepth * 0.25f + 0.12f, hullLength * 0.4f),
            new Vector3(0.15f, 0.1f, 0.1f),
            matMetallo);

        // ── LUCI DI NAVIGAZIONE ──
        // Luce verde a tribordo (destra)
        Material matVerde = CreaMaterialeEmissivo(shader, new Color(0.1f, 0.7f, 0.1f), 1.5f);
        CreaComponente("Luce_Tribordo", boatRoot,
            new Vector3(hullWidth * 0.48f, hullDepth * 0.25f + 0.35f, hullLength * 0.2f),
            new Vector3(0.06f, 0.08f, 0.06f),
            matVerde);

        // Luce rossa a babordo (sinistra)
        Material matLuceRossa = CreaMaterialeEmissivo(shader, new Color(0.8f, 0.1f, 0.1f), 1.5f);
        CreaComponente("Luce_Babordo", boatRoot,
            new Vector3(-hullWidth * 0.48f, hullDepth * 0.25f + 0.35f, hullLength * 0.2f),
            new Vector3(0.06f, 0.08f, 0.06f),
            matLuceRossa);

        // Luce bianca a poppa
        Material matLuceBianca = CreaMaterialeEmissivo(shader, new Color(1f, 0.95f, 0.8f), 2f);
        CreaComponente("Luce_Poppa", boatRoot,
            new Vector3(0, hullDepth * 0.25f + 0.4f, -hullLength * 0.4f),
            new Vector3(0.06f, 0.08f, 0.06f),
            matLuceBianca);
    }

    // ================================================================
    //  HELPER — creazione componenti e materiali
    // ================================================================

    Shader FindShader()
    {
        Shader s = Shader.Find("Universal Render Pipeline/Lit");
        if (s != null) return s;
        s = Shader.Find("HDRP/Lit");
        if (s != null) return s;
        s = Shader.Find("Standard");
        if (s != null) return s;
        return Shader.Find("Unlit/Color");
    }

    Material CreaMateriale(Shader shader, Color color)
    {
        Material mat = new Material(shader);
        mat.color = color;
        if (mat.HasProperty("_Metallic"))   mat.SetFloat("_Metallic", 0.1f);
        if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.3f);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.3f);
        return mat;
    }

    Material CreaMaterialeTrasparente(Shader shader, Color color)
    {
        Material mat = new Material(shader);
        mat.color = color;

        string shaderName = shader.name;
        if (shaderName.Contains("Universal Render Pipeline"))
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        else if (shaderName == "Standard")
        {
            mat.SetFloat("_Mode", 3f);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        if (mat.HasProperty("_Metallic"))   mat.SetFloat("_Metallic", 0.4f);
        if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.9f);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.9f);
        return mat;
    }

    Material CreaMaterialeEmissivo(Shader shader, Color color, float intensity)
    {
        Material mat = CreaMateriale(shader, color);
        mat.EnableKeyword("_EMISSION");
        if (mat.HasProperty("_EmissionColor"))
            mat.SetColor("_EmissionColor", color * intensity);
        return mat;
    }

    /// <summary>
    /// Crea un cubo primitivo come componente della barca.
    /// </summary>
    GameObject CreaComponente(string name, GameObject parent,
        Vector3 localPos, Vector3 localScale, Material mat)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.parent = parent.transform;
        go.transform.localPosition = localPos;
        go.transform.localScale = localScale;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        Object.Destroy(go.GetComponent<Collider>());
        return go;
    }

    /// <summary>
    /// Crea un cubo primitivo con rotazione locale.
    /// </summary>
    GameObject CreaComponenteRuotato(string name, GameObject parent,
        Vector3 localPos, Vector3 localScale, Vector3 eulerAngles, Material mat)
    {
        GameObject go = CreaComponente(name, parent, localPos, localScale, mat);
        go.transform.localRotation = Quaternion.Euler(eulerAngles);
        return go;
    }
}
