using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Sistema IA per branchi di tonni nel Golfo di La Spezia.
/// Gestisce movimento, comportamento, abboccata e combattimento.
///
/// Ogni branco (branco) contiene 3-12 tonni che nuotano in formazione.
/// I tonni reagiscono alle esche trascinate dalla barca.
/// </summary>
public class TunaAI : MonoBehaviour
{
    [Header("Popolazione")]
    public int branchCount = 6;           // numero di branchi nel golfo
    public int minPerBranch = 3;
    public int maxPerBranch = 10;

    [Header("Comportamento")]
    public float swimSpeed = 4f;          // velocita' crociera (m/s)
    public float chaseSpeed = 8f;         // velocita' inseguimento esca
    public float depthMin = 3f;           // profondita' minima (m sotto superficie)
    public float depthMax = 25f;          // profondita' massima
    public float biteRange = 5f;          // distanza per tentare abboccata
    public float biteChancePerSecond = 0.3f; // probabilita' base abboccata/secondo

    [Header("Combattimento")]
    public float minWeight = 8f;          // peso minimo tonno (kg)
    public float maxWeight = 80f;         // peso massimo tonno (kg)
    public float fightStrengthPerKg = 0.8f; // forza combattimento per kg
    public float staminaDecayRate = 0.02f;  // perdita stamina per secondo durante lotta
    public float rushChance = 0.15f;      // probabilita' di fuga improvvisa per secondo
    public float jumpChance = 0.05f;      // probabilita' di salto fuori dall'acqua

    [Header("Aspetto")]
    public float bodyLengthMin = 0.6f;    // lunghezza corpo minima (m)
    public float bodyLengthMax = 1.8f;    // lunghezza corpo massima (m)

    // Stato interno
    private List<TunaShoal> shoals = new List<TunaShoal>();
    private Bounds waterBounds;
    private float seaLevel;
    private TrollingSystem trollingSystem;
    private bool initialized;

    // Materiali condivisi
    private Material tunaMat;
    private Material tunaFinMat;

    // ================================================================
    //  CLASSI INTERNE
    // ================================================================

    class TunaShoal
    {
        public GameObject parent;
        public List<TunaFish> fish = new List<TunaFish>();
        public Vector3 target;           // destinazione branco
        public float changeTimer;        // timer cambio direzione
        public float depth;              // profondita' corrente del branco
        public float targetDepth;
    }

    public enum FishState { Swimming, Chasing, Hooked, Landed, Escaped }

    class TunaFish
    {
        public GameObject go;
        public Transform body;
        public float weight;             // kg
        public float length;             // m
        public float stamina;            // 0-1, decresce durante combattimento
        public FishState state;
        public Vector3 offset;           // offset dal centro branco
        public float tailPhase;          // fase animazione coda
        public float tailSpeed;
        public int targetRod;            // indice canna che sta inseguendo (-1 = nessuna)
        public float hookTimer;          // tempo sull'amo
        public float fightForce;         // forza trazione corrente (N)
    }

    // ================================================================
    //  INIT
    // ================================================================

    void Start()
    {
        FindWaterBounds();
        CreateMaterials();
        SpawnShoals();

        // Cerca il sistema di traina sulla barca
        var boat = FindAnyObjectByType<FishingBoat>();
        if (boat != null)
            trollingSystem = boat.GetComponent<TrollingSystem>();

        initialized = true;
    }

    void FindWaterBounds()
    {
        var world = GameObject.Find("CityBuilder_World");
        if (world != null)
        {
            Terrain[] terrains = world.GetComponentsInChildren<Terrain>();
            if (terrains.Length > 0)
            {
                waterBounds = new Bounds(terrains[0].transform.position, Vector3.zero);
                foreach (var t in terrains)
                {
                    waterBounds.Encapsulate(t.transform.position);
                    waterBounds.Encapsulate(t.transform.position + t.terrainData.size);
                }
            }
        }
        if (waterBounds.size.sqrMagnitude < 1f)
            waterBounds = new Bounds(Vector3.zero, Vector3.one * 5000f);

        // Cerca livello mare
        var mare = GameObject.Find("Acqua_Mare");
        if (mare != null)
            seaLevel = mare.transform.position.y;
    }

    void CreateMaterials()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                     ?? Shader.Find("Standard");

