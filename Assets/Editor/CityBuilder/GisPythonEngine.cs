using UnityEngine;
using UnityEditor;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;

namespace CityBuilder
{
    public static class GisPythonEngine
    {
        private static readonly int[] ValidResolutions = { 33, 65, 129, 257, 513, 1025, 2049, 4097 };

        /// <summary>
        /// Scarica il DEM Copernicus GLO-30 da AWS per un bounding box dato.
        /// Nessuna autenticazione richiesta. Risoluzione ~30m, Float32.
        /// </summary>
        public static async Task<string> DownloadCopernicusDemAsync(float minLon, float minLat, float maxLon, float maxLat, string outputDir)
        {
            EditorUtility.DisplayProgressBar("CityBuilder", "Download DEM Copernicus da AWS...", 0.1f);

            string uid = System.Guid.NewGuid().ToString("N").Substring(0, 8);
            string tmpDir = Path.Combine(Path.GetTempPath(), "citybuilder_dl_" + uid);
            Directory.CreateDirectory(tmpDir);

            string pyPath = Path.Combine(tmpDir, "download_dem.py");
            string outputTif = Path.Combine(outputDir, "copernicus_dem.tif");
            string errorPath = Path.Combine(tmpDir, "py_error.log");

            // Lo script Python:
            // 1. Calcola quali tile Copernicus servono per coprire il bbox
            // 2. Li scarica da S3 via GDAL /vsis3/ (no auth)
            // 3. Li fonde e ritaglia al bbox richiesto
            string pyScript = $@"
import sys, os, traceback, math
from osgeo import gdal, osr
import numpy as np

os.environ['AWS_NO_SIGN_REQUEST'] = 'YES'

try:
    gdal.UseExceptions()
    min_lon = float(sys.argv[1])
    min_lat = float(sys.argv[2])
    max_lon = float(sys.argv[3])
    max_lat = float(sys.argv[4])
    output_tif = sys.argv[5]

    # Calcola tile necessari (ogni tile copre 1x1 gradi)
    lat_start = int(math.floor(min_lat))
    lat_end = int(math.floor(max_lat))
    lon_start = int(math.floor(min_lon))
    lon_end = int(math.floor(max_lon))

    tile_urls = []
    for lat in range(lat_start, lat_end + 1):
        for lon in range(lon_start, lon_end + 1):
            ns = 'N' if lat >= 0 else 'S'
            ew = 'E' if lon >= 0 else 'W'
            alat = abs(lat)
            alon = abs(lon)
            tile_name = f'Copernicus_DSM_COG_10_{{ns}}{{alat:02d}}_00_{{ew}}{{alon:03d}}_00_DEM'
            url = f'/vsis3/copernicus-dem-30m/{{tile_name}}/{{tile_name}}.tif'

            # Verifica che il tile esista
            ds_test = gdal.Open(url)
            if ds_test is not None:
                tile_urls.append(url)
                ds_test = None
                print(f'Tile trovato: {{ns}}{{alat:02d}} {{ew}}{{alon:03d}}')
            else:
                print(f'WARN: tile mancante {{ns}}{{alat:02d}} {{ew}}{{alon:03d}} (probabilmente oceano)')

    if len(tile_urls) == 0:
        raise RuntimeError(f'Nessun tile DEM trovato per bbox [{{min_lon}},{{min_lat}}]-[{{max_lon}},{{max_lat}}]')

    # Fondi i tile e ritaglia al bbox con resampling cubico
    print(f'Fusione e ritaglio di {{len(tile_urls)}} tile...')
    gdal.Warp(
        output_tif,
        tile_urls,
        outputBounds=(min_lon, min_lat, max_lon, max_lat),
        resampleAlg=gdal.GRA_Cubic,
        dstSRS='EPSG:32632',  # UTM 32N per avere coordinate in metri
        dstNodata=-9999,
        multithread=True
    )

    # Verifica risultato
    ds = gdal.Open(output_tif)
    if ds is None:
        raise RuntimeError('Fusione fallita!')

    arr = ds.GetRasterBand(1).ReadAsArray()
    valid = arr[arr != -9999]
    print(f'OK: {{ds.RasterXSize}}x{{ds.RasterYSize}}, range={{np.min(valid):.1f}}-{{np.max(valid):.1f}}m')
    ds = None

except Exception as e:
    with open(r'{errorPath.Replace("\\", "\\\\")}', 'w') as f:
        f.write(traceback.format_exc())
    print(traceback.format_exc(), file=sys.stderr)
    sys.exit(1)
";
            File.WriteAllText(pyPath, pyScript);

            Process p = new Process();
            p.StartInfo.FileName = "python3";
            p.StartInfo.Arguments = $"\"{pyPath}\" {minLon} {minLat} {maxLon} {maxLat} \"{outputTif}\"";
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.Start();

            string stdout = "";
            string stderr = "";
            var stdoutTask = Task.Run(() => stdout = p.StandardOutput.ReadToEnd());
            var stderrTask = Task.Run(() => stderr = p.StandardError.ReadToEnd());

            while (!p.HasExited) { await Task.Delay(200); }
            await stdoutTask;
            await stderrTask;

            if (!string.IsNullOrEmpty(stdout)) UnityEngine.Debug.Log("Download DEM: " + stdout.Trim());

            if (p.ExitCode != 0)
            {
                string err = File.Exists(errorPath) ? File.ReadAllText(errorPath) : stderr;
                UnityEngine.Debug.LogError("Download DEM fallito:\n" + err);
                CleanupTempDir(tmpDir);
                return null;
            }

            CleanupTempDir(tmpDir);

            if (!File.Exists(outputTif))
            {
                UnityEngine.Debug.LogError("File DEM non generato!");
                return null;
            }

            UnityEngine.Debug.Log("DEM scaricato: " + outputTif);
            return outputTif;
        }

