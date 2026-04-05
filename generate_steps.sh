#!/bin/bash
# ================================================================
# CityBuilder - Generazione a STEP SEPARATI
# Ogni step = un processo Unity indipendente.
# La RAM viene liberata completamente tra uno step e l'altro.
# ================================================================
set -e

UNITY_PATH="$HOME/Unity/Hub/Editor/6000.4.0f1/Editor/Unity"
PROJECT_PATH="$(cd "$(dirname "$0")" && pwd)"
LOG_DIR="/tmp"
TIF="${1:-$PROJECT_PATH/DATA/laspeza_10km.tif}"
GRID="${2:-2}"

echo "=== CityBuilder Step-by-Step ==="
echo "DEM: $TIF"
echo "Griglia: ${GRID}x${GRID}"
echo ""

wait_unity_exit() {
    # Aspetta che eventuali processi Unity precedenti terminino
    local MAX_WAIT=30
    local WAITED=0
    while pgrep -f "Unity.*-projectPath.*$PROJECT_PATH" > /dev/null 2>&1; do
        if [ $WAITED -eq 0 ]; then
            echo -n "  (attendo chiusura Unity precedente...) "
        fi
        sleep 1
        WAITED=$((WAITED + 1))
        if [ $WAITED -ge $MAX_WAIT ]; then
            echo "timeout! Forzo chiusura."
            pkill -f "Unity.*-projectPath.*$PROJECT_PATH" 2>/dev/null
            sleep 2
            break
        fi
    done
    [ $WAITED -gt 0 ] && echo "ok"
    # Ora e' safe rimuovere il lock
    rm -f "$PROJECT_PATH/Temp/UnityLockfile" 2>/dev/null
}

run_step() {
    local STEP_NAME="$1"
    local STEP_ID="$2"
    local LOG="$LOG_DIR/citybuilder_step_${STEP_ID}.log"

    # Aspetta che Unity precedente sia davvero uscito
    wait_unity_exit

    echo -n "[$STEP_ID] $STEP_NAME... "

    local START=$(date +%s)

    "$UNITY_PATH" \
        -batchmode \
        -nographics \
        -disable-gpu-skinning \
        -projectPath "$PROJECT_PATH" \
        -executeMethod CityBuilder.CityBuilderCLI.Generate \
        -logFile "$LOG" \
        -- --tif "$TIF" --grid "$GRID" --step "$STEP_ID" --save 2>/dev/null

    local EXIT=$?
    local END=$(date +%s)
    local DURATION=$((END - START))

    if [ $EXIT -eq 0 ]; then
        # Estrai info dal log
        local MEM=$(grep "\[MEM\] POST" "$LOG" 2>/dev/null | tail -1 | grep -oP 'Managed: \K[0-9]+MB' || echo "?")
        echo "OK (${DURATION}s, ${MEM})"
    else
        echo "ERRORE! (exit $EXIT, ${DURATION}s)"
        echo "  Log: $LOG"
        grep -E "error|Error|FALLITO" "$LOG" 2>/dev/null | tail -5
        return $EXIT
    fi

    # Aspetta che la RAM si liberi
    sleep 3
}

echo "Inizio generazione..."
echo ""

run_step "Terreno + Texture"      "terrain"
run_step "Texture terreno"        "textures"
run_step "Download dati OSM"      "osm"
run_step "Edifici"                "buildings"
run_step "Strade"                 "roads"
run_step "Piazze"                 "squares"
run_step "Acqua"                  "water"
run_step "Vegetazione"            "vegetation"
run_step "Setup scena"            "scene"

echo ""
echo "=== COMPLETATO ==="
echo "Apri Unity e premi Play!"