        // Corpo tonno: blu-argento dorsale
        tunaMat = new Material(shader);
        tunaMat.color = new Color(0.15f, 0.22f, 0.4f); // blu scuro dorsale
        if (tunaMat.HasProperty("_Metallic")) tunaMat.SetFloat("_Metallic", 0.4f);
        if (tunaMat.HasProperty("_Smoothness")) tunaMat.SetFloat("_Smoothness", 0.7f);

        // Pinne
        tunaFinMat = new Material(shader);
        tunaFinMat.color = new Color(0.2f, 0.25f, 0.35f);
        if (tunaFinMat.HasProperty("_Smoothness")) tunaFinMat.SetFloat("_Smoothness", 0.5f);
    }

    // ================================================================
    //  SPAWN BRANCHI
    // ================================================================

    void SpawnShoals()
    {
        Random.InitState(54321);

        for (int s = 0; s < branchCount; s++)
        {
            var shoal = new TunaShoal
            {
                parent = new GameObject($"Branco_Tonni_{s}"),
                depth = Random.Range(depthMin, depthMax),
                targetDepth = Random.Range(depthMin, depthMax),
                changeTimer = Random.Range(10f, 30f)
            };

            // Posizione iniziale: zona acqua aperta (lontano dalla costa)
            Vector3 pos = new Vector3(
                Random.Range(waterBounds.min.x + waterBounds.size.x * 0.2f,
                             waterBounds.max.x - waterBounds.size.x * 0.1f),
                seaLevel - shoal.depth,
                Random.Range(waterBounds.min.z + waterBounds.size.z * 0.2f,
                             waterBounds.max.z - waterBounds.size.z * 0.1f)
            );
            shoal.parent.transform.position = pos;
            shoal.target = PickShoalTarget(shoal.depth);

            // Spawn tonni nel branco
            int count = Random.Range(minPerBranch, maxPerBranch + 1);
            for (int f = 0; f < count; f++)
            {
                var fish = CreateTuna(shoal.parent.transform);
                shoal.fish.Add(fish);
            }

            shoals.Add(shoal);
        }

        Debug.Log($"TunaAI: {branchCount} branchi di tonni creati nel golfo.");
    }

    TunaFish CreateTuna(Transform parent)
    {
        float weight = Random.Range(minWeight, maxWeight);
        float lengthRatio = Mathf.InverseLerp(minWeight, maxWeight, weight);
        float length = Mathf.Lerp(bodyLengthMin, bodyLengthMax, lengthRatio);

        GameObject go = new GameObject("Tonno");
        go.transform.parent = parent;
        go.transform.localPosition = new Vector3(
            Random.Range(-4f, 4f),
            Random.Range(-1f, 1f),
            Random.Range(-4f, 4f)
        );

        // Corpo: ellissoide affusolato (sfera schiacciata)
        GameObject body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Object.Destroy(body.GetComponent<Collider>());
        body.transform.parent = go.transform;
        body.transform.localPosition = Vector3.zero;
        body.transform.localScale = new Vector3(
            length * 0.35f,   // larghezza
            length * 0.3f,    // altezza
            length             // lunghezza
        );
        body.GetComponent<Renderer>().sharedMaterial = tunaMat;

        // Pinna caudale (coda)
        GameObject tail = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.Destroy(tail.GetComponent<Collider>());
        tail.transform.parent = go.transform;
        tail.transform.localPosition = new Vector3(0, 0, -length * 0.55f);
        tail.transform.localScale = new Vector3(length * 0.02f, length * 0.25f, length * 0.2f);
        tail.GetComponent<Renderer>().sharedMaterial = tunaFinMat;

        // Pinna dorsale
        GameObject dorsal = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.Destroy(dorsal.GetComponent<Collider>());
        dorsal.transform.parent = go.transform;
        dorsal.transform.localPosition = new Vector3(0, length * 0.18f, length * 0.05f);
        dorsal.transform.localScale = new Vector3(length * 0.02f, length * 0.12f, length * 0.25f);
        dorsal.GetComponent<Renderer>().sharedMaterial = tunaFinMat;

        return new TunaFish
        {
            go = go,
            body = body.transform,
            weight = weight,
            length = length,
            stamina = 1f,
            state = FishState.Swimming,
            offset = go.transform.localPosition,
            tailPhase = Random.Range(0f, Mathf.PI * 2f),
            tailSpeed = Random.Range(3f, 5f),
            targetRod = -1
        };
    }

    // ================================================================
    //  UPDATE
    // ================================================================

    void Update()
    {
        if (!initialized) return;

        // Cerca trolling system se non trovato
        if (trollingSystem == null)
        {
            var boat = FindAnyObjectByType<FishingBoat>();
            if (boat != null)
                trollingSystem = boat.GetComponent<TrollingSystem>();
        }

        foreach (var shoal in shoals)
            UpdateShoal(shoal);
    }

    void UpdateShoal(TunaShoal shoal)
    {
        if (shoal.parent == null) return;

        // Movimento branco verso target
        Vector3 pos = shoal.parent.transform.position;
        Vector3 dir = shoal.target - pos;
        float dist = dir.magnitude;

        if (dist < 30f)
        {
            shoal.target = PickShoalTarget(shoal.targetDepth);
            shoal.targetDepth = Random.Range(depthMin, depthMax);
            shoal.changeTimer = Random.Range(15f, 40f);
        }

        // Movimento verso target
        dir.Normalize();
        float speed = swimSpeed;
        pos += dir * speed * Time.deltaTime;

        // Interpolazione profondita'
        shoal.depth = Mathf.Lerp(shoal.depth, shoal.targetDepth, Time.deltaTime * 0.1f);
        float waterY = GetWaterHeight(pos);
        pos.y = waterY - shoal.depth;

        shoal.parent.transform.position = pos;

        // Orienta il branco
        Quaternion targetRot = Quaternion.LookRotation(dir);
        shoal.parent.transform.rotation = Quaternion.Slerp(
            shoal.parent.transform.rotation, targetRot, Time.deltaTime * 1.5f);

        // Timer cambio direzione
        shoal.changeTimer -= Time.deltaTime;
        if (shoal.changeTimer <= 0)
        {
            shoal.target = PickShoalTarget(shoal.depth);
            shoal.changeTimer = Random.Range(15f, 40f);
        }

        // Aggiorna ogni tonno
        foreach (var fish in shoal.fish)
            UpdateFish(fish, shoal);
    }

    void UpdateFish(TunaFish fish, TunaShoal shoal)
    {
        if (fish.go == null) return;

        switch (fish.state)
        {
            case FishState.Swimming:
                UpdateSwimming(fish, shoal);
                break;
            case FishState.Chasing:
                UpdateChasing(fish, shoal);
                break;
            case FishState.Hooked:
                UpdateHooked(fish);
                break;
        }

        // Animazione coda (sempre attiva)
        AnimateTail(fish);
    }

    // ================================================================
    //  COMPORTAMENTO: NUOTO LIBERO
    // ================================================================

    void UpdateSwimming(TunaFish fish, TunaShoal shoal)
    {
        // Oscillazione attorno alla posizione nel branco
        float t = Time.time;
        Vector3 localPos = fish.offset;
        localPos.x += Mathf.Sin(t * 0.4f + fish.tailPhase) * 0.3f;
        localPos.z += Mathf.Cos(t * 0.3f + fish.tailPhase * 0.7f) * 0.3f;
        fish.go.transform.localPosition = Vector3.Lerp(
            fish.go.transform.localPosition, localPos, Time.deltaTime * 2f);

        // Controlla se c'e' un'esca vicina
        if (trollingSystem == null) return;

        for (int rod = 0; rod < 2; rod++)
        {
            if (!trollingSystem.IsRodAvailable(rod)) continue;

            Vector3 lurePos = trollingSystem.GetLureWorldPosition(rod);
            float lureDist = Vector3.Distance(
                fish.go.transform.position, lurePos);

            // Probabilita' di interessarsi all'esca
            if (lureDist < 40f)
            {
                float boatSpeed = trollingSystem.GetBoatSpeed();
                // Velocita' ideale traina: 2-5 m/s
                float speedFactor = 1f - Mathf.Abs(boatSpeed - 3.5f) / 3.5f;
                speedFactor = Mathf.Clamp01(speedFactor);

                // Profondita' esca vs profondita' pesce
                float lureDepth = trollingSystem.GetLureDepth(rod);
                float depthDiff = Mathf.Abs(shoal.depth - lureDepth);
                float depthFactor = 1f - Mathf.Clamp01(depthDiff / 10f);

                float interest = speedFactor * depthFactor * 0.02f * Time.deltaTime;
                if (Random.value < interest)
                {
                    fish.state = FishState.Chasing;
                    fish.targetRod = rod;
                }
            }
        }
    }

    // ================================================================
    //  COMPORTAMENTO: INSEGUIMENTO ESCA
    // ================================================================

    void UpdateChasing(TunaFish fish, TunaShoal shoal)
    {
        if (trollingSystem == null || fish.targetRod < 0 ||
            !trollingSystem.IsRodAvailable(fish.targetRod))
        {
            fish.state = FishState.Swimming;
            fish.targetRod = -1;
            return;
        }

        Vector3 lurePos = trollingSystem.GetLureWorldPosition(fish.targetRod);
        Vector3 toTarget = lurePos - fish.go.transform.position;
        float dist = toTarget.magnitude;

        // Nuota verso l'esca
        if (dist > 0.5f)
        {
            Vector3 worldDir = toTarget / dist;
            fish.go.transform.position += worldDir * chaseSpeed * Time.deltaTime;
            fish.go.transform.rotation = Quaternion.Slerp(
                fish.go.transform.rotation,
                Quaternion.LookRotation(worldDir),
                Time.deltaTime * 4f);
        }

        // Abboccata! (bite)
        if (dist < biteRange)
        {
            float chance = biteChancePerSecond * Time.deltaTime;
            // Tonni piu' grandi sono piu' cauti
            chance *= Mathf.Lerp(1.5f, 0.5f, Mathf.InverseLerp(minWeight, maxWeight, fish.weight));

            if (Random.value < chance)
            {
                // ABBOCCATA!
                float strength = fish.weight * fightStrengthPerKg;
                trollingSystem.OnFishBite(fish.targetRod, strength, fish.go);
                fish.state = FishState.Hooked;
                fish.hookTimer = 0f;
                fish.stamina = 1f;
                Debug.Log($"ABBOCCATA! Tonno {fish.weight:F1}kg sulla canna {fish.targetRod + 1}!");
            }
        }

        // Perde interesse dopo un po'
        if (dist > 50f)
        {
            fish.state = FishState.Swimming;
            fish.targetRod = -1;
        }
    }

    // ================================================================
    //  COMPORTAMENTO: AGGANCIATO (combattimento)
    // ================================================================

    void UpdateHooked(TunaFish fish)
    {
        fish.hookTimer += Time.deltaTime;
        fish.stamina -= staminaDecayRate * Time.deltaTime;
        fish.stamina = Mathf.Clamp01(fish.stamina);

        // Forza di trazione: proporzionale a peso e stamina
        float baseForce = fish.weight * fightStrengthPerKg;
        fish.fightForce = baseForce * fish.stamina;

        // Fughe improvvise (corsa del tonno)
        if (Random.value < rushChance * Time.deltaTime * fish.stamina)
        {
            // Fuga in direzione casuale
            Vector3 rushDir = new Vector3(
                Random.Range(-1f, 1f), Random.Range(-0.3f, 0f), Random.Range(-1f, 1f)
            ).normalized;
            fish.go.transform.position += rushDir * chaseSpeed * 2f * Time.deltaTime;
            fish.fightForce *= 2f; // doppia forza durante la fuga
        }

        // Salto fuori dall'acqua
        if (Random.value < jumpChance * Time.deltaTime * fish.stamina)
        {
            float waterY = GetWaterHeight(fish.go.transform.position);
            if (fish.go.transform.position.y < waterY + 0.5f)
            {
                // Salto!
                fish.go.transform.position += Vector3.up * (1f + fish.weight * 0.02f);
                Debug.Log($"Il tonno salta! ({fish.weight:F1}kg)");
            }
        }

        // Resistenza all'essere tirato: nuota in direzione opposta alla barca
        if (trollingSystem != null)
        {
            Vector3 lurePos = trollingSystem.GetLureWorldPosition(fish.targetRod);
            Vector3 awayFromLure = (fish.go.transform.position - lurePos).normalized;
            float pullForce = fish.fightForce * 0.01f * fish.stamina;
            fish.go.transform.position += awayFromLure * pullForce * Time.deltaTime;
        }

        // Mantieni il pesce sott'acqua (tranne salti)
        float wy = GetWaterHeight(fish.go.transform.position);
        if (fish.go.transform.position.y > wy)
        {
            Vector3 p = fish.go.transform.position;
            p.y = Mathf.Lerp(p.y, wy - 1f, Time.deltaTime * 3f);
            fish.go.transform.position = p;
        }
    }

    // ================================================================
    //  NOTIFICHE DAL TROLLING SYSTEM
    // ================================================================

    /// <summary>
    /// Chiamato dal TrollingSystem quando il pesce scappa (lenza rotta o slamato).
    /// </summary>
    public void OnFishEscaped(GameObject fishGO)
    {
        foreach (var shoal in shoals)
        {
            foreach (var fish in shoal.fish)
            {
                if (fish.go == fishGO)
                {
                    fish.state = FishState.Escaped;
                    fish.targetRod = -1;
                    // Ritorna nel branco dopo un po'
                    fish.go.transform.parent = shoal.parent.transform;
                    fish.go.transform.localPosition = fish.offset;
                    // Reset dopo 30 secondi
                    StartCoroutine(ResetFishCoroutine(fish, 30f));
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Chiamato quando il pesce viene salpato (catturato).
    /// Ritorna il peso del tonno.
    /// </summary>
    public float OnFishLanded(GameObject fishGO)
    {
        foreach (var shoal in shoals)
        {
            foreach (var fish in shoal.fish)
            {
                if (fish.go == fishGO)
                {
                    float w = fish.weight;
                    fish.state = FishState.Landed;
                    fish.go.SetActive(false);
                    Debug.Log($"TONNO SALPATO! {w:F1}kg — Complimenti!");
                    // Respawn dopo 2 minuti
                    StartCoroutine(RespawnFishCoroutine(fish, shoal, 120f));
                    return w;
                }
            }
        }
        return 0f;
    }

    System.Collections.IEnumerator ResetFishCoroutine(TunaFish fish, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (fish.go != null)
        {
            fish.state = FishState.Swimming;
            fish.stamina = 1f;
        }
    }

    System.Collections.IEnumerator RespawnFishCoroutine(TunaFish fish, TunaShoal shoal, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (fish.go != null)
        {
            fish.go.SetActive(true);
            fish.state = FishState.Swimming;
            fish.stamina = 1f;
            fish.go.transform.parent = shoal.parent.transform;
            fish.go.transform.localPosition = fish.offset;
        }
    }

    // ================================================================
    //  ANIMAZIONE
    // ================================================================

    void AnimateTail(TunaFish fish)
    {
        if (fish.go == null) return;

        // Velocita' coda: piu' veloce quando insegue o combatte
        float speedMul = fish.state == FishState.Swimming ? 1f :
                         fish.state == FishState.Chasing ? 2f : 1.5f;
        float angle = Mathf.Sin(Time.time * fish.tailSpeed * speedMul + fish.tailPhase) * 15f;

        // Ruota il corpo leggermente per simulare il nuoto ondulatorio
        if (fish.body != null)
        {
            fish.body.localRotation = Quaternion.Euler(0, angle, 0);
        }
    }

    // ================================================================
    //  UTILITY
    // ================================================================

    Vector3 PickShoalTarget(float depth)
    {
        float wy = seaLevel;
        return new Vector3(
            Random.Range(waterBounds.min.x + waterBounds.size.x * 0.15f,
                         waterBounds.max.x - waterBounds.size.x * 0.1f),
            wy - depth,
            Random.Range(waterBounds.min.z + waterBounds.size.z * 0.15f,
                         waterBounds.max.z - waterBounds.size.z * 0.1f)
        );
    }

    float GetWaterHeight(Vector3 pos)
    {
        // Usa WaterSurface se disponibile, altrimenti seaLevel piatto
        // WaterSurface.GetWaveHeight e' l'API statica del sistema onde
        var waterType = System.Type.GetType("WaterSurface");
        if (waterType != null)
        {
            var method = waterType.GetMethod("GetWaveHeight",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (method != null)
                return (float)method.Invoke(null, new object[] { pos });
        }
        return seaLevel;
    }

    // ================================================================
    //  PUBLIC API (per UI e debug)
    // ================================================================

    /// <summary>Numero totale di tonni attivi nel golfo.</summary>
    public int GetTotalFishCount()
    {
        int count = 0;
        foreach (var s in shoals)
            foreach (var f in s.fish)
                if (f.state != FishState.Landed) count++;
        return count;
    }

    /// <summary>Numero di tonni attualmente agganciati.</summary>
    public int GetHookedCount()
    {
        int count = 0;
        foreach (var s in shoals)
            foreach (var f in s.fish)
                if (f.state == FishState.Hooked) count++;
        return count;
    }

    /// <summary>Info sul pesce agganciato sulla canna specificata.</summary>
    public bool GetHookedFishInfo(int rodIndex, out float weight, out float stamina, out float force)
    {
        weight = stamina = force = 0;
        foreach (var s in shoals)
        {
            foreach (var f in s.fish)
            {
                if (f.state == FishState.Hooked && f.targetRod == rodIndex)
                {
                    weight = f.weight;
                    stamina = f.stamina;
                    force = f.fightForce;
                    return true;
                }
            }
        }
        return false;
    }
}
