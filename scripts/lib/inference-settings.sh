#!/usr/bin/env bash
# Shared helpers for the installers: persist the shared inference knobs to
# ~/.openmono/settings.json. settings.json is the single source of truth;
# scripts/apply_settings.sh materializes inference.port / parallel_slots into
# the backend files (docker/.env, strata.env, generated Strata configs, Caddy
# upstream) on every `openmono start`/`restart`.
#
# These helpers only write settings.json. They never touch backend files, so an
# installer can call them before the backend exists.
#
# Usage:
#   om_choose_parallel_slots 1     # prompt (interactive) / env / default,
#                                   # persist, and set $OM_PARALLEL_SLOTS
#   om_persist_parallel_slots "$n"
#   om_persist_inference_port 7474

# jq is required to write settings.json. Fall back to echo-only (best effort):
# apply_settings.sh pins the defaults into settings.json on first start anyway.
_om_settings_file() {
    printf '%s' "${OPENMONO_SETTINGS_FILE:-$HOME/.openmono/settings.json}"
}

_om_settings_set() {
    # $1 = jq filter, rest = jq args
    local filter="$1"; shift
    command -v jq &>/dev/null || return 0
    local settings; settings="$(_om_settings_file)"
    mkdir -p "$(dirname "$settings")"
    [[ -f "$settings" ]] || echo '{}' > "$settings"
    if jq "$@" "$filter" "$settings" > "${settings}.tmp" 2>/dev/null; then
        mv "${settings}.tmp" "$settings"
        chmod 0600 "$settings" 2>/dev/null || true
    else
        rm -f "${settings}.tmp"
    fi
}

om_persist_parallel_slots() {
    local n="$1"
    [[ "$n" =~ ^[0-9]+$ ]] && (( n >= 1 )) || return 0
    _om_settings_set '.inference = (.inference // {}) | .inference.parallel_slots = $n' \
        --argjson n "$n"
}

om_persist_inference_port() {
    local p="$1"
    [[ "$p" =~ ^[0-9]+$ ]] && (( p >= 1 && p <= 65535 )) || return 0
    _om_settings_set '.inference = (.inference // {}) | .inference.port = $p' \
        --argjson p "$p"
}

# Ask for the number of parallel slots and set $OM_PARALLEL_SLOTS to a valid
# positive integer.
#
# Same shape as role_prompt (sets $OPENMONO_ROLE) and prompt_model_choice (sets
# $INFERENCE_BACKEND): prompt on stdout, hand the answer back through a
# variable. It deliberately does NOT return the value through a command
# substitution, because that makes stdout two things at once — the display
# channel and the return channel. That single choice is what made this prompt
# hard: either the prompt text was captured into the value ("\n2" instead of
# "2"), or the text moved to stderr where `openmono setup` sends it to the log
# file and the user saw a blank hung input.
#
# Non-interactive installs take $OPENMONO_PARALLEL_SLOTS when set, else the
# default. The answer is always persisted to settings.json.
om_choose_parallel_slots() {
    local default="${1:-1}" n="${OPENMONO_PARALLEL_SLOTS:-}"
    OM_PARALLEL_SLOTS=""
    # Sanitize the default up front so the fallback path always has a valid int.
    if ! [[ "$default" =~ ^[0-9]+$ ]] || [ "$default" -lt 1 ]; then default=1; fi
    if [[ -z "$n" ]] && [[ -t 0 ]]; then
        # Drain keystrokes buffered by earlier prompts/long steps so a stray
        # Enter cannot silently answer this prompt.
        flush_stdin 2>/dev/null || true
        echo ""
        echo "  Parallel slots: how many requests may the model serve at once?"
        echo "  More slots cost memory and can cost speed, but how depends on the"
        echo "  backend:"
        echo "    llama.cpp / native - each slot gets its OWN full-context KV"
        echo "                         cache, so KV memory grows with the slot count."
        echo "    Strata             - each slot keeps its own K/V for the WHOLE"
        echo "                         context (the context is not divided between"
        echo "                         slots), but that K/V is taken from the expert"
        echo "                         cache, and Strata serves at most 8 slots."
        echo "  Use 1 for a single interactive agent; raise it only if you run"
        echo "  several sessions/agents at once."
        echo ""
        printf "  Slots [default: %s]: " "$default"
        read -r n || n=""
    fi
    # Coerce to a valid positive integer; empty, junk or EOF falls back to the
    # default, so OM_PARALLEL_SLOTS is never left empty or non-numeric.
    if ! [[ "${n:-}" =~ ^[0-9]+$ ]] || [ "${n:-0}" -lt 1 ]; then
        n="$default"
    fi
    om_persist_parallel_slots "$n"
    OM_PARALLEL_SLOTS="$n"
    return 0
}

