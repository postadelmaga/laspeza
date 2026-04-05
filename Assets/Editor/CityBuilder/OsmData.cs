using System.Collections.Generic;

namespace CityBuilder
{
    /// <summary>
    /// Structured OSM data classes for buildings, roads, water, vegetation, and amenities.
    /// Parsed from Overpass API JSON responses.
    /// </summary>

    [System.Serializable]
    public class LatLon
    {
        public double lat;
        public double lon;

        public LatLon() { }
        public LatLon(double lat, double lon) { this.lat = lat; this.lon = lon; }
    }

    [System.Serializable]
    public class OsmBuildingData
    {
        public long id;
        public List<LatLon> footprint = new List<LatLon>();
        public float height;          // metres (from height or building:height or levels*3)
        public float minHeight;       // min_height tag (for buildings on stilts)
        public int levels;            // building:levels
        public string material;       // building:material (brick, concrete, etc.)
        public string colour;         // building:colour
        public string roofShape;      // roof:shape (flat, gabled, hipped, etc.)
        public string roofMaterial;   // roof:material
        public int roofLevels;        // roof:levels
        public string buildingType;   // building tag value (yes, apartments, commercial, etc.)
    }

    [System.Serializable]
    public class OsmRoadData
    {
        public long id;
        public List<LatLon> centerline = new List<LatLon>();
        public string highwayType;    // motorway, primary, residential, footway, etc.
        public int lanes;
        public float width;           // metres
        public string surface;        // asphalt, cobblestone, gravel, etc.
        public string name;
        public bool oneway;
        public int maxspeed;          // km/h
    }

    [System.Serializable]
    public class OsmWaterData
    {
        public long id;
        public List<LatLon> polygon = new List<LatLon>();
        public string waterType;      // sea, river, lake, harbour, marina, stream, canal, coastline
    }

    [System.Serializable]
    public class OsmVegetationData
    {
        public long id;
        public LatLon point;                              // for trees (node)
        public List<LatLon> polygon = new List<LatLon>(); // for parks/forests (way)
        public string vegType;        // tree, park, forest, grass, wood, garden
        public bool isPoint;          // true = single node (tree), false = area
    }

    [System.Serializable]
    public class OsmAmenityData
    {
        public long id;
        public LatLon point;
        public string amenityType;    // bench, bus_stop, traffic_signals, parking, etc.
        public string name;
    }

    [System.Serializable]
    public class OsmPedestrianAreaData
    {
        public long id;
        public List<LatLon> polygon = new List<LatLon>();
        public string areaType;       // square, pedestrian, marketplace, plaza
        public string surface;        // paving_stones, cobblestone, sett, asphalt, etc.
        public string name;
    }

    [System.Serializable]
    public class OsmDownloadResult
    {
        public List<OsmBuildingData> buildings = new List<OsmBuildingData>();
        public List<OsmRoadData> roads = new List<OsmRoadData>();
        public List<OsmWaterData> water = new List<OsmWaterData>();
        public List<OsmVegetationData> vegetation = new List<OsmVegetationData>();
        public List<OsmAmenityData> amenities = new List<OsmAmenityData>();
        public List<OsmPedestrianAreaData> pedestrianAreas = new List<OsmPedestrianAreaData>();

        public double minLon, minLat, maxLon, maxLat;
        public string downloadTimestamp;

        public int TotalFeatures =>
            buildings.Count + roads.Count + water.Count + vegetation.Count +
            amenities.Count + pedestrianAreas.Count;
    }
}
