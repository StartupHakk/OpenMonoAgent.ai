#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai — model catalog resolver
#
# Loads scripts/models.json (a JSON file that is the single source of truth for
# every selectable model: identity, per-platform tier ctx/kv/accuracy, picker
# labels, capability gating, and the backend/manager-script hook) and exposes
# tiny jq helpers so the installers (install.sh, install_macos.sh) and
# prism.sh never hardcode model data again.
#
# Sourced by the installers. Do not execute directly.
#
# Resilience: if scripts/models.json is missing or fails to parse, mc_load falls
# back to an embedded minimal Qwen-only catalog and warns. The default install
# path therefore never breaks on a bad catalog edit — only the optional
# Bonsai/Strata entries drop out of the menu.
#
# API (all echo to stdout; "" means "not present in this catalog"):
#   mc_load                      load/parse catalog into MC_JSON (idempotent)
#   mc_default                   default model id
#   mc_ids                       newline-separated list of model ids
#   mc_id_for <name-or-alias>   canonical id for a --model= value ("" if unknown)
#   mc_meta <id> <field>        top-level entry field (backend, prism, accuracy,
#                               script, port_env, port_default, deny, ...)
#   mc_requires <id>            capability flag name (CAN_BONSAI/CAN_STRATA) or ""
#   mc_flag <name>              resolve a capability flag name to 0/1 in this shell
#   mc_menu <id> <field>        menu.label | menu.tag | menu.line
#   mc_file_field <id> <field>  files.name|url|mmproj|mmproj_url (fixed-identity models)
#   mc_tier_field <id> <plat> <key> <field>   tier ctx|kv|accuracy|name|url|mmproj|...
#   mc_min_mem <id> <plat>      minimum RAM/VRAM (GB) to offer (macOS gating) or ""
#   mc_ctx <plat> <key>         context.* constant (vision, vision_16g, cpu, ...)
#   mc_tier_exists <id> <plat> <key>
#                               success when the model has a row for that tier
#   mc_pick_tier <id> <plat> <value>
#                               nearest numeric tier key for a memory value (GB):
#                               largest key <= value, clamped to the smallest
#                               key when the box is under the lowest tier
#                               (mirrors the old if/elif VRAM & RAM ladders).
#   mc_select <id> <plat> <key> set MODEL_NAME/MODEL_URL/MODEL_ACCURACY/
#                               MODEL_MMPROJ/MODEL_MMPROJ_URL/MODEL_ALIAS/
#                               _MODEL_LABEL/IS_BONSAI from the catalog (this is
#                               what replaces the hardcoded select_model tables)
#   mc_files_select <id>        same, using only files.* + accuracy (fixed-identity
#                               models such as Bonsai — ctx/label come from tiers)
#   mc_auto_pick                hardware-gated default model id for this box:
#                               flash-next | bonsai-pq2 | bonsai-ptq1 | qwen
#                               (reads _GPU_TIER, GPU_MODE, AMD_IGPU_MODE,
#                               TOTAL_MEM, CAN_STRATA, CAN_BONSAI)
#   mc_auto_reason              one-line why for the same pick (menu/log text)
#   mc_auto_pick_menu_line <plat> <key>
#                               menu text for the menu row the auto pick maps to
#   mc_vision_default           "yes" when setup should default vision on with no
#                               prompt, "ask" when it should prompt; honouring
#                               OPENMONO_SETUP_OPTIONS
#   mc_parallel_slots_default   parallel-slots value when setup should not ask
#                               (1), else "ask"
#
# <plat> is "linux" or "macos".
# ──────────────────────────────────────────────────────────────────────────────

# MC_PLATFORM is "macos" on Darwin and "linux" everywhere else — mc_auto_pick
# uses it to apply the Apple-Silicon unified-memory row of the policy table.
if [ "$(uname -s)" = "Darwin" ]; then MC_PLATFORM=macos; else MC_PLATFORM=linux; fi