        /// <summary>
        /// Processa un GeoTIFF (locale o scaricato) in un .raw float32 normalizzato per Unity.
        /// </summary>
        public static async Task<TerrainMetaData> CleanTifAsync(string tifPath, string rawPath)
        {
            if (!File.Exists(tifPath))
            {
                UnityEngine.Debug.LogError("File GeoTIFF non trovato: " + tifPath);
                return null;
            }

            EditorUtility.DisplayProgressBar("CityBuilder", "Estrazione GPS e Pulizia TIF (Python)...", 0.1f);

            string uid = System.Guid.NewGuid().ToString("N").Substring(0, 8);
            string tmpDir = Path.Combine(Path.GetTempPath(), "citybuilder_" + uid);
            Directory.CreateDirectory(tmpDir);

            string pyPath = Path.Combine(tmpDir, "cleaner.py");
            string metaPath = Path.Combine(tmpDir, "terrain_meta.json");
            string errorPath = Path.Combine(tmpDir, "py_error.log");
            string cleanTifPath = Path.Combine(tmpDir, "temp_clean.tif");
            string warpedTifPath = Path.Combine(tmpDir, "temp_warped.tif");

            string pyScript = $@"
import sys, os, json, traceback, math
from osgeo import gdal, osr
import numpy as np

VALID_RES = [33, 65, 129, 257, 513, 1025, 2049, 4097]
# Default: 4097 per massima fedelta', riducibile via env var su sistemi con poca RAM
MAX_RES = int(os.environ.get('CITYBUILDER_MAX_RES', '4097'))

def choose_res(w, h):
    max_side = max(w, h)
    # Forza almeno 4097 per dettaglio fondale e territorio
    if MAX_RES >= 4097:
        return 4097
    for r in VALID_RES:
        if r >= max_side:
            return min(r, MAX_RES)
    return min(4097, MAX_RES)

try:
    gdal.UseExceptions()
    input_file = sys.argv[1]
    output_raw = sys.argv[2]

    ds = gdal.Open(input_file)
    if ds is None:
        raise RuntimeError(f'Impossibile aprire: {{input_file}}')

    src_w = ds.RasterXSize
    src_h = ds.RasterYSize
    target_res = choose_res(src_w, src_h)

    arr = ds.GetRasterBand(1).ReadAsArray().astype(np.float64)
    nodata = ds.GetRasterBand(1).GetNoDataValue()

    if nodata is not None:
        mask = np.isclose(arr, nodata) | np.isnan(arr)
    else:
        mask = (arr < -1000) | np.isnan(arr)

    has_nodata = np.any(mask)
    gt = ds.GetGeoTransform()

    # Fallback profondita' se batimetria non disponibile
    SEA_FLOOR_DEPTH = -10.0

    # --- Tentativo download batimetria reale (EMODnet / GEBCO) ---
    # Disabilitabile con variabile d'ambiente per risparmiare RAM
    skip_bathy = os.environ.get('CITYBUILDER_SKIP_BATHY', '0') == '1'
    bathy_arr = None
    if has_nodata and not skip_bathy:
        try:
            # Calcola bounding box in WGS84 per il download batimetrico
            proj = osr.SpatialReference(wkt=ds.GetProjection())
            wgs84 = osr.SpatialReference()
            wgs84.ImportFromEPSG(4326)
            if int(gdal.VersionInfo()) >= 3000000:
                proj.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
                wgs84.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
            to_wgs = osr.CoordinateTransformation(proj, wgs84)

            minx = gt[0]
            maxx = gt[0] + src_w * gt[1] + src_h * gt[2]
            maxy = gt[3]
            miny = gt[3] + src_w * gt[4] + src_h * gt[5]

            b_lon_min, b_lat_min, _ = to_wgs.TransformPoint(minx, miny)
            b_lon_max, b_lat_max, _ = to_wgs.TransformPoint(maxx, maxy)
            b_lon_min, b_lon_max = min(b_lon_min, b_lon_max), max(b_lon_min, b_lon_max)
            b_lat_min, b_lat_max = min(b_lat_min, b_lat_max), max(b_lat_min, b_lat_max)

            # Margine 10% per evitare bordi senza dati
            margin_lon = (b_lon_max - b_lon_min) * 0.1
            margin_lat = (b_lat_max - b_lat_min) * 0.1
            b_lon_min -= margin_lon; b_lon_max += margin_lon
            b_lat_min -= margin_lat; b_lat_max += margin_lat

            print(f'Download batimetria per [{{b_lon_min:.4f}},{{b_lat_min:.4f}}]-[{{b_lon_max:.4f}},{{b_lat_max:.4f}}]...')

            import urllib.request, tempfile
            bathy_download = os.path.join(tempfile.gettempdir(), 'cb_bathy_download.tif')
            bathy_ds = None

            # Tentativo 1: EMODnet WCS 1.0.0 (alta ris., mari europei)
            emodnet_urls = [
                ('https://ows.emodnet-bathymetry.eu/wcs?service=WCS&version=1.0.0'
                 '&request=GetCoverage&coverage=emodnet:mean'
                 f'&bbox={{b_lon_min}},{{b_lat_min}},{{b_lon_max}},{{b_lat_max}}'
                 '&CRS=EPSG:4326&width=1024&height=1024&format=GeoTIFF'),
                ('https://ows.emodnet-bathymetry.eu/wcs?service=WCS&version=2.0.1'
                 '&request=GetCoverage&CoverageId=emodnet__mean'
                 f'&subset=Long({{b_lon_min}},{{b_lon_max}})'
                 f'&subset=Lat({{b_lat_min}},{{b_lat_max}})'
                 '&format=image/tiff'),
            ]

            for i, url in enumerate(emodnet_urls):
                try:
                    print(f'  EMODnet tentativo {{i+1}}...')
                    req = urllib.request.Request(url, headers={{'User-Agent': 'CityBuilder/1.0'}})
                    with urllib.request.urlopen(req, timeout=30) as resp:
                        with open(bathy_download, 'wb') as f:
                            f.write(resp.read())
                    bathy_ds = gdal.Open(bathy_download)
                    if bathy_ds is not None:
                        sample = bathy_ds.GetRasterBand(1).ReadAsArray(
                            0, 0, min(100, bathy_ds.RasterXSize), min(100, bathy_ds.RasterYSize))
                        valid = sample[~np.isnan(sample)]
                        if valid.size > 10 and np.min(valid) < -1.0:
                            print(f'  EMODnet OK: {{bathy_ds.RasterXSize}}x{{bathy_ds.RasterYSize}}')
                            break
                        bathy_ds = None
                except Exception as e:
                    print(f'  EMODnet {{i+1}} fallito: {{e}}')
                    bathy_ds = None

            # Tentativo 2: GEBCO WMS (globale, ~450m)
            if bathy_ds is None:
                try:
                    print('  Provo GEBCO...')
                    gebco_url = (
                        'https://www.gebco.net/data_and_products/gebco_web_services/web_map_service/'
                        'mapserv?SERVICE=WMS&VERSION=1.1.1&REQUEST=GetMap'
                        '&LAYERS=gebco_latest'
                        f'&BBOX={{b_lat_min}},{{b_lon_min}},{{b_lat_max}},{{b_lon_max}}'
                        '&SRS=EPSG:4326&WIDTH=1024&HEIGHT=1024&FORMAT=image/tiff'
                    )
                    req = urllib.request.Request(gebco_url, headers={{'User-Agent': 'CityBuilder/1.0'}})
                    with urllib.request.urlopen(req, timeout=30) as resp:
                        with open(bathy_download, 'wb') as f:
                            f.write(resp.read())
                    bathy_ds = gdal.Open(bathy_download)
                    if bathy_ds is not None:
                        print(f'  GEBCO OK: {{bathy_ds.RasterXSize}}x{{bathy_ds.RasterYSize}}')
                except Exception as e:
                    print(f'  GEBCO fallito: {{e}}')

            if bathy_ds is not None:
                # Riproietta la batimetria sulla griglia del DEM
                bathy_warped = gdal.Warp(
                    '', bathy_ds,
                    format='MEM',
                    width=src_w, height=src_h,
                    outputBounds=(minx, miny, maxx, maxy),
                    dstSRS=ds.GetProjection(),
                    resampleAlg=gdal.GRA_Bilinear
                )
                if bathy_warped is not None:
                    bathy_arr = bathy_warped.GetRasterBand(1).ReadAsArray().astype(np.float64)
                    bathy_arr = np.minimum(bathy_arr, -0.5)
                    print(f'Batimetria OK: range {{np.nanmin(bathy_arr):.1f}} a {{np.nanmax(bathy_arr):.1f}}m')
                    bathy_warped = None
                bathy_ds = None
            else:
                print(f'Nessuna batimetria disponibile, uso fondale piatto a {{SEA_FLOOR_DEPTH}}m')

        except Exception as bathy_err:
            print(f'WARN: batimetria non disponibile ({{bathy_err}}), uso fondale piatto a {{SEA_FLOOR_DEPTH}}m')
            bathy_arr = None

    if has_nodata:
        driver = gdal.GetDriverByName('GTiff')
        fill_ds = driver.Create(r'{cleanTifPath.Replace("\\", "\\\\")}', src_w, src_h, 1, gdal.GDT_Float64)
        fill_ds.SetGeoTransform(gt)
        fill_ds.SetProjection(ds.GetProjection())
        band = fill_ds.GetRasterBand(1)
        band.SetNoDataValue(np.nan)
        arr_write = arr.copy()

        if bathy_arr is not None:
            # Usa batimetria reale per i pixel mare
            arr_write[mask] = bathy_arr[mask]
        else:
            arr_write[mask] = SEA_FLOOR_DEPTH

        band.WriteArray(arr_write)

        mask_arr = np.where(mask, 0, 255).astype(np.uint8)
        mask_ds = gdal.GetDriverByName('MEM').Create('', src_w, src_h, 1, gdal.GDT_Byte)
        mask_ds.GetRasterBand(1).WriteArray(mask_arr)

        fill_ds.FlushCache()
        gdal.FillNodata(band, mask_ds.GetRasterBand(1), maxSearchDist=100, smoothingIterations=2)
        arr = band.ReadAsArray()
        fill_ds = None
        mask_ds = None

        # Forza i pixel originariamente NoData (mare) alla batimetria reale o fondale piatto
        if bathy_arr is not None:
            arr[mask] = bathy_arr[mask]
        else:
            arr[mask] = SEA_FLOOR_DEPTH
    else:
        driver = gdal.GetDriverByName('GTiff')
        fill_ds = driver.Create(r'{cleanTifPath.Replace("\\", "\\\\")}', src_w, src_h, 1, gdal.GDT_Float64)
        fill_ds.SetGeoTransform(gt)
        fill_ds.SetProjection(ds.GetProjection())
        fill_ds.GetRasterBand(1).WriteArray(arr)
        fill_ds.FlushCache()
        fill_ds = None

    # Salva DEM completo (terreno + batimetria) come GeoTIFF riutilizzabile
    merged_tif = os.path.splitext(input_file)[0] + '_merged_bathymetry.tif'
    merged_driver = gdal.GetDriverByName('GTiff')
    merged_ds = merged_driver.Create(merged_tif, src_w, src_h, 1, gdal.GDT_Float32,
        ['COMPRESS=DEFLATE', 'TILED=YES', 'PREDICTOR=2'])
    merged_ds.SetGeoTransform(gt)
    merged_ds.SetProjection(ds.GetProjection())
    merged_ds.GetRasterBand(1).WriteArray(arr.astype(np.float32))
    merged_ds.GetRasterBand(1).SetNoDataValue(-9999)
    merged_ds.FlushCache()
    merged_ds = None
    print(f'DEM completo (terreno+batimetria) salvato: {{merged_tif}}')

    min_e = float(np.nanmin(arr))
    max_e = float(np.nanmax(arr))
    elevation_range = max_e - min_e

    if elevation_range < 0.01:
        raise RuntimeError(f'DEM piatto: min={{min_e}}, max={{max_e}}. Controlla il file.')

    sea_level_norm = max(0.0, (0.0 - min_e) / elevation_range)

    warped_ds = gdal.Warp(
        r'{warpedTifPath.Replace("\\", "\\\\")}',
        r'{cleanTifPath.Replace("\\", "\\\\")}',
        width=target_res, height=target_res,
        resampleAlg=gdal.GRA_Cubic
    )
    warped_arr = warped_ds.GetRasterBand(1).ReadAsArray().astype(np.float64)
    warped_arr = np.clip(warped_arr, min_e, max_e)
    norm = ((warped_arr - min_e) / elevation_range).astype(np.float32)
    norm = np.flipud(norm)
    warped_ds = None

    with open(output_raw, 'wb') as f:
        f.write(norm.tobytes())

    width = ds.RasterXSize
    height = ds.RasterYSize
    minx = gt[0]
    maxx = gt[0] + width * gt[1] + height * gt[2]
    maxy = gt[3]
    miny = gt[3] + width * gt[4] + height * gt[5]

    proj = osr.SpatialReference(wkt=ds.GetProjection())
    wgs84 = osr.SpatialReference()
    wgs84.ImportFromEPSG(4326)

    if int(gdal.VersionInfo()) >= 3000000:
        proj.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
        wgs84.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)

