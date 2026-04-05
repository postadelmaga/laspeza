#!/bin/bash
# ================================================================
# Demo BotW - Genera solo terreno + acqua + scena (no edifici/strade)
# Usa molta meno RAM della pipeline completa.
#
# Uso:
#   ./demo.sh                                  # default: laspeza_5km.tif
#   ./demo.sh DATA/laspeza_5km.tif             # tif specifico
#   ./demo.sh DATA/laspeza_5km.tif 1           # griglia 1x1 (meno RAM)
# ================================================================
set -e

UNITY_PATH="$HOME/Unity/Hub/Editor/6000.4.0f1/Editor/Unity"
PROJECT_PATH="$(cd "$(dirname "$0")" && pwd)"
TIF="${1:-$PROJECT_PATH/DATA/laspeza_5km.tif}"
GRID="${2:-2}"
LOG="/tmp/demo_$(date +%Y%m%d_%H%M%S).log"

if [ ! -f "$UNITY_PATH" ]; then
    echo "Unity non trovato: $UNITY_PATH"
    exit 1
fi

echo "=== Demo BotW La Spezia ==="
echo "DEM:     $TIF"
echo "Griglia: ${GRID}x${GRID}"
echo "Log:     $LOG"
echo ""

# Pulizia RAM preventiva
sync
rm -f "$PROJECT_PATH/Temp/UnityLockfile" 2>/dev/null

echo "Generazione terreno + acqua + scena..."
echo "(senza edifici/strade/vegetazione per risparmiare RAM)"
echo ""

"$UNITY_PATH" \
    -batchmode \
    -nographics \
    -disable-gpu-skinning \
    -projectPath "$PROJECT_PATH" \
    -executeMethod CityBuilder.CityBuilderCLI.Generate \
    -logFile "$LOG" \
    -- --tif "$TIF" --grid "$GRID" --step demo --save

EXIT=$?

if [ $EXIT -eq 0 ]; then
    echo ""
    echo "=== DEMO PRONTO ==="
    echo "Apri Unity e premi Play!"
    echo ""
    echo "Oppure compila:"
    echo "  $UNITY_PATH -batchmode -projectPath $PROJECT_PATH \\"
    echo "    -executeMethod CityBuilder.DemoBuilder.BuildLinuxCLI -quit"
else
    echo ""
    echo "ERRORE (exit $EXIT). Ultimi errori:"
    grep -E "error|Error|FALLITO" "$LOG" 2>/dev/null | tail -10
    echo "Log completo: $LOG"
fi