# MC_JSON holds the parsed catalog text once mc_load runs.
MC_LOADED=0
MC_JSON=""
MC_CATALOG_FILE=""
MC_USING_FALLBACK=0

# Fallback catalog: minimal, Qwen-only, mirrors scripts/models.json's linux+macos
# Qwen tiers exactly. Used only when the real catalog is absent/corrupt.
_mc_fallback_json() {
cat <<'JSON'
{"schema":1,"default":"qwen","models":[
 {"id":"qwen","backend":"llama","prism":false,"aliases":["qwen"],"requires":null,
  "menu":{"label":"Qwen on llama.cpp","tag":"default — standard, most compatible","line":"Qwen on llama.cpp  [default — standard, most compatible]"},
 "platforms":{
  "linux":{"tiers":{
    "24":{"name":"Qwen3.8-27B-UD-Q4_K_M.gguf","url":"https://huggingface.co/unsloth/Qwen3.8-27B-GGUF/resolve/main/Qwen3.8-27B-UD-Q4_K_M.gguf","mmproj":"mmproj-qwen3.8-27B-F16.gguf","mmproj_url":"https://huggingface.co/unsloth/Qwen3.8-27B-GGUF/resolve/main/mmproj-F16.gguf","ctx":196608,"kv":"q8_0","accuracy":"full","label":"Qwen3.8-27B-UD-Q4_K_M (~15GB) [GPU 24GB+ — full accuracy]"},
    "16":{"name":"Qwen3.8-27B-UD-IQ3_XXS.gguf","url":"https://huggingface.co/unsloth/Qwen3.8-27B-GGUF/resolve/main/Qwen3.8-27B-UD-IQ3_XXS.gguf","mmproj":"mmproj-qwen3.8-27B-F16.gguf","mmproj_url":"https://huggingface.co/unsloth/Qwen3.8-27B-GGUF/resolve/main/mmproj-F16.gguf","ctx":180224,"kv":"q4_0","accuracy":"lower","label":"Qwen3.8-27B-UD-IQ3_XXS (~12GB) [GPU 16GB — lower accuracy]"},
    "12":{"name":"Qwen3.5-9B-UD-Q4_K_XL.gguf","url":"https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-UD-Q4_K_XL.gguf","mmproj":"mmproj-qwen3.5-9B-F16.gguf","mmproj_url":"https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/mmproj-F16.gguf","ctx":196608,"kv":"q4_0","accuracy":"lower","label":"Qwen3.5-9B-UD-Q4_K_XL (~5GB) [GPU 12GB — lower accuracy]"},
    "cpu":{"name":"Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf","url":"https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF/resolve/main/Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf","mmproj":"mmproj-qwen3.6-35B-A3B-F16.gguf","mmproj_url":"https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF/resolve/main/mmproj-F16.gguf","ctx":196608,"kv":"q8_0","accuracy":"standard","label":"Qwen3.6-35B-A3B (~17.6GB) [CPU]","label_igpu":"Qwen3.6-35B-A3B (~17.6GB) [AMD iGPU / Vulkan]"}}},
  "macos":{"tiers":{
    "48":{"name":"Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf","url":"https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF/resolve/main/Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf","mmproj":"mmproj-qwen3.6-35B-A3B-F16.gguf","mmproj_url":"https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF/resolve/main/mmproj-F16.gguf","ctx":196608,"accuracy":"standard","label":"Qwen3.6-35B-A3B (~17.6GB) [Apple Silicon >=48GB — standard]"},
    "32":{"name":"Qwen3.5-9B-UD-Q4_K_XL.gguf","url":"https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-UD-Q4_K_XL.gguf","mmproj":"mmproj-qwen3.5-9B-F16.gguf","mmproj_url":"https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/mmproj-F16.gguf","ctx":65536,"accuracy":"lower","label":"Qwen3.5-9B-UD-Q4_K_XL (~5GB) [Apple Silicon 32GB]"},
    "16":{"name":"Qwen3.5-9B-UD-Q4_K_XL.gguf","url":"https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-UD-Q4_K_XL.gguf","mmproj":"mmproj-qwen3.5-9B-F16.gguf","mmproj_url":"https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/mmproj-F16.gguf","ctx":16384,"accuracy":"lower","label":"Qwen3.5-9B-UD-Q4_K_XL (~5GB) [Apple Silicon 16GB — lower accuracy]"},
    "8":{"name":"Qwen3.5-9B-UD-Q4_K_XL.gguf","url":"https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-UD-Q4_K_XL.gguf","mmproj":"","mmproj_url":"","ctx":8192,"accuracy":"lower","label":"Qwen3.5-9B-UD-Q4_K_XL (~5GB) [Apple Silicon <16GB]"}}}}},
 {"id":"flash-next","backend":"strata","prism":false,"aliases":["flash-next"],"requires":"CAN_STRATA",
  "deny":"Qwen3.8-Flash-Next (Strata) needs Linux + an NVIDIA GPU (CUDA 13, auto-installed by scripts/strata.sh) and a 48GB-class box (~70GB weights, streamed from disk). Not available on this box — re-run with --model=qwen.",
  "menu":{"label":"Qwen3.8-Flash-Next via Strata (~70GB)","tag":"NVIDIA / CUDA 13","line":"Qwen3.8-Flash-Next via Strata (~70GB, NVIDIA / CUDA 13)"},
 "script":"scripts/strata.sh","port_env":"INFERENCE_PORT","port_default":7474}],
 "context":{"linux":{"vision":172032,"vision_16g":98304,"cpu":196608},"macos":{"vision":172032,"vision_32g":49152,"vision_16g":12288}}}
JSON
}