    transform = osr.CoordinateTransformation(proj, wgs84)

    try:
        lon_min, lat_min, _ = transform.TransformPoint(minx, miny)
        lon_max, lat_max, _ = transform.TransformPoint(maxx, maxy)
    except Exception as te:
        print(f'WARN: trasformazione GPS fallita ({{te}}), uso coordinate proiettate', file=sys.stderr)
        lon_min, lat_min, lon_max, lat_max = minx, miny, maxx, maxy

    meta = {{
        'minLon': min(lon_min, lon_max), 'maxLon': max(lon_min, lon_max),
        'minLat': min(lat_min, lat_max), 'maxLat': max(lat_min, lat_max),
        'widthM': abs(maxx - minx), 'lengthM': abs(maxy - miny),
        'heightM': float(elevation_range), 'seaLevelNorm': sea_level_norm,
        'rawResolution': target_res
    }}

    with open(r'{metaPath.Replace("\\", "\\\\")}', 'w') as f:
        json.dump(meta, f)

    nodata_pct = (np.sum(mask) / mask.size * 100) if has_nodata else 0
    print(f'OK: {{src_w}}x{{src_h}} -> {{target_res}}x{{target_res}} (float32 cubic), range={{elevation_range:.1f}}m, nodata={{nodata_pct:.1f}}%')

except Exception as e:
    with open(r'{errorPath.Replace("\\", "\\\\")}', 'w') as f:
        f.write(traceback.format_exc())
    print(traceback.format_exc(), file=sys.stderr)
    sys.exit(1)
";
            File.WriteAllText(pyPath, pyScript);

