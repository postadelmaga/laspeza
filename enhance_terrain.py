#!/usr/bin/env python3
"""
Migliora il dettaglio del terreno oltre la risoluzione sorgente del DEM.

Combina 3 tecniche:
  1. Ridge/valley sharpening — creste più affilate, valli più incise
  2. Slope-dependent fractal noise — texture geologica plausibile
  3. Hydraulic erosion — canali di drenaggio e sedimentazione

Lavora sul .raw float32 prodotto da prepare_terrain.py.
NON modifica i dati dove sono corretti (zone piatte, mare), aggiunge
dettaglio solo dove il terreno è ripido o montuoso.

Uso:
  python3 enhance_terrain.py                          # default: DATA/
  python3 enhance_terrain.py --data-dir DATA/         # dir esplicita
  python3 enhance_terrain.py --strength 0.5           # meno dettaglio
  python3 enhance_terrain.py --no-erosion             # salta erosione
  python3 enhance_terrain.py --erosion-iters 200      # più erosione

L'output sovrascrive processed_terrain.raw (backup salvato come .raw.bak).
"""
import sys, os, json, argparse, time
import numpy as np


# ════════════════════════════════════════════════════════════════════
#  1. RIDGE / VALLEY SHARPENING
#     Unsharp mask guidato dalla curvatura: amplifica solo creste e
#     valli, non le superfici piatte o il mare.
# ════════════════════════════════════════════════════════════════════

def sharpen_ridges(h, strength=0.3, sigma_px=3):
    """Sharpening selettivo: amplifica la curvatura locale (creste/valli)."""
    from scipy.ndimage import gaussian_filter

    smooth = gaussian_filter(h, sigma=sigma_px)
    detail = h - smooth  # alta frequenza = curvatura locale

    # Maschera: applica solo dove c'è pendenza (no mare, no pianura)
    gy, gx = np.gradient(h)
    slope = np.sqrt(gx**2 + gy**2)
    slope_mask = np.clip(slope / np.percentile(slope[slope > 0], 90), 0, 1)

    # Curvatura: Laplaciano come indicatore di creste (+) e valli (-)
    laplacian = (np.roll(h, 1, 0) + np.roll(h, -1, 0) +
                 np.roll(h, 1, 1) + np.roll(h, -1, 1) - 4 * h)
    curv_mag = np.abs(laplacian)
    curv_mask = np.clip(curv_mag / np.percentile(curv_mag[curv_mag > 0], 95), 0, 1)

    # Combina: sharpening forte su creste/valli ripide, zero su piatto/mare
    mask = slope_mask * curv_mask
    # Limita il dettaglio massimo aggiunto (evita spike di 60m+)
    max_delta = 0.01  # ~8m su range 800m
    clipped_detail = np.clip(detail * strength * mask, -max_delta, max_delta)
    enhanced = h + clipped_detail

    return np.clip(enhanced, 0, 1)


# ════════════════════════════════════════════════════════════════════
#  2. SLOPE-DEPENDENT FRACTAL NOISE
#     Aggiunge texture geologica plausibile: molto dettaglio su
#     pendii rocciosi, poco su pianura, zero su mare.
# ════════════════════════════════════════════════════════════════════

def _perlin_octave(shape, frequency, seed=0):
    """Un singolo ottavo di rumore Perlin 2D via interpolazione bicubica."""
    rng = np.random.RandomState(seed)
    h, w = shape

    # Griglia di gradienti casuali
    gw = int(np.ceil(w * frequency)) + 2
    gh = int(np.ceil(h * frequency)) + 2
    angles = rng.uniform(0, 2 * np.pi, (gh, gw))
    grad_x = np.cos(angles)
    grad_y = np.sin(angles)

    # Coordinate nel campo di gradienti
    y_coords = np.linspace(0, (gh - 2) * 1.0, h, endpoint=False)
    x_coords = np.linspace(0, (gw - 2) * 1.0, w, endpoint=False)
    xg, yg = np.meshgrid(x_coords, y_coords)

    # Celle intere e frazioni
    xi = xg.astype(int)
    yi = yg.astype(int)
    xf = xg - xi
    yf = yg - yi

    # Fade curve (6t^5 - 15t^4 + 10t^3)
    u = xf * xf * xf * (xf * (xf * 6 - 15) + 10)
    v = yf * yf * yf * (yf * (yf * 6 - 15) + 10)

    # Dot products ai 4 angoli
    def dot_grad(iy, ix, dy, dx):
        iy_c = np.clip(iy, 0, gh - 1)
        ix_c = np.clip(ix, 0, gw - 1)
        return grad_x[iy_c, ix_c] * dx + grad_y[iy_c, ix_c] * dy

    n00 = dot_grad(yi, xi, yf, xf)
    n10 = dot_grad(yi, xi + 1, yf, xf - 1)
    n01 = dot_grad(yi + 1, xi, yf - 1, xf)
    n11 = dot_grad(yi + 1, xi + 1, yf - 1, xf - 1)

    # Interpolazione bilineare con fade
    nx0 = n00 * (1 - u) + n10 * u
    nx1 = n01 * (1 - u) + n11 * u
    return nx0 * (1 - v) + nx1 * v