mc_load() {
    [ "$MC_LOADED" = "1" ] && return 0
    MC_LOADED=1

    # Locate the catalog relative to the sourcing script (scripts/models.json).
    local candidate=""
    if [ -n "${MC_SCRIPT_DIR:-}" ]; then
        candidate="$MC_SCRIPT_DIR/models.json"
    elif [ -n "${SCRIPT_DIR:-}" ]; then
        candidate="$SCRIPT_DIR/models.json"
    fi
    MC_CATALOG_FILE="$candidate"

    local json=""
    if [ -n "$candidate" ] && [ -f "$candidate" ] && command -v jq &>/dev/null \
        && json="$(cat "$candidate" 2>/dev/null)" && [ -n "$json" ] \
        && printf '%s' "$json" | jq -e '.schema and .models' &>/dev/null; then
        MC_JSON="$json"
        MC_USING_FALLBACK=0
    else
        MC_JSON="$(_mc_fallback_json)"
        MC_USING_FALLBACK=1
        # Warn only if jq is present (so we could otherwise have parsed the file)
        # and the file was there but invalid, vs. simply absent.
        if command -v warn &>/dev/null; then
            if [ -n "$candidate" ] && [ -f "$candidate" ]; then
                warn "Model catalog $candidate failed to parse — using built-in Qwen-only defaults. Restore or fix models.json to enable Bonsai/Strata."
            fi
        fi
    fi
}

# Low-level: run jq on the loaded catalog. Extra args (--arg name value ...) are
# passed through to jq; the filter is whichever positional jq expects.
_mc_q() {
    mc_load
    printf '%s' "$MC_JSON" | jq -r "$@" 2>/dev/null
}

mc_default()  { _mc_q '.default'; }
mc_ids()      { _mc_q '.models[].id'; }

