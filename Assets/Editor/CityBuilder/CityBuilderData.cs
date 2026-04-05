using UnityEngine;
using System.Collections.Generic;

namespace CityBuilder
{
    [System.Serializable]
    public class TerrainMetaData
    {
        public float minLon, maxLon, minLat, maxLat, widthM, lengthM, heightM, seaLevelNorm;
        public int rawResolution;  // risoluzione del file .raw (es. 4097, 2049, 1025...)

        public TerrainMetaData Clone()
        {
            return new TerrainMetaData
            {
                minLon = this.minLon, maxLon = this.maxLon,
                minLat = this.minLat, maxLat = this.maxLat,
                widthM = this.widthM, lengthM = this.lengthM,
                heightM = this.heightM, seaLevelNorm = this.seaLevelNorm,
                rawResolution = this.rawResolution
            };
        }
    }

    public class TileMeshData
    {
        public List<Vector3> vertices;
        public List<int> triangles;

        public TileMeshData(int vertCapacity = 256, int triCapacity = 512)
        {
            vertices = new List<Vector3>(vertCapacity);
            triangles = new List<int>(triCapacity);
        }

        public void Free()
        {
            vertices.Clear(); vertices.TrimExcess();
            triangles.Clear(); triangles.TrimExcess();
        }
    }

    public class OsmFeature
    {
        public bool isBuilding;
        public bool isHighway;
        public float widthOrHeight;
        public List<Vector3> points = new List<Vector3>();
        public int tileX = -1;
        public int tileZ = -1;
        public int originalId;
    }

    /// <summary>
    /// Building data in world coordinates (Vector3), ready for mesh generation.
    /// Converted from OsmData.OsmBuildingData (LatLon) by the UI pipeline.
    /// </summary>
    public class WorldBuildingData
    {
        public List<Vector3> footprint = new List<Vector3>();
        public float height;
        public float minHeight;
        public float levels;
        public string material;
        public string colour;
        public string roofShape;
        public string roofMaterial;
        public int tileX;
        public int tileZ;
    }

    /// <summary>
    /// Per-tile mesh data with multiple submeshes (walls, windows, roofs).
    /// </summary>
    public class TileBuildingMeshData
    {
        public List<Vector3> vertices;
        public List<Vector2> uvs;
        public List<int> wallTriangles;
        public List<int> windowTriangles;
        public List<int> roofTriangles;
        public List<int> groundFloorTriangles;

        public TileBuildingMeshData(int vertCapacity = 4096, int triCapacity = 6144)
        {
            vertices = new List<Vector3>(vertCapacity);
            uvs = new List<Vector2>(vertCapacity);
            wallTriangles = new List<int>(triCapacity);
            windowTriangles = new List<int>(triCapacity / 4);
            roofTriangles = new List<int>(triCapacity / 4);
            groundFloorTriangles = new List<int>(triCapacity / 4);
        }

        public void Free()
        {
            vertices = null; uvs = null;
            wallTriangles = null; windowTriangles = null;
            roofTriangles = null; groundFloorTriangles = null;
        }
    }
}