def fractal_noise(shape, octaves=5, lacunarity=2.0, persistence=0.5, seed=42):
    """fBM (fractional Brownian motion) — somma di ottavi di Perlin."""
    result = np.zeros(shape, dtype=np.float64)
    amplitude = 1.0
    frequency = 1.0 / max(shape)  # frequenza base: ~1 ciclo sull'intero heightmap
    total_amp = 0.0

    for i in range(octaves):
        result += _perlin_octave(shape, frequency * max(shape) * 0.05, seed=seed + i * 17) * amplitude
        total_amp += amplitude
        amplitude *= persistence
        frequency *= lacunarity

    return result / total_amp  # normalizzato in [-1, 1] circa


def add_slope_noise(h, sea_level, strength=0.15, octaves=5):
    """Aggiunge rumore fractal modulato dalla pendenza e altitudine."""
    res = h.shape[0]

    # Pendenza (gradiente)
    gy, gx = np.gradient(h)
    slope = np.sqrt(gx**2 + gy**2)

    # Normalizza pendenza
    slope_norm = np.clip(slope / np.percentile(slope[slope > 0], 95), 0, 1)

    # Maschera mare: zero sotto sea level + margine
    margin = 0.01  # ~8m di transizione sopra il livello del mare
    sea_mask = np.clip((h - sea_level - margin) / (margin), 0, 1)  # transizione morbida

    # Altitudine relativa: più noise in quota (roccia esposta)
    alt_factor = np.clip((h - sea_level) / (1.0 - sea_level + 1e-6), 0, 1)
    alt_factor = 0.3 + 0.7 * alt_factor  # minimo 30% anche in pianura

    # Genera noise multi-ottava
    noise = fractal_noise(h.shape, octaves=octaves, seed=42)

    # Modula: forte su pendii ripidi, debole su piatto, zero su mare
    modulation = slope_norm * alt_factor * sea_mask

    # Scala l'ampiezza del noise alla pendenza locale
    # Su una pendenza del 30%, aggiungi noise proporzionale
    noise_amplitude = strength * modulation
    enhanced = h + noise * noise_amplitude

    return np.clip(enhanced, 0, 1)


# ════════════════════════════════════════════════════════════════════
#  3. HYDRAULIC EROSION (particle-based)
#     Simula gocce di pioggia che scorrono a valle, erodono e
#     depositano sedimento. Crea canali di drenaggio realistici.
# ════════════════════════════════════════════════════════════════════

