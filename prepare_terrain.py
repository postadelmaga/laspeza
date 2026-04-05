#!/usr/bin/env python3
"""
Fase 0: Prepara il terreno SENZA Unity.
Processa il GeoTIFF in un .raw float32 normalizzato pronto per Unity.

Versione memory-efficient: lavora a blocchi tramite GDAL, mai tutto in RAM.

Uso:
  python3 prepare_terrain.py DATA/laspeza_10km.tif
  python3 prepare_terrain.py DATA/spesa.tif --center 9.82 44.10 --radius 3000
  python3 prepare_terrain.py DATA/spesa.tif --max-res 1025 --skip-bathy

Output nella stessa cartella del TIF:
  - processed_terrain.raw     Heightmap float32 normalizzata per Unity
  - terrain_meta_saved.json   Metadata (bounds, dimensioni, seaLevel)
  - *_merged_bathymetry.tif   DEM completo con fondali (opzionale)
"""
import sys, os, json, math, argparse, tempfile
import numpy as np


# Dimensione blocco per lettura a tile (righe per volta)
BLOCK_ROWS = 512


def get_stats_blocked(ds, band_idx=1):
    """Calcola min/max/nodata% leggendo a blocchi, senza caricare tutto in RAM."""
    band = ds.GetRasterBand(band_idx)
    nodata = band.GetNoDataValue()
    w, h = ds.RasterXSize, ds.RasterYSize

    global_min = float('inf')
    global_max = float('-inf')
    nodata_count = 0
    total = 0

    for y in range(0, h, BLOCK_ROWS):
        rows = min(BLOCK_ROWS, h - y)
        block = band.ReadAsArray(0, y, w, rows).astype(np.float32)
        total += block.size

        if nodata is not None:
            mask = np.isclose(block, nodata) | np.isnan(block)
        else:
            mask = (block < -1000) | np.isnan(block)

        nodata_count += np.sum(mask)
        valid = block[~mask]
        if valid.size > 0:
            global_min = min(global_min, float(np.min(valid)))
            global_max = max(global_max, float(np.max(valid)))

    has_nodata = nodata_count > 0
    nodata_pct = nodata_count / total * 100 if total > 0 else 0
    return global_min, global_max, has_nodata, nodata_pct


def get_bounds_wgs84(ds):
    """Restituisce i bounds in WGS84 (lon/lat)."""
    from osgeo import osr, gdal

    gt = ds.GetGeoTransform()
    w, h = ds.RasterXSize, ds.RasterYSize
    proj_wkt = ds.GetProjection()

    minx = gt[0]
    maxx = gt[0] + w * gt[1] + h * gt[2]
    maxy = gt[3]
    miny = gt[3] + w * gt[4] + h * gt[5]

    proj = osr.SpatialReference(wkt=proj_wkt)
    wgs84 = osr.SpatialReference()
    wgs84.ImportFromEPSG(4326)
    if int(gdal.VersionInfo()) >= 3000000:
        proj.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
        wgs84.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
    transform = osr.CoordinateTransformation(proj, wgs84)

    try:
        lon_min, lat_min, _ = transform.TransformPoint(minx, miny)
        lon_max, lat_max, _ = transform.TransformPoint(maxx, maxy)
    except Exception:
        lon_min, lat_min, lon_max, lat_max = minx, miny, maxx, maxy

    return (min(lon_min, lon_max), min(lat_min, lat_max),
            max(lon_min, lon_max), max(lat_min, lat_max),
            float(abs(maxx - minx)), float(abs(maxy - miny)))


