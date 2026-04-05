using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace CityBuilder
{
    public static class OsmParser
    {
        private static readonly Regex LatRegex = new Regex("\"lat\"\\s*:\\s*([\\d\\.\\-]+)", RegexOptions.Compiled);
        private static readonly Regex LonRegex = new Regex("\"lon\"\\s*:\\s*([\\d\\.\\-]+)", RegexOptions.Compiled);

        // Tag altezza edifici (in ordine di priorita)
        private static readonly Regex HeightRegex = new Regex("\"height\"\\s*:\\s*\"?([\\d\\.]+)\"?", RegexOptions.Compiled);
        private static readonly Regex BuildingHeightRegex = new Regex("\"building:height\"\\s*:\\s*\"?([\\d\\.]+)\"?", RegexOptions.Compiled);
        private static readonly Regex LevelsRegex = new Regex("\"building:levels\"\\s*:\\s*\"?(\\d+)\"?", RegexOptions.Compiled);
        private static readonly Regex MinHeightRegex = new Regex("\"min_height\"\\s*:\\s*\"?([\\d\\.]+)\"?", RegexOptions.Compiled);

        // Tag larghezza strade
        private static readonly Regex WidthRegex = new Regex("\"width\"\\s*:\\s*\"?([\\d\\.]+)\"?", RegexOptions.Compiled);
        private static readonly Regex LanesRegex = new Regex("\"lanes\"\\s*:\\s*\"?(\\d+)\"?", RegexOptions.Compiled);

        private const float FLOOR_HEIGHT = 3.0f;
        private const float DEFAULT_BUILDING_HEIGHT = 6.0f;
        private const float DEFAULT_LANE_WIDTH = 3.5f;  // larghezza standard corsia in metri

        // Larghezze di fallback per tipo di strada (in metri reali, scalate dopo)
        private static readonly Dictionary<string, float> RoadDefaults = new Dictionary<string, float>
        {
            { "motorway", 14.0f },      // 4 corsie
            { "trunk", 10.5f },          // 3 corsie
            { "primary", 7.0f },         // 2 corsie larghe
            { "secondary", 6.0f },       // 2 corsie
            { "tertiary", 5.0f },        // 2 corsie strette
            { "residential", 4.0f },     // 1.5 corsie
            { "service", 3.0f },         // 1 corsia
            { "footway", 1.5f },
            { "cycleway", 1.5f },
            { "path", 1.0f }
        };

        /// <summary>
        /// Estrae l'altezza dell'edificio in metri dai tag OSM.
        /// Priorita: height > building:height > building:levels * 3m > default 6m.
        /// </summary>
        private static float ParseBuildingHeight(string way)
        {
            // 1. Tag "height" diretto (valore in metri)
            Match m = HeightRegex.Match(way);
            if (m.Success && float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float h) && h > 0)
                return h;

            // 2. Tag "building:height"
            m = BuildingHeightRegex.Match(way);
            if (m.Success && float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float bh) && bh > 0)
                return bh;

            // 3. Tag "building:levels" * altezza per piano
            m = LevelsRegex.Match(way);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int levels) && levels > 0)
                return levels * FLOOR_HEIGHT;

            return DEFAULT_BUILDING_HEIGHT;
        }

        /// <summary>
        /// Estrae min_height per edifici con base sopraelevata (es. edifici su pilotis).
        /// </summary>
        private static float ParseMinHeight(string way)
        {
            Match m = MinHeightRegex.Match(way);
            if (m.Success && float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float mh) && mh > 0)
                return mh;
            return 0f;
        }

        /// <summary>
        /// Estrae la larghezza della strada in metri dai tag OSM.
        /// Priorita: width > lanes * 3.5m > default per tipo.
        /// </summary>
        private static float ParseRoadWidth(string way)
        {
            // 1. Tag "width" diretto
            Match m = WidthRegex.Match(way);
            if (m.Success && float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float w) && w > 0)
                return w;

            // 2. Tag "lanes" * larghezza standard corsia
            m = LanesRegex.Match(way);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int lanes) && lanes > 0)
                return lanes * DEFAULT_LANE_WIDTH;

            // 3. Fallback per tipo di strada
            foreach (var kv in RoadDefaults)
            {
                if (way.Contains("\"" + kv.Key + "\""))
                    return kv.Value;
            }

            return 4.0f; // default generico
        }

        public static async Task<List<OsmFeature>> ParseAsync(string jsonPath, TerrainMetaData tMeta, float cropWidthM, float cropLengthM, int gridCount, Terrain[,] terrains)
        {
            List<OsmFeature> features = new List<OsmFeature>();

            if (!File.Exists(jsonPath))
            {
                UnityEngine.Debug.LogError("File JSON OSM non trovato: " + jsonPath);
                return features;
            }

            string json = File.ReadAllText(jsonPath);
            if (string.IsNullOrWhiteSpace(json) || json.Length < 10)
            {
                UnityEngine.Debug.LogError("Il file JSON è vuoto o corrotto!");
                return features;
            }

            string[] ways = json.Split(new string[] { "\"type\": \"way\"" }, System.StringSplitOptions.RemoveEmptyEntries);

            float tileWidthM = cropWidthM / gridCount;
            float tileLengthM = cropLengthM / gridCount;

            // Scala mondo: le dimensioni OSM sono in metri reali,
            // ma il terreno e scalato di WORLD_SCALE (10x). Applichiamo la stessa scala.
            const float WORLD_SCALE = 10f;

            int buildingCount = 0, roadCount = 0;
            int heightFromTag = 0, heightFromLevels = 0, heightDefault = 0;
            int widthFromTag = 0, widthFromLanes = 0, widthDefault = 0;

            for (int i = 1; i < ways.Length; i++)
            {
                if (i % 200 == 0)
                {
                    EditorUtility.DisplayProgressBar("CityBuilder", $"Lettura Dati OSM {i}/{ways.Length}...", (float)i / ways.Length);
                    await Task.Yield();
                }

                string way = ways[i];
                OsmFeature feat = new OsmFeature();
                feat.originalId = i;
                feat.isBuilding = way.Contains("\"building\":");
                feat.isHighway = way.Contains("\"highway\":");

                if (!feat.isBuilding && !feat.isHighway) continue;

                if (feat.isHighway)
                {
                    float realWidth = ParseRoadWidth(way);
                    feat.widthOrHeight = realWidth / WORLD_SCALE;

                    // Statistiche
                    if (WidthRegex.IsMatch(way)) widthFromTag++;
                    else if (LanesRegex.IsMatch(way)) widthFromLanes++;
                    else widthDefault++;

                    roadCount++;
                }
                else
                {
                    float realHeight = ParseBuildingHeight(way);
                    feat.widthOrHeight = realHeight / WORLD_SCALE;

                    // Statistiche
                    if (HeightRegex.IsMatch(way) || BuildingHeightRegex.IsMatch(way)) heightFromTag++;
                    else if (LevelsRegex.IsMatch(way)) heightFromLevels++;
                    else heightDefault++;

                    buildingCount++;
                }

                int geomIdx = way.IndexOf("\"geometry\": [");
                if (geomIdx == -1) geomIdx = way.IndexOf("\"geometry\":[");
                if (geomIdx == -1) continue;

                string geomStr = way.Substring(geomIdx);
                int depth = 0;
                int endIdx = -1;
                for (int c = geomStr.IndexOf('['); c < geomStr.Length; c++)
                {
                    if (geomStr[c] == '[') depth++;
                    else if (geomStr[c] == ']') { depth--; if (depth == 0) { endIdx = c; break; } }
                }
                if (endIdx != -1) geomStr = geomStr.Substring(0, endIdx + 1);

                string[] coords = geomStr.Split('}');
                foreach (var coord in coords)
                {
                    Match mLat = LatRegex.Match(coord);
                    Match mLon = LonRegex.Match(coord);

                    if (!mLat.Success || !mLon.Success) continue;

                    if (!float.TryParse(mLat.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float lat)) continue;
                    if (!float.TryParse(mLon.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float lon)) continue;

                    float worldX = Mathf.InverseLerp(tMeta.minLon, tMeta.maxLon, lon) * cropWidthM;
                    float worldZ = Mathf.InverseLerp(tMeta.minLat, tMeta.maxLat, lat) * cropLengthM;

                    int gx = Mathf.Clamp((int)(worldX / tileWidthM), 0, gridCount - 1);
                    int gz = Mathf.Clamp((int)(worldZ / tileLengthM), 0, gridCount - 1);

                    float worldY = 0;
                    if (terrains != null && terrains[gx, gz] != null)
                        worldY = terrains[gx, gz].SampleHeight(new Vector3(worldX, 0, worldZ));

                    if (feat.tileX == -1) { feat.tileX = gx; feat.tileZ = gz; }
                    feat.points.Add(new Vector3(worldX, worldY, worldZ));
                }

                if (feat.points.Count >= 3 || (feat.isHighway && feat.points.Count >= 2))
                    features.Add(feat);
            }

            UnityEngine.Debug.Log(
                $"OSM: {buildingCount} edifici (height tag: {heightFromTag}, levels: {heightFromLevels}, default: {heightDefault}), " +
                $"{roadCount} strade (width tag: {widthFromTag}, lanes: {widthFromLanes}, default: {widthDefault})");

            return features;
        }
    }
}
