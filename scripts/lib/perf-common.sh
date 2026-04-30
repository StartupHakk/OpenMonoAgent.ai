#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# perf-common.sh — shared harness for cpu-test.sh and gpu-test.sh.
#
# Probes llama.cpp's /v1/chat/completions endpoint with a FIXED test suite
# so results are comparable across hardware running the same model. Uses
# non-streaming requests so we can read llama.cpp's built-in `timings` block
# (prefill + decode tok/s) straight from the response.
#
# Not intended to be run directly. Source from cpu-test.sh / gpu-test.sh.
# ─────────────────────────────────────────────────────────────────────────────

set -euo pipefail

# ── Config (override via env vars) ───────────────────────────────────────────
ENDPOINT="${OPENMONO_ENDPOINT:-http://localhost:7474}"
ITERATIONS="${PERF_ITERATIONS:-3}"
WARMUP_ENABLED="${PERF_WARMUP:-1}"
MAX_PROMPT_PROCESSING_S="${PERF_PROMPT_TIMEOUT:-120}"
MAX_DECODE_S="${PERF_DECODE_TIMEOUT:-300}"

# ── ANSI colors ──────────────────────────────────────────────────────────────
if [ -t 1 ]; then
    RED=$'\033[0;31m'; GREEN=$'\033[0;32m'; YELLOW=$'\033[1;33m'
    BLUE=$'\033[38;2;163;255;102m'; DIM=$'\033[2m'; NC=$'\033[0m'
else
    RED=""; GREEN=""; YELLOW=""; BLUE=""; DIM=""; NC=""
fi

info()  { echo "${BLUE}[INFO]${NC} $*"; }
ok()    { echo "${GREEN}[OK]${NC} $*"; }
warn()  { echo "${YELLOW}[WARN]${NC} $*"; }
err()   { echo "${RED}[ERROR]${NC} $*" >&2; }
die()   { err "$*"; exit 1; }

# ── Dependency checks ────────────────────────────────────────────────────────
perf_check_deps() {
    local missing=()
    for cmd in curl jq awk; do
        command -v "$cmd" &>/dev/null || missing+=("$cmd")
    done
    if [ ${#missing[@]} -gt 0 ]; then
        err "Missing required commands: ${missing[*]}"
        err "Install with: sudo apt install ${missing[*]}"
        exit 1
    fi
    info "Dependencies OK: curl jq awk"
}

perf_check_server() {
    info "Checking llama-server health at ${ENDPOINT}/health..."
    # Capture both status code and a small body sample so the error message
    # is diagnostic, not "connection failed" with no context.
    local tmp status body
    tmp=$(mktemp)
    status=$(curl -sS -o "$tmp" -w '%{http_code}' --max-time 10 \
        "${ENDPOINT}/health" 2>&1) || status="000"
    body=$(head -c 200 "$tmp" 2>/dev/null); rm -f "$tmp"

    case "$status" in
        200)
            ok "llama-server is healthy (HTTP 200)"
            ;;
        503)
            # llama.cpp returns 503 during model load. Warn but proceed —
            # the first benchmark request will either succeed (once loaded)
            # or error out with a clear message.
            warn "llama-server returned 503 (model still loading). Will proceed; first iteration may be slow."
            ;;
        000|"")
            err "Could not reach llama-server at ${ENDPOINT}."
            err "curl output: $status"
            err "Is llama-server running? Check: curl ${ENDPOINT}/health"
            err "Override endpoint with: OPENMONO_ENDPOINT=http://host:port $0"
            exit 1
            ;;
        *)
            err "llama-server at ${ENDPOINT} returned HTTP $status"
            [ -n "$body" ] && err "response: $body"
            exit 1
            ;;
    esac
}

# Returns hostname sanitized for use in a filename.
perf_safe_hostname() {
    hostname | tr -c '[:alnum:]-' '_' | sed 's/_*$//'
}

# UTC timestamp safe for filenames. e.g. 2026-04-18T14-30-00Z
perf_timestamp() {
    date -u '+%Y-%m-%dT%H-%M-%SZ'
}