def crop_to_center(input_tif, data_dir, lon, lat, radius_m):
    """Ritaglia il DEM attorno a un centro. Salva su disco, non in RAM."""
    from osgeo import gdal

    cropped_tif = os.path.join(data_dir, "cropped_dem.tif")
    lat_rad = math.radians(lat)
    d_lat = radius_m / 111320.0
    d_lon = radius_m / (111320.0 * math.cos(lat_rad))
    utm_zone = int((lon + 180) / 6) + 1

    print(f"Ritaglio: centro {lon},{lat}, raggio {radius_m}m")
    gdal.Warp(cropped_tif, input_tif,
              outputBounds=(lon - d_lon, lat - d_lat, lon + d_lon, lat + d_lat),
              outputBoundsSRS='EPSG:4326',
              dstSRS=f'EPSG:{32600 + utm_zone}',
              resampleAlg=gdal.GRA_Cubic,
              dstNodata=-9999,
              creationOptions=['COMPRESS=DEFLATE', 'TILED=YES', 'BIGTIFF=YES'])
    print(f"  Ritagliato: {cropped_tif}")
    return cropped_tif


def fill_nodata_on_disk(input_tif, output_tif, sea_depth, skip_bathy):
    """Riempi NoData con batimetria o fondale piatto. Lavora su disco."""
    from osgeo import gdal, osr

    ds = gdal.Open(input_tif)
    w, h = ds.RasterXSize, ds.RasterYSize
    gt = ds.GetGeoTransform()
    proj_wkt = ds.GetProjection()
    band = ds.GetRasterBand(1)
    nodata = band.GetNoDataValue()

    # Crea copia su disco per lavorarci
    driver = gdal.GetDriverByName('GTiff')
    work_ds = driver.Create(output_tif, w, h, 1, gdal.GDT_Float32,
                            ['COMPRESS=DEFLATE', 'TILED=YES', 'BIGTIFF=YES'])
    work_ds.SetGeoTransform(gt)
    work_ds.SetProjection(proj_wkt)
    work_band = work_ds.GetRasterBand(1)
    work_band.SetNoDataValue(np.nan)

    # Prova batimetria
    bathy_ds = None
    if not skip_bathy:
        try:
            bathy_ds = _download_bathymetry(ds)
        except Exception as e:
            print(f"  Batimetria fallita: {e}")

    # Copia blocco per blocco, sostituendo NoData
    print("  Fill NoData a blocchi...")
    for y in range(0, h, BLOCK_ROWS):
        rows = min(BLOCK_ROWS, h - y)
        block = band.ReadAsArray(0, y, w, rows).astype(np.float32)

        if nodata is not None:
            mask = np.isclose(block, nodata, atol=0.1) | np.isnan(block)
        else:
            mask = (block < -1000) | np.isnan(block)

        if np.any(mask):
            if bathy_ds is not None:
                bathy_block = bathy_ds.GetRasterBand(1).ReadAsArray(0, y, w, rows).astype(np.float32)
                # Usa batimetria solo dove e' negativa (mare), altrimenti fondale piatto
                bathy_vals = np.where(bathy_block < -0.5, bathy_block, sea_depth)
                block[mask] = bathy_vals[mask]
                del bathy_block, bathy_vals
            else:
                block[mask] = sea_depth

        work_band.WriteArray(block, 0, y)
        del block

    work_ds.FlushCache()
    bathy_ds = None

    # FillNodata di GDAL per eventuali buchi residui (opera su disco)
    mask_band = work_ds.GetRasterBand(1).GetMaskBand()
    gdal.FillNodata(work_band, mask_band, maxSearchDist=100, smoothingIterations=2)
    work_ds.FlushCache()

    # Seconda passata: forza valori mare originali dopo FillNodata
    # (FillNodata potrebbe aver interpolato sopra i pixel mare)
    ds2 = gdal.Open(input_tif)
    band2 = ds2.GetRasterBand(1)

    # Riapri batimetria se disponibile
    bathy_restore = None
    bathy_aligned = os.path.join(tempfile.gettempdir(), 'cb_bathy_aligned.tif')
    if os.path.exists(bathy_aligned):
        bathy_restore = gdal.Open(bathy_aligned)

    for y in range(0, h, BLOCK_ROWS):
        rows = min(BLOCK_ROWS, h - y)
        orig = band2.ReadAsArray(0, y, w, rows).astype(np.float32)

        if nodata is not None:
            mask = np.isclose(orig, nodata, atol=0.1) | np.isnan(orig)
        else:
            mask = (orig < -1000) | np.isnan(orig)

        if np.any(mask):
            filled = work_band.ReadAsArray(0, y, w, rows).astype(np.float32)
            if bathy_restore is not None:
                bathy_block = bathy_restore.GetRasterBand(1).ReadAsArray(0, y, w, rows).astype(np.float32)
                bathy_vals = np.where(bathy_block < -0.5, bathy_block, sea_depth)
                filled[mask] = bathy_vals[mask]
                del bathy_block, bathy_vals
            else:
                filled[mask] = sea_depth
            work_band.WriteArray(filled, 0, y)
            del filled
        del orig

    work_ds.FlushCache()
    work_ds = None
    ds = None
    ds2 = None
    bathy_restore = None
    print(f"  NoData riempito: {output_tif}")
    return output_tif


