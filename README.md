# 🏙️ La Spezia — Generatore procedurale di città 3D

Genera una ricostruzione 3D navigabile di La Spezia (e potenzialmente qualsiasi città) a partire da dati GeoTIFF e OpenStreetMap, tutto dentro Unity.

![Unity 6](https://img.shields.io/badge/Unity-6000.4.0f1-blue)
![URP](https://img.shields.io/badge/Pipeline-URP-green)

> **Nota**: i file GeoTIFF (DEM) non sono nel repo perché troppo grandi.
> Vedi la sezione [Quick Start](#-quick-start) per come ottenerli.

## ✨ Cosa fa

1. **Terreno** — Importa DEM (GeoTIFF) con batimetria, genera heightmap multi-tile
2. **Texture satellitari** — Scarica tile da servizi WMS e le proietta sul terreno
3. **Edifici** — Scarica dati OSM via Overpass, genera mesh procedurali con tetti
4. **Strade** — Mesh stradali con larghezze reali da tag OSM
5. **Piazze & aree pedonali** — Poligoni da dati OSM
6. **Acqua** — Shader stilizzato per mare e fiumi
7. **Vegetazione** — Placement procedurale alberi toon-style
8. **Scena esplorabile** — Camera fly-through con atmosfera BotW-style

## 📋 Prerequisiti

| Dipendenza | Versione | Note |
|-----------|---------|------|
| **Unity** | 6000.4.0f1 | Via [Unity Hub](https://unity.com/download) |
| **Python 3** | 3.8+ | Per pre-processing terreno |
| **GDAL** | 3.x | Libreria geospaziale per Python |
| **NumPy** | — | `pip install numpy` |

### Installazione dipendenze

#### 🐧 Linux (Ubuntu/Debian)

```bash
sudo apt install python3 python3-pip gdal-bin libgdal-dev
pip install numpy gdal
```

#### 🍎 macOS

```bash
brew install python gdal
pip3 install numpy gdal
```

#### 🪟 Windows

Installa Python da [python.org](https://python.org), poi:
```
pip install numpy GDAL
```
Oppure usa conda: `conda install -c conda-forge gdal numpy`

## 🚀 Quick Start

### 1. Clona il repository

```bash
git clone https://github.com/USER/laspeza.git
cd laspeza
```

### 2. Scarica i dati DEM

I file GeoTIFF non sono nel repo. Tre opzioni:

**Opzione A — Download automatico** (lo script scarica da Copernicus):
```bash
./generate.sh --download
```

**Opzione B — Copernicus EU-DEM** (manuale):
- Vai su https://land.copernicus.eu/imagery-in-situ/eu-dem
- Scarica il tile che copre La Spezia
- Metti il `.tif` in `DATA/`

**Opzione C — Chiedi al maintainer** il file `laspeza_7km.tif` (~7 MB).

### 3. Apri in Unity

- Apri **Unity Hub** → **Open** → seleziona la cartella `laspeza`
- Unity scaricherà automaticamente i pacchetti URP, Input System, ecc.

### 4. Genera la città (da terminale)

**Demo veloce** (solo terreno + acqua, poca RAM):
```bash
./demo.sh
```

**Pipeline completa** (terreno + edifici + strade + vegetazione):
```bash
./generate.sh
```

**Con opzioni**:
```bash
./generate.sh --tif DATA/laspeza_7km.tif --grid 2 --save
```

**Step-by-step** (libera la RAM tra uno step e l'altro):
```bash
./generate_steps.sh DATA/laspeza_7km.tif 2
```

### 5. Build eseguibile

```bash
./build_demo.sh              # build Linux
./build_demo.sh --run        # build e lancia
```

## 🍎 Note per macOS

Gli script cercano Unity in `$HOME/Unity/Hub/Editor/6000.4.0f1/Editor/Unity`.
Su macOS il percorso è diverso. Imposta la variabile d'ambiente prima di lanciare gli script:

```bash
export UNITY_PATH="/Applications/Unity/Hub/Editor/6000.4.0f1/Unity.app/Contents/MacOS/Unity"
```

Altre differenze:
- `stat -c%s` → su macOS usa `stat -f%z` (in `build_demo.sh` potrebbe servire un piccolo fix)
- `pgrep` e `pkill` funzionano uguale

## 📁 Struttura progetto

```
laspeza/
├── Assets/
│   ├── Editor/CityBuilder/    # Pipeline procedurale (editor scripts)
│   ├── Scripts/               # Runtime (camera, atmosfera, fishing, ecc.)
│   ├── Shaders/               # Shader toon (acqua, vegetazione)
│   ├── Scenes/                # Scena principale
│   └── Settings/              # URP render pipeline settings
├── DATA/                      # GeoTIFF e cache (esclusi da git, vedi sopra)
├── Packages/                  # Unity package manifest
├── ProjectSettings/           # Impostazioni Unity (versionabili)
├── build_demo.sh              # Build automatica con rigenerazione smart
├── generate.sh                # Generazione città completa
├── generate_steps.sh          # Generazione step-by-step (meno RAM)
├── demo.sh                    # Demo veloce (solo terreno + acqua)
├── prepare_terrain.py         # Pre-processing Python del DEM
└── crop_dem.py                # Utility: ritaglia un GeoTIFF su un'area
```

## 🗺️ Usare dati di un'altra zona

1. Scarica un DEM GeoTIFF della zona (es. Copernicus, SRTM, ALOS)
2. (Opzionale) Ritaglia l'area di interesse:
   ```bash
   python3 crop_dem.py input.tif output.tif --center 11.25 43.77 --radius 5000
   ```
3. Genera:
   ```bash
   ./generate.sh --tif output.tif --grid 4 --save
   ```

## 📜 Licenza

Progetto personale / educational.