mc_id_for() {
    local want="${1:-}"
    [ -z "$want" ] && return 0
    _mc_q --arg w "$want" '
        (.models[]
         | select((.id==$w) or ((.aliases // []) | index($w)))
         | .id) // empty' 2>/dev/null
}

mc_meta()     { local id="${1:-}" f="${2:-}"; [ -z "$id" ] && return 0
                _mc_q --arg i "$id" --arg f "$f" '.models[]|select(.id==$i)|(.[$f] // empty)' 2>/dev/null; }

mc_requires() { mc_meta "${1:-}" requires; }

mc_menu()     { local id="${1:-}" f="${2:-}"; [ -z "$id" ] && return 0
                _mc_q --arg i "$id" --arg f "$f" '.models[]|select(.id==$i)|(.menu[$f] // empty)' 2>/dev/null; }

# mc_menu_line <id> [plat] — menu row text with platform override: a bonsai /
# engine row can carry menu.line_macos (e.g. "[Metal]" instead of the CUDA tag)
# or menu.line_amd_igpu (7940HS path — the Prism image is Vulkan-only there);
# falls back to menu.line. Keeps the installers from hand-writing model names.
mc_menu_line() {
    local id="${1:-}" plat="${2:-$MC_PLATFORM}" _ln=""
    if [ "$plat" = "macos" ]; then _ln="$(mc_menu "$id" line_macos)"; fi
    if [ -z "$_ln" ] && [ "$plat" = "linux" ] && [ "${AMD_IGPU_MODE:-0}" = "1" ]; then
        _ln="$(mc_menu "$id" line_amd_igpu)"
    fi
    [ -z "$_ln" ] && _ln="$(mc_menu "$id" line)"
    printf '%s' "$_ln"
}

mc_deny()     { mc_meta "${1:-}" deny; }

# mc_qwen_tier_label <plat> <key> — concrete label of a Qwen tier row, honouring
# the AMD iGPU variant: a cpu-tier Qwen on a 7940HS-class box runs on the 780M
# through the Vulkan image, not on CPU, so its tier may carry "label_igpu".
mc_qwen_tier_label() {
    local plat="${1:-$MC_PLATFORM}" key="${2:-}" tier=""
    [ -z "$key" ] && return 0
    if [ "$plat" = "linux" ] && [ "${AMD_IGPU_MODE:-0}" = "1" ]; then
        tier="$(mc_tier_field qwen "$plat" "$key" label_igpu)"
    fi
    [ -z "$tier" ] && tier="$(mc_tier_field qwen "$plat" "$key" label)"
    printf '%s' "$tier"
}

# mc_qwen_menu_line <plat> <key> — menu text for a Qwen row: the concrete file
# label of the selected tier ("Qwen3.6-35B-A3B (~17.6GB) [CPU]", not the vague
# "Qwen on llama.cpp"). <key> is a linux VRAM bucket (24|16|12|cpu) or a macOS
# unified-RAM tier key (48|32|16|8). Falls back to the catalog menu line when
# the label is missing, so a row never renders empty.
mc_qwen_menu_line() {
    local plat="${1:-$MC_PLATFORM}" key="${2:-}" tier
    [ -n "$key" ] && tier="$(mc_qwen_tier_label "$plat" "$key")"
    if [ -n "$tier" ]; then printf '%s' "$tier"; else mc_menu qwen line; fi
}

# mc_auto_pick_menu_line <plat> <key> — menu text for the row that maps to the
# hardware gate's pick: the other engine's menu line verbatim when the gate
# would pick Bonsai/Strata, otherwise the concrete Qwen tier label.
mc_auto_pick_menu_line() {
    local plat="${1:-$MC_PLATFORM}" key="${2:-}" _id _ln
    _id="$(mc_auto_pick)"
    if [ "$_id" != "qwen" ]; then
        _ln="$(mc_menu_line "$_id" "$plat")"
        [ -n "$_ln" ] && { printf '%s' "$_ln"; return; }
    fi
    mc_qwen_menu_line "$plat" "$key"
}

# mc_flag <NAME> — echo the value of capability flag NAME as it is set in the
# current shell (0 when unset). Lets the catalog gate models by name instead of
# the installer hand-coding "if [ $CAN_BONSAI = 1 ]".
mc_flag() {
    local name="${1:-}"
    case "$name" in
        CAN_BONSAI|CAN_STRATA) printf '%s' "${!name:-0}" ;;
        *)                     printf '0' ;;
    esac
}

# mc_tier_exists <id> <plat> <key> — success when that model/platform/tier row
# exists. Used to skip menu options and tier lookups that a platform lacks
# (e.g. Bonsai PTQ1_0 has no 8GB macOS row).
mc_tier_exists() {
    local id="${1:-}" plat="${2:-}" key="${3:-}"
    [ -z "$id" ] && return 1
    mc_load
    printf '%s' "$MC_JSON" | jq -e --arg i "$id" --arg p "$plat" --arg k "$key" \
        '.models[]|select(.id==$i)|(.platforms[$p].tiers[$k]) != null' &>/dev/null
}

mc_file_field() { local id="${1:-}" f="${2:-}"; [ -z "$id" ] && return 0
                  _mc_q --arg i "$id" --arg f "$f" '.models[]|select(.id==$i)|(.files[$f] // empty)' 2>/dev/null; }

mc_tier_field() {
    local id="${1:-}" plat="${2:-}" key="${3:-}" f="${4:-}"
    [ -z "$id" ] && return 0
    _mc_q --arg i "$id" --arg p "$plat" --arg k "$key" --arg f "$f" \
        '.models[]|select(.id==$i)|(.platforms[$p].tiers[$k][$f] // empty)' 2>/dev/null
}

mc_min_mem() { local id="${1:-}" plat="${2:-}"; [ -z "$id" ] && return 0
               _mc_q --arg i "$id" --arg p "$plat" '.models[]|select(.id==$i)|(.platforms[$p].min_mem // empty)' 2>/dev/null; }

mc_ctx() { local plat="${1:-}" key="${2:-}"; [ -z "$plat" ] && return 0
           _mc_q --arg p "$plat" --arg k "$key" '.context[$p][$k] // empty' 2>/dev/null; }

# mc_pick_tier <id> <plat> <value> — echo the tier key that applies to a
# memory size in GB: the largest numeric key <= value; when the box sits below
# every listed tier, clamp to the smallest one (the old installers did exactly
# this with their if/elif ladders). Non-numeric keys (e.g. "cpu") are ignored.
mc_pick_tier() {
    local id="${1:-}" plat="${2:-}" val="${3:-0}"
    [ -z "$id" ] && return 0
    _mc_q --arg i "$id" --arg p "$plat" --argjson v "$val" '
        (.models[]|select(.id==$i)|.platforms[$p].tiers|keys
         | map(select(test("^[0-9]+$"))|tonumber) | sort) as $t
        | if ($t|length) == 0 then ""
          elif ($t | map(select(. <= $v)) | length) > 0
          then ($t | map(select(. <= $v)) | max | tostring)
          else ($t|min | tostring) end' 2>/dev/null
}

# _mc_set_identity <name> <url> <mmproj> <mmproj_url> <accuracy> <label> <prism> [id]
# Assigns the MODEL_* contract every downstream installer step reads. _MODEL_ID
# records which catalog entry was applied so ctx/kv/label lookups downstream can
# read the same row without re-parsing the selection.
_mc_set_identity() {
    MODEL_NAME="${1:-}"
    MODEL_URL="${2:-}"
    MODEL_MMPROJ="${3:-}"
    MODEL_MMPROJ_URL="${4:-}"
    MODEL_ACCURACY="${5:-}"
    _MODEL_LABEL="${6:-}"
    MODEL_ALIAS="${MODEL_NAME%.gguf}"
    IS_BONSAI="${7:-0}"
    _MODEL_ID="${8:-}"
}

# ── Auto-default model policy (hardware gate, no --options) ────────────────────
#
# Without --options, setup picks the best model this box can run instead of
# asking. The gate is ordered — FIRST MATCH WINS — and the VRAM gate runs before
# the system-RAM gate, so the same card can land on two different rows depending
# on how much RAM is behind it. Supported NVIDIA cards start at 12GB; smaller
# cards keep yesterday's Qwen default (see docs/MODELS.md).
#   1. NVIDIA ≥24GB VRAM + ≥48GB RAM (CAN_STRATA) → flash-next
#   2. NVIDIA 12GB VRAM             (CAN_BONSAI)  → bonsai-pq2
#   3. AMD iGPU / UMA (7940HS, 780M), 32GB RAM+ (CAN_BONSAI) → bonsai-pq2
#   4. macOS unified 24-47GB       (CAN_BONSAI)  → bonsai-pq2
#   5. macOS unified 16-23GB       (CAN_BONSAI)  → bonsai-ptq1
#   6. everything else                            → qwen
# Row 6 covers NVIDIA 16GB and 24GB+ with under 48GB RAM: both run Qwen3.8-27B
# (IQ3_XXS and Q4_K_M respectively) — a 27B Qwen fits, so no ternary quant.
# PQ2_0 (7.21GB) is the better ternary build and is used wherever it fits: the
# 12GB tier budgets 7.21 + ~0.63mmproj + KV in VRAM, and the iGPU path shares
# system RAM over UMA, so the higher quant is fine there too.
mc_auto_pick() {
    local vram="${_GPU_TIER:-0}" ram="${TOTAL_MEM:-0}"
    # 44, not 48: a 48GB-class box reports ~46-47 GiB once firmware/kernel
    # reservations are subtracted, and this is the same bar install.sh uses to
    # set CAN_STRATA. Using 48 here would silently drop real 48GB boxes to Qwen.
    if [ "${CAN_STRATA:-0}" = "1" ] && [ "$vram" -ge 24 ] && [ "$ram" -ge 44 ]; then
        echo flash-next; return
    fi
    if [ "${CAN_BONSAI:-0}" = "1" ]; then
        # NVIDIA 12GB is the only Bonsai band. 16GB and 24GB+ keep Qwen3.8-27B
        # (IQ3_XXS / Q4_K_M) — a 27B Qwen fits, so there is no reason to drop to
        # a ternary quant. 12GB is where Qwen has to fall back to 9B, which is
        # exactly where PQ2_0 (7.2GB) buys a real 27B. The band is the 12GB tier:
        # install.sh buckets 12000-15999MB there, and NVIDIA ships no 13-15GB card,
        # so in practice this is 12GB and nothing else.
        if [ "${GPU_MODE:-0}" = "1" ] && [ "$vram" -ge 12 ] && [ "$vram" -lt 16 ]; then
            echo bonsai-pq2; return
        fi
        # The iGPU is UMA: weights, KV cache and the OS all come out of system
        # RAM, so the row is documented at 32GB+. Below that the pick is still
        # Bonsai — PTQ1_0/PQ2_0 need ~7GB and the Qwen CPU model needs ~20GB, so
        # a smaller box is strictly better off on the ternary build; install.sh
        # warns rather than downgrading it to a model that cannot load.
        if [ "${AMD_IGPU_MODE:-0}" = "1" ]; then echo bonsai-pq2; return; fi
        if [ "$MC_PLATFORM" = "macos" ] && [ "$ram" -ge 24 ] && [ "$ram" -lt 48 ]; then
            echo bonsai-pq2; return
        fi
        if [ "$MC_PLATFORM" = "macos" ] && [ "$ram" -ge 16 ] && [ "$ram" -lt 24 ]; then
            echo bonsai-ptq1; return
        fi
    fi
    echo qwen
}

# Human-readable reason for the pick above — logged so a silent default is still
# an explainable one.
mc_auto_reason() {
    local vram="${_GPU_TIER:-0}" ram="${TOTAL_MEM:-0}" id
    id="$(mc_auto_pick)"
    case "$id" in
        flash-next) echo "≥24GB VRAM and ${ram}GB system RAM — Strata runs Qwen3.8-Flash-Next natively" ;;
        bonsai-pq2) if [ "${AMD_IGPU_MODE:-0}" = "1" ]; then
                        echo "AMD iGPU (UMA shares system RAM) — PQ2_0 is the better ternary build"
                    elif [ "$MC_PLATFORM" = "macos" ]; then
                        echo "${ram}GB unified memory — PQ2_0 leaves room for KV + vision"
                    else
                        echo "${vram}GB VRAM — Qwen has to drop to 9B here; PQ2_0 (7.2GB) keeps a real 27B"
                    fi ;;
        bonsai-ptq1) echo "${ram}GB unified memory — PTQ1_0 (5.95GB) is the quant that fits" ;;
        *)           if [ "${GPU_MODE:-0}" = "1" ] && [ "$vram" -ge 16 ]; then
                        echo "${vram}GB VRAM — Qwen3.8-27B fits, so no ternary fallback needed"
                     else
                        echo "no accelerated Bonsai/Strata target — stock Qwen for this tier"
                     fi ;;
    esac
}

