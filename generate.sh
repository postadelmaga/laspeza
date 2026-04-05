#!/bin/bash
# ================================================================
# CityBuilder CLI - Genera la citta' da terminale
# ================================================================
#
# Uso:
#   ./generate.sh                              # pipeline completa (usa DEM in DATA/)
#   ./generate.sh --download                   # scarica DEM Copernicus e genera
#   ./generate.sh --tif /path/to/dem.tif       # usa un GeoTIFF specifico
#   ./generate.sh --step terrain               # esegui solo uno step
#   ./generate.sh --download --grid 6 --save   # scarica, griglia 6x6, salva scena
#
# Opzioni:
#   --tif PATH        File GeoTIFF sorgente
#   --grid N          Griglia terreno (default: 4)
#   --no-crop         Disabilita auto-crop oceano
#   --download        Scarica DEM Copernicus
#   --minlon N        Longitudine minima (default: 9.75)
#   --minlat N        Latitudine minima (default: 44.05)
#   --maxlon N        Longitudine massima (default: 9.90)
#   --maxlat N        Latitudine massima (default: 44.15)
#   --step NOME       Esegui solo: terrain,textures,osm,buildings,roads,squares,water,vegetation,scene
#   --save            Salva la scena dopo la generazione
#   --verbose         Mostra log completo Unity
# ================================================================

set -e

UNITY_PATH="$HOME/Unity/Hub/Editor/6000.4.0f1/Editor/Unity"
PROJECT_PATH="$(cd "$(dirname "$0")" && pwd)"

if [ ! -f "$UNITY_PATH" ]; then
    echo "Unity non trovato in: $UNITY_PATH"
    echo "Modifica UNITY_PATH in questo script."
    exit 1
fi

# Separa --verbose dal resto
VERBOSE=false
ARGS=()
for arg in "$@"; do
    if [ "$arg" = "--verbose" ]; then
        VERBOSE=true
    else
        ARGS+=("$arg")
    fi
done

LOG_FILE="/tmp/citybuilder_$(date +%Y%m%d_%H%M%S).log"

echo "=== CityBuilder CLI ==="
echo "Progetto: $PROJECT_PATH"
echo "Unity:    $UNITY_PATH"
echo "Log:      $LOG_FILE"
echo ""

if $VERBOSE; then
    "$UNITY_PATH" \
        -batchmode \
        -nographics \
        -disable-gpu-skinning \
        -projectPath "$PROJECT_PATH" \
        -executeMethod CityBuilder.CityBuilderCLI.Generate \
        -logFile "$LOG_FILE" \
        -- "${ARGS[@]}" &

    UNITY_PID=$!
    # Follow log in tempo reale
    tail -f "$LOG_FILE" --pid=$UNITY_PID 2>/dev/null
    wait $UNITY_PID
    EXIT_CODE=$?
else
    echo "Generazione in corso... (usa --verbose per dettagli)"
    "$UNITY_PATH" \
        -batchmode \
        -nographics \
        -disable-gpu-skinning \
        -projectPath "$PROJECT_PATH" \
        -executeMethod CityBuilder.CityBuilderCLI.Generate \
        -logFile "$LOG_FILE" \
        -- "${ARGS[@]}"

    EXIT_CODE=$?

    # Mostra solo le righe rilevanti del log
    grep -E "CityBuilder|PYTHON|OSM|Terrain|Building|Road|Water|Vegetation|Piazze|Scene|COMPLETATO|FALLITO|Error|error CS" "$LOG_FILE" 2>/dev/null

    if [ $EXIT_CODE -ne 0 ]; then
        echo ""
        echo "ERRORE! Ultimi errori nel log:"
        grep -i "error" "$LOG_FILE" | tail -20
        echo ""
        echo "Log completo: $LOG_FILE"
        exit $EXIT_CODE
    fi
fi

echo ""
echo "=== Fatto! ==="
