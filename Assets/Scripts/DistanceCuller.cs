using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Disabilita i renderer degli oggetti troppo lontani dalla camera.
/// Si attacca automaticamente al CityBuilder_World dal CityExplorer.
/// Aggiorna ogni N frame per non pesare.
/// </summary>
public class DistanceCuller : MonoBehaviour
{
    [Header("Distanze")]
    public float buildingCullDistance = 800f;
    public float vegetationCullDistance = 400f;
    public float detailCullDistance = 200f;

    [Header("Performance")]
    public int updateInterval = 10; // ogni N frame

    private List<CullTarget> targets = new List<CullTarget>();
    private Transform cam;
    private int frameCounter;

    struct CullTarget
    {
        public Renderer renderer;
        public float cullDistance;
    }

    void Start()
    {
        cam = Camera.main?.transform;
        if (cam == null) cam = transform;

        // Raccogli tutti i renderer figli con le distanze appropriate
        foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
        {
            float dist = buildingCullDistance;
            string name = r.gameObject.name.ToLower();
            string parentName = r.transform.parent != null ? r.transform.parent.name.ToLower() : "";

            if (parentName.Contains("vegetaz") || name.Contains("chiom") || name.Contains("tronc"))
                dist = vegetationCullDistance;
            else if (name.Contains("segnalet") || name.Contains("marking") || parentName.Contains("strad"))
                dist = detailCullDistance;

            targets.Add(new CullTarget { renderer = r, cullDistance = dist });
        }

        Debug.Log($"DistanceCuller: {targets.Count} renderer tracciati");
    }

    void Update()
    {
        frameCounter++;
        if (frameCounter % updateInterval != 0) return;
        if (cam == null) { cam = Camera.main?.transform; return; }

        Vector3 camPos = cam.position;

        for (int i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            if (t.renderer == null) continue;

            float sqrDist = (t.renderer.bounds.center - camPos).sqrMagnitude;
            bool visible = sqrDist < t.cullDistance * t.cullDistance;
            if (t.renderer.enabled != visible)
                t.renderer.enabled = visible;
        }
    }
}