# Non-interactive defaults for the two prompts that are hardware-defaulted:
# "ask" means setup should still prompt (user passed --options).
mc_vision_default()      { [ "${OPENMONO_SETUP_OPTIONS:-0}" = "1" ] && echo ask || echo yes; }
mc_parallel_slots_default() { [ "${OPENMONO_SETUP_OPTIONS:-0}" = "1" ] && echo ask || echo 1; }

# mc_files_select <id> — set MODEL_* from files.* + accuracy. For models whose
# GGUF never changes across tiers (Bonsai): tiers only tune ctx/kv/label.
mc_files_select() {
    local id="${1:-}"
    [ -z "$id" ] && return 1
    local name url mm mmurl acc prism
    name="$(mc_file_field "$id" name)";   [ -z "$name" ] && return 1
    url="$(mc_file_field "$id" url)"
    mm="$(mc_file_field "$id" mmproj)"
    mmurl="$(mc_file_field "$id" mmproj_url)"
    acc="$(mc_meta "$id" accuracy)"
    label="$(mc_menu "$id" label)"
    [ -z "$label" ] && label="$(mc_meta "$id" label)"
    [ "$(mc_meta "$id" prism)" = "true" ] && prism=1 || prism=0
    _mc_set_identity "$name" "$url" "$mm" "$mmurl" "$acc" "$label" "$prism" "$id"
    return 0
}

