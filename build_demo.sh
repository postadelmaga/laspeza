#!/bin/bash
# ================================================================
# Build Demo BotW - Script unificato intelligente
#
# Rigenera la scena solo se necessario, poi builda.
#
# Uso:
#   ./build_demo.sh                   # build (rigenera se serve)
#   ./build_demo.sh --force           # forza rigenerazione scena
#   ./build_demo.sh --build-only      # solo build, no rigenerazione
#   ./build_demo.sh --gen-only        # solo genera scena, no build
#   ./build_demo.sh --tif FILE        # usa un TIF diverso
#   ./build_demo.sh --grid N          # griglia NxN (default: 2)
#   ./build_demo.sh --run             # lancia il demo dopo la build
# ================================================================
set -e

# Detecta OS e configura percorsi
case "$(uname -s)" in
    Darwin*)
        UNITY_PATH="/Applications/Unity/Hub/Editor/6000.4.0f1/Unity.app/Contents/MacOS/Unity"
        STAT_SIZE() { stat -f%z "$1" 2>/dev/null || echo 0; }
        BUILD_TARGET="LaSpeziaDemo_Mac"
        ;;
    *)
        UNITY_PATH="$HOME/Unity/Hub/Editor/6000.4.0f1/Editor/Unity"
        STAT_SIZE() { stat -c%s "$1" 2>/dev/null || echo 0; }
        BUILD_TARGET="LaSpeziaDemo_Linux"
        ;;
esac

PROJECT_PATH="$(cd "$(dirname "$0")" && pwd)"
SCENE="$PROJECT_PATH/Assets/Scenes/SampleScene.unity"
DEFAULT_TIF="$PROJECT_PATH/DATA/laspeza_10km.tif"
BUILD_DIR="$PROJECT_PATH/Build/$BUILD_TARGET"
LOG_DIR="/tmp"

# Defaults
FORCE=false
BUILD_ONLY=false
GEN_ONLY=false
RUN_AFTER=false
TIF="$DEFAULT_TIF"
GRID=2

# Parse args
while [[ $# -gt 0 ]]; do
    case "$1" in
        --force)      FORCE=true ;;
        --build-only) BUILD_ONLY=true ;;
        --gen-only)   GEN_ONLY=true ;;
        --run)        RUN_AFTER=true ;;
        --tif)        TIF="$2"; shift ;;
        --grid)       GRID="$2"; shift ;;
        *)            echo "Opzione sconosciuta: $1"; exit 1 ;;
    esac
    shift
done

if [ ! -f "$UNITY_PATH" ]; then
    echo "Unity non trovato: $UNITY_PATH"
    exit 1
fi

# Funzioni
cleanup() {
    rm -f "$PROJECT_PATH/Temp/UnityLockfile" 2>/dev/null
}

wait_unity() {
    local MAX=30 W=0
    while pgrep -f "Unity.*-projectPath.*$PROJECT_PATH" > /dev/null 2>&1; do
        [ $W -eq 0 ] && echo -n "  Attendo chiusura Unity..."
        sleep 1; W=$((W+1))
        if [ $W -ge $MAX ]; then
            echo " timeout, forzo."
            pkill -9 -f "Unity.*-projectPath.*$PROJECT_PATH" 2>/dev/null
            sleep 2; break
        fi
    done
    [ $W -gt 0 ] && echo " ok"
    cleanup
}

need_regen() {
    # Rigenera se:
    # 1. Scena non esiste o è vuota (<100KB)
    # 2. --force
    # 3. TIF più recente della scena
    # 4. Script C# più recenti della scena
    if $FORCE; then return 0; fi
    if [ ! -f "$SCENE" ]; then return 0; fi

    local SCENE_SIZE=$(STAT_SIZE "$SCENE")
    if [ "$SCENE_SIZE" -lt 100000 ]; then return 0; fi

    # TIF più recente della scena?
    if [ "$TIF" -nt "$SCENE" ]; then return 0; fi

    # Qualche .cs più recente della scena?
    local NEWER=$(find "$PROJECT_PATH/Assets" -name "*.cs" -newer "$SCENE" 2>/dev/null | head -1)
    if [ -n "$NEWER" ]; then return 0; fi

    # Qualche shader più recente della scena?
    NEWER=$(find "$PROJECT_PATH/Assets" -name "*.shader" -newer "$SCENE" 2>/dev/null | head -1)
    if [ -n "$NEWER" ]; then return 0; fi

    return 1  # non serve rigenerare
}

