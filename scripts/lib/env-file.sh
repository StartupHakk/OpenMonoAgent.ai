#!/usr/bin/env bash
# Atomic dotenv updates. Files stay mode 0600 and never use a shared /tmp name.

upsert_env_key() {
    local file="$1" key="$2" value="$3"
    local dir tmp
    dir="$(dirname "$file")"
    mkdir -p "$dir"
    if [[ ! -f "$file" ]]; then
        : > "$file"
        chmod 0600 "$file"
    fi
    tmp="$(mktemp "$dir/.env.XXXXXX")"
    grep -v "^${key}=" "$file" > "$tmp" || true
    printf '%s=%s\n' "$key" "$value" >> "$tmp"
    mv "$tmp" "$file"
    chmod 0600 "$file"
}

drop_env_keys() {
    local file="$1" pattern="$2"
    local dir tmp
    [[ -f "$file" ]] || return 0
    dir="$(dirname "$file")"
    tmp="$(mktemp "$dir/.env.XXXXXX")"
    grep -v -E "$pattern" "$file" > "$tmp" || true
    mv "$tmp" "$file"
    chmod 0600 "$file"
}
