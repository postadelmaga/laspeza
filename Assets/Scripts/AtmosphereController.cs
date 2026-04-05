using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

/// <summary>
/// Gestisce atmosfera realistica: ciclo giorno/notte, cielo stellato,
/// nuvole procedurali e stormi di uccelli.
///
/// Controlli:
///   N         = alterna giorno / notte
///   L         = ciclo continuo alba→tramonto→notte (time-lapse)
/// </summary>
public class AtmosphereController : MonoBehaviour
{
    [Header("Ciclo Giorno/Notte")]
    public bool nightMode = false;
    public bool timeLapse = false;
    public float timeLapseSpeed = 0.02f; // velocita' ciclo (0=fermo, 1=veloce)
    [Range(0f, 1f)]
    public float timeOfDay = 0.35f; // 0=mezzanotte, 0.25=alba, 0.5=mezzogiorno, 0.75=tramonto

    [Header("Stelle")]
    public int starCount = 2000;

    [Header("Nuvole")]
    public int cloudCount = 25;
    public float cloudAltitude = 800f;
    public float cloudSpeed = 2f;

    [Header("Uccelli")]
    public int flockCount = 4;
    public int birdsPerFlock = 8;
    public float birdAltitudeMin = 40f;
    public float birdAltitudeMax = 120f;
    public float birdSpeed = 15f;

    // Riferimenti interni
    private Light sunLight;
    private Light fillLight;
    private Light moonLight;
    private GameObject starSphere;
    private Material starMaterial;
    private Material skyMatDay;
    private Material skyMatNight;

    private List<CloudData> clouds = new List<CloudData>();
    private List<FlockData> flocks = new List<FlockData>();
    private Material cloudMaterial;

    private Bounds worldBounds;
    private bool initialized;

    // ================================================================
    //  INIT
    // ================================================================

    void Start()
    {
        FindWorldBounds();
        FindLights();
        CreateStarSphere();
        CreateClouds();
        CreateBirdFlocks();

        // Salva skybox day
        skyMatDay = RenderSettings.skybox;

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

    void FindLights()
    {
        foreach (var l in FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (l.gameObject.name == "Sole_Mediterraneo") sunLight = l;
            else if (l.gameObject.name == "Luce_Riempimento") fillLight = l;
        }

        // Crea luce lunare
        var moonGo = new GameObject("Luna");
        moonLight = moonGo.AddComponent<Light>();
        moonLight.type = LightType.Directional;
        moonLight.color = new Color(0.4f, 0.45f, 0.6f);
        moonLight.intensity = 0f;
        moonLight.shadows = LightShadows.Soft;
        moonLight.shadowStrength = 0.4f;
        moonLight.shadowResolution = UnityEngine.Rendering.LightShadowResolution.Low;
        moonLight.transform.rotation = Quaternion.Euler(35f, 150f, 0f);
    }

    // ================================================================
    //  STELLE
    // ================================================================

    void CreateStarSphere()
    {
        // Sfera invertita gigante con texture stelle procedurale
        starSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        starSphere.name = "CieloStellato";
        Destroy(starSphere.GetComponent<Collider>());

        float radius = worldBounds.size.magnitude * 0.8f;
        starSphere.transform.position = worldBounds.center;
        starSphere.transform.localScale = Vector3.one * radius;

        // Inverti le normali per renderizzare dall'interno
        Mesh mesh = starSphere.GetComponent<MeshFilter>().mesh;
        int[] tris = mesh.triangles;
        for (int i = 0; i < tris.Length; i += 3)
        {
            int tmp = tris[i];
            tris[i] = tris[i + 2];
            tris[i + 2] = tmp;
        }
        mesh.triangles = tris;
        mesh.RecalculateNormals();

        // Texture stellata procedurale
        Texture2D starTex = GenerateStarTexture(1024, starCount);

        Shader shader = Shader.Find("Unlit/Transparent");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Texture");

        starMaterial = new Material(shader);
        starMaterial.mainTexture = starTex;
        starMaterial.color = Color.white;
        starMaterial.renderQueue = 1000; // prima di tutto il resto

        // Per URP: setta transparent
        if (starMaterial.HasProperty("_Surface"))
        {
            starMaterial.SetFloat("_Surface", 1f);
            starMaterial.SetOverrideTag("RenderType", "Transparent");
            starMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            starMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        }

        starSphere.GetComponent<Renderer>().sharedMaterial = starMaterial;
        starSphere.SetActive(false); // nascosto di giorno
    }

    Texture2D GenerateStarTexture(int size, int count)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        Color[] pixels = new Color[size * size];

        // Sfondo trasparente scuro
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Color(0, 0, 0, 0);

        Random.InitState(12345);
        for (int i = 0; i < count; i++)
        {
            int x = Random.Range(0, size);
            int y = Random.Range(0, size);
            float brightness = Random.Range(0.5f, 1f);
            float starSize = Random.Range(0f, 1f);

            // Colore stella: bianco, leggermente giallo o blu
            Color starColor;
            float r = Random.value;
            if (r < 0.7f) starColor = new Color(1f, 1f, brightness, brightness); // bianca
            else if (r < 0.85f) starColor = new Color(1f, 0.9f, 0.7f, brightness); // gialla
            else starColor = new Color(0.7f, 0.8f, 1f, brightness); // blu

            pixels[y * size + x] = starColor;

            // Stelle piu' grandi: bagliore 3x3
            if (starSize > 0.85f)
            {
                float glow = brightness * 0.3f;
                Color glowColor = new Color(starColor.r, starColor.g, starColor.b, glow);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int gx = Mathf.Clamp(x + dx, 0, size - 1);
                        int gy = Mathf.Clamp(y + dy, 0, size - 1);
                        if (pixels[gy * size + gx].a < glow)
                            pixels[gy * size + gx] = glowColor;
                    }
            }
        }