monitor_unity() {
    local LOG="$1" LABEL="$2"
    local PID=$(pgrep -f "Unity.*-projectPath.*$PROJECT_PATH" | head -1)
    local START=$(date +%s)

    while kill -0 $PID 2>/dev/null; do
        local EL=$(( $(date +%s) - START ))
        local RSS=$(ps -o rss= -p $PID 2>/dev/null | tr -d ' ')
        local RSS_MB=$(( ${RSS:-0} / 1024 ))
        local LAST=$(grep -E "Compiling shader|BUILD|COMPLETATO|error CS|ottimizzat" "$LOG" 2>/dev/null | tail -1 | cut -c1-80)
        printf "\r\033[K  [%02d:%02d] %4dMB | %s" "$((EL/60))" "$((EL%60))" "$RSS_MB" "$LAST"
        sleep 5
    done

    local TOTAL=$(( $(date +%s) - START ))
    echo ""
    echo "  $LABEL: $((TOTAL/60))m$((TOTAL%60))s"
}

# ================================================================
echo "╔══════════════════════════════════════╗"
echo "║   La Spezia Demo BotW — Builder     ║"
echo "╚══════════════════════════════════════╝"
echo ""
echo "TIF:    $TIF"
echo "Griglia: ${GRID}x${GRID}"
echo "Scena:  $SCENE"
echo ""

wait_unity

# ── Step 1: Pre-process terreno (Python, se serve) ──
RAW="$PROJECT_PATH/DATA/processed_terrain.raw"
META="$PROJECT_PATH/DATA/terrain_meta_saved.json"
if [ ! -f "$RAW" ] || [ ! -f "$META" ] || [ "$TIF" -nt "$RAW" ]; then
    echo "▸ Pre-processing terreno (Python)..."
    python3 "$PROJECT_PATH/prepare_terrain.py" "$TIF" --max-res 1025 --skip-bathy --sea-depth -10
    echo ""
fi

# ── Step 2: Genera scena (Unity, se serve) ──
if ! $BUILD_ONLY; then
    if need_regen; then
        echo "▸ Generazione scena demo..."
        GEN_LOG="$LOG_DIR/demo_gen_$(date +%H%M%S).log"

        "$UNITY_PATH" \
            -batchmode -nographics -disable-gpu-skinning \
            -projectPath "$PROJECT_PATH" \
            -executeMethod CityBuilder.CityBuilderCLI.Generate \
            -logFile "$GEN_LOG" \
            -- --tif "$TIF" --grid "$GRID" --step demo --save &

        monitor_unity "$GEN_LOG" "Generazione"

        # Verifica
        if grep -q "COMPLETATO" "$GEN_LOG" 2>/dev/null; then
            echo "  ✓ Scena generata: $(du -h "$SCENE" | cut -f1)"
        else
            echo "  ✗ ERRORE generazione!"
            grep -E "error|Error|FALLITO" "$GEN_LOG" 2>/dev/null | tail -5
            exit 1
        fi

        cleanup; sleep 2
    else
        echo "▸ Scena già aggiornata ($(du -h "$SCENE" | cut -f1)), skip."
    fi
fi

if $GEN_ONLY; then
    echo ""
    echo "Done (solo generazione)."
    exit 0
fi

# ── Step 3: Build (Unity) ──
echo ""
echo "▸ Build demo Linux..."
BUILD_LOG="$LOG_DIR/demo_build_$(date +%H%M%S).log"

"$UNITY_PATH" \
    -batchmode -nographics -disable-gpu-skinning \
    -projectPath "$PROJECT_PATH" \
    -executeMethod CityBuilder.DemoBuilder.BuildLinuxCLI \
    -logFile "$BUILD_LOG" \
    -quit &

monitor_unity "$BUILD_LOG" "Build"

# Killa Unity se rimasto appeso dopo il -quit
sleep 3
pkill -f "Unity.*-projectPath.*$PROJECT_PATH" 2>/dev/null
cleanup

# Verifica
if grep -q "BUILD RIUSCITA" "$BUILD_LOG" 2>/dev/null; then
    SIZE=$(du -sh "$BUILD_DIR" | cut -f1)
    echo ""
    echo "╔══════════════════════════════════════╗"
    echo "║   ✓ BUILD RIUSCITA — $SIZE            "
    echo "╚══════════════════════════════════════╝"
    echo ""
    echo "  Esegui: ./Build/LaSpeziaDemo_Linux/LaSpeziaDemo"
    echo ""

    if $RUN_AFTER; then
        echo "Lancio demo..."
        "$BUILD_DIR/LaSpeziaDemo" &
    fi
else
    echo ""
    echo "✗ BUILD FALLITA"
    grep -E "error|Error|BUILD" "$BUILD_LOG" 2>/dev/null | tail -10
    echo ""
    echo "Log: $BUILD_LOG"
    exit 1
fi