            Process p = new Process();
            p.StartInfo.FileName = "python3";
            p.StartInfo.Arguments = $"\"{pyPath}\" \"{tifPath}\" \"{rawPath}\"";
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.Start();

            string stdout = "";
            string stderr = "";
            var stdoutTask = Task.Run(() => stdout = p.StandardOutput.ReadToEnd());
            var stderrTask = Task.Run(() => stderr = p.StandardError.ReadToEnd());

            while (!p.HasExited) { await Task.Delay(100); }
            await stdoutTask;
            await stderrTask;

            if (!string.IsNullOrEmpty(stdout)) UnityEngine.Debug.Log("Python: " + stdout.Trim());

            if (p.ExitCode != 0)
            {
                string err = File.Exists(errorPath) ? File.ReadAllText(errorPath) : stderr;
                UnityEngine.Debug.LogError("PYTHON HA FALLITO:\n" + err);
                CleanupTempDir(tmpDir);
                return null;
            }

            if (!string.IsNullOrEmpty(stderr) && !stderr.Contains("WARN:"))
                UnityEngine.Debug.LogWarning("Python stderr: " + stderr.Trim());

            if (!File.Exists(metaPath))
            {
                UnityEngine.Debug.LogError("Python non ha generato terrain_meta.json!");
                CleanupTempDir(tmpDir);
                return null;
            }

            TerrainMetaData tMeta = JsonUtility.FromJson<TerrainMetaData>(File.ReadAllText(metaPath));
            CleanupTempDir(tmpDir);

            // Scala 1:1 — coordinate in metri reali
            UnityEngine.Debug.Log($"GeoTIFF: {tMeta.widthM:F0}x{tMeta.lengthM:F0}m, h={tMeta.heightM:F0}m, res={tMeta.rawResolution}, GPS [{tMeta.minLon:F4},{tMeta.minLat:F4}]-[{tMeta.maxLon:F4},{tMeta.maxLat:F4}]");
            return tMeta;
        }

        private static void CleanupTempDir(string tmpDir)
        {
            try { Directory.Delete(tmpDir, true); }
            catch (System.Exception e) { UnityEngine.Debug.LogWarning("Pulizia temp fallita: " + e.Message); }
        }
    }
}
