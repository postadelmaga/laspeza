using UnityEngine;

/// <summary>
/// Runtime wave animation for water meshes.
/// Attach to any GameObject with a MeshFilter (e.g. "Acqua_Mare").
/// Replaces the flat water quad with a subdivided grid and animates it
/// each frame using a sum of Gerstner-style sine waves.
///
/// Provides a static API:  WaterSurface.GetWaveHeight(worldPos)
/// so that boats, fish, buoys etc. can query the surface height anywhere.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class WaterSurface : MonoBehaviour
{
    // ================================================================
    //  SINGLETON
    // ================================================================

    private static WaterSurface _instance;

    /// <summary>
    /// Cached singleton. Set on Awake, cleared on OnDestroy.
    /// If multiple WaterSurface exist, the last one to Awake wins.
    /// </summary>
    public static WaterSurface Instance => _instance;

    // ================================================================
    //  INSPECTOR
    // ================================================================

    [Header("Sea State")]
    [Tooltip("0 = calm lake, 1 = light ripple, 2 = moderate, 3 = choppy, 4 = rough, 5 = storm")]
    [Range(0f, 5f)]
    public float seaState = 1.5f;

    [Header("Grid Resolution")]
    [Tooltip("Number of vertices along each axis of the subdivided water mesh.")]
    public int gridResolution = 100;

    [Header("Performance")]
    [Tooltip("Only animate vertices within this radius of the main camera (metres).")]
    public float animationRadius = 500f;

    // ================================================================
    //  WAVE DEFINITIONS
    // ================================================================

    /// <summary>
    /// Describes a single sine-wave component.
    /// Direction is a unit vector on the XZ plane.
    /// </summary>
    [System.Serializable]
    public struct WaveParams
    {
        public float wavelength;  // metres crest-to-crest
        public float amplitude;   // metres (half height)
        public Vector2 direction; // normalised XZ direction
        public float speed;       // phase speed m/s

        /// <summary>Angular frequency  k = 2pi / wavelength.</summary>
        public float K => 2f * Mathf.PI / wavelength;
    }

    /// <summary>
    /// Default wave table -- three overlapping components that produce a
    /// convincing Mediterranean sea surface.
    /// Directions use Unity's XZ plane: X = east, Z = north.
    /// SW = (-1, -1) normalised,  S = (0, -1),  SE = (1, -1) normalised.
    /// </summary>
    private static readonly WaveParams[] _waves = new WaveParams[]
    {
        // Wave 1 -- large swell from SW
        new WaveParams
        {
            wavelength = 60f,
            amplitude  = 0.3f,
            direction  = new Vector2(-0.707f, -0.707f), // SW normalised
            speed      = 4.0f,
        },
        // Wave 2 -- medium chop from S
        new WaveParams
        {
            wavelength = 25f,
            amplitude  = 0.15f,
            direction  = new Vector2(0f, -1f), // S
            speed      = 2.5f,
        },
        // Wave 3 -- small ripples from SE
        new WaveParams
        {
            wavelength = 8f,
            amplitude  = 0.05f,
            direction  = new Vector2(0.707f, -0.707f), // SE normalised
            speed      = 1.2f,
        },
    };

    // ================================================================
    //  RUNTIME STATE
    // ================================================================

    private Mesh       _mesh;
    private Vector3[]  _baseVertices;   // flat grid positions (local space)
    private Vector3[]  _animVertices;   // animated positions  (local space)
    private int[]      _triangles;

    /// <summary>Y position of the original flat water surface (world space).</summary>
    private float _seaLevel;

    /// <summary>Mesh bounds min/max in world XZ, used to build the grid.</summary>
    private float _minX, _maxX, _minZ, _maxZ;

    // ================================================================
    //  LIFECYCLE
    // ================================================================

    private void Awake()
    {
        _instance = this;
    }

    private void Start()
    {
        MeshFilter mf = GetComponent<MeshFilter>();
        Mesh originalMesh = mf.sharedMesh;

        // Read sea level from the original mesh (average Y of existing verts)
        Vector3[] origVerts = originalMesh.vertices;
        float ySum = 0f;
        for (int i = 0; i < origVerts.Length; i++)
            ySum += origVerts[i].y;
        _seaLevel = transform.TransformPoint(new Vector3(0, ySum / origVerts.Length, 0)).y;

        // Compute world-space XZ bounds of the original mesh
        Bounds b = originalMesh.bounds;
        Vector3 worldMin = transform.TransformPoint(b.min);
        Vector3 worldMax = transform.TransformPoint(b.max);
        _minX = Mathf.Min(worldMin.x, worldMax.x);
        _maxX = Mathf.Max(worldMin.x, worldMax.x);
        _minZ = Mathf.Min(worldMin.z, worldMax.z);
        _maxZ = Mathf.Max(worldMin.z, worldMax.z);

        // Build the subdivided grid mesh
        BuildGrid();

        // Assign the new mesh (use instance, not shared, to avoid modifying the asset)
        mf.mesh = _mesh;

        // Enhance the water material
        ApplyWaterMaterial();
    }

    private void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
    }

    // ================================================================
    //  GRID CONSTRUCTION
    // ================================================================

    /// <summary>
    /// Replaces the existing mesh with a regular grid that spans the same
    /// XZ footprint.  All vertices start at the flat sea level.
    /// </summary>
    private void BuildGrid()
    {
        int res = Mathf.Max(gridResolution, 2);
        int vertCount = res * res;
        int quadCount = (res - 1) * (res - 1);

        _baseVertices = new Vector3[vertCount];
        _animVertices = new Vector3[vertCount];
        _triangles    = new int[quadCount * 6];

        // Local-space Y that corresponds to sea level
        float localY = transform.InverseTransformPoint(new Vector3(0, _seaLevel, 0)).y;

        // Local-space min/max XZ
        Vector3 localMin = transform.InverseTransformPoint(new Vector3(_minX, _seaLevel, _minZ));
        Vector3 localMax = transform.InverseTransformPoint(new Vector3(_maxX, _seaLevel, _maxZ));

        float lMinX = Mathf.Min(localMin.x, localMax.x);
        float lMaxX = Mathf.Max(localMin.x, localMax.x);
        float lMinZ = Mathf.Min(localMin.z, localMax.z);
        float lMaxZ = Mathf.Max(localMin.z, localMax.z);

        // Populate vertices
        for (int z = 0; z < res; z++)
        {
            float tz = (float)z / (res - 1);
            float pz = Mathf.Lerp(lMinZ, lMaxZ, tz);
            for (int x = 0; x < res; x++)
            {
                float tx = (float)x / (res - 1);
                float px = Mathf.Lerp(lMinX, lMaxX, tx);
                int idx = z * res + x;
                _baseVertices[idx] = new Vector3(px, localY, pz);
                _animVertices[idx] = _baseVertices[idx];
            }
        }

        // Populate triangles (two tris per quad)
        int ti = 0;
        for (int z = 0; z < res - 1; z++)
        {
            for (int x = 0; x < res - 1; x++)
            {
                int bl = z * res + x;
                int br = bl + 1;
                int tl = bl + res;
                int tr = tl + 1;

                // Triangle 1
                _triangles[ti++] = bl;
                _triangles[ti++] = tl;
                _triangles[ti++] = br;
                // Triangle 2
                _triangles[ti++] = br;
                _triangles[ti++] = tl;
                _triangles[ti++] = tr;
            }
        }

        // Create the mesh
        _mesh = new Mesh();
        _mesh.indexFormat = vertCount > 65535
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        _mesh.vertices  = _animVertices;
        _mesh.triangles = _triangles;
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();
    }

    // ================================================================
    //  MATERIAL
    // ================================================================

    /// <summary>
    /// Creates and assigns a semi-transparent deep-blue water material.
    /// Handles URP, HDRP, and Built-in Standard shaders.
    /// </summary>
    private void ApplyWaterMaterial()
    {
        Shader shader = FindWaterShader();
        Material mat = new Material(shader);
        string sn = shader.name;

        Color baseColor    = new Color(0.03f, 0.10f, 0.22f, 0.85f); // deep teal-blue
        Color emissionTint = new Color(0.01f, 0.06f, 0.14f, 1f) * 0.5f; // subtle glow

        // --- Transparency ---
        if (sn.Contains("Universal Render Pipeline"))
        {
            mat.SetFloat("_Surface", 1f);  // Transparent
            mat.SetFloat("_Blend", 0f);    // Alpha blend
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        else if (sn == "Standard")
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
        else if (sn.Contains("HDRP"))
        {
            mat.SetFloat("_SurfaceType", 1f);
            mat.SetFloat("_BlendMode", 0f);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        mat.color = baseColor;

        if (mat.HasProperty("_Metallic"))   mat.SetFloat("_Metallic", 0.1f);
        if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.92f);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.92f);

        mat.EnableKeyword("_EMISSION");
        if (mat.HasProperty("_EmissionColor"))
            mat.SetColor("_EmissionColor", emissionTint);

        GetComponent<MeshRenderer>().material = mat;
    }

    private static Shader FindWaterShader()
    {
        Shader s;
        s = Shader.Find("Universal Render Pipeline/Lit"); if (s != null) return s;
        s = Shader.Find("HDRP/Lit");                     if (s != null) return s;
        s = Shader.Find("Standard");                      if (s != null) return s;
        return Shader.Find("Unlit/Color");
    }

    // ================================================================
    //  ANIMATION (every frame)
    // ================================================================

    private void Update()
    {
        if (_mesh == null || _animVertices == null) return;

        float t = Time.time;
        float stateMul = seaState; // amplitude multiplier

        // Camera position for LOD culling
        Camera cam = Camera.main;
        Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
        float radiusSq = animationRadius * animationRadius;
        bool doCull = cam != null;

        int count = _baseVertices.Length;
        for (int i = 0; i < count; i++)
        {
            Vector3 baseLocal = _baseVertices[i];

            // Convert to world XZ for distance check and wave calc
            Vector3 worldPos = transform.TransformPoint(baseLocal);

            if (doCull)
            {
                float dx = worldPos.x - camPos.x;
                float dz = worldPos.z - camPos.z;
                if (dx * dx + dz * dz > radiusSq)
                {
                    // Outside animation radius -- keep flat
                    _animVertices[i] = baseLocal;
                    continue;
                }
            }

            // Sum of sine waves evaluated at world XZ
            float yOffset = EvaluateWaves(worldPos.x, worldPos.z, t, stateMul);

            _animVertices[i] = new Vector3(
                baseLocal.x,
                baseLocal.y + yOffset,
                baseLocal.z);
        }

        _mesh.vertices = _animVertices;
        _mesh.RecalculateNormals();

        // Keep bounds generous so the mesh is not frustum-culled while waves peak
        Bounds bounds = _mesh.bounds;
        float maxAmp = MaxPossibleAmplitude(stateMul);
        bounds.Expand(new Vector3(0, maxAmp * 2f, 0));
        _mesh.bounds = bounds;
    }

    // ================================================================
    //  WAVE MATH (shared between animation and static query)
    // ================================================================

    /// <summary>
    /// Evaluates the sum of all wave components at a world XZ position.
    /// Returns the Y offset relative to the flat sea level.
    /// </summary>
    private static float EvaluateWaves(float worldX, float worldZ, float time, float stateMul)
    {
        float y = 0f;
        for (int w = 0; w < _waves.Length; w++)
        {
            float k   = _waves[w].K;
            float amp = _waves[w].amplitude * stateMul;
            float spd = _waves[w].speed;
            Vector2 dir = _waves[w].direction;

            // dot(direction, position) gives the phase spatial component
            float phase = k * (dir.x * worldX + dir.y * worldZ) - spd * k * time;
            y += amp * Mathf.Sin(phase);
        }
        return y;
    }

    /// <summary>
    /// Returns the maximum possible absolute wave displacement for the
    /// current sea state (sum of all amplitudes * state multiplier).
    /// </summary>
    private static float MaxPossibleAmplitude(float stateMul)
    {
        float sum = 0f;
        for (int w = 0; w < _waves.Length; w++)
            sum += _waves[w].amplitude;
        return sum * stateMul;
    }

    // ================================================================
    //  STATIC API
    // ================================================================

    /// <summary>
    /// Returns the world-space Y of the water surface at the given XZ
    /// position.  Works anywhere (not limited to mesh bounds).
    /// Uses the same wave formula as the mesh animation.
    ///
    /// If no WaterSurface is active, returns 0.
    /// </summary>
    public static float GetWaveHeight(Vector3 worldPos)
    {
        if (_instance == null) return 0f;

        float yOffset = EvaluateWaves(
            worldPos.x, worldPos.z,
            Time.time,
            _instance.seaState);

        return _instance._seaLevel + yOffset;
    }

    /// <summary>
    /// Returns the flat (un-animated) sea level in world Y.
    /// Useful for depth calculations.
    /// </summary>
    public static float GetSeaLevel()
    {
        return _instance != null ? _instance._seaLevel : 0f;
    }
}
