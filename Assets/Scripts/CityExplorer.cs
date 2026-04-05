using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

/// <summary>
/// Controller camera per esplorare la citta generata.
/// Usa il nuovo Input System.
///
/// Controlli:
///   WASD / Frecce  = muovi
///   Mouse          = guarda
///   Scroll / Q-E   = su/giu
///   Shift          = veloce
///   Tab            = cambia modalita (volo / camminata)
///   F              = centra sulla citta
///   G              = modalita guida (percorre le strade)
///   1-3            = preset camera (panoramica, strada, volo alto)
///
/// In modalita guida:
///   W / Freccia su = accelera
///   S / Freccia giu = frena / retromarcia
///   A/D            = sterza
///   Spazio         = freno a mano
///   V              = cambia vista (dietro, cofano, laterale)
///   ESC            = esci dalla guida
/// </summary>
[RequireComponent(typeof(Camera))]
public class CityExplorer : MonoBehaviour
{
    [Header("Movimento")]
    public float moveSpeed = 200f;
    public float fastMultiplier = 5f;
    public float scrollSpeed = 500f;

    [Header("Mouse Look")]
    public float mouseSensitivity = 2f;
    public float smoothing = 5f;

    [Header("Camminata")]
    public float walkHeight = 1.8f;
    public float walkSpeed = 5f;

    [Header("Limiti")]
    public float minHeight = 0.5f;
    public float maxHeight = 20000f;

    [Header("Guida")]
    public float maxDriveSpeed = 15f;
    public float acceleration = 8f;
    public float brakeForce = 15f;
    public float steerSpeed = 90f;
    public float vehicleHeight = 1.2f;

    private float rotX, rotY;
    private bool cursorLocked;

    // Driving mode
    private enum Mode { Fly, Walk, Drive }
    private Mode currentMode = Mode.Fly;

    private float driveSpeed;
    private float driveYaw;
    private Vector3 drivePosition;
    private int cameraView; // 0=behind, 1=hood, 2=side

    // Vehicle mesh
    private GameObject vehicleGO;

    // Road waypoints for AI follow (optional)
    private List<List<Vector3>> roadPaths;
    private int currentPathIndex;
    private int currentWaypointIndex;
    // private bool autoFollow; // riservato per follow automatico futuro

    private Keyboard kb;
    private Mouse mouse;
    private GUIStyle cachedGuiStyle;

    void Start()
    {
        kb = Keyboard.current;
        mouse = Mouse.current;

        LockCursor(true);

        var world = GameObject.Find("CityBuilder_World");
        if (world != null)
        {
            Bounds b = CalculateWorldBounds(world);
            transform.position = b.center + Vector3.up * Mathf.Max(b.size.x, b.size.z) * 0.4f;
            transform.LookAt(b.center);
        }

        Vector3 euler = transform.eulerAngles;
        rotX = euler.y;
        rotY = euler.x;
    }

    void Update()
    {
        if (kb == null) kb = Keyboard.current;
        if (mouse == null) mouse = Mouse.current;
        if (kb == null || mouse == null) return;

        HandleCursorLock();
        if (!cursorLocked) return;

        // Mode switching
        if (kb.tabKey.wasPressedThisFrame && currentMode != Mode.Drive)
        {
            currentMode = currentMode == Mode.Fly ? Mode.Walk : Mode.Fly;
            Debug.Log("CityExplorer: " + (currentMode == Mode.Fly ? "Volo libero" : "Camminata"));
        }

        if (kb.gKey.wasPressedThisFrame)
        {
            if (currentMode == Mode.Drive)
                ExitDriveMode();
            else
                EnterDriveMode();
        }

        // Barca da pesca (disponibile solo con FishingSim scripts)
        if (kb.bKey.wasPressedThisFrame)
        {
            var boatType = System.Type.GetType("FishingBoat");
            if (boatType != null)
            {
                var boat = FindAnyObjectByType(boatType) as MonoBehaviour;
                if (boat != null)
                {
                    boatType.GetMethod("EnterBoat")?.Invoke(boat, null);
                    enabled = false;
                    Debug.Log("CityExplorer: passaggio a modalita' barca. Premi B per uscire.");
                }
            }
        }

        if (currentMode == Mode.Drive)
        {
            if (kb.escapeKey.wasPressedThisFrame)
            {
                ExitDriveMode();
                return;
            }
            HandleDriveMode();
            return;
        }

        HandleMouseLook();

        if (kb.fKey.wasPressedThisFrame) FocusOnCity();
        if (kb.digit1Key.wasPressedThisFrame) PresetPanoramica();
        if (kb.digit2Key.wasPressedThisFrame) PresetStrada();
        if (kb.digit3Key.wasPressedThisFrame) PresetVoloAlto();

        if (currentMode == Mode.Fly)
            FlyMovement();
        else
            WalkMovement();
    }