        tex.SetPixels(pixels);
        tex.Apply();
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Point;
        return tex;
    }

    // ================================================================
    //  NUVOLE
    // ================================================================

    class CloudData
    {
        public GameObject go;
        public Vector3 velocity;
        public float baseY;
    }

    void CreateClouds()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");

        cloudMaterial = new Material(shader);
        cloudMaterial.color = new Color(0.95f, 0.95f, 0.97f, 0.6f);

        // Transparent setup
        if (cloudMaterial.HasProperty("_Surface"))
        {
            cloudMaterial.SetFloat("_Surface", 1f);
            cloudMaterial.SetFloat("_Blend", 0f);
            cloudMaterial.SetOverrideTag("RenderType", "Transparent");
            cloudMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            cloudMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            cloudMaterial.SetInt("_ZWrite", 0);
            cloudMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        else if (cloudMaterial.HasProperty("_Mode"))
        {
            cloudMaterial.SetFloat("_Mode", 3f);
            cloudMaterial.SetOverrideTag("RenderType", "Transparent");
            cloudMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            cloudMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            cloudMaterial.SetInt("_ZWrite", 0);
            cloudMaterial.EnableKeyword("_ALPHABLEND_ON");
            cloudMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        if (cloudMaterial.HasProperty("_Smoothness")) cloudMaterial.SetFloat("_Smoothness", 0f);
        if (cloudMaterial.HasProperty("_Metallic")) cloudMaterial.SetFloat("_Metallic", 0f);

        // Emission morbida per luminosita' propria
        cloudMaterial.EnableKeyword("_EMISSION");
        if (cloudMaterial.HasProperty("_EmissionColor"))
            cloudMaterial.SetColor("_EmissionColor", new Color(0.4f, 0.4f, 0.42f));

        Random.InitState(777);

        for (int i = 0; i < cloudCount; i++)
        {
            // Ogni nuvola = gruppo di ellissoidi sovrapposti
            GameObject cloud = new GameObject($"Nuvola_{i}");

            float cx = Random.Range(worldBounds.min.x, worldBounds.max.x);
            float cz = Random.Range(worldBounds.min.z, worldBounds.max.z);
            float cy = cloudAltitude + Random.Range(-100f, 100f);

            cloud.transform.position = new Vector3(cx, cy, cz);

            int blobCount = Random.Range(3, 7);
            for (int b = 0; b < blobCount; b++)
            {
                GameObject blob = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(blob.GetComponent<Collider>());
                blob.transform.parent = cloud.transform;

                float bx = Random.Range(-80f, 80f);
                float by = Random.Range(-10f, 15f);
                float bz = Random.Range(-40f, 40f);
                blob.transform.localPosition = new Vector3(bx, by, bz);

                float sx = Random.Range(60f, 180f);
                float sy = Random.Range(15f, 40f);
                float sz = Random.Range(40f, 100f);
                blob.transform.localScale = new Vector3(sx, sy, sz);

                blob.GetComponent<Renderer>().sharedMaterial = cloudMaterial;
            }

            Vector3 vel = new Vector3(
                Random.Range(-1f, 1f) * cloudSpeed,
                0,
                Random.Range(-0.5f, 0.5f) * cloudSpeed * 0.3f
            );

            clouds.Add(new CloudData { go = cloud, velocity = vel, baseY = cy });
        }
    }

    // ================================================================
    //  UCCELLI
    // ================================================================

    class FlockData
    {
        public GameObject parent;
        public Vector3 target;
        public List<BirdData> birds;
        public float changeTimer;
    }

    class BirdData
    {
        public GameObject go;
        public Vector3 offset;
        public float wingPhase;
        public float wingSpeed;
        public Transform leftWing, rightWing;
    }

    void CreateBirdFlocks()
    {
        Random.InitState(999);

        Material birdMat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
        birdMat.color = new Color(0.12f, 0.12f, 0.15f);
        if (birdMat.HasProperty("_Smoothness")) birdMat.SetFloat("_Smoothness", 0.1f);

        for (int f = 0; f < flockCount; f++)
        {
            var flock = new FlockData
            {
                parent = new GameObject($"Stormo_{f}"),
                birds = new List<BirdData>(),
                changeTimer = Random.Range(5f, 15f)
            };

            Vector3 startPos = new Vector3(
                Random.Range(worldBounds.min.x, worldBounds.max.x),
                Random.Range(birdAltitudeMin, birdAltitudeMax),
                Random.Range(worldBounds.min.z, worldBounds.max.z)
            );
            flock.parent.transform.position = startPos;
            flock.target = PickFlockTarget();

            for (int b = 0; b < birdsPerFlock; b++)
            {
                var bird = CreateBird(birdMat);
                bird.go.transform.parent = flock.parent.transform;
                bird.offset = new Vector3(
                    Random.Range(-8f, 8f),
                    Random.Range(-2f, 2f),
                    Random.Range(-8f, 8f)
                );
                bird.go.transform.localPosition = bird.offset;
                bird.wingPhase = Random.Range(0f, Mathf.PI * 2f);
                bird.wingSpeed = Random.Range(6f, 10f);
                flock.birds.Add(bird);
            }

            flocks.Add(flock);
        }
    }

    BirdData CreateBird(Material mat)
    {
        GameObject bird = new GameObject("Uccello");

        // Corpo: piccolo ellissoide
        GameObject body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(body.GetComponent<Collider>());
        body.transform.parent = bird.transform;
        body.transform.localScale = new Vector3(0.3f, 0.25f, 0.8f);
        body.GetComponent<Renderer>().sharedMaterial = mat;

        // Ali: due quad piatti che battono
        GameObject leftWing = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(leftWing.GetComponent<Collider>());
        leftWing.transform.parent = bird.transform;
        leftWing.transform.localPosition = new Vector3(-0.5f, 0, 0);
        leftWing.transform.localScale = new Vector3(1f, 0.03f, 0.4f);
        leftWing.GetComponent<Renderer>().sharedMaterial = mat;

        GameObject rightWing = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(rightWing.GetComponent<Collider>());
        rightWing.transform.parent = bird.transform;
        rightWing.transform.localPosition = new Vector3(0.5f, 0, 0);
        rightWing.transform.localScale = new Vector3(1f, 0.03f, 0.4f);
        rightWing.GetComponent<Renderer>().sharedMaterial = mat;

        return new BirdData
        {
            go = bird,
            leftWing = leftWing.transform,
            rightWing = rightWing.transform
        };
    }

    Vector3 PickFlockTarget()
    {
        return new Vector3(
            Random.Range(worldBounds.min.x, worldBounds.max.x),
            Random.Range(birdAltitudeMin, birdAltitudeMax),
            Random.Range(worldBounds.min.z, worldBounds.max.z)
        );
    }

    // ================================================================
    //  UPDATE
    // ================================================================

    void Update()
    {
        if (!initialized) return;

        // Toggle notte
        var kb = Keyboard.current;
        if (kb != null && kb.nKey.wasPressedThisFrame)
        {
            nightMode = !nightMode;
            timeOfDay = nightMode ? 0f : 0.35f;
            ApplyTimeOfDay();
        }

        // Toggle time-lapse
        if (kb != null && kb.lKey.wasPressedThisFrame)
        {
            timeLapse = !timeLapse;
            Debug.Log("Atmosfera: time-lapse " + (timeLapse ? "ON" : "OFF"));
        }

        // Ciclo continuo
        if (timeLapse)
        {
            timeOfDay += timeLapseSpeed * Time.deltaTime;
            if (timeOfDay > 1f) timeOfDay -= 1f;
            ApplyTimeOfDay();
        }

        UpdateClouds();
        UpdateBirdFlocks();
    }

    // ================================================================
    //  CICLO GIORNO/NOTTE
    // ================================================================

    void ApplyTimeOfDay()
    {
        // timeOfDay: 0=mezzanotte, 0.25=alba, 0.5=mezzogiorno, 0.75=tramonto

        // Angolo sole: mappa timeOfDay a rotazione
        float sunAngle = (timeOfDay - 0.25f) * 360f; // alba a 0°, mezzogiorno a 90°
        if (sunLight != null)
            sunLight.transform.rotation = Quaternion.Euler(sunAngle, -35f, 0f);

        // Intensita' sole: max a mezzogiorno, 0 di notte
        float sunIntensity = Mathf.Clamp01(Mathf.Sin(timeOfDay * Mathf.PI));
        float nightFactor = 1f - sunIntensity;
        float horizonFactor = Mathf.Clamp01(1f - Mathf.Abs(timeOfDay - 0.5f) * 4f); // max ad alba/tramonto

        if (sunLight != null)
        {
            sunLight.intensity = sunIntensity * 1.4f;
            Color sunColor = Color.Lerp(
                new Color(1f, 0.92f, 0.82f),    // mezzogiorno
                new Color(1f, 0.55f, 0.2f),      // alba/tramonto
                horizonFactor * 0.6f
            );
            sunLight.color = sunColor;
        }

        // Fill light
        if (fillLight != null)
            fillLight.intensity = sunIntensity * 0.25f;

        // Luna
        if (moonLight != null)
            moonLight.intensity = nightFactor * 0.15f;

        // Stelle
        if (starSphere != null)
        {
            starSphere.SetActive(nightFactor > 0.1f);
            if (starMaterial != null)
                starMaterial.color = new Color(1, 1, 1, nightFactor);
        }

        // Ambient
        Color daySky = new Color(0.55f, 0.68f, 0.88f);
        Color nightSky = new Color(0.02f, 0.02f, 0.06f);
        Color dayEquator = new Color(0.75f, 0.70f, 0.62f);
        Color nightEquator = new Color(0.03f, 0.03f, 0.07f);
        Color dayGround = new Color(0.35f, 0.30f, 0.25f);
        Color nightGround = new Color(0.01f, 0.01f, 0.02f);

        RenderSettings.ambientSkyColor = Color.Lerp(nightSky, daySky, sunIntensity);
        RenderSettings.ambientEquatorColor = Color.Lerp(nightEquator, dayEquator, sunIntensity);
        RenderSettings.ambientGroundColor = Color.Lerp(nightGround, dayGround, sunIntensity);
        RenderSettings.ambientIntensity = Mathf.Lerp(0.3f, 1.1f, sunIntensity);

        // Fog
        Color dayFog = new Color(0.68f, 0.72f, 0.78f);
        Color nightFog = new Color(0.02f, 0.02f, 0.05f);
        // Alba/tramonto: foschia dorata
        Color sunsetFog = new Color(0.6f, 0.35f, 0.2f);
        Color currentFog = Color.Lerp(nightFog, dayFog, sunIntensity);
        if (horizonFactor > 0)
            currentFog = Color.Lerp(currentFog, sunsetFog, horizonFactor * 0.3f);
        RenderSettings.fogColor = currentFog;

        // Skybox: modifica exposure e tint
        Material sky = RenderSettings.skybox;
        if (sky != null)
        {
            if (sky.HasProperty("_Exposure"))
                sky.SetFloat("_Exposure", Mathf.Lerp(0.05f, 1.2f, sunIntensity));
            if (sky.HasProperty("_AtmosphereThickness"))
                sky.SetFloat("_AtmosphereThickness", Mathf.Lerp(0.3f, 1.2f, sunIntensity));
            if (sky.HasProperty("_SkyTint"))
            {
                Color dayTint = new Color(0.45f, 0.58f, 0.82f);
                Color nightTint = new Color(0.05f, 0.05f, 0.15f);
                sky.SetColor("_SkyTint", Color.Lerp(nightTint, dayTint, sunIntensity));
            }
        }

        // Nuvole: colore in base all'ora
        if (cloudMaterial != null)
        {
            Color dayCloud = new Color(0.95f, 0.95f, 0.97f, 0.55f);
            Color nightCloud = new Color(0.08f, 0.08f, 0.12f, 0.4f);
            Color sunsetCloud = new Color(0.95f, 0.6f, 0.35f, 0.6f);

            Color cloudColor = Color.Lerp(nightCloud, dayCloud, sunIntensity);
            if (horizonFactor > 0)
                cloudColor = Color.Lerp(cloudColor, sunsetCloud, horizonFactor * 0.5f);
            cloudMaterial.color = cloudColor;

            Color emDay = new Color(0.4f, 0.4f, 0.42f);
            Color emNight = new Color(0.01f, 0.01f, 0.02f);
            Color emSunset = new Color(0.5f, 0.25f, 0.1f);
            Color em = Color.Lerp(emNight, emDay, sunIntensity);
            if (horizonFactor > 0)
                em = Color.Lerp(em, emSunset, horizonFactor * 0.4f);
            if (cloudMaterial.HasProperty("_EmissionColor"))
                cloudMaterial.SetColor("_EmissionColor", em);
        }

        DynamicGI.UpdateEnvironment();
    }

    // ================================================================
    //  NUVOLE UPDATE
    // ================================================================

    void UpdateClouds()
    {
        float halfW = worldBounds.size.x * 0.7f;
        float halfZ = worldBounds.size.z * 0.7f;

        foreach (var c in clouds)
        {
            if (c.go == null) continue;

            Vector3 pos = c.go.transform.position;
            pos += c.velocity * Time.deltaTime;

            // Wrap around: se esce dai limiti, riappare dall'altro lato
            if (pos.x > worldBounds.center.x + halfW) pos.x -= halfW * 2f;
            if (pos.x < worldBounds.center.x - halfW) pos.x += halfW * 2f;
            if (pos.z > worldBounds.center.z + halfZ) pos.z -= halfZ * 2f;
            if (pos.z < worldBounds.center.z - halfZ) pos.z += halfZ * 2f;

            c.go.transform.position = pos;
        }
    }

    // ================================================================
    //  UCCELLI UPDATE
    // ================================================================

    void UpdateBirdFlocks()
    {
        foreach (var flock in flocks)
        {
            if (flock.parent == null) continue;

            // Muovi lo stormo verso il target
            Vector3 pos = flock.parent.transform.position;
            Vector3 dir = (flock.target - pos);
            float dist = dir.magnitude;

            if (dist < 50f)
            {
                flock.target = PickFlockTarget();
                flock.changeTimer = Random.Range(8f, 20f);
            }
            else
            {
                dir /= dist;
                pos += dir * birdSpeed * Time.deltaTime;
                flock.parent.transform.position = pos;

                // Orienta lo stormo nella direzione di volo
                Quaternion targetRot = Quaternion.LookRotation(dir);
                flock.parent.transform.rotation = Quaternion.Slerp(
                    flock.parent.transform.rotation, targetRot, Time.deltaTime * 2f);
            }

            // Timer per cambiare target casualmente
            flock.changeTimer -= Time.deltaTime;
            if (flock.changeTimer <= 0)
            {
                flock.target = PickFlockTarget();
                flock.changeTimer = Random.Range(8f, 20f);
            }

            // Anima ogni uccello
            foreach (var bird in flock.birds)
            {
                if (bird.go == null) continue;

                // Oscillazione leggera attorno alla posizione nello stormo
                float t = Time.time;
                Vector3 localPos = bird.offset;
                localPos.x += Mathf.Sin(t * 0.5f + bird.wingPhase) * 0.5f;
                localPos.y += Mathf.Sin(t * 0.3f + bird.wingPhase * 1.3f) * 0.3f;
                bird.go.transform.localPosition = Vector3.Lerp(
                    bird.go.transform.localPosition, localPos, Time.deltaTime * 3f);

                // Battito d'ali
                float wingAngle = Mathf.Sin(t * bird.wingSpeed + bird.wingPhase) * 30f;
                if (bird.leftWing != null)
                    bird.leftWing.localRotation = Quaternion.Euler(0, 0, wingAngle);
                if (bird.rightWing != null)
                    bird.rightWing.localRotation = Quaternion.Euler(0, 0, -wingAngle);
            }
        }
    }

    // ================================================================
    //  CLEANUP
    // ================================================================

    void OnDestroy()
    {
        if (starSphere != null) Destroy(starSphere);
        foreach (var c in clouds) { if (c.go != null) Destroy(c.go); }
        foreach (var f in flocks) { if (f.parent != null) Destroy(f.parent); }
        if (moonLight != null) Destroy(moonLight.gameObject);
    }
}