# ── Hardware info collection ─────────────────────────────────────────────────
perf_cpu_info() {
    local cpu_model cpu_cores cpu_threads
    # x86 uses "model name"; aarch64 uses "Model name" (lscpu) or nothing.
    # Try progressively less-structured sources. On x86 "model name" exists
    # in /proc/cpuinfo; on aarch64 lscpu may return "-" or Linux kernel may
    # only expose "Hardware". Treat "-" and empty alike.
    _empty_or_dash() { [ -z "$1" ] || [ "$1" = "-" ]; }
    cpu_model=$(lscpu 2>/dev/null | awk -F: '/^Model name/ {sub(/^ +/, "", $2); print $2; exit}')
    _empty_or_dash "$cpu_model" && cpu_model=$(awk -F: '/model name/ {sub(/^ +/, "", $2); print $2; exit}' /proc/cpuinfo 2>/dev/null)
    _empty_or_dash "$cpu_model" && cpu_model=$(awk -F: '/^Hardware/ {sub(/^ +/, "", $2); print $2; exit}' /proc/cpuinfo 2>/dev/null)
    _empty_or_dash "$cpu_model" && cpu_model=$(awk -F: '/^Vendor ID/ {sub(/^ +/, "", $2); print $2; exit}' <(lscpu 2>/dev/null))
    _empty_or_dash "$cpu_model" && cpu_model="unknown ($(uname -m))"
    unset -f _empty_or_dash
    cpu_threads=$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo unknown)
    cpu_cores=$(lscpu -b -p=Core,Socket 2>/dev/null | grep -v '^#' | sort -u | wc -l)
    [ "$cpu_cores" = "0" ] && cpu_cores=unknown
    echo "Model: $cpu_model"
    echo "Physical cores: $cpu_cores"
    echo "Logical threads: $cpu_threads"
}

perf_mem_info() {
    free -h 2>/dev/null | awk 'NR==2 {print "Total: " $2 "   Used: " $3 "   Free: " $4}' \
        || echo "(free command not available)"
}

perf_os_info() {
    local os kernel
    os=$(cat /etc/os-release 2>/dev/null | awk -F= '$1=="PRETTY_NAME" {print $2}' | tr -d '"' || echo unknown)
    kernel=$(uname -r 2>/dev/null || echo unknown)
    echo "OS: $os"
    echo "Kernel: $kernel"
    echo "Architecture: $(uname -m)"
}