def hydraulic_erosion(h, sea_level, iterations=5000, seed=42,
                      inertia=0.05, capacity=4.0, deposition=0.3,
                      erosion=0.3, evaporation=0.02, gravity=4.0,
                      min_slope=0.01, max_lifetime=80, radius=3):
    """Erosione idraulica particle-based. Modifica h in-place."""
    res = h.shape[0]
    rng = np.random.RandomState(seed)

    # Solo su terra sopra il mare
    land_mask_1d = h.ravel() > (sea_level + 0.005)

    eroded = 0
    deposited = 0

    for i in range(iterations):
        # Posizione iniziale casuale sulla terra
        for _ in range(10):  # max tentativi per trovare terra
            px = rng.uniform(radius, res - radius - 1)
            py = rng.uniform(radius, res - radius - 1)
            ix, iy = int(px), int(py)
            if 0 <= ix < res and 0 <= iy < res and h[iy, ix] > sea_level + 0.005:
                break
        else:
            continue

        dir_x, dir_y = 0.0, 0.0
        speed = 1.0
        water = 1.0
        sediment = 0.0

        for step in range(max_lifetime):
            ix, iy = int(px), int(py)
            if ix < 1 or ix >= res - 1 or iy < 1 or iy >= res - 1:
                break

            # Gradiente bilineare
            fx, fy = px - ix, py - iy
            h00 = h[iy, ix]
            h10 = h[iy, ix + 1]
            h01 = h[iy + 1, ix]
            h11 = h[iy + 1, ix + 1]

            grad_x = (h10 - h00) * (1 - fy) + (h11 - h01) * fy
            grad_y = (h01 - h00) * (1 - fx) + (h11 - h10) * fx

            # Aggiorna direzione con inerzia
            dir_x = dir_x * inertia - grad_x * (1 - inertia)
            dir_y = dir_y * inertia - grad_y * (1 - inertia)
            d_len = np.sqrt(dir_x**2 + dir_y**2)
            if d_len < 1e-8:
                # Direzione casuale se piatto
                angle = rng.uniform(0, 2 * np.pi)
                dir_x, dir_y = np.cos(angle), np.sin(angle)
                d_len = 1.0
            dir_x /= d_len
            dir_y /= d_len

            # Nuova posizione
            new_px = px + dir_x
            new_py = py + dir_y
            new_ix, new_iy = int(new_px), int(new_py)
            if new_ix < 0 or new_ix >= res or new_iy < 0 or new_iy >= res:
                break

            # Differenza di altezza
            new_h = h[new_iy, new_ix] if (0 <= new_ix < res and 0 <= new_iy < res) else h00
            h_diff = new_h - h00

            # Sotto il mare? Deposita tutto e fermati
            if new_h <= sea_level + 0.002:
                # Deposita sedimento nei pixel circostanti
                _deposit(h, ix, iy, sediment, radius, res)
                deposited += sediment
                break

            # Capacità di trasporto
            c = max(-h_diff, min_slope) * speed * water * capacity

            if sediment > c:
                # Troppo sedimento: deposita
                deposit_amount = (sediment - c) * deposition
                _deposit(h, ix, iy, deposit_amount, radius, res)
                sediment -= deposit_amount
                deposited += deposit_amount
            else:
                # Può erodere
                erode_amount = min((c - sediment) * erosion, -h_diff)
                erode_amount = max(erode_amount, 0)
                _erode(h, ix, iy, erode_amount, radius, res, sea_level)
                sediment += erode_amount
                eroded += erode_amount

            # Aggiorna velocità e acqua
            speed = np.sqrt(max(speed * speed + h_diff * gravity, 0.01))
            water *= (1 - evaporation)

            px, py = new_px, new_py

            if water < 0.01:
                break

    return eroded, deposited


def _erode(h, cx, cy, amount, radius, res, sea_level):
    """Erode un'area circolare attorno a (cx,cy)."""
    if amount <= 0:
        return
    total_w = 0
    weights = []
    for dy in range(-radius, radius + 1):
        for dx in range(-radius, radius + 1):
            nx, ny = cx + dx, cy + dy
            if 0 <= nx < res and 0 <= ny < res:
                d2 = dx * dx + dy * dy
                if d2 <= radius * radius:
                    w = max(0, 1.0 - np.sqrt(d2) / radius)
                    weights.append((ny, nx, w))
                    total_w += w
    if total_w < 1e-8:
        return
    for ny, nx, w in weights:
        delta = amount * w / total_w
        # Non erodere sotto il mare
        h[ny, nx] = max(h[ny, nx] - delta, sea_level)


def _deposit(h, cx, cy, amount, radius, res):
    """Deposita sedimento in un'area circolare."""
    if amount <= 0:
        return
    total_w = 0
    weights = []
    for dy in range(-radius, radius + 1):
        for dx in range(-radius, radius + 1):
            nx, ny = cx + dx, cy + dy
            if 0 <= nx < res and 0 <= ny < res:
                d2 = dx * dx + dy * dy
                if d2 <= radius * radius:
                    w = max(0, 1.0 - np.sqrt(d2) / radius)
                    weights.append((ny, nx, w))
                    total_w += w
    if total_w < 1e-8:
        return
    for ny, nx, w in weights:
        h[ny, nx] += amount * w / total_w


# ════════════════════════════════════════════════════════════════════
#  MAIN
# ════════════════════════════════════════════════════════════════════