    // ================================================================
    //  DRIVE MODE
    // ================================================================

    void EnterDriveMode()
    {
        currentMode = Mode.Drive;
        driveSpeed = 0f;
        driveYaw = rotX;
        drivePosition = transform.position;
        drivePosition.y = GetGroundHeight(drivePosition) + vehicleHeight;
        cameraView = 0;

        // Collect road paths if not done yet
        if (roadPaths == null)
            CollectRoadPaths();

        // Find nearest road to start on
        SnapToNearestRoad();

        // Create vehicle mesh
        if (vehicleGO == null)
            vehicleGO = CreateVehicleMesh();
        vehicleGO.SetActive(true);

        Debug.Log("CityExplorer: GUIDA — WASD sterza/accelera, V vista, G/ESC esci");
    }

    void ExitDriveMode()
    {
        currentMode = Mode.Fly;
        driveSpeed = 0f;

        if (vehicleGO != null)
            vehicleGO.SetActive(false);

        UpdateRotFromTransform();
        Debug.Log("CityExplorer: Volo libero");
    }

    void HandleDriveMode()
    {
        // Camera view switch
        if (kb.vKey.wasPressedThisFrame)
            cameraView = (cameraView + 1) % 3;

        // Steering
        float steerInput = 0f;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) steerInput -= 1f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) steerInput += 1f;

        // Only steer when moving
        if (Mathf.Abs(driveSpeed) > 0.5f)
            driveYaw += steerInput * steerSpeed * Time.deltaTime * Mathf.Sign(driveSpeed);

        // Throttle / brake
        float throttle = 0f;
        if (kb.wKey.isPressed || kb.upArrowKey.isPressed) throttle = 1f;
        if (kb.sKey.isPressed || kb.downArrowKey.isPressed) throttle = -1f;

        // Handbrake
        bool handbrake = kb.spaceKey.isPressed;

        if (handbrake)
        {
            driveSpeed = Mathf.MoveTowards(driveSpeed, 0f, brakeForce * 2f * Time.deltaTime);
        }
        else if (throttle != 0f)
        {
            // Accelerate or brake depending on direction
            if (Mathf.Sign(throttle) == Mathf.Sign(driveSpeed) || Mathf.Abs(driveSpeed) < 0.1f)
            {
                // Same direction: accelerate
                float maxSpd = throttle > 0 ? maxDriveSpeed : maxDriveSpeed * 0.4f;
                driveSpeed += throttle * acceleration * Time.deltaTime;
                driveSpeed = Mathf.Clamp(driveSpeed, -maxSpd, maxSpd);
            }
            else
            {
                // Opposite direction: brake first
                driveSpeed = Mathf.MoveTowards(driveSpeed, 0f, brakeForce * Time.deltaTime);
            }
        }
        else
        {
            // No input: gentle deceleration (friction)
            driveSpeed = Mathf.MoveTowards(driveSpeed, 0f, 3f * Time.deltaTime);
        }

        // Fast mode
        float speedMul = kb.leftShiftKey.isPressed ? fastMultiplier : 1f;

        // Move vehicle
        Vector3 forward = Quaternion.Euler(0, driveYaw, 0) * Vector3.forward;
        drivePosition += forward * driveSpeed * speedMul * Time.deltaTime;

        // Stick to ground
        float groundY = GetGroundHeight(drivePosition);
        drivePosition.y = Mathf.Lerp(drivePosition.y, groundY + vehicleHeight, Time.deltaTime * 8f);

        // Update vehicle GO
        if (vehicleGO != null)
        {
            vehicleGO.transform.position = drivePosition - Vector3.up * (vehicleHeight - 0.3f);
            vehicleGO.transform.rotation = Quaternion.Euler(0, driveYaw, 0);
        }

        // Camera follows vehicle
        UpdateDriveCamera(forward);
    }

    void UpdateDriveCamera(Vector3 forward)
    {
        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

        switch (cameraView)
        {
            case 0: // Behind
                Vector3 behindPos = drivePosition - forward * 6f + Vector3.up * 2.5f;
                transform.position = Vector3.Lerp(transform.position, behindPos, Time.deltaTime * 6f);
                Quaternion lookRot = Quaternion.LookRotation(drivePosition + forward * 5f - transform.position);
                transform.rotation = Quaternion.Slerp(transform.rotation, lookRot, Time.deltaTime * 5f);
                break;

            case 1: // Hood (first person)
                transform.position = drivePosition + forward * 0.8f + Vector3.up * 0.5f;
                // Allow mouse look in hood view
                HandleMouseLook();
                break;

            case 2: // Side/orbital
                float orbitAngle = Time.time * 15f;
                Vector3 orbitOffset = Quaternion.Euler(20f, driveYaw + orbitAngle, 0) * Vector3.back * 8f;
                transform.position = Vector3.Lerp(transform.position, drivePosition + orbitOffset, Time.deltaTime * 4f);
                transform.LookAt(drivePosition);
                break;
        }

        // Update rotX/rotY to match
        if (cameraView != 1)
            UpdateRotFromTransform();
    }

    // ================================================================
    //  ROAD PATH COLLECTION
    // ================================================================

    void CollectRoadPaths()
    {
        roadPaths = new List<List<Vector3>>();

        var world = GameObject.Find("CityBuilder_World");
        if (world == null) return;

        // Find the "Strade" parent and collect road meshes
        // We reconstruct paths from the road mesh vertices
        var stradeParent = world.transform.Find("Strade");
        if (stradeParent == null) return;

        // Find asphalt mesh - roads are stored as a single combined mesh
        // Instead, we look for the original road data if available
        // Since we can't access editor data at runtime, we use a different approach:
        // Sample the terrain to find road-like surfaces, or use the mesh bounds

        // Simpler approach: find all MeshFilter children of Strade
        // and extract centerlines from the mesh strip topology
        var asphaltObj = stradeParent.Find("Strade_Asfalto");
        if (asphaltObj == null) return;

        var mf = asphaltObj.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return;

        // Extract road network from mesh - sample vertex positions
        // Group vertices into road segments by proximity
        Vector3[] verts = mf.sharedMesh.vertices;
        if (verts.Length < 4) return;

        // Road mesh is built as quad strips: pairs of vertices per centerline point
        // We extract centerlines by averaging pairs
        List<Vector3> centers = new List<Vector3>();
        for (int i = 0; i < verts.Length - 1; i += 2)
        {
            Vector3 c = (verts[i] + verts[i + 1]) * 0.5f;
            centers.Add(c);
        }

        // Split into separate paths when distance between consecutive centers is large
        List<Vector3> currentPath = new List<Vector3>();
        float splitThreshold = 5f; // distance that indicates a new road segment

        for (int i = 0; i < centers.Count; i++)
        {
            if (currentPath.Count > 0)
            {
                float dist = Vector3.Distance(centers[i], currentPath[currentPath.Count - 1]);
                if (dist > splitThreshold)
                {
                    if (currentPath.Count >= 3)
                        roadPaths.Add(new List<Vector3>(currentPath));
                    currentPath.Clear();
                }
            }
            currentPath.Add(centers[i]);
        }
        if (currentPath.Count >= 3)
            roadPaths.Add(new List<Vector3>(currentPath));

        Debug.Log($"CityExplorer: {roadPaths.Count} percorsi stradali trovati ({centers.Count} waypoints totali)");
    }

    void SnapToNearestRoad()
    {
        if (roadPaths == null || roadPaths.Count == 0) return;

        float bestDist = float.MaxValue;
        int bestPath = 0;
        int bestWP = 0;

        for (int p = 0; p < roadPaths.Count; p++)
        {
            for (int w = 0; w < roadPaths[p].Count; w++)
            {
                float d = Vector3.Distance(drivePosition, roadPaths[p][w]);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestPath = p;
                    bestWP = w;
                }
            }
        }

        currentPathIndex = bestPath;
        currentWaypointIndex = bestWP;

        if (bestDist < 50f)
        {
            drivePosition = roadPaths[bestPath][bestWP];
            drivePosition.y = GetGroundHeight(drivePosition) + vehicleHeight;

            // Orient towards next waypoint
            if (bestWP + 1 < roadPaths[bestPath].Count)
            {
                Vector3 dir = roadPaths[bestPath][bestWP + 1] - roadPaths[bestPath][bestWP];
                dir.y = 0;
                if (dir.sqrMagnitude > 0.01f)
                    driveYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            }
        }
    }

    // ================================================================
    //  VEHICLE MESH (procedural)
    // ================================================================

    GameObject CreateVehicleMesh()
    {
        GameObject car = new GameObject("Veicolo_Guida");

        // Body
        GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.transform.parent = car.transform;
        body.transform.localPosition = new Vector3(0, 0.4f, 0);
        body.transform.localScale = new Vector3(1.8f, 0.7f, 4f);
        var bodyMat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
        bodyMat.color = new Color(0.15f, 0.25f, 0.5f); // blu scuro
        body.GetComponent<Renderer>().sharedMaterial = bodyMat;
        Object.Destroy(body.GetComponent<Collider>());

        // Cabin (upper part)
        GameObject cabin = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cabin.transform.parent = car.transform;
        cabin.transform.localPosition = new Vector3(0, 0.95f, -0.3f);
        cabin.transform.localScale = new Vector3(1.6f, 0.6f, 2f);
        var cabinMat = new Material(bodyMat);
        cabinMat.color = new Color(0.2f, 0.3f, 0.55f);
        cabin.GetComponent<Renderer>().sharedMaterial = cabinMat;
        Object.Destroy(cabin.GetComponent<Collider>());

        // Wheels (4 cylinders)
        float[][] wheelPos = new float[][] {
            new float[] { -0.8f, 0.15f,  1.2f },
            new float[] {  0.8f, 0.15f,  1.2f },
            new float[] { -0.8f, 0.15f, -1.2f },
            new float[] {  0.8f, 0.15f, -1.2f }
        };

        var wheelMat = new Material(bodyMat);
        wheelMat.color = new Color(0.1f, 0.1f, 0.1f);

        foreach (var wp in wheelPos)
        {
            GameObject wheel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            wheel.transform.parent = car.transform;
            wheel.transform.localPosition = new Vector3(wp[0], wp[1], wp[2]);
            wheel.transform.localScale = new Vector3(0.35f, 0.12f, 0.35f);
            wheel.transform.localRotation = Quaternion.Euler(0, 0, 90);
            wheel.GetComponent<Renderer>().sharedMaterial = wheelMat;
            Object.Destroy(wheel.GetComponent<Collider>());
        }

        // Headlights
        var lightMat = new Material(bodyMat);
        lightMat.color = new Color(1f, 0.95f, 0.8f);
        lightMat.EnableKeyword("_EMISSION");
        if (lightMat.HasProperty("_EmissionColor"))
            lightMat.SetColor("_EmissionColor", new Color(1f, 0.9f, 0.7f) * 2f);

        for (int side = -1; side <= 1; side += 2)
        {
            GameObject light = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            light.transform.parent = car.transform;
            light.transform.localPosition = new Vector3(side * 0.6f, 0.4f, 2.05f);
            light.transform.localScale = new Vector3(0.25f, 0.2f, 0.1f);
            light.GetComponent<Renderer>().sharedMaterial = lightMat;
            Object.Destroy(light.GetComponent<Collider>());
        }

        // Taillights
        var tailMat = new Material(bodyMat);
        tailMat.color = new Color(0.8f, 0.05f, 0.05f);
        tailMat.EnableKeyword("_EMISSION");
        if (tailMat.HasProperty("_EmissionColor"))
            tailMat.SetColor("_EmissionColor", new Color(0.8f, 0.05f, 0.05f) * 1.5f);

        for (int side = -1; side <= 1; side += 2)
        {
            GameObject tail = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            tail.transform.parent = car.transform;
            tail.transform.localPosition = new Vector3(side * 0.6f, 0.4f, -2.05f);
            tail.transform.localScale = new Vector3(0.3f, 0.15f, 0.1f);
            tail.GetComponent<Renderer>().sharedMaterial = tailMat;
            Object.Destroy(tail.GetComponent<Collider>());
        }

        return car;
    }

    // ================================================================
    //  MOUSE LOOK
    // ================================================================

    void HandleMouseLook()
    {
        Vector2 delta = mouse.delta.ReadValue();
        float mx = delta.x * mouseSensitivity * 0.1f;
        float my = delta.y * mouseSensitivity * 0.1f;

        rotX += mx;
        rotY -= my;
        rotY = Mathf.Clamp(rotY, -89f, 89f);

        Quaternion target = Quaternion.Euler(rotY, rotX, 0);
        transform.rotation = Quaternion.Lerp(transform.rotation, target, Time.deltaTime * smoothing * 5f);
    }

    // ================================================================
    //  VOLO LIBERO
    // ================================================================

    void FlyMovement()
    {
        float speed = moveSpeed * (kb.leftShiftKey.isPressed ? fastMultiplier : 1f);

        Vector3 move = Vector3.zero;
        if (kb.wKey.isPressed || kb.upArrowKey.isPressed) move += transform.forward;
        if (kb.sKey.isPressed || kb.downArrowKey.isPressed) move -= transform.forward;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) move -= transform.right;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) move += transform.right;
        if (kb.eKey.isPressed) move += Vector3.up;
        if (kb.qKey.isPressed) move -= Vector3.up;

        float scroll = mouse.scroll.ReadValue().y;
        move += Vector3.up * scroll * scrollSpeed * 0.01f;

        if (move.sqrMagnitude > 0.001f)
            transform.position += move.normalized * speed * Time.deltaTime;

        Vector3 pos = transform.position;
        pos.y = Mathf.Clamp(pos.y, minHeight, maxHeight);
        transform.position = pos;
    }

    // ================================================================
    //  CAMMINATA A TERRA
    // ================================================================

    void WalkMovement()
    {
        float speed = walkSpeed * (kb.leftShiftKey.isPressed ? fastMultiplier : 1f);

        Vector3 forward = transform.forward;
        forward.y = 0; forward.Normalize();
        Vector3 right = transform.right;
        right.y = 0; right.Normalize();

        Vector3 move = Vector3.zero;
        if (kb.wKey.isPressed || kb.upArrowKey.isPressed) move += forward;
        if (kb.sKey.isPressed || kb.downArrowKey.isPressed) move -= forward;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) move -= right;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) move += right;

        Vector3 pos = transform.position + move.normalized * speed * Time.deltaTime;

        float groundY = GetGroundHeight(pos);
        pos.y = groundY + walkHeight;

        transform.position = pos;
    }

    float GetGroundHeight(Vector3 pos)
    {
        if (Physics.Raycast(pos + Vector3.up * 500f, Vector3.down, out RaycastHit hit, 1000f))
            return hit.point.y;

        if (Terrain.activeTerrain != null)
            return Terrain.activeTerrain.SampleHeight(pos);

        return 0;
    }

    // ================================================================
    //  PRESET CAMERA
    // ================================================================

    void FocusOnCity()
    {
        var world = GameObject.Find("CityBuilder_World");
        if (world == null) return;

        Bounds b = CalculateWorldBounds(world);
        transform.position = b.center + Vector3.up * b.size.magnitude * 0.3f + Vector3.back * b.size.z * 0.2f;
        transform.LookAt(b.center);
        UpdateRotFromTransform();
        currentMode = Mode.Fly;
    }

    void PresetPanoramica()
    {
        var world = GameObject.Find("CityBuilder_World");
        if (world == null) return;
        Bounds b = CalculateWorldBounds(world);

        transform.position = b.center + new Vector3(-b.size.x * 0.3f, b.size.magnitude * 0.25f, -b.size.z * 0.3f);
        transform.LookAt(b.center);
        UpdateRotFromTransform();
        currentMode = Mode.Fly;
    }

    void PresetStrada()
    {
        var world = GameObject.Find("CityBuilder_World");
        if (world == null) return;
        Bounds b = CalculateWorldBounds(world);

        Vector3 streetPos = b.center;
        streetPos.y = GetGroundHeight(streetPos) + walkHeight;
        transform.position = streetPos;
        rotY = 0;
        UpdateRotFromTransform();
        currentMode = Mode.Walk;
    }

    void PresetVoloAlto()
    {
        var world = GameObject.Find("CityBuilder_World");
        if (world == null) return;
        Bounds b = CalculateWorldBounds(world);

        transform.position = b.center + Vector3.up * b.size.magnitude * 0.6f;
        transform.rotation = Quaternion.Euler(90, 0, 0);
        rotX = 0; rotY = 90;
        currentMode = Mode.Fly;
    }

    // ================================================================
    //  UTILITY
    // ================================================================

    void UpdateRotFromTransform()
    {
        Vector3 euler = transform.eulerAngles;
        rotX = euler.y;
        rotY = euler.x;
        if (rotY > 180) rotY -= 360;
    }

    void HandleCursorLock()
    {
        if (currentMode == Mode.Drive)
        {
            // In drive mode, keep cursor locked
            if (!cursorLocked && mouse.leftButton.wasPressedThisFrame)
                LockCursor(true);
            return;
        }

        if (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame)
            LockCursor(true);
        if (kb.escapeKey.wasPressedThisFrame)
            LockCursor(false);
    }

    void LockCursor(bool locked)
    {
        cursorLocked = locked;
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    Bounds CalculateWorldBounds(GameObject root)
    {
        Terrain[] terrains = root.GetComponentsInChildren<Terrain>();
        if (terrains.Length > 0)
        {
            Bounds b = new Bounds(terrains[0].transform.position, Vector3.zero);
            foreach (var t in terrains)
            {
                b.Encapsulate(t.transform.position);
                b.Encapsulate(t.transform.position + t.terrainData.size);
            }
            return b;
        }

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
        if (renderers.Length > 0)
        {
            Bounds b = renderers[0].bounds;
            foreach (var r in renderers)
                b.Encapsulate(r.bounds);
            return b;
        }

        return new Bounds(root.transform.position, Vector3.one * 100);
    }

    void OnGUI()
    {
        if (!cursorLocked && currentMode != Mode.Drive)
        {
            GUI.Label(new Rect(10, 10, 400, 25), "Click per controllare la camera | ESC per liberare il mouse");
            return;
        }

        if (cachedGuiStyle == null)
        {
            cachedGuiStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            cachedGuiStyle.normal.textColor = Color.white;
        }
        GUIStyle style = cachedGuiStyle;

        string info;
        if (currentMode == Mode.Drive)
        {
            float kmh = Mathf.Abs(driveSpeed) * 3.6f;
            string viewName = cameraView == 0 ? "Dietro" : cameraView == 1 ? "Cofano" : "Orbitale";
            info = $"GUIDA | {kmh:F0} km/h | Vista: {viewName} | WASD guida | Shift turbo | Spazio freno | V vista | G esci";
        }
        else
        {
            string mode = currentMode == Mode.Fly ? "VOLO" : "CAMMINATA";
            float h = transform.position.y;
            info = $"{mode} | Alt: {h:F1}m | WASD muovi | Shift veloce | Tab cambia | G guida | F centra | 1-3 preset";
        }

        GUI.color = Color.black;
        GUI.Label(new Rect(11, Screen.height - 29, 900, 25), info, style);
        GUI.color = Color.white;
        GUI.Label(new Rect(10, Screen.height - 30, 900, 25), info, style);
    }

    void OnDestroy()
    {
        if (vehicleGO != null)
            Destroy(vehicleGO);
    }
}
