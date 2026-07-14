#!/usr/bin/env bash
# run_quality_bench.sh — Code-Qualitäts-Benchmark über alle Modelle.
#
# Liest die Modell-Liste aus einer Speed-Bench-CSV (Zeilen mit status=OK),
# schaltet llama-server per `openmono model use` auf jedes Modell und misst
# pass@1 auf dem HumanEval-Subset via scripts/quality_bench.py.
#
# Usage: run_quality_bench.sh <speed-results.csv> <humaneval.jsonl> <out.csv> [restore-model]
set -uo pipefail

SPEED_CSV="${1:?speed csv}"
TASKS="${2:?humaneval jsonl}"
OUT="${3:?output csv}"
RESTORE="${4:-}"
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OPENMONO="$REPO_DIR/openmono"

wait_healthy() {
    for _ in $(seq 1 240); do
        if curl -fsS http://localhost:7474/health 2>/dev/null | grep -q '"status" *: *"ok"'; then
            return 0
        fi
        sleep 2.5
    done
    return 1
}

echo "model;passed;total;pass_rate;truncated;gen_tps;gen_s" > "$OUT"

mapfile -t MODELS < <(awk -F';' 'NR>1 && $3=="OK" {print $1}' "$SPEED_CSV")
echo ">> ${#MODELS[@]} Modelle"

for model in "${MODELS[@]}"; do
    echo "== $model =="
    if ! "$OPENMONO" model use "$model" >/dev/null 2>&1; then
        echo "$model;;;;;;switch_failed" >> "$OUT"
        continue
    fi
    if ! wait_healthy; then
        echo "$model;;;;;;health_timeout" >> "$OUT"
        continue
    fi
    max_tokens=640
    case "$model" in
        *QwQ*|*Think*|*think*|*reasoning*|*VibeThinker*|*Olympic*) max_tokens=1152 ;;
    esac
    python3 "$REPO_DIR/scripts/quality_bench.py" \
        --tasks "$TASKS" --model-label "$model" \
        --max-tokens "$max_tokens" --out "$OUT" \
        || echo "$model;;;;;;bench_error" >> "$OUT"
done

if [[ -n "$RESTORE" ]]; then
    echo ">> restore $RESTORE"
    "$OPENMONO" model use "$RESTORE" >/dev/null 2>&1
    wait_healthy || true
fi
echo ">> fertig: $OUT"