# Warn (never clamp) about what the chosen slot count costs on THIS backend.
# Called after om_choose_parallel_slots, where the backend is known.
#   $1 = backend (strata | llama | native-metal)
#   $2 = slots
#   $3 = context tokens, optional — only used to make the llama.cpp note concrete
#
# The two engines behave differently enough that one generic sentence misleads:
#
#   llama.cpp / native: --parallel N allocates N independent KV buffers, each
#   sized for the full --ctx-size. KV memory really is ~N x single-slot.
#
#   Strata: "parallel" is the engine's --batch/--slots. The configured context is
#   NOT divided between slots — every slot owns a full-context K/V session. But
#   that K/V is carved out of the VRAM that would otherwise hold the model's
#   expert cache, so the cost is per-request speed, not a smaller context.
#   Upstream docs/BATCHING.md: 2..8 slots normally (8 per group with a layer
#   split), ~0.56 GiB per slot at a 32K context with 8-bit KV, and the engine
#   "never refuses a count it cannot run" — it logs the real number and serves
#   what fits. So asking for 24 is not an error, it is silently reduced.
om_warn_parallel_slots() {
    local backend="$1" n="${2:-1}" ctx="${3:-}"
    [[ "$n" =~ ^[0-9]+$ ]] || return 0

    if [ "$backend" = "strata" ]; then
        if [ "$n" -gt 8 ]; then
            warn "Strata serves at most 8 batch slots (8 per group with a layer split)."
            warn "  $n will be reduced by the engine at start — it logs 'batch_slots=N'"
            warn "  and GET /v1/status reports the real count under concurrency.serving."
            warn "  Lower it to avoid surprises: openmono config set inference.parallel_slots 8"
        fi
        if [ "$n" -gt 1 ]; then
            info "Strata: the context is NOT split between slots — each slot keeps its own"
            info "  full-context K/V, taken from the expert cache. On a 12/16 GB card that"
            info "  costs roughly 10-25% speed per request; it only pays off for concurrent sessions."
        fi
        return 0
    fi

    # llama.cpp / native-metal
    if [ "$n" -gt 1 ]; then
        if [ -n "$ctx" ] && [[ "$ctx" =~ ^[0-9]+$ ]]; then
            info "llama.cpp: each of the $n slots reserves its own $ctx-token KV cache."
            info "  KV memory is ~${n}x the single-slot cost — lower inference.ctx_size if it will not fit."
        else
            info "llama.cpp: each slot reserves its own full-context KV cache, so KV memory"
            info "  grows with the slot count — watch VRAM/RAM headroom."
        fi
    fi
    return 0
}

# ── Model-config persistence (settings.json is the single source of truth) ───
#
# These write the model knobs into ~/.openmono/settings.json so that
# scripts/apply_settings.sh (and thus `openmono upgrade`) can re-materialize
# them into docker/.env across every launch path (Docker llama, native Metal,
# Strata). They only write settings.json — never backend files — matching the
# port/parallel helpers above.
#
# Usage:
#   om_persist_model_name  "Ternary-Bonsai-2-27B-PQ2_0.gguf"
#   om_persist_model_alias "Ternary-Bonsai-2-27B-PQ2_0"
#   om_persist_mmproj      "Ternary-Bonsai-2-27B-mmproj-Q8_0.gguf"   # empty ok
#   om_persist_ctx_size    98304
#   om_persist_gpu_tier    16
#   om_persist_model_accuracy full|lower

om_persist_model_name() {
    local name="$1"
    [[ -n "$name" ]] || return 0
    _om_settings_set '.inference = (.inference // {}) | .inference.model_name = $n' \
        --arg n "$name"
}

om_persist_model_alias() {
    local alias="$1"
    [[ -n "$alias" ]] || return 0
    _om_settings_set '.inference = (.inference // {}) | .inference.model_alias = $n' \
        --arg n "$alias"
}

om_persist_mmproj() {
    local proj="$1"
    # Empty string is valid (vision off) — always persist it so the value is
    # explicit rather than left to a stale value.
    _om_settings_set '.inference = (.inference // {}) | .inference.mmproj = $n' \
        --arg n "$proj"
}

om_persist_ctx_size() {
    local n="$1"
    [[ "$n" =~ ^[0-9]+$ ]] && (( n > 0 )) || return 0
    _om_settings_set '.inference = (.inference // {}) | .inference.ctx_size = $n' \
        --argjson n "$n"
}

om_persist_gpu_tier() {
    local tier="$1"
    [[ "$tier" =~ ^[0-9]+$ ]] || return 0
    _om_settings_set '.inference = (.inference // {}) | .inference.gpu_tier = $n' \
        --argjson n "$tier"
}

om_persist_model_accuracy() {
    local acc="$1"
    case "$acc" in
        full|lower)
            _om_settings_set '.inference = (.inference // {}) | .inference.model_accuracy = $n' \
                --arg n "$acc"
            ;;
        *) return 0 ;;
    esac
}