def _download_bathymetry(ds):
    """Scarica batimetria EMODnet/GEBCO e la allinea al DEM. Restituisce dataset su disco."""
    from osgeo import gdal, osr
    import urllib.request

    w, h = ds.RasterXSize, ds.RasterYSize
    gt = ds.GetGeoTransform()
    proj_wkt = ds.GetProjection()

    proj = osr.SpatialReference(wkt=proj_wkt)
    wgs84 = osr.SpatialReference()
    wgs84.ImportFromEPSG(4326)
    if int(gdal.VersionInfo()) >= 3000000:
        proj.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
        wgs84.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
    to_wgs = osr.CoordinateTransformation(proj, wgs84)

    minx = gt[0]
    maxx = gt[0] + w * gt[1] + h * gt[2]
    maxy = gt[3]
    miny = gt[3] + w * gt[4] + h * gt[5]
    b_lon_min, b_lat_min, _ = to_wgs.TransformPoint(minx, miny)
    b_lon_max, b_lat_max, _ = to_wgs.TransformPoint(maxx, maxy)
    b_lon_min, b_lon_max = min(b_lon_min, b_lon_max), max(b_lon_min, b_lon_max)
    b_lat_min, b_lat_max = min(b_lat_min, b_lat_max), max(b_lat_min, b_lat_max)

    # Margine 10% per evitare bordi senza dati
    margin_lon = (b_lon_max - b_lon_min) * 0.1
    margin_lat = (b_lat_max - b_lat_min) * 0.1
    b_lon_min -= margin_lon; b_lon_max += margin_lon
    b_lat_min -= margin_lat; b_lat_max += margin_lat

    bathy_download = os.path.join(tempfile.gettempdir(), 'cb_bathy_download.tif')
    bathy_src = None

    # ── Tentativo 1: EMODnet WMS/WCS (alta risoluzione, mari europei) ──
    # EMODnet 2024: ~115m risoluzione, gratis, CC-BY
    emodnet_urls = [
        # WCS 1.0.0 con bbox in ordine lon,lat
        ('https://ows.emodnet-bathymetry.eu/wcs?service=WCS&version=1.0.0'
         '&request=GetCoverage&coverage=emodnet:mean'
         f'&bbox={b_lon_min},{b_lat_min},{b_lon_max},{b_lat_max}'
         '&CRS=EPSG:4326&width=1024&height=1024&format=GeoTIFF'),
        # WCS 2.0.1 alternativo
        ('https://ows.emodnet-bathymetry.eu/wcs?service=WCS&version=2.0.1'
         '&request=GetCoverage&CoverageId=emodnet__mean'
         f'&subset=Long({b_lon_min},{b_lon_max})'
         f'&subset=Lat({b_lat_min},{b_lat_max})'
         '&format=image/tiff'),
    ]

    for i, url in enumerate(emodnet_urls):
        print(f"  Download batimetria EMODnet (tentativo {i+1})...")
        try:
            req = urllib.request.Request(url, headers={'User-Agent': 'CityBuilder/1.0'})
            with urllib.request.urlopen(req, timeout=30) as resp:
                with open(bathy_download, 'wb') as f:
                    f.write(resp.read())
            bathy_src = gdal.Open(bathy_download)
            if bathy_src is not None:
                # Verifica che contenga dati reali (non solo nodata)
                sample = bathy_src.GetRasterBand(1).ReadAsArray(0, 0,
                    min(100, bathy_src.RasterXSize), min(100, bathy_src.RasterYSize))
                nd = bathy_src.GetRasterBand(1).GetNoDataValue()
                valid = sample[~np.isnan(sample)] if nd is None else sample[(sample != nd) & ~np.isnan(sample)]
                if valid.size > 10 and np.min(valid) < -1.0:
                    print(f"  EMODnet OK: {bathy_src.RasterXSize}x{bathy_src.RasterYSize}, "
                          f"profondita' {np.min(valid):.1f} a {np.max(valid):.1f}m")
                    break
                else:
                    print(f"  EMODnet: dati non validi (min={np.min(valid) if valid.size > 0 else 'N/A'})")
                    bathy_src = None
        except Exception as e:
            print(f"  EMODnet tentativo {i+1} fallito: {e}")
            bathy_src = None

    # ── Tentativo 2: GEBCO 2024 via GDAL /vsicurl/ (globale, ~450m) ──
    if bathy_src is None:
        print("  Provo GEBCO 2024...")
        try:
            # GEBCO offre un WMS tile service
            gebco_url = (
                'https://www.gebco.net/data_and_products/gebco_web_services/web_map_service/'
                'mapserv?SERVICE=WMS&VERSION=1.1.1&REQUEST=GetMap'
                '&LAYERS=gebco_latest'
                f'&BBOX={b_lat_min},{b_lon_min},{b_lat_max},{b_lon_max}'
                '&SRS=EPSG:4326&WIDTH=1024&HEIGHT=1024&FORMAT=image/tiff'
            )
            gebco_download = os.path.join(tempfile.gettempdir(), 'cb_gebco.tif')
            req = urllib.request.Request(gebco_url, headers={'User-Agent': 'CityBuilder/1.0'})
            with urllib.request.urlopen(req, timeout=30) as resp:
                with open(gebco_download, 'wb') as f:
                    f.write(resp.read())
            bathy_src = gdal.Open(gebco_download)
            if bathy_src is not None:
                print(f"  GEBCO OK: {bathy_src.RasterXSize}x{bathy_src.RasterYSize}")
                bathy_download = gebco_download
            else:
                print("  GEBCO: file non valido")
        except Exception as e:
            print(f"  GEBCO fallito: {e}")

    if bathy_src is None:
        print("  Nessuna batimetria disponibile, uso fondale piatto")
        return None

    # Warp batimetria alla stessa griglia del DEM - su file temp
    bathy_tmp = os.path.join(tempfile.gettempdir(), 'cb_bathy_aligned.tif')
    bathy_warped = gdal.Warp(bathy_tmp, bathy_src,
                             width=w, height=h,
                             outputBounds=(minx, miny, maxx, maxy),
                             dstSRS=proj_wkt,
                             resampleAlg=gdal.GRA_Bilinear,
                             creationOptions=['COMPRESS=DEFLATE', 'TILED=YES'])
    bathy_src = None

    if bathy_warped is not None:
        bathy_warped.FlushCache()
        arr_check = bathy_warped.GetRasterBand(1).ReadAsArray()
        sea_vals = arr_check[arr_check < -0.5]
        if sea_vals.size > 0:
            print(f"  Batimetria allineata: {sea_vals.size:,} pixel mare, "
                  f"profondita' {np.min(sea_vals):.1f} a {np.max(sea_vals):.1f}m, "
                  f"media {np.mean(sea_vals):.1f}m")
        else:
            print("  WARN: batimetria allineata ma nessun pixel negativo!")
        del arr_check
        return bathy_warped
    return None