perf_gpu_info() {
    if command -v nvidia-smi &>/dev/null && nvidia-smi &>/dev/null 2>&1; then
        nvidia-smi --query-gpu=name,memory.total,memory.free,driver_version \
            --format=csv,noheader 2>/dev/null | head -1
    else
        HAS_HW=false
        if command -v lspci &>/dev/null && lspci 2>/dev/null | grep -qi 'nvidia'; then
            HAS_HW=true
        elif grep -qi "0x10de" /sys/bus/pci/devices/*/vendor 2>/dev/null; then
            HAS_HW=true
        fi

        if [ "$HAS_HW" = true ]; then
            echo "NVIDIA hardware detected, but drivers not working/installed"
        else
            echo "No NVIDIA GPU detected"
        fi
    fi
}

# ── Model info (asks the llama-server what it's running) ─────────────────────
perf_model_info() {
    local props
    props=$(curl -sf --max-time 5 "${ENDPOINT}/props" 2>/dev/null || echo '{}')
    local model_path ctx_size
    model_path=$(echo "$props" | jq -r '.model_path // .default_generation_settings.model // "unknown"')
    ctx_size=$(echo "$props" | jq -r '.default_generation_settings.n_ctx // .n_ctx // "unknown"')
    echo "Model path: $model_path"
    echo "Context size: $ctx_size"
}

# ── Fixed test prompts ───────────────────────────────────────────────────────
# These strings are hardcoded so identical requests flow across hardware.

PERF_SHORT_PROMPT='Write a Python function that computes the factorial of n using recursion. Include a docstring and one example in a comment.'

# ~1500 tokens. Deliberately prosaic so the model has to work rather than
# pattern-match a common benchmark prompt.
PERF_LONG_PROMPT=$'You are reviewing a pull request for a distributed system. The PR adds a new endpoint /api/sync-batch that accepts up to 1000 records per call, each with an id, timestamp, payload, and checksum. The records are written to a PostgreSQL table via a bulk INSERT ... ON CONFLICT DO UPDATE. A redis queue fans the records out to three downstream workers: an analytics worker that aggregates them into hourly buckets, a notification worker that emits webhooks for records matching subscriber rules, and an audit worker that writes an append-only ledger to S3. The PR author claims latency is 120ms p50 and 400ms p99 for a batch of 1000. However, you notice: the PostgreSQL INSERT uses a single prepared statement with parameterized arrays; the redis queue uses LPUSH without any backpressure; the webhook subscriber rules are evaluated inside a Lua script with 200 lines of conditional logic; the S3 writer uses multipart uploads but never reaps incomplete parts; and the checksum is validated in application code rather than as a generated column. The test suite covers the happy path with 10 records but has no tests for partial failure, no tests for duplicate ids, no load test, and no chaos test. The PR description does not mention the memory overhead of the Lua script or the fact that the webhook worker currently holds an open HTTP/1.1 connection per subscriber. There are 47 active subscribers in production. Identify the top five correctness issues, top five performance issues, and top five operational issues, with a one-sentence rationale each and a concrete remediation that can be applied before merging. Do not include issues you cannot justify from the description above; do not duplicate items across categories; and rank within each category from highest to lowest impact. For each remediation, also note whether it requires a schema change, a protocol change, a code-only change, or an operational/runbook change. Finally, estimate the total additional engineering-days required to implement the full remediation list, assuming one senior engineer working in uninterrupted focus hours with standard CI/CD turnaround, and state any assumptions you made about the existing test and deploy infrastructure. Respond with numbered lists and keep each bullet to at most three sentences.'

# ── Single benchmark run ─────────────────────────────────────────────────────
# Runs a POST /v1/chat/completions, echoes tab-separated metrics on stdout:
#   <prompt_n>\t<prompt_per_sec>\t<predicted_n>\t<predicted_per_sec>\t<total_ms>
# On error prints "ERROR" and returns non-zero.
perf_run_once() {
    local prompt="$1"
    local max_tokens="$2"

    local body
    body=$(jq -n \
        --arg p "$prompt" \
        --argjson mt "$max_tokens" \
        '{
            model: "benchmark",
            messages: [{role: "user", content: $p}],
            max_tokens: $mt,
            temperature: 0,
            top_k: 1,
            seed: 42,
            stream: false,
            cache_prompt: false,
            timings_per_token: false
        }')

    local start end total_ms resp http_code
    start=$(date +%s%3N)
    resp=$(curl -sS \
        --max-time "$((MAX_PROMPT_PROCESSING_S + MAX_DECODE_S))" \
        -H 'Content-Type: application/json' \
        -w '\n__HTTP__%{http_code}' \
        -d "$body" \
        "${ENDPOINT}/v1/chat/completions" 2>&1) || {
            echo "ERROR curl_failed"
            return 1
        }
    end=$(date +%s%3N)
    total_ms=$((end - start))

    http_code=$(printf '%s' "$resp" | awk -F'__HTTP__' 'END{print $2}')
    local json_body
    json_body=$(printf '%s' "$resp" | awk -F'__HTTP__' '{print $1}')

    if [ "$http_code" != "200" ]; then
        echo "ERROR http_${http_code}"
        return 1
    fi

    # Pull timings. llama.cpp returns these when the OpenAI-compat endpoint
    # is used without streaming.
    local prompt_n prompt_per_sec predicted_n predicted_per_sec
    prompt_n=$(echo "$json_body" | jq -r '.timings.prompt_n // .usage.prompt_tokens // 0')
    prompt_per_sec=$(echo "$json_body" | jq -r '.timings.prompt_per_second // 0')
    predicted_n=$(echo "$json_body" | jq -r '.timings.predicted_n // .usage.completion_tokens // 0')
    predicted_per_sec=$(echo "$json_body" | jq -r '.timings.predicted_per_second // 0')

    printf '%s\t%s\t%s\t%s\t%s\n' \
        "$prompt_n" "$prompt_per_sec" "$predicted_n" "$predicted_per_sec" "$total_ms"
}

# ── Statistics ───────────────────────────────────────────────────────────────
# Reads whitespace-separated numbers from stdin, prints: "min max mean median"
perf_stats() {
    awk '
        { a[NR]=$1; s+=$1; if (NR==1 || $1<min) min=$1; if (NR==1 || $1>max) max=$1 }
        END {
            if (NR==0) { print "0 0 0 0"; exit }
            n=asort(a);
            mid=int((n+1)/2);
            median = (n%2==1) ? a[mid] : (a[mid]+a[mid+1])/2
            printf "%.2f %.2f %.2f %.2f\n", min, max, s/NR, median
        }'
}

# ── Test suite runner ────────────────────────────────────────────────────────
# Runs the full suite and appends per-iteration lines + per-test summaries to
# the file passed in $1. Also appends a final machine-readable TSV block.
perf_run_suite() {
    local log_file="$1"

    local -a cases=(
        "short-gen|Short decode (short prompt, 128 tokens out)|$PERF_SHORT_PROMPT|128"
        "long-gen|Sustained decode (short prompt, 512 tokens out)|$PERF_SHORT_PROMPT|512"
        "long-prefill|Prefill-heavy (long prompt, 128 tokens out)|$PERF_LONG_PROMPT|128"
        "combined|Combined (long prompt, 512 tokens out)|$PERF_LONG_PROMPT|512"
    )

    # Warmup (not recorded). Ensures the model's KV caches and CUDA kernels
    # (if any) are warm before the first timed iteration.
    if [ "$WARMUP_ENABLED" = "1" ]; then
        info "Warmup iteration (not recorded)..."
        perf_run_once "$PERF_SHORT_PROMPT" 32 >/dev/null 2>&1 \
            || warn "Warmup failed; continuing anyway"
    fi

    {
        echo ""
        echo "## Per-iteration results"
        echo ""
        printf "%-14s  %-3s  %10s  %10s  %10s  %10s  %10s\n" \
            "test" "it" "prefill_n" "prefill/s" "decode_n" "decode/s" "wall_ms"
        printf "%-14s  %-3s  %10s  %10s  %10s  %10s  %10s\n" \
            "----" "--" "---------" "---------" "--------" "--------" "-------"
    } >> "$log_file"

    # TSV machine-readable block — accumulate in memory and dump at the end.
    local tsv_lines=()
    tsv_lines+=("test	iteration	prefill_tokens	prefill_per_sec	decode_tokens	decode_per_sec	wall_ms")

    # Per-test aggregates for the human-readable summary.
    declare -A prefill_accum
    declare -A decode_accum

    local case spec name desc prompt max_tokens i line
    local prompt_n prompt_ps predicted_n predicted_ps wall_ms
    for spec in "${cases[@]}"; do
        IFS='|' read -r name desc prompt max_tokens <<< "$spec"
        info "Running $name × $ITERATIONS — $desc"
        {
            echo ""
            echo "### $name — $desc"
            echo ""
        } >> "$log_file"

        for i in $(seq 1 "$ITERATIONS"); do
            if ! line=$(perf_run_once "$prompt" "$max_tokens"); then
                warn "  iteration $i FAILED: $line"
                printf "%-14s  %-3s  %s\n" "$name" "$i" "$line" >> "$log_file"
                continue
            fi
            IFS=$'\t' read -r prompt_n prompt_ps predicted_n predicted_ps wall_ms <<< "$line"
            printf "  %-14s  %3d  %10s  %10s  %10s  %10s  %10s\n" \
                "$name" "$i" "$prompt_n" "$prompt_ps" "$predicted_n" "$predicted_ps" "$wall_ms" \
                | tee -a "$log_file"
            tsv_lines+=("$(printf '%s\t%d\t%s\t%s\t%s\t%s\t%s' \
                "$name" "$i" "$prompt_n" "$prompt_ps" "$predicted_n" "$predicted_ps" "$wall_ms")")
            prefill_accum[$name]+="$prompt_ps "
            decode_accum[$name]+="$predicted_ps "
        done
    done

    # Per-test summary
    {
        echo ""
        echo "## Per-test summary (tokens/sec)"
        echo ""
        printf "%-14s  %-8s  %8s  %8s  %8s  %8s\n" "test" "metric" "min" "max" "mean" "median"
        printf "%-14s  %-8s  %8s  %8s  %8s  %8s\n" "----" "------" "---" "---" "----" "------"
    } >> "$log_file"

    for spec in "${cases[@]}"; do
        IFS='|' read -r name _ _ _ <<< "$spec"
        local prefill_stats decode_stats
        prefill_stats=$(echo "${prefill_accum[$name]:-0}" | tr ' ' '\n' | grep -v '^$' | perf_stats)
        decode_stats=$(echo "${decode_accum[$name]:-0}" | tr ' ' '\n' | grep -v '^$' | perf_stats)
        # shellcheck disable=SC2086
        printf "%-14s  %-8s  %8s  %8s  %8s  %8s\n" "$name" "prefill" $prefill_stats >> "$log_file"
        # shellcheck disable=SC2086
        printf "%-14s  %-8s  %8s  %8s  %8s  %8s\n" "$name" "decode"  $decode_stats  >> "$log_file"
    done

    # Machine-readable TSV for easy diffing / grafana import.
    {
        echo ""
        echo "## Raw data (TSV)"
        echo ""
        echo '```tsv'
        printf '%s\n' "${tsv_lines[@]}"
        echo '```'
    } >> "$log_file"
}

# ─────────────────────────────────────────────────────────────────────────────
# Deep system info — for diagnosing why "identical" machines differ.
# Each subsection is self-contained and degrades gracefully when tools
# (dmidecode, sensors, numactl) are missing, so you can diff two logs
# from different boxes and spot exactly what's different.
# ─────────────────────────────────────────────────────────────────────────────

# Lowercase-true if sudo exists AND we can run a harmless command without prompt.
perf_have_sudo() {
    command -v sudo &>/dev/null && sudo -n true &>/dev/null
}

# ── CPU: governor, freq, microcode, flags, mitigations ──────────────────────
perf_cpu_deep() {
    echo "### CPU frequency scaling"
    echo ""
    local driver governor min max
    driver=$(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_driver 2>/dev/null || echo unknown)
    echo "Scaling driver : $driver"

    # Per-core governor — report unique values so a uniform setting prints once.
    local govs
    govs=$(cat /sys/devices/system/cpu/cpu*/cpufreq/scaling_governor 2>/dev/null | sort -u | paste -sd, -)
    echo "Governor(s)    : ${govs:-unknown}"

    min=$(cat /sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_min_freq 2>/dev/null || echo 0)
    max=$(cat /sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq 2>/dev/null || echo 0)
    if [ "$min" != "0" ] && [ "$max" != "0" ]; then
        echo "Freq range     : $((min/1000)) - $((max/1000)) MHz (cpu0)"
    fi

    # Current freq snapshot across all cores.
    local cur_freqs
    cur_freqs=$(cat /sys/devices/system/cpu/cpu*/cpufreq/scaling_cur_freq 2>/dev/null \
        | awk '{ printf "%d ", $1/1000 }' | sed 's/ $//')
    echo "Current MHz    : ${cur_freqs:-unavailable}"

    # Frequency boost / turbo state.
    if [ -r /sys/devices/system/cpu/cpufreq/boost ]; then
        local boost
        boost=$(cat /sys/devices/system/cpu/cpufreq/boost)
        echo "Boost enabled  : $([ "$boost" = "1" ] && echo yes || echo no)"
    fi
    if [ -r /sys/devices/system/cpu/intel_pstate/no_turbo ]; then
        local notb
        notb=$(cat /sys/devices/system/cpu/intel_pstate/no_turbo)
        echo "Turbo disabled : $([ "$notb" = "1" ] && echo yes || echo no)"
    fi

    echo ""
    echo "### CPU microcode + vendor"
    echo ""
    awk -F: '
        /^vendor_id/     && !v { sub(/^ +/,"",$2); print "Vendor         : " $2; v=1 }
        /^cpu family/    && !f { sub(/^ +/,"",$2); print "Family         : " $2; f=1 }
        /^model/         && !/^model name/ && !m { sub(/^ +/,"",$2); print "Model number   : " $2; m=1 }
        /^stepping/      && !s { sub(/^ +/,"",$2); print "Stepping       : " $2; s=1 }
        /^microcode/     && !u { sub(/^ +/,"",$2); print "Microcode      : " $2; u=1 }
    ' /proc/cpuinfo 2>/dev/null

    echo ""
    echo "### CPU flags (relevant to llama.cpp)"
    echo ""
    # llama.cpp picks different kernels based on these. A missing AVX2 or
    # AVX-512 is a smoking-gun explanation for slower inference.
    local flags_line
    flags_line=$(awk '/^flags/ || /^Features/ {sub(/^.*: /,""); print; exit}' /proc/cpuinfo 2>/dev/null)
    for flag in avx avx2 avx512f avx512bw avx512dq avx512vbmi avx512vnni fma sse4_1 sse4_2 bmi1 bmi2 f16c aes; do
        if echo " $flags_line " | grep -q " $flag "; then
            printf "  %-12s yes\n" "$flag"
        else
            printf "  %-12s no\n" "$flag"
        fi
    done

    echo ""
    echo "### CPU vulnerability mitigations"
    echo ""
    if [ -d /sys/devices/system/cpu/vulnerabilities ]; then
        for f in /sys/devices/system/cpu/vulnerabilities/*; do
            local name val
            name=$(basename "$f")
            val=$(cat "$f" 2>/dev/null)
            printf "  %-20s %s\n" "$name" "${val:-unknown}"
        done
    else
        echo "  (/sys/devices/system/cpu/vulnerabilities not available)"
    fi
}

# ── Memory: slot topology, speeds, channels ─────────────────────────────────
perf_memory_deep() {
    echo "### Memory slots (dmidecode type 17)"
    echo ""
    if ! command -v dmidecode &>/dev/null; then
        echo "  (dmidecode not installed — install with: sudo apt install dmidecode)"
        return 0
    fi

    local dmi_out
    if perf_have_sudo; then
        dmi_out=$(sudo -n dmidecode --type 17 2>/dev/null || true)
    else
        dmi_out=$(dmidecode --type 17 2>/dev/null || true)
    fi

    if [ -z "$dmi_out" ]; then
        echo "  (requires sudo — re-run with 'sudo $0' for full memory topology)"
        return 0
    fi

    # Summarize each populated slot on one line.
    echo "$dmi_out" | awk '
        /^Memory Device$/            { in_dev=1; next }
        in_dev && /^[[:space:]]*Size: / {
            sub(/^[[:space:]]*Size: /, "", $0); size=$0
        }
        in_dev && /^[[:space:]]*Locator: / {
            sub(/^[[:space:]]*Locator: /, "", $0); loc=$0
        }
        in_dev && /^[[:space:]]*Speed: / {
            sub(/^[[:space:]]*Speed: /, "", $0); speed=$0
        }
        in_dev && /^[[:space:]]*Configured Memory Speed: / {
            sub(/^[[:space:]]*Configured Memory Speed: /, "", $0); cfg=$0
        }
        in_dev && /^[[:space:]]*Manufacturer: / {
            sub(/^[[:space:]]*Manufacturer: /, "", $0); mfg=$0
        }
        in_dev && /^[[:space:]]*Part Number: / {
            sub(/^[[:space:]]*Part Number: /, "", $0); pn=$0
        }
        /^$/ && in_dev {
            if (size && size != "No Module Installed") {
                populated++
                printf "  %-14s %-12s rated %-14s configured %-14s %s %s\n",
                       loc, size, speed, cfg, mfg, pn
            }
            in_dev=0; size=""; loc=""; speed=""; cfg=""; mfg=""; pn=""
        }
        END { printf "\nPopulated DIMMs: %d\n", populated+0 }
    '

    echo ""
    echo "### NUMA topology"
    echo ""
    if command -v numactl &>/dev/null; then
        numactl --hardware 2>/dev/null | head -20
    else
        lscpu 2>/dev/null | grep -E '^NUMA' || echo "  (numactl not installed; lscpu shows no NUMA info)"
    fi

    echo ""
    echo "### Memory tuning"
    echo ""
    echo "  vm.swappiness       : $(cat /proc/sys/vm/swappiness 2>/dev/null || echo unknown)"
    echo "  vm.vfs_cache_pressure: $(cat /proc/sys/vm/vfs_cache_pressure 2>/dev/null || echo unknown)"
    echo "  Transparent HPs     : $(cat /sys/kernel/mm/transparent_hugepage/enabled 2>/dev/null || echo unknown)"
}

# ── Thermal zones snapshot ──────────────────────────────────────────────────
perf_thermal_snapshot() {
    echo "### Thermal zones (snapshot)"
    echo ""
    local found=0
    for zone in /sys/class/thermal/thermal_zone*; do
        [ -e "$zone/type" ] || continue
        local t_type t_temp
        t_type=$(cat "$zone/type" 2>/dev/null || echo unknown)
        t_temp=$(cat "$zone/temp" 2>/dev/null || echo 0)
        if [ "$t_temp" -gt 0 ]; then
            printf "  %-30s %s°C\n" "$t_type" "$((t_temp / 1000))"
            found=1
        fi
    done
    if [ "$found" = "0" ]; then
        echo "  (/sys/class/thermal has no zones; try: sudo apt install lm-sensors && sudo sensors-detect)"
    fi

    echo ""
    echo "### lm-sensors snapshot"
    if command -v sensors &>/dev/null; then
        echo ""
        sensors 2>/dev/null | sed 's/^/  /' | head -60 || echo "  (sensors command failed)"
    else
        echo ""
        echo "  (sensors not installed — install with: sudo apt install lm-sensors)"
    fi
}

# ── Intel RAPL power limits (huge NUC perf differentiator) ──────────────────
perf_power_limits() {
    echo "### Intel RAPL power limits"
    echo ""
    if ! [ -d /sys/class/powercap ]; then
        echo "  (no powercap interface — not Intel or kernel lacks intel-rapl)"
        return 0
    fi
    local found=0
    for dom in /sys/class/powercap/intel-rapl:*; do
        [ -d "$dom" ] || continue
        found=1
        local name pl1 pl2 pl1_tw pl2_tw
        name=$(cat "$dom/name" 2>/dev/null || echo "$(basename "$dom")")
        pl1=$(cat "$dom/constraint_0_power_limit_uw" 2>/dev/null || echo "")
        pl2=$(cat "$dom/constraint_1_power_limit_uw" 2>/dev/null || echo "")
        pl1_tw=$(cat "$dom/constraint_0_time_window_us" 2>/dev/null || echo "")
        pl2_tw=$(cat "$dom/constraint_1_time_window_us" 2>/dev/null || echo "")

        echo "  $name:"
        [ -n "$pl1" ] && printf "    PL1 (long):  %6d W  (%d s window)\n" "$((pl1 / 1000000))" "$((pl1_tw / 1000000))"
        [ -n "$pl2" ] && printf "    PL2 (short): %6d W  (%d s window)\n" "$((pl2 / 1000000))" "$((pl2_tw / 1000000))"
    done
    [ "$found" = "0" ] && echo "  (no intel-rapl:* domains present)"

    # Also report the platform power profile if present (systemd, ACPI, thermald).
    echo ""
    if command -v powerprofilesctl &>/dev/null; then
        echo "  power-profile-daemon: $(powerprofilesctl get 2>/dev/null || echo unavailable)"
    fi
    if [ -r /sys/firmware/acpi/platform_profile ]; then
        echo "  ACPI platform_profile: $(cat /sys/firmware/acpi/platform_profile)"
    fi
}

# ── BIOS + system identity (verifies the two boxes are the same SKU) ────────
perf_bios_info() {
    echo "### BIOS / system identity"
    echo ""
    if ! command -v dmidecode &>/dev/null; then
        echo "  (dmidecode not installed)"
        return 0
    fi
    local sudo_cmd=""
    if ! dmidecode -s bios-version &>/dev/null && perf_have_sudo; then
        sudo_cmd="sudo -n"
    fi
    local fields=(
        "bios-vendor"
        "bios-version"
        "bios-release-date"
        "system-manufacturer"
        "system-product-name"
        "system-version"
        "system-serial-number"
        "system-uuid"
        "baseboard-manufacturer"
        "baseboard-product-name"
        "baseboard-version"
        "chassis-type"
    )
    for f in "${fields[@]}"; do
        local v
        v=$($sudo_cmd dmidecode -s "$f" 2>/dev/null | grep -v '^#' | head -1 || echo "")
        printf "  %-25s %s\n" "$f" "${v:-(unavailable)}"
    done
    [ -z "$sudo_cmd" ] && ! dmidecode -s bios-version &>/dev/null \
        && echo "  (re-run with sudo for full BIOS info)"
}

# ── Kernel / boot-time knobs ────────────────────────────────────────────────
perf_kernel_deep() {
    echo "### Kernel + boot"
    echo ""
    echo "  uname -a     : $(uname -a)"
    echo "  cmdline      : $(cat /proc/cmdline 2>/dev/null | tr '\n' ' ' | sed 's/[[:space:]]*$//')"
    echo ""
    echo "  Key sysctls:"
    for k in kernel.randomize_va_space kernel.sched_rt_runtime_us vm.max_map_count vm.overcommit_memory; do
        printf "    %-32s %s\n" "$k" "$(sysctl -n "$k" 2>/dev/null || echo unknown)"
    done
}

# ── Top CPU consumers at test start ─────────────────────────────────────────
perf_top_processes() {
    echo "### Top 10 processes by CPU (snapshot)"
    echo ""
    if command -v top &>/dev/null; then
        top -bn1 -o %CPU 2>/dev/null \
            | awk '/^ *PID/ {print; found=1; next} found {print; count++; if (count>=10) exit}' \
            | sed 's/^/  /'
    else
        ps -eo pcpu,pid,user,comm --sort=-pcpu 2>/dev/null | head -11 | sed 's/^/  /'
    fi
}

# ── llama-server active configuration ───────────────────────────────────────
perf_llama_config() {
    echo "### llama-server configuration (from /props)"
    echo ""
    local props
    props=$(curl -sf --max-time 5 "${ENDPOINT}/props" 2>/dev/null || echo '{}')

    # /props shape has changed across llama.cpp versions — probe the most useful
    # fields with jq fallback to "?".
    local function_keys=(
        "default_generation_settings.n_threads"
        "default_generation_settings.n_threads_batch"
        "default_generation_settings.n_ctx"
        "default_generation_settings.n_batch"
        "default_generation_settings.n_ubatch"
        "default_generation_settings.n_gpu_layers"
        "n_threads"
        "n_threads_batch"
        "total_slots"
        "chat_template"
    )
    for key in "${function_keys[@]}"; do
        local v
        v=$(echo "$props" | jq -r "$(echo ".$key" | sed 's/\./.\"/g; s/$/\"/; s/^\.\"/./') // \"?\"" 2>/dev/null \
            | head -c 120)
        printf "  %-50s %s\n" "$key" "${v:-?}"
    done
}

# ── Orchestrator: call once from test scripts ───────────────────────────────
perf_deep_system_info() {
    local log_file="$1"
    # Relax strict mode: a missing sub-tool in one section must NOT kill
    # the whole test run. Each helper already handles its own absence
    # gracefully; we just guard against unexpected non-zero exits.
    set +e
    {
        echo ""
        echo "## Deep system info"
        echo "_(diff two logs from 'identical' boxes here to pinpoint hardware/software drift)_"
        echo ""
        perf_cpu_deep 2>&1 || echo "  (perf_cpu_deep failed: $?)"
        echo ""
        perf_memory_deep 2>&1 || echo "  (perf_memory_deep failed: $?)"
        echo ""
        perf_thermal_snapshot 2>&1 || echo "  (perf_thermal_snapshot failed: $?)"
        echo ""
        perf_power_limits 2>&1 || echo "  (perf_power_limits failed: $?)"
        echo ""
        perf_bios_info 2>&1 || echo "  (perf_bios_info failed: $?)"
        echo ""
        perf_kernel_deep 2>&1 || echo "  (perf_kernel_deep failed: $?)"
        echo ""
        perf_top_processes 2>&1 || echo "  (perf_top_processes failed: $?)"
        echo ""
        perf_llama_config 2>&1 || echo "  (perf_llama_config failed: $?)"
        echo ""
    } >> "$log_file"
    set -e
}

# ─────────────────────────────────────────────────────────────────────────────
# Background sampler — captures CPU freq + thermal zone temps every 1s
# during the test suite, then summarizes min/max/mean per metric.
# ─────────────────────────────────────────────────────────────────────────────

PERF_SAMPLE_FILE=""
PERF_SAMPLER_PID=""

perf_start_sampler() {
    PERF_SAMPLE_FILE=$(mktemp -t openmono-perf-sample.XXXXXX)
    (
        trap 'exit 0' TERM INT
        while true; do
            # Freq: one line per second, space-separated MHz values across cores.
            local freqs
            freqs=$(cat /sys/devices/system/cpu/cpu*/cpufreq/scaling_cur_freq 2>/dev/null \
                | awk '{printf "%d ", $1/1000}' | sed 's/ $//')
            [ -n "$freqs" ] && echo "FREQ $freqs" >> "$PERF_SAMPLE_FILE"

            # Temp: one line per zone.
            for zone in /sys/class/thermal/thermal_zone*; do
                [ -e "$zone/type" ] || continue
                local ztype ztemp
                ztype=$(cat "$zone/type" 2>/dev/null || continue)
                ztemp=$(cat "$zone/temp" 2>/dev/null || continue)
                [ "$ztemp" -gt 0 ] && echo "TEMP $ztype $((ztemp / 1000))" >> "$PERF_SAMPLE_FILE"
            done

            sleep 1
        done
    ) &
    PERF_SAMPLER_PID=$!
    info "Background sampler started (pid $PERF_SAMPLER_PID; writing to $PERF_SAMPLE_FILE)"
}

