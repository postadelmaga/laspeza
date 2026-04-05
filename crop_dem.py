#!/usr/bin/env python3
"""
Ritaglia un GeoTIFF centrato su un punto con un raggio dato.
Utile per ridurre l'area prima di passarla a CityBuilder.

Uso:
  python3 crop_dem.py input.tif output.tif --center 9.82 44.10 --radius 3000
  python3 crop_dem.py input.tif output.tif --center 9.82 44.10 --radius 2000 --city "La Spezia centro"

Il centro e' in lon,lat (WGS84), il raggio in metri.
"""
import sys, os, math, argparse

def main():
    parser = argparse.ArgumentParser(description="Ritaglia DEM in cerchio/quadrato attorno a un punto")
    parser.add_argument("input", help="GeoTIFF sorgente")
    parser.add_argument("output", help="GeoTIFF ritagliato")
    parser.add_argument("--center", nargs=2, type=float, required=True,
                        metavar=("LON", "LAT"), help="Centro in lon lat (es: 9.82 44.10)")
    parser.add_argument("--radius", type=float, default=3000,
                        help="Raggio in metri (default: 3000)")
    parser.add_argument("--city", type=str, default="", help="Nome citta' (solo per log)")
    args = parser.parse_args()

    try:
        from osgeo import gdal
        gdal.UseExceptions()
    except ImportError:
        print("ERRORE: serve GDAL. Installa con: pip install GDAL")
        sys.exit(1)

    lon, lat = args.center
    radius_m = args.radius

    # Converti raggio da metri a gradi (approssimato alla latitudine)
    # 1 grado lat ≈ 111320 m, 1 grado lon ≈ 111320 * cos(lat) m
    lat_rad = math.radians(lat)
    d_lat = radius_m / 111320.0
    d_lon = radius_m / (111320.0 * math.cos(lat_rad))

    min_lon = lon - d_lon
    max_lon = lon + d_lon
    min_lat = lat - d_lat
    max_lat = lat + d_lat

    name = args.city or f"{lon:.4f},{lat:.4f}"
    print(f"Ritaglio DEM: {name}")
    print(f"  Centro: {lon:.5f}, {lat:.5f}")
    print(f"  Raggio: {radius_m:.0f}m")
    print(f"  BBox: [{min_lon:.5f}, {min_lat:.5f}] - [{max_lon:.5f}, {max_lat:.5f}]")
    print(f"  Input:  {args.input}")
    print(f"  Output: {args.output}")

    # Apri sorgente per verificare
    ds = gdal.Open(args.input)
    if ds is None:
        print(f"ERRORE: impossibile aprire {args.input}")
        sys.exit(1)

    print(f"  Sorgente: {ds.RasterXSize}x{ds.RasterYSize}")
    ds = None

    # Ritaglia con GDAL Warp (riproietta in UTM per avere metri)
    gdal.Warp(
        args.output,
        args.input,
        outputBounds=(min_lon, min_lat, max_lon, max_lat),
        outputBoundsSRS='EPSG:4326',
        dstSRS=f'EPSG:{32600 + int((lon + 180) / 6) + 1}',  # UTM zona auto
        resampleAlg=gdal.GRA_Cubic,
        dstNodata=-9999,
    )

    # Verifica output
    ds_out = gdal.Open(args.output)
    if ds_out is None:
        print("ERRORE: ritaglio fallito!")
        sys.exit(1)

    import numpy as np
    arr = ds_out.GetRasterBand(1).ReadAsArray()
    valid = arr[arr != -9999]
    w, h = ds_out.RasterXSize, ds_out.RasterYSize
    gt = ds_out.GetGeoTransform()
    width_m = abs(w * gt[1])
    height_m = abs(h * gt[5])
    ds_out = None

    print(f"  Output: {w}x{h} pixel, {width_m:.0f}x{height_m:.0f}m")
    print(f"  Elevazione: {np.min(valid):.1f} - {np.max(valid):.1f}m")
    print(f"  File: {os.path.getsize(args.output) / 1024:.0f} KB")
    print("OK!")

if __name__ == "__main__":
    main()