def normalize_to_raw(input_tif, raw_path, min_e, max_e, target_res):
    """Normalizza DEM -> .raw float32. Warp su disco + lettura a blocchi."""
    from osgeo import gdal

    elevation_range = max_e - min_e

    # Warp alla risoluzione target su file temporaneo
    warped_tif = os.path.join(tempfile.gettempdir(), 'cb_warped_final.tif')
    print(f"  Warp a {target_res}x{target_res}...")
    gdal.Warp(warped_tif, input_tif,
              width=target_res, height=target_res,
              resampleAlg=gdal.GRA_Cubic,
              creationOptions=['COMPRESS=DEFLATE', 'TILED=YES', 'BIGTIFF=YES'])

    # Scrivi .raw a blocchi (flipud = leggiamo dal basso verso l'alto)
    ds = gdal.Open(warped_tif)
    band = ds.GetRasterBand(1)
    w, h = ds.RasterXSize, ds.RasterYSize
    assert w == target_res and h == target_res, f"Warp fallito: {w}x{h} != {target_res}x{target_res}"

    print(f"  Scrittura .raw ({target_res}x{target_res}, {target_res*target_res*4//1024//1024}MB)...")
    with open(raw_path, 'wb') as f:
        # flipud: leggiamo le righe dal basso verso l'alto
        for y in range(h - 1, -1, -BLOCK_ROWS):
            y_start = max(0, y - BLOCK_ROWS + 1)
            rows = y - y_start + 1
            block = band.ReadAsArray(0, y_start, w, rows).astype(np.float32)
            block = np.clip(block, min_e, max_e)
            block = (block - min_e) / elevation_range
            # Flip il blocco (le righe sono gia' in ordine top-down, vogliamo bottom-up)
            block = np.flipud(block)
            f.write(block.tobytes())
            del block

    ds = None
    # Cleanup
    try:
        os.remove(warped_tif)
    except OSError:
        pass

    print(f"  Heightmap: {raw_path} ({os.path.getsize(raw_path)//1024}KB)")