perf_stop_sampler() {
    local log_file="$1"
    [ -n "$PERF_SAMPLER_PID" ] && kill "$PERF_SAMPLER_PID" 2>/dev/null
    wait "$PERF_SAMPLER_PID" 2>/dev/null || true

    {
        echo ""
        echo "## During-test resource usage (1s sampler)"
        echo ""
    } >> "$log_file"

    if [ ! -s "$PERF_SAMPLE_FILE" ]; then
        echo "  (no samples captured)" >> "$log_file"
        rm -f "$PERF_SAMPLE_FILE"
        return 0
    fi

    # CPU frequency summary — per-core min/max/mean MHz across the run.
    {
        echo "### CPU frequency during test (MHz, per core)"
        echo ""
        printf "  %-4s  %6s  %6s  %6s  %6s\n" "core" "min" "max" "mean" "samples"
        printf "  %-4s  %6s  %6s  %6s  %6s\n" "----" "----" "----" "----" "-------"
        awk '/^FREQ / {
                for (i=2;i<=NF;i++) {
                    c = i-2
                    sum[c] += $i; n[c]++
                    if (n[c]==1 || $i<min[c]) min[c]=$i
                    if (n[c]==1 || $i>max[c]) max[c]=$i
                }
            }
            END {
                for (c=0; c<length(min); c++) {
                    printf "  %-4d  %6d  %6d  %6.0f  %6d\n",
                           c, min[c], max[c], sum[c]/n[c], n[c]
                }
            }' "$PERF_SAMPLE_FILE"
    } >> "$log_file"

    # Thermal summary — per zone.
    {
        echo ""
        echo "### Thermal zone temperatures during test (°C)"
        echo ""
        printf "  %-30s  %4s  %4s  %6s  %6s\n" "zone" "min" "max" "mean" "samples"
        printf "  %-30s  %4s  %4s  %6s  %6s\n" "----" "---" "---" "----" "-------"
        awk '/^TEMP / {
                zone=$2; temp=$3
                sum[zone]+=temp; n[zone]++
                if (n[zone]==1 || temp<min[zone]) min[zone]=temp
                if (n[zone]==1 || temp>max[zone]) max[zone]=temp
            }
            END {
                for (z in sum) {
                    printf "  %-30s  %4d  %4d  %6.1f  %6d\n",
                           z, min[z], max[z], sum[z]/n[z], n[z]
                }
            }' "$PERF_SAMPLE_FILE" | sort
    } >> "$log_file"

    rm -f "$PERF_SAMPLE_FILE"
    PERF_SAMPLE_FILE=""
    PERF_SAMPLER_PID=""
}