def main():
    parser = argparse.ArgumentParser(description="Enhance terrain detail beyond DEM resolution")
    parser.add_argument("--data-dir", default="DATA",
                        help="Directory con processed_terrain.raw e terrain_meta_saved.json")
    parser.add_argument("--strength", type=float, default=0.7,
                        help="Forza complessiva enhancement (0=nulla, 1=massima, default: 0.7)")
    parser.add_argument("--no-sharpen", action="store_true",
                        help="Salta ridge/valley sharpening")
    parser.add_argument("--no-noise", action="store_true",
                        help="Salta fractal noise")
    parser.add_argument("--no-erosion", action="store_true",
                        help="Salta hydraulic erosion")
    parser.add_argument("--erosion-iters", type=int, default=8000,
                        help="Iterazioni erosione (default: 8000)")
    parser.add_argument("--no-backup", action="store_true",
                        help="Non salvare backup .raw.bak")
    args = parser.parse_args()

    data_dir = args.data_dir
    raw_path = os.path.join(data_dir, "processed_terrain.raw")
    meta_path = os.path.join(data_dir, "terrain_meta_saved.json")

    if not os.path.exists(raw_path):
        print(f"ERRORE: {raw_path} non trovato. Esegui prima prepare_terrain.py")
        sys.exit(1)
    if not os.path.exists(meta_path):
        print(f"ERRORE: {meta_path} non trovato.")
        sys.exit(1)

    meta = json.load(open(meta_path))
    res = meta['rawResolution']
    sea_level = meta['seaLevelNorm']
    width_m = meta['widthM']
    pixel_m = width_m / res

    print(f"═══════════════════════════════════════════")
    print(f"  Terrain Enhancement")
    print(f"═══════════════════════════════════════════")
    print(f"  Heightmap: {res}×{res} ({pixel_m:.1f} m/pixel)")
    print(f"  Area: {width_m:.0f}×{meta['lengthM']:.0f} m")
    print(f"  Sea level: {sea_level:.4f}")
    print(f"  Strength: {args.strength}")
    print()

    # Carica heightmap
    h = np.fromfile(raw_path, dtype=np.float32).reshape(res, res).astype(np.float64)
    h_original = h.copy()

    # Maschera mare globale: protegge il mare da TUTTE le modifiche
    sea_margin = sea_level + 0.015  # ~12m sopra il livello del mare = zona costiera protetta
    is_sea = h_original < sea_margin

    t0 = time.time()

    # ── Step 1: Ridge/Valley Sharpening ──
    if not args.no_sharpen:
        print("  [1/3] Ridge/valley sharpening...", end=" ", flush=True)
        t1 = time.time()
        try:
            h = sharpen_ridges(h, strength=0.4 * args.strength, sigma_px=max(2, int(res / 500)))
            print(f"OK ({time.time()-t1:.1f}s)")
        except ImportError:
            print("SKIP (scipy non disponibile)")
    else:
        print("  [1/3] Ridge/valley sharpening: SKIP")

    # ── Step 2: Slope-dependent fractal noise ──
    if not args.no_noise:
        print("  [2/3] Slope-dependent fractal noise...", end=" ", flush=True)
        t2 = time.time()
        h = add_slope_noise(h, sea_level, strength=0.012 * args.strength, octaves=5)
        print(f"OK ({time.time()-t2:.1f}s)")
    else:
        print("  [2/3] Fractal noise: SKIP")

    # ── Step 3: Hydraulic erosion ──
    if not args.no_erosion:
        print(f"  [3/3] Hydraulic erosion ({args.erosion_iters} drops)...", end=" ", flush=True)
        t3 = time.time()

        # Scala parametri alla risoluzione
        iters = args.erosion_iters
        erode_radius = max(1, int(3 * (res / 1025)))  # raggio proporzionale

        eroded, deposited = hydraulic_erosion(
            h, sea_level,
            iterations=iters,
            radius=erode_radius,
            erosion=0.3 * args.strength,
            deposition=0.3 * args.strength,
            capacity=4.0,
            max_lifetime=int(60 * (res / 1025)),
        )
        print(f"OK ({time.time()-t3:.1f}s, eroded={eroded:.4f}, deposited={deposited:.4f})")
    else:
        print("  [3/3] Hydraulic erosion: SKIP")

    # ── Ripristina mare (protezione assoluta) ──
    h[is_sea] = h_original[is_sea]

    # ── Statistiche ──
    diff = h - h_original
    land = h_original > (sea_level + 0.005)
    land_diff = diff[land]

    print()
    print(f"  Tempo totale: {time.time()-t0:.1f}s")
    print(f"  Differenza (solo terra):")
    print(f"    Media:  {np.mean(land_diff)*meta['heightM']:.3f} m")
    print(f"    Std:    {np.std(land_diff)*meta['heightM']:.3f} m")
    print(f"    Max Δ+: {np.max(land_diff)*meta['heightM']:.3f} m")
    print(f"    Max Δ-: {np.min(land_diff)*meta['heightM']:.3f} m")
    print(f"  Mare modificato: {np.any(diff[~land] != 0)}")

    # ── Salva ──
    h = np.clip(h, 0, 1).astype(np.float32)

    if not args.no_backup:
        bak_path = raw_path + ".bak"
        if not os.path.exists(bak_path):
            os.rename(raw_path, bak_path)
            print(f"  Backup: {bak_path}")
        else:
            print(f"  Backup esistente: {bak_path}")

    h.tofile(raw_path)
    print(f"  Salvato: {raw_path} ({os.path.getsize(raw_path)//1024} KB)")
    print()
    print("  ✓ Enhancement completato!")
    print("═══════════════════════════════════════════")


if __name__ == "__main__":
    main()