# mc_select <id> <plat> <key> — set MODEL_* for one tier. Tier fields win; a
# missing tier field falls back to files.* (fixed-identity models). Returns
# non-zero when the catalog has no such tier, leaving every variable untouched
# so the caller can decide (fall back to another tier, or die).
mc_select() {
    local id="${1:-}" plat="${2:-}" key="${3:-}"
    mc_tier_exists "$id" "$plat" "$key" || return 1

    local name url mm mmurl acc label prism
    name="$(mc_tier_field "$id" "$plat" "$key" name)"
    url="$(mc_tier_field "$id" "$plat" "$key" url)"
    mm="$(mc_tier_field "$id" "$plat" "$key" mmproj)"
    mmurl="$(mc_tier_field "$id" "$plat" "$key" mmproj_url)"
    acc="$(mc_tier_field "$id" "$plat" "$key" accuracy)"
    if [ "$id" = "qwen" ]; then
        label="$(mc_qwen_tier_label "$plat" "$key")"
    else
        label="$(mc_tier_field "$id" "$plat" "$key" label)"
    fi

    # Fixed-identity models (Bonsai) put the file in files.* instead.
    if [ -z "$name" ];  then name="$(mc_file_field "$id" name)";       fi
    if [ -z "$url" ];   then url="$(mc_file_field "$id" url)";         fi
    if [ -z "$mmurl" ] && [ -n "$mm" ]; then mmurl="$(mc_file_field "$id" mmproj_url)"; fi
    if [ -z "$acc" ];   then acc="$(mc_meta "$id" accuracy)";          fi
    if [ -z "$label" ]; then label="$(mc_menu "$id" label)";           fi

    [ -z "$name" ] && return 1
    [ "$(mc_meta "$id" prism)" = "true" ] && prism=1 || prism=0
    _mc_set_identity "$name" "$url" "$mm" "$mmurl" "$acc" "$label" "$prism" "$id"
    return 0
}
