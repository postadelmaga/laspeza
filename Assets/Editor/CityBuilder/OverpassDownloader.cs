using UnityEngine;
using UnityEditor;
using UnityEngine.Networking;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace CityBuilder
{
    /// <summary>
    /// Downloads all useful OSM data for a city area via the Overpass API in a single query.
    /// Parses the JSON response into structured C# data classes (see OsmData.cs).
    /// Designed for use in the Unity Editor with progress bar feedback.
    /// </summary>
    public static class OverpassDownloader
    {
        // Server mirrors — se uno e sovraccarico prova il prossimo
        private static readonly string[] OVERPASS_ENDPOINTS = {
            "https://overpass-api.de/api/interpreter",
            "https://overpass.kumi.systems/api/interpreter",
            "https://maps.mail.ru/osm/tools/overpass/api/interpreter",
        };
        private const int QUERY_TIMEOUT = 180;       // Overpass server-side timeout in seconds
        private const int MAX_RETRIES = 3;
        private const int REQUEST_TIMEOUT = 180;     // UnityWebRequest timeout (extra margin)
        private const float FLOOR_HEIGHT = 3.0f;
        private const float DEFAULT_BUILDING_HEIGHT = 6.0f;

        // ── Height heuristics per building type + context ──
        // La Spezia: centro storico 4-6 piani, lungomare 3-4, periferia 2-3, industriale 6-8m
        private static readonly Dictionary<string, float> BuildingTypeHeights = new Dictionary<string, float>
        {
            // Residenziale multi-piano
            { "apartments",    15.0f },  // 5 piani
            { "residential",    9.0f },  // 3 piani
            { "house",          7.5f },  // 2.5 piani
            { "detached",       7.5f },
            { "semidetached_house", 7.5f },
            { "terrace",        9.0f },
            { "dormitory",     12.0f },  // 4 piani

            // Commerciale
            { "commercial",    12.0f },  // 4 piani
            { "office",        15.0f },  // 5 piani
            { "retail",         6.0f },  // 2 piani
            { "supermarket",    6.0f },
            { "hotel",         18.0f },  // 6 piani

            // Pubblico
            { "public",        12.0f },
            { "civic",         12.0f },
            { "government",    12.0f },
            { "hospital",      15.0f },
            { "school",         9.0f },
            { "university",    12.0f },
            { "church",        15.0f },
            { "cathedral",     25.0f },
            { "chapel",         8.0f },
            { "mosque",        12.0f },
            { "synagogue",     12.0f },
            { "temple",        12.0f },

            // Industriale / servizi
            { "industrial",     8.0f },
            { "warehouse",      7.0f },
            { "manufacture",    8.0f },
            { "garage",         3.5f },
            { "garages",        3.5f },
            { "parking",        3.0f },  // per piano
            { "hangar",        10.0f },

            // Piccoli
            { "shed",           3.0f },
            { "hut",            3.0f },
            { "cabin",          4.0f },
            { "kiosk",          3.5f },
            { "transformer_tower", 4.0f },
            { "ruins",          4.0f },

            // Default "yes"
            { "yes",            9.0f },  // 3 piani — media italiana
        };

        // -------------------------------------------------------------------
        //  PUBLIC API
        // -------------------------------------------------------------------

        /// <summary>
        /// Downloads all OSM data for the given bounding box from the Overpass API.
        /// Shows a progress bar in the Editor during the operation.
        /// </summary>
        /// <param name="minLon">Western longitude boundary</param>
        /// <param name="minLat">Southern latitude boundary</param>
        /// <param name="maxLon">Eastern longitude boundary</param>
        /// <param name="maxLat">Northern latitude boundary</param>
        /// <param name="cachePath">If non-null, saves the raw JSON here and reuses it on subsequent calls</param>
        /// <returns>Parsed OsmDownloadResult or null on failure</returns>
        public static async Task<OsmDownloadResult> DownloadAsync(
            double minLon, double minLat, double maxLon, double maxLat,
            string cachePath = null)
        {
            try
            {
                // 1. Check cache
                string json = null;
                if (!string.IsNullOrEmpty(cachePath) && File.Exists(cachePath))
                {
                    EditorUtility.DisplayProgressBar("CityBuilder - Overpass",
                        "Caricamento dati OSM dalla cache...", 0.05f);
                    json = File.ReadAllText(cachePath);
                    Debug.Log($"[OverpassDownloader] Usando cache: {cachePath} ({json.Length / 1024} KB)");
                }

                // 2. Download if no cache
                if (string.IsNullOrEmpty(json))
                {
                    EditorUtility.DisplayProgressBar("CityBuilder - Overpass",
                        "Costruzione query Overpass...", 0.02f);

                    string query = BuildQuery(minLon, minLat, maxLon, maxLat);
                    Debug.Log($"[OverpassDownloader] Query ({query.Length} chars) per bbox " +
                              $"[{minLon}, {minLat}, {maxLon}, {maxLat}]");

                    EditorUtility.DisplayProgressBar("CityBuilder - Overpass",
                        "Download dati OSM dall'Overpass API...", 0.05f);

                    json = await PostQueryAsync(query);
                    if (json == null) return null;

                    Debug.Log($"[OverpassDownloader] Ricevuti {json.Length / 1024} KB di dati JSON");

                    // Save cache
                    if (!string.IsNullOrEmpty(cachePath))
                    {
                        string dir = Path.GetDirectoryName(cachePath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        File.WriteAllText(cachePath, json);
                        Debug.Log($"[OverpassDownloader] Cache salvata: {cachePath}");
                    }
                }

                // 3. Parse
                EditorUtility.DisplayProgressBar("CityBuilder - Overpass",
                    "Parsing dati OSM...", 0.50f);
                await Task.Yield(); // let the progress bar render

                var result = ParseJson(json);
                result.minLon = minLon;
                result.minLat = minLat;
                result.maxLon = maxLon;
                result.maxLat = maxLat;
                result.downloadTimestamp = DateTime.UtcNow.ToString("o");

                EditorUtility.DisplayProgressBar("CityBuilder - Overpass",
                    "Parsing completato!", 1.0f);

                Debug.Log($"[OverpassDownloader] Parsing completato: " +
                          $"{result.buildings.Count} edifici, {result.roads.Count} strade, " +
                          $"{result.water.Count} acqua, {result.vegetation.Count} vegetazione, " +
                          $"{result.pedestrianAreas.Count} piazze, " +
                          $"{result.amenities.Count} amenities (totale: {result.TotalFeatures})");

                return result;
            }
            catch (Exception e)
            {
                Debug.LogError($"[OverpassDownloader] Errore: {e}");
                return null;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        // -------------------------------------------------------------------
        //  QUERY BUILDER
        // -------------------------------------------------------------------

        /// <summary>
        /// Builds a single comprehensive Overpass QL query that fetches buildings, roads,
        /// water, vegetation, and amenities in one request.
        /// </summary>
        private static string BuildQuery(double minLon, double minLat, double maxLon, double maxLat)
        {
            // Bbox format for Overpass: south, west, north, east (lat, lon, lat, lon)
            string bb = string.Format(CultureInfo.InvariantCulture,
                "{0},{1},{2},{3}", minLat, minLon, maxLat, maxLon);

            var sb = new StringBuilder(2048);
            sb.AppendLine($"[out:json][timeout:{QUERY_TIMEOUT}];");
            sb.AppendLine("(");

            // --- Buildings (ways and relations) ---
            sb.AppendLine($"  way[\"building\"]({bb});");
            sb.AppendLine($"  relation[\"building\"]({bb});");

            // --- Roads / Highways (ways) ---
            sb.AppendLine($"  way[\"highway\"]({bb});");

            // --- Water features ---
            sb.AppendLine($"  way[\"natural\"=\"water\"]({bb});");
            sb.AppendLine($"  relation[\"natural\"=\"water\"]({bb});");
            sb.AppendLine($"  way[\"natural\"=\"coastline\"]({bb});");
            sb.AppendLine($"  way[\"waterway\"]({bb});");
            sb.AppendLine($"  relation[\"waterway\"]({bb});");
            sb.AppendLine($"  way[\"leisure\"=\"marina\"]({bb});");
            sb.AppendLine($"  node[\"leisure\"=\"marina\"]({bb});");
            sb.AppendLine($"  way[\"harbour\"]({bb});");
            sb.AppendLine($"  node[\"harbour\"]({bb});");

            // --- Pedestrian areas / piazze ---
            sb.AppendLine($"  way[\"highway\"=\"pedestrian\"][\"area\"=\"yes\"]({bb});");
            sb.AppendLine($"  relation[\"highway\"=\"pedestrian\"][\"area\"=\"yes\"]({bb});");
            sb.AppendLine($"  way[\"place\"=\"square\"]({bb});");
            sb.AppendLine($"  relation[\"place\"=\"square\"]({bb});");
            sb.AppendLine($"  way[\"leisure\"=\"playground\"]({bb});");
            sb.AppendLine($"  way[\"amenity\"=\"marketplace\"]({bb});");

            // --- Vegetation ---
            sb.AppendLine($"  node[\"natural\"=\"tree\"]({bb});");
            sb.AppendLine($"  way[\"natural\"=\"wood\"]({bb});");
            sb.AppendLine($"  relation[\"natural\"=\"wood\"]({bb});");
            sb.AppendLine($"  way[\"leisure\"=\"park\"]({bb});");
            sb.AppendLine($"  relation[\"leisure\"=\"park\"]({bb});");
            sb.AppendLine($"  way[\"leisure\"=\"garden\"]({bb});");
            sb.AppendLine($"  way[\"landuse\"=\"forest\"]({bb});");
            sb.AppendLine($"  relation[\"landuse\"=\"forest\"]({bb});");
            sb.AppendLine($"  way[\"landuse\"=\"grass\"]({bb});");

            // --- Amenities (nodes) ---
            sb.AppendLine($"  node[\"amenity\"=\"bench\"]({bb});");
            sb.AppendLine($"  node[\"highway\"=\"bus_stop\"]({bb});");
            sb.AppendLine($"  node[\"highway\"=\"traffic_signals\"]({bb});");
            sb.AppendLine($"  node[\"amenity\"=\"parking\"]({bb});");
            sb.AppendLine($"  way[\"amenity\"=\"parking\"]({bb});");

            sb.AppendLine(");");
            // Recurse down to get all node coordinates for ways and relations
            sb.AppendLine("(._;>;);");
            sb.AppendLine("out body;");

            return sb.ToString();
        }

        // -------------------------------------------------------------------
        //  HTTP DOWNLOAD
        // -------------------------------------------------------------------

        private static async Task<string> PostQueryAsync(string query)
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes("data=" + Uri.EscapeDataString(query));

            for (int attempt = 0; attempt < MAX_RETRIES; attempt++)
            {
                string endpoint = OVERPASS_ENDPOINTS[attempt % OVERPASS_ENDPOINTS.Length];
                if (attempt > 0)
                {
                    int waitSec = 5 * attempt;
                    Debug.Log($"[OverpassDownloader] Retry {attempt}/{MAX_RETRIES} tra {waitSec}s usando {endpoint}...");
                    await Task.Delay(waitSec * 1000);
                }

            using (var request = new UnityWebRequest(endpoint, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
                request.timeout = REQUEST_TIMEOUT;

                var op = request.SendWebRequest();

                // Poll until done, updating progress bar
                while (!op.isDone)
                {
                    float p = Mathf.Lerp(0.05f, 0.45f, op.progress);
                    EditorUtility.DisplayProgressBar("CityBuilder - Overpass",
                        $"Download in corso... ({(int)(op.progress * 100)}%)", p);
                    await Task.Delay(200);
                }

                if (request.result == UnityWebRequest.Result.ConnectionError ||
                    request.result == UnityWebRequest.Result.ProtocolError)
                {
                    Debug.LogWarning($"[OverpassDownloader] Tentativo {attempt+1} fallito ({endpoint}): {request.error} (code {request.responseCode})");

                    if (request.downloadHandler?.text != null && request.downloadHandler.text.Length < 2000 &&
                        request.downloadHandler.text.Contains("Error"))
                        Debug.LogWarning($"[OverpassDownloader] Dettaglio: {request.downloadHandler.text}");

                    continue; // prova il prossimo server
                }

                return request.downloadHandler.text;
            }
            } // fine retry loop

            Debug.LogError($"[OverpassDownloader] Tutti i {MAX_RETRIES} tentativi falliti su tutti i server mirror.");
            return null;
        }

        // -------------------------------------------------------------------
        //  JSON PARSER  (manual, no external dependencies)
        // -------------------------------------------------------------------

        /// <summary>
        /// Parses the Overpass JSON response into an OsmDownloadResult.
        /// The response contains an "elements" array of nodes, ways, and relations.
        /// We first index all nodes by ID, then resolve way geometries.
        /// </summary>
        public static OsmDownloadResult ParseJson(string json)
        {
            var result = new OsmDownloadResult();

            // Find the "elements" array
            int elemStart = json.IndexOf("\"elements\"", StringComparison.Ordinal);
            if (elemStart < 0)
            {
                Debug.LogError("[OverpassDownloader] JSON non contiene 'elements'");
                return result;
            }

            // Parse all elements into lightweight dicts
            var elements = ParseElements(json, elemStart);
            Debug.Log($"[OverpassDownloader] Elementi totali parsati: {elements.Count}");

            // Index nodes by id for coordinate lookup
            var nodeIndex = new Dictionary<long, LatLon>(elements.Count / 2);
            // Index ways by id per lookup O(1) nelle relations
            var wayIndex = new Dictionary<long, RawElement>(elements.Count / 4);
            foreach (var el in elements)
            {
                if (el.type == "node" && el.lat != 0 && el.lon != 0)
                    nodeIndex[el.id] = new LatLon(el.lat, el.lon);
                else if (el.type == "way")
                    wayIndex[el.id] = el;
            }

            // Process ways and relations
            foreach (var el in elements)
            {
                if (el.type == "node")
                {
                    ProcessNode(el, result);
                }
                else if (el.type == "way")
                {
                    ProcessWay(el, nodeIndex, result);
                }
                else if (el.type == "relation")
                {
                    ProcessRelation(el, nodeIndex, wayIndex, result);
                }
            }

            // Libera indici — non servono piu'
            nodeIndex = null;
            wayIndex = null;

            return result;
        }

        // -------------------------------------------------------------------
        //  ELEMENT PARSING
        // -------------------------------------------------------------------

        private class RawElement
        {
            public string type;  // node, way, relation
            public long id;
            public double lat, lon;
            public List<long> nodeRefs;                    // for ways
            public List<(string role, long refId, string refType)> members;  // for relations
            public Dictionary<string, string> tags;
        }

        /// <summary>
        /// Fast manual parser that extracts elements from the JSON.
        /// Avoids allocating a full JSON DOM - walks the string character by character.
        /// </summary>
        private static List<RawElement> ParseElements(string json, int elemKeyPos)
        {
            var list = new List<RawElement>(4096);

            // Find the opening '[' of the elements array
            int arrStart = json.IndexOf('[', elemKeyPos);
            if (arrStart < 0) return list;

            int pos = arrStart + 1;
            int len = json.Length;

            while (pos < len)
            {
                // Find next '{'
                pos = json.IndexOf('{', pos);
                if (pos < 0) break;

                int objEnd = FindMatchingBrace(json, pos);
                if (objEnd < 0) break;

                string objStr = json.Substring(pos, objEnd - pos + 1);
                var el = ParseSingleElement(objStr);
                if (el != null)
                    list.Add(el);

                pos = objEnd + 1;
            }

            return list;
        }

        private static int FindMatchingBrace(string json, int openPos)
        {
            int depth = 0;
            bool inString = false;
            bool escape = false;

            for (int i = openPos; i < json.Length; i++)
            {
                char c = json[i];
                if (escape) { escape = false; continue; }
                if (c == '\\' && inString) { escape = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static RawElement ParseSingleElement(string obj)
        {
            var el = new RawElement();
            el.tags = new Dictionary<string, string>(8);

            // Type
            el.type = ExtractStringValue(obj, "\"type\"");
            if (el.type != "node" && el.type != "way" && el.type != "relation")
                return null;

            // ID
            string idStr = ExtractRawValue(obj, "\"id\"");
            if (idStr != null) long.TryParse(idStr, NumberStyles.Any, CultureInfo.InvariantCulture, out el.id);

            // Lat/Lon (for nodes)
            if (el.type == "node")
            {
                string latStr = ExtractRawValue(obj, "\"lat\"");
                string lonStr = ExtractRawValue(obj, "\"lon\"");
                if (latStr != null) double.TryParse(latStr, NumberStyles.Any, CultureInfo.InvariantCulture, out el.lat);
                if (lonStr != null) double.TryParse(lonStr, NumberStyles.Any, CultureInfo.InvariantCulture, out el.lon);
            }

            // Node refs (for ways) - "nodes": [id, id, ...]
            if (el.type == "way")
            {
                el.nodeRefs = new List<long>();
                int nodesIdx = obj.IndexOf("\"nodes\"", StringComparison.Ordinal);
                if (nodesIdx >= 0)
                {
                    int arrS = obj.IndexOf('[', nodesIdx);
                    int arrE = obj.IndexOf(']', arrS);
                    if (arrS >= 0 && arrE > arrS)
                    {
                        string arrContent = obj.Substring(arrS + 1, arrE - arrS - 1);
                        foreach (string part in arrContent.Split(','))
                        {
                            string trimmed = part.Trim();
                            if (long.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out long nid))
                                el.nodeRefs.Add(nid);
                        }
                    }
                }
            }

            // Members (for relations) - "members": [ { "type":"way", "ref":123, "role":"outer" }, ... ]
            if (el.type == "relation")
            {
                el.members = new List<(string, long, string)>();
                int membIdx = obj.IndexOf("\"members\"", StringComparison.Ordinal);
                if (membIdx >= 0)
                {
                    int arrS = obj.IndexOf('[', membIdx);
                    if (arrS >= 0)
                    {
                        int mPos = arrS + 1;
                        while (true)
                        {
                            int mStart = obj.IndexOf('{', mPos);
                            if (mStart < 0) break;
                            int mEnd = obj.IndexOf('}', mStart);
                            if (mEnd < 0) break;

                            string mObj = obj.Substring(mStart, mEnd - mStart + 1);
                            string mType = ExtractStringValue(mObj, "\"type\"");
                            string mRefStr = ExtractRawValue(mObj, "\"ref\"");
                            string mRole = ExtractStringValue(mObj, "\"role\"");
                            long mRef = 0;
                            if (mRefStr != null) long.TryParse(mRefStr, NumberStyles.Any, CultureInfo.InvariantCulture, out mRef);
                            el.members.Add((mRole ?? "", mRef, mType ?? ""));

                            mPos = mEnd + 1;
                        }
                    }
                }
            }

            // Tags
            int tagsIdx = obj.IndexOf("\"tags\"", StringComparison.Ordinal);
            if (tagsIdx >= 0)
            {
                int tBrace = obj.IndexOf('{', tagsIdx);
                if (tBrace >= 0)
                {
                    int tEnd = FindMatchingBrace(obj, tBrace);
                    if (tEnd > tBrace)
                    {
                        string tagsStr = obj.Substring(tBrace + 1, tEnd - tBrace - 1);
                        ParseTags(tagsStr, el.tags);
                    }
                }
            }

            return el;
        }

        private static void ParseTags(string tagsContent, Dictionary<string, string> dict)
        {
            // Simple parser for "key": "value" pairs
            int pos = 0;
            int len = tagsContent.Length;
            while (pos < len)
            {
                // Find key
                int kStart = tagsContent.IndexOf('"', pos);
                if (kStart < 0) break;
                int kEnd = tagsContent.IndexOf('"', kStart + 1);
                if (kEnd < 0) break;
                string key = tagsContent.Substring(kStart + 1, kEnd - kStart - 1);

                // Find colon then value
                int colon = tagsContent.IndexOf(':', kEnd + 1);
                if (colon < 0) break;

                // Value might be string or number
                int vStart = -1;
                for (int i = colon + 1; i < len; i++)
                {
                    if (tagsContent[i] != ' ' && tagsContent[i] != '\t' && tagsContent[i] != '\n' && tagsContent[i] != '\r')
                    { vStart = i; break; }
                }
                if (vStart < 0) break;

                string value;
                int nextPos;
                if (tagsContent[vStart] == '"')
                {
                    int vEnd = tagsContent.IndexOf('"', vStart + 1);
                    if (vEnd < 0) break;
                    value = tagsContent.Substring(vStart + 1, vEnd - vStart - 1);
                    nextPos = vEnd + 1;
                }
                else
                {
                    // Numeric or bare value - read until comma or end
                    int vEnd = tagsContent.IndexOfAny(new[] { ',', '}', '\n' }, vStart);
                    if (vEnd < 0) vEnd = len;
                    value = tagsContent.Substring(vStart, vEnd - vStart).Trim();
                    nextPos = vEnd;
                }

                // Handle keys with colons (e.g. "building:levels") - these appear
                // in the JSON as "building:levels" as one quoted string, already parsed above.
                dict[key] = value;
                pos = nextPos;
            }
        }

        /// <summary>Extracts a JSON string value for the given key (e.g. "type" -> "node")</summary>
        private static string ExtractStringValue(string json, string key)
        {
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return null;

            int colon = json.IndexOf(':', idx + key.Length);
            if (colon < 0) return null;

            int qStart = json.IndexOf('"', colon + 1);
            if (qStart < 0) return null;

            int qEnd = json.IndexOf('"', qStart + 1);
            if (qEnd < 0) return null;

            return json.Substring(qStart + 1, qEnd - qStart - 1);
        }

        /// <summary>Extracts a raw (unquoted) JSON value - works for numbers</summary>
        private static string ExtractRawValue(string json, string key)
        {
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return null;

            int colon = json.IndexOf(':', idx + key.Length);
            if (colon < 0) return null;

            // Skip whitespace
            int start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
            if (start >= json.Length) return null;

            // If it's a quoted string, extract it
            if (json[start] == '"')
            {
                int qEnd = json.IndexOf('"', start + 1);
                return qEnd > start ? json.Substring(start + 1, qEnd - start - 1) : null;
            }

            // Otherwise read until separator
            int end = start;
            while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']'
                   && json[end] != '\n' && json[end] != '\r' && json[end] != ' ')
                end++;

            return json.Substring(start, end - start);
        }

        // -------------------------------------------------------------------
        //  FEATURE CLASSIFICATION
        // -------------------------------------------------------------------

        private static string GetTag(Dictionary<string, string> tags, string key)
        {
            return tags != null && tags.TryGetValue(key, out string val) ? val : null;
        }

        private static float ParseFloat(string s, float fallback = 0f)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            // Strip non-numeric suffixes like "m" or " m"
            s = s.Trim();
            int i = 0;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == '-')) i++;
            if (i == 0) return fallback;
            s = s.Substring(0, i);
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
        }

        private static int ParseInt(string s, int fallback = 0)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            s = s.Trim();
            int i = 0;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i == 0) return fallback;
            s = s.Substring(0, i);
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
        }

        private static List<LatLon> ResolveNodes(List<long> nodeRefs, Dictionary<long, LatLon> nodeIndex)
        {
            var points = new List<LatLon>(nodeRefs.Count);
            foreach (long nid in nodeRefs)
            {
                if (nodeIndex.TryGetValue(nid, out LatLon ll))
                    points.Add(ll);
            }
            return points;
        }

        // -------------------------------------------------------------------
        //  NODE PROCESSING (trees, amenities, harbours/marinas as points)
        // -------------------------------------------------------------------

        private static void ProcessNode(RawElement el, OsmDownloadResult result)
        {
            if (el.tags == null || el.tags.Count == 0) return;

            var point = new LatLon(el.lat, el.lon);

            // Trees
            if (GetTag(el.tags, "natural") == "tree")
            {
                result.vegetation.Add(new OsmVegetationData
                {
                    id = el.id,
                    point = point,
                    isPoint = true,
                    vegType = "tree"
                });
                return;
            }

            // Amenities
            string amenity = GetTag(el.tags, "amenity");
            string highway = GetTag(el.tags, "highway");

            if (amenity == "bench" || amenity == "parking")
            {
                result.amenities.Add(new OsmAmenityData
                {
                    id = el.id,
                    point = point,
                    amenityType = amenity,
                    name = GetTag(el.tags, "name")
                });
                return;
            }

            if (highway == "bus_stop")
            {
                result.amenities.Add(new OsmAmenityData
                {
                    id = el.id,
                    point = point,
                    amenityType = "bus_stop",
                    name = GetTag(el.tags, "name")
                });
                return;
            }

            if (highway == "traffic_signals")
            {
                result.amenities.Add(new OsmAmenityData
                {
                    id = el.id,
                    point = point,
                    amenityType = "traffic_signals"
                });
                return;
            }

            // Harbour / marina as point
            if (GetTag(el.tags, "harbour") != null || GetTag(el.tags, "leisure") == "marina")
            {
                result.water.Add(new OsmWaterData
                {
                    id = el.id,
                    polygon = new List<LatLon> { point },
                    waterType = GetTag(el.tags, "leisure") == "marina" ? "marina" : "harbour"
                });
            }
        }

        // -------------------------------------------------------------------
        //  WAY PROCESSING
        // -------------------------------------------------------------------

        private static void ProcessWay(RawElement el, Dictionary<long, LatLon> nodeIndex, OsmDownloadResult result)
        {
            if (el.nodeRefs == null || el.nodeRefs.Count < 2) return;
            if (el.tags == null || el.tags.Count == 0) return;

            var points = ResolveNodes(el.nodeRefs, nodeIndex);
            if (points.Count < 2) return;

            // Buildings
            if (GetTag(el.tags, "building") != null)
            {
                if (points.Count < 3) return; // need at least a triangle

                float h = ParseFloat(GetTag(el.tags, "height"));
                if (h <= 0) h = ParseFloat(GetTag(el.tags, "building:height"));
                int levels = ParseInt(GetTag(el.tags, "building:levels"));
                if (h <= 0 && levels > 0) h = levels * FLOOR_HEIGHT;
                if (h <= 0)
                {
                    string btype = GetTag(el.tags, "building") ?? "yes";
                    h = EstimateHeight(btype, points);
                }

                result.buildings.Add(new OsmBuildingData
                {
                    id = el.id,
                    footprint = points,
                    height = h,
                    minHeight = ParseFloat(GetTag(el.tags, "min_height")),
                    levels = levels > 0 ? levels : (int)(h / FLOOR_HEIGHT),
                    material = GetTag(el.tags, "building:material") ?? "",
                    colour = GetTag(el.tags, "building:colour") ?? "",
                    roofShape = GetTag(el.tags, "roof:shape") ?? "",
                    roofMaterial = GetTag(el.tags, "roof:material") ?? "",
                    roofLevels = ParseInt(GetTag(el.tags, "roof:levels")),
                    buildingType = GetTag(el.tags, "building") ?? "yes"
                });
                return;
            }

            // Pedestrian areas / piazze (must be checked BEFORE roads)
            string hwType = GetTag(el.tags, "highway");
            string areaTag = GetTag(el.tags, "area");
            string placeTag = GetTag(el.tags, "place");
            string amenityTag2 = GetTag(el.tags, "amenity");
            string leisureTag = GetTag(el.tags, "leisure");

            bool isPedArea = (hwType == "pedestrian" && areaTag == "yes");
            bool isSquare = (placeTag == "square");
            bool isMarketplace = (amenityTag2 == "marketplace");
            bool isPlayground = (leisureTag == "playground");

            if ((isPedArea || isSquare || isMarketplace || isPlayground) && points.Count >= 3)
            {
                string pedType = "pedestrian";
                if (isSquare) pedType = "square";
                else if (isMarketplace) pedType = "marketplace";
                else if (isPlayground) pedType = "playground";

                result.pedestrianAreas.Add(new OsmPedestrianAreaData
                {
                    id = el.id,
                    polygon = points,
                    areaType = pedType,
                    surface = GetTag(el.tags, "surface") ?? "",
                    name = GetTag(el.tags, "name") ?? ""
                });
                return;
            }

            // Roads (linear, not area)
            if (hwType != null)
            {
                float w = ParseFloat(GetTag(el.tags, "width"));
                int lanes = ParseInt(GetTag(el.tags, "lanes"));
                string onewayStr = GetTag(el.tags, "oneway");
                bool oneway = onewayStr == "yes" || onewayStr == "1" || onewayStr == "true";

                result.roads.Add(new OsmRoadData
                {
                    id = el.id,
                    centerline = points,
                    highwayType = hwType,
                    lanes = lanes,
                    width = w,
                    surface = GetTag(el.tags, "surface") ?? "",
                    name = GetTag(el.tags, "name") ?? "",
                    oneway = oneway,
                    maxspeed = ParseInt(GetTag(el.tags, "maxspeed"))
                });
                return;
            }

            // Water
            string natural = GetTag(el.tags, "natural");
            string waterway = GetTag(el.tags, "waterway");
            if (natural == "water" || natural == "coastline" || waterway != null)
            {
                string wType = "water";
                if (natural == "coastline") wType = "coastline";
                else if (waterway == "river" || waterway == "riverbank") wType = "river";
                else if (waterway == "stream") wType = "stream";
                else if (waterway == "canal") wType = "canal";
                else
                {
                    string waterTag = GetTag(el.tags, "water");
                    if (waterTag == "lake" || waterTag == "reservoir") wType = "lake";
                    else if (waterTag == "river") wType = "river";
                    else if (waterTag == "sea" || waterTag == "ocean") wType = "sea";
                }

                result.water.Add(new OsmWaterData
                {
                    id = el.id,
                    polygon = points,
                    waterType = wType
                });
                return;
            }

            // Harbour / Marina
            if (GetTag(el.tags, "harbour") != null)
            {
                result.water.Add(new OsmWaterData
                {
                    id = el.id,
                    polygon = points,
                    waterType = "harbour"
                });
                return;
            }
            if (GetTag(el.tags, "leisure") == "marina")
            {
                result.water.Add(new OsmWaterData
                {
                    id = el.id,
                    polygon = points,
                    waterType = "marina"
                });
                return;
            }

            // Vegetation
            string leisure = GetTag(el.tags, "leisure");
            string landuse = GetTag(el.tags, "landuse");
            string vegType = null;

            if (natural == "wood") vegType = "wood";
            else if (leisure == "park") vegType = "park";
            else if (leisure == "garden") vegType = "garden";
            else if (landuse == "forest") vegType = "forest";
            else if (landuse == "grass") vegType = "grass";

            if (vegType != null)
            {
                result.vegetation.Add(new OsmVegetationData
                {
                    id = el.id,
                    polygon = points,
                    isPoint = false,
                    vegType = vegType
                });
                return;
            }

            // Parking as amenity way
            if (GetTag(el.tags, "amenity") == "parking")
            {
                // Use centroid as point
                double cLat = 0, cLon = 0;
                foreach (var p in points) { cLat += p.lat; cLon += p.lon; }
                cLat /= points.Count; cLon /= points.Count;

                result.amenities.Add(new OsmAmenityData
                {
                    id = el.id,
                    point = new LatLon(cLat, cLon),
                    amenityType = "parking",
                    name = GetTag(el.tags, "name") ?? ""
                });
            }
        }

        // -------------------------------------------------------------------
        //  RELATION PROCESSING
        // -------------------------------------------------------------------

        private static void ProcessRelation(RawElement el, Dictionary<long, LatLon> nodeIndex,
            Dictionary<long, RawElement> wayIndex, OsmDownloadResult result)
        {
            if (el.tags == null || el.tags.Count == 0) return;
            if (el.members == null || el.members.Count == 0) return;

            // Usa il dizionario per lookup O(1) invece di O(n)
            var outerPoints = new List<LatLon>();
            foreach (var (role, refId, refType) in el.members)
            {
                if (refType != "way") continue;
                if (role != "outer" && role != "") continue;

                if (wayIndex.TryGetValue(refId, out RawElement wayEl) && wayEl.nodeRefs != null)
                {
                    var wayPoints = ResolveNodes(wayEl.nodeRefs, nodeIndex);
                    outerPoints.AddRange(wayPoints);
                }
            }

            if (outerPoints.Count < 3) return;

            // Classify the relation the same way as a way
            if (GetTag(el.tags, "building") != null)
            {
                float h = ParseFloat(GetTag(el.tags, "height"));
                if (h <= 0) h = ParseFloat(GetTag(el.tags, "building:height"));
                int levels = ParseInt(GetTag(el.tags, "building:levels"));
                if (h <= 0 && levels > 0) h = levels * FLOOR_HEIGHT;
                if (h <= 0)
                {
                    string btype = GetTag(el.tags, "building") ?? "yes";
                    h = EstimateHeight(btype, outerPoints);
                }

                result.buildings.Add(new OsmBuildingData
                {
                    id = el.id,
                    footprint = outerPoints,
                    height = h,
                    minHeight = ParseFloat(GetTag(el.tags, "min_height")),
                    levels = levels > 0 ? levels : (int)(h / FLOOR_HEIGHT),
                    material = GetTag(el.tags, "building:material") ?? "",
                    colour = GetTag(el.tags, "building:colour") ?? "",
                    roofShape = GetTag(el.tags, "roof:shape") ?? "",
                    roofMaterial = GetTag(el.tags, "roof:material") ?? "",
                    roofLevels = ParseInt(GetTag(el.tags, "roof:levels")),
                    buildingType = GetTag(el.tags, "building") ?? "yes"
                });
                return;
            }

            // Pedestrian areas / piazze (relations)
            string relHwType = GetTag(el.tags, "highway");
            string relAreaTag = GetTag(el.tags, "area");
            string relPlaceTag = GetTag(el.tags, "place");

            if ((relHwType == "pedestrian" && relAreaTag == "yes") || relPlaceTag == "square")
            {
                string pedType = relPlaceTag == "square" ? "square" : "pedestrian";
                result.pedestrianAreas.Add(new OsmPedestrianAreaData
                {
                    id = el.id,
                    polygon = outerPoints,
                    areaType = pedType,
                    surface = GetTag(el.tags, "surface") ?? "",
                    name = GetTag(el.tags, "name") ?? ""
                });
                return;
            }

            string natural = GetTag(el.tags, "natural");
            string waterway = GetTag(el.tags, "waterway");
            if (natural == "water" || waterway != null)
            {
                string wType = "water";
                if (waterway == "river" || waterway == "riverbank") wType = "river";
                string waterTag = GetTag(el.tags, "water");
                if (waterTag == "lake" || waterTag == "reservoir") wType = "lake";
                else if (waterTag == "sea" || waterTag == "ocean") wType = "sea";
                else if (waterTag == "river") wType = "river";

                result.water.Add(new OsmWaterData
                {
                    id = el.id,
                    polygon = outerPoints,
                    waterType = wType
                });
                return;
            }

            string leisure = GetTag(el.tags, "leisure");
            string landuse = GetTag(el.tags, "landuse");
            string vegType = null;

            if (natural == "wood") vegType = "wood";
            else if (leisure == "park") vegType = "park";
            else if (landuse == "forest") vegType = "forest";

            if (vegType != null)
            {
                result.vegetation.Add(new OsmVegetationData
                {
                    id = el.id,
                    polygon = outerPoints,
                    isPoint = false,
                    vegType = vegType
                });
            }
        }

        // -------------------------------------------------------------------
        //  UTILITY: Delete cache
        // -------------------------------------------------------------------

        // -------------------------------------------------------------------
        //  HEIGHT ESTIMATION HEURISTICS
        // -------------------------------------------------------------------

        /// <summary>
        /// Stima l'altezza di un edificio basandosi su:
        /// 1. Tipo edificio (building=apartments, commercial, ecc.)
        /// 2. Area del footprint (edifici grandi bassi = industriali, piccoli alti = residenziali)
        /// 3. Lookup table per tipo
        /// </summary>
        private static float EstimateHeight(string buildingType, List<LatLon> footprint)
        {
            // 1. Lookup per tipo
            string btype = (buildingType ?? "yes").ToLowerInvariant().Trim();
            float baseHeight;
            if (!BuildingTypeHeights.TryGetValue(btype, out baseHeight))
                baseHeight = BuildingTypeHeights.ContainsKey("yes") ? BuildingTypeHeights["yes"] : 9.0f;

            // 2. Aggiustamento per area footprint
            if (footprint != null && footprint.Count >= 3)
            {
                // Calcola area approssimata in m² (proiezione locale)
                double area = ApproxAreaM2(footprint);

                // Edifici grandi senza tipo specifico: probabilmente industriali/commerciali bassi
                if (btype == "yes")
                {
                    if (area > 2000) baseHeight = 7.0f;       // capannone
                    else if (area > 500) baseHeight = 9.0f;   // commerciale
                    else if (area > 150) baseHeight = 12.0f;  // condominio
                    else if (area > 60) baseHeight = 9.0f;    // palazzina
                    else baseHeight = 7.0f;                   // casetta
                }

                // Variazione casuale ma deterministica per evitare skyline piatto
                // Hash del primo punto per avere lo stesso valore ad ogni run
                if (footprint.Count > 0)
                {
                    int hash = (int)(footprint[0].lat * 100000) ^ (int)(footprint[0].lon * 100000);
                    float variation = ((hash & 0x7F) / 127.0f) * 0.4f - 0.2f; // [-0.2, +0.2]
                    baseHeight *= (1.0f + variation);
                }
            }

            return Mathf.Clamp(baseHeight, 3.0f, 90.0f);
        }

        /// <summary>
        /// Calcola area approssimata in m² di un poligono lat/lon.
        /// Usa proiezione locale semplificata (cos(lat) correction).
        /// </summary>
        private static double ApproxAreaM2(List<LatLon> polygon)
        {
            if (polygon.Count < 3) return 0;

            // Centro per proiezione locale
            double cLat = 0, cLon = 0;
            foreach (var p in polygon) { cLat += p.lat; cLon += p.lon; }
            cLat /= polygon.Count; cLon /= polygon.Count;

            double cosLat = Math.Cos(cLat * Math.PI / 180.0);
            const double DEG_TO_M = 111320.0;

            // Shoelace formula su coordinate metriche locali
            double area = 0;
            for (int i = 0; i < polygon.Count; i++)
            {
                int j = (i + 1) % polygon.Count;
                double xi = (polygon[i].lon - cLon) * DEG_TO_M * cosLat;
                double yi = (polygon[i].lat - cLat) * DEG_TO_M;
                double xj = (polygon[j].lon - cLon) * DEG_TO_M * cosLat;
                double yj = (polygon[j].lat - cLat) * DEG_TO_M;
                area += xi * yj - xj * yi;
            }
            return Math.Abs(area) * 0.5;
        }

        // -------------------------------------------------------------------
        //  UTILITY: Delete cache
        // -------------------------------------------------------------------

        /// <summary>Deletes the cached JSON file if it exists.</summary>
        public static bool ClearCache(string cachePath)
        {
            if (!string.IsNullOrEmpty(cachePath) && File.Exists(cachePath))
            {
                File.Delete(cachePath);
                Debug.Log($"[OverpassDownloader] Cache eliminata: {cachePath}");
                return true;
            }
            return false;
        }
    }
}