def main():
    parser = argparse.ArgumentParser(description="Prepara terreno per Unity CityBuilder")
    parser.add_argument("input", help="GeoTIFF sorgente")
    parser.add_argument("--center", nargs=2, type=float, metavar=("LON", "LAT"),
                        help="Ritaglia attorno a questo centro (lon lat)")
    parser.add_argument("--radius", type=float, default=5000,
                        help="Raggio ritaglio in metri (default: 5000)")
    parser.add_argument("--max-res", type=int, default=4097,
                        help="Risoluzione max heightmap (default: 4097)")
    parser.add_argument("--skip-bathy", action="store_true",
                        help="Salta download batimetria")
    parser.add_argument("--sea-depth", type=float, default=-10.0,
                        help="Profondita' fondale piatto se no batimetria (default: -10)")
    args = parser.parse_args()

    try:
        from osgeo import gdal, osr
        gdal.UseExceptions()
    except ImportError:
        print("ERRORE: serve GDAL. Installa con: pip install GDAL")
        sys.exit(1)

    input_tif = args.input
    if not os.path.exists(input_tif):
        print(f"ERRORE: file non trovato: {input_tif}")
        sys.exit(1)

    data_dir = os.path.dirname(os.path.abspath(input_tif))
    raw_path = os.path.join(data_dir, "processed_terrain.raw")
    meta_path = os.path.join(data_dir, "terrain_meta_saved.json")

    # ── Step 1: Ritaglio opzionale ──
    working_tif = input_tif
    if args.center:
        working_tif = crop_to_center(input_tif, data_dir, args.center[0], args.center[1], args.radius)

    # ── Step 2: Statistiche (a blocchi, no full load) ──
    print(f"Lettura DEM: {working_tif}")
    ds = gdal.Open(working_tif)
    if ds is None:
        print(f"ERRORE: impossibile aprire {working_tif}")
        sys.exit(1)

    src_w, src_h = ds.RasterXSize, ds.RasterYSize
    gt = ds.GetGeoTransform()
    pixel_mb = src_w * src_h * 4 / 1024 / 1024
    print(f"  Dimensione: {src_w}x{src_h} ({pixel_mb:.0f}MB come float32)")

    min_e, max_e, has_nodata, nodata_pct = get_stats_blocked(ds)
    print(f"  Elevazione: {min_e:.1f} - {max_e:.1f}m")
    print(f"  NoData: {nodata_pct:.1f}%")

    elevation_range = max_e - min_e
    if elevation_range < 0.01:
        print(f"ERRORE: DEM piatto (min={min_e}, max={max_e})")
        sys.exit(1)

    sea_level_norm = max(0.0, (0.0 - min_e) / elevation_range)

    # Bounds per metadata
    lon_min, lat_min, lon_max, lat_max, width_m, length_m = get_bounds_wgs84(ds)
    ds = None  # chiudi subito

    # ── Step 3: Fill NoData (su disco) ──
    if has_nodata:
        merged_tif = os.path.splitext(os.path.abspath(input_tif))[0] + '_merged_bathymetry.tif'
        fill_nodata_on_disk(working_tif, merged_tif, args.sea_depth, args.skip_bathy)
        working_tif = merged_tif
        print(f"  DEM merged: {merged_tif} ({os.path.getsize(merged_tif)//1024}KB)")

        # Ricalcola stats dopo fill
        ds2 = gdal.Open(merged_tif)
        min_e, max_e, _, _ = get_stats_blocked(ds2)
        ds2 = None
        elevation_range = max_e - min_e
        sea_level_norm = max(0.0, (0.0 - min_e) / elevation_range)
        print(f"  Elevazione post-fill: {min_e:.1f} - {max_e:.1f}m")
    else:
        # Salva comunque merged (copia)
        merged_tif = os.path.splitext(os.path.abspath(input_tif))[0] + '_merged_bathymetry.tif'
        gdal.Translate(merged_tif, working_tif,
                       creationOptions=['COMPRESS=DEFLATE', 'TILED=YES', 'BIGTIFF=YES'])
        print(f"  DEM merged (no NoData): {merged_tif}")

    # ── Step 4: Risoluzione target ──
    VALID_RES = [33, 65, 129, 257, 513, 1025, 2049, 4097]
    max_side = max(src_w, src_h)
    target_res = args.max_res
    for r in VALID_RES:
        if r >= max_side:
            target_res = min(r, args.max_res)
            break

    print(f"  Target: {src_w}x{src_h} -> {target_res}x{target_res}")
    print(f"  Sea level norm: {sea_level_norm:.4f}")

    # ── Step 5: Normalizza e scrivi .raw (a blocchi) ──
    normalize_to_raw(working_tif, raw_path, min_e, max_e, target_res)

    # ── Step 6: Salva metadata ──
    meta = {
        'minLon': lon_min, 'maxLon': lon_max,
        'minLat': lat_min, 'maxLat': lat_max,
        'widthM': width_m,
        'lengthM': length_m,
        'heightM': float(elevation_range),
        'seaLevelNorm': sea_level_norm,
        'rawResolution': target_res
    }

    with open(meta_path, 'w') as f:
        json.dump(meta, f, indent=2)
    print(f"  Metadata: {meta_path}")

    # ── Riepilogo ──
    print()
    print("=" * 50)
    print(f"TERRENO PRONTO!")
    print(f"  Area: {width_m:.0f}x{length_m:.0f}m")
    print(f"  GPS: [{lon_min:.4f},{lat_min:.4f}]-[{lon_max:.4f},{lat_max:.4f}]")
    print(f"  Elevazione: {min_e:.1f} - {max_e:.1f}m")
    print(f"  Heightmap: {target_res}x{target_res}")
    print(f"  Sea level: {sea_level_norm:.4f}")
    print()
    print("Prossimo step: apri Unity e usa il plugin CityBuilder")
    print(f"  oppure: ./generate.sh --step terrain --tif {working_tif}")
    print("=" * 50)

    # Cleanup file temp
    for tmp in ['cb_bathy_aligned.tif', 'cb_warped_final.tif', 'cb_fill_tmp.tif', 'cb_clean.tif']:
        p = os.path.join(tempfile.gettempdir(), tmp)
        try:
            os.remove(p)
        except OSError:
            pass


if __name__ == "__main__":
    main()
