# OpenMono.ai — Installationsanleitung für AMD iGPU (Ryzen 9 PRO 8945HS / Radeon 780M)

**Dein System:**
- **CPU:** AMD Ryzen 9 PRO 8945HS mit Radeon 780M Graphics (8 CUs)
- **RAM:** 130.7 GB
- **OS:** Linux Mint 22.3 Cinnamon 64-bit, Kernel 6.17.0-35-generic
- **GPU-Treiber:** amdgpu, Vulkan aktiviert

---

## Wichtige Hinweise für AMD-Nutzer

- **VRAM-Überwachung:** Wenn der Container abstürzt oder "OOM" (Out of Memory) meldet, ist dein VRAM zu klein. Nutze dann eine niedrigere Quantisierung (z. B. `Q3_K_M` oder `IQ3_XXS`) und wiederhole Schritt 2.
  - Download für Q3: `.../resolve/main/Qwen3.6-35B-A3B-UD-Q3_K_M.gguf`
- **MTP:** Falls MTP nicht automatisch funktioniert, prüfe in den Logs, ob `llama.cpp` die MTP-Schichten erkennt. Unsloth GGUFs enthalten diese standardmäßig.
- **Vision:** Da Qwen3.6-35B-A3B ein rein textbasiertes Modell ist, ist kein `MODEL_MMPROJ` erforderlich. Wenn du später ein Vision-Modell (z. B. Qwen2-VL) nutzen willst, musst du diesen Pfad separat konfigurieren.

Falls du Fehlermeldungen erhältst, kopiere sie hier herein, und ich helfe dir bei der Diagnose!

---

## Schritt 1: AMD iGPU Kernel-Optimierungen anwenden

Damit die Radeon 780M Grafik den gesamten verfügbaren System-RAM als VRAM nutzen kann (GTT-Allokation), müssen GRUB-Parameter gesetzt werden.

```bash
# Kernel-Parameter für amdgpu GTT zuweisen (28 GB für iGPU)
echo "options amdgpu gttsize=28672" | sudo tee /etc/modprobe.d/amdgpu.conf

# GRUB aktualisieren
sudo update-grub

# System neu starten (zwingend erforderlich!)
sudo reboot
```

> **Nach dem Neustart:** Melde dich wieder an und fahre mit Schritt 2 fort.

---

## Schritt 2: Verzeichnisstruktur prüfen

Stelle sicher, dass der Zielordner für OpenMonoAI existiert.

```bash
mkdir -p /workspace/openmono.ai/models
cd /workspace/openmono.ai
```

---

## Schritt 3: Kompatible Modelle im Modelshelf auflisten

Dieser Befehl sucht in deinem `Modelshelf`-Ordner nach allen `.gguf`-Dateien, die das Modell **Qwen3.6** (oder Qwen3 Coder/MoE Varianten) enthalten.

```bash
# Suche nach allen Qwen3.6 oder Qwen3-Coder GGUF Dateien im Modelshelf
echo "=== Verfügbare Qwen3.6 / Coder Modelle im Modelshelf ==="
find ~/Modelshelf -type f -name "*Qwen3*gguf" -o -name "*qwen3*gguf" | sort

# Optional: Zeige auch die Dateigrößen an, um die beste Wahl zu treffen
echo ""
echo "=== Details (Name und Größe) ==="
find ~/Modelshelf -type f \( -name "*Qwen3*gguf" -o -name "*qwen3*gguf" \) -exec ls -lh {} \; | awk '{print $9, $5}'
```

*Erwartete Ausgabe:* Eine Liste von Dateien wie `Qwen3.6-35B-A3B-UD-Q4_K_M.gguf` oder `Qwen3-Coder-Next-Q8_0.gguf`.

**Wähle die Datei aus**, die du nutzen möchtest (z. B. die mit `Q4_K_M` für beste Balance oder `Q5_K_M` für höhere Qualität). Merke dir den Dateinamen für den nächsten Schritt.

> **Tipp für AMD iGPU:** Die Radeon 780M teilt sich den RAM mit dem System. Mit 130 GB RAM ist alles möglich, aber empfehlenswert:
> - **Q4_K_M** (~19 GB) — gute Balance zwischen Qualität und Performance
> - **Q4_K_XL** (~21 GB) — höhere Qualität, etwas langsamer
> - **Q5_K_M** (~23 GB) — beste Qualität, noch verfügbar

---

## Schritt 4: Modell von Modelshelf nach OpenMonoAI kopieren

Ersetze `<DEIN_GEWUENSCHTER_DATEINAME.gguf>` durch den exakten Dateinamen aus der Liste oben (z. B. `Qwen3.6-35B-A3B-UD-Q4_K_M.gguf`).

```bash
# Automatische Suche nach Q4_K_M Modell (empfohlen für AMD iGPU)
SOURCE_FILE=$(find ~/Modelshelf -type f -name "*Qwen3*gguf" | grep "Q4_K_M" | head -n 1)

if [ -z "$SOURCE_FILE" ]; then
    echo "Kein Q4_K_M Modell gefunden — suche nach beliebigem Qwen3.6 GGUF..."
    SOURCE_FILE=$(find ~/Modelshelf -type f \( -name "*Qwen3*gguf" -o -name "*qwen3*gguf" \) | head -n 1)
fi

if [ -z "$SOURCE_FILE" ]; then
    echo "Fehler: Keine passende Datei gefunden. Bitte manuell den Pfad angeben."
    echo "Beispiel-Befehl: cp ~/Modelshelf/Pfad/zum/Datei.gguf /workspace/openmono.ai/models/"
    exit 1
fi

echo "Kopiere Modell: $SOURCE_FILE"
cp "$SOURCE_FILE" /workspace/openmono.ai/models/

# Bestätige den Kopievorgang
ls -lh /workspace/openmono.ai/models/
```

*Falls du mehrere Modelle hast und ein spezifisches auswählen willst:*
```bash
# Manuelle Auswahl (ersetze den Pfad manuell)
cp ~/Modelshelf/dein-exakter-dateiname.gguf /workspace/openmono.ai/models/
```

---

## Schritt 5: Umgebungsvariablen (.env) aktualisieren

Bearbeite die `.env`-Datei, damit Docker das kopierte Modell erkennt.

```bash
cd /workspace/openmono.ai/docker
nano .env
```

Aktualisiere folgende Zeilen (ersetze `<DATEINAME>` durch den Namen der kopierten Datei):

```bash
# Der EXAKTE Dateiname im Ordner /workspace/openmono.ai/models/
MODEL_NAME=<DATEINAME>.gguf

# Beispiel: MODEL_NAME=Qwen3.6-35B-A3B-UD-Q4_K_M.gguf
MODEL_ALIAS=qwen3.6-coder-mtp

# Vision deaktivieren (Qwen3.6-35B-A3B ist textbasiert)
MODEL_MMPROJ=
OPENMONO_VISION_ENABLED=0

# Kontextgröße für AMD iGPU mit 130 GB RAM
CTX_SIZE=196608

LLAMA_PORT=7474
```

Speichern (`Strg+O`, `Enter`) und Beenden (`Strg+X`).

---

## Schritt 6: Docker Compose prüfen (AMD Vulkan Konfiguration)

Stelle sicher, dass deine `docker-compose.override.yml` die AMD-Vulkan-Konfiguration verwendet. Prüfe den Inhalt:

```bash
cat /workspace/openmono.ai/docker/docker-compose.override.yml
```

**Erwartet wird folgende Konfiguration für AMD iGPU:**

```yaml
# AMD Radeon iGPU / Vulkan configuration
services:
  llama-server:
    image: ghcr.io/ggml-org/llama.cpp:server-vulkan
    devices:
      - /dev/dri:/dev/dri
    group_add:
      - "44"
      - "109"
    volumes:
      - ~/openmono.ai/models:/models
    ports:
      - "${LLAMA_PORT:-7474}:${LLAMA_PORT:-7474}"
    shm_size: "16gb"
    environment:
      - GGML_VULKAN_DEVICE=0
      - RADV_PERFTEST=compute
      - LD_LIBRARY_PATH=/app
    command: >
      --model /models/${MODEL_NAME}
      --alias ${MODEL_ALIAS:-model}
      --host 0.0.0.0
      --port 7474
      --ctx-size 196608
      --no-mmap
      --threads 8
      --threads-batch 8
      --batch-size 512
      --ubatch-size 256
      -ngl 99
      --n-gpu-layers 99
      --flash-attn on
      --cache-type-k q8_0
      --cache-type-v q8_0
      --parallel 1
      --jinja
      --reasoning off
      --metrics
```

**Falls die Datei nicht stimmt, ersetze sie:**

```bash
cat > /workspace/openmono.ai/docker/docker-compose.override.yml << 'EOF'
# AMD Radeon iGPU / Vulkan configuration (auto-generated)
services:
  llama-server:
    image: ghcr.io/ggml-org/llama.cpp:server-vulkan
    devices:
      - /dev/dri:/dev/dri
    group_add:
      - "44"
      - "109"
    volumes:
      - ~/openmono.ai/models:/models
    ports:
      - "${LLAMA_PORT:-7474}:${LLAMA_PORT:-7474}"
    shm_size: "16gb"
    environment:
      - GGML_VULKAN_DEVICE=0
      - RADV_PERFTEST=compute
      - LD_LIBRARY_PATH=/app
    command: >
      --model /models/${MODEL_NAME}
      --alias ${MODEL_ALIAS:-model}
      --host 0.0.0.0
      --port 7474
      --ctx-size 196608
      --no-mmap
      --threads 8
      --threads-batch 8
      --batch-size 512
      --ubatch-size 256
      -ngl 99
      --n-gpu-layers 99
      --flash-attn on
      --cache-type-k q8_0
      --cache-type-v q8_0
      --parallel 1
      --jinja
      --reasoning off
      --metrics
EOF
```

---

## Schritt 7: Docker Container starten

```bash
cd /workspace/openmono.ai/docker

# Stoppe alte Container
docker compose down

# Starte den llama-server
docker compose up -d llama-server
```

---

## Schritt 8: Erfolg prüfen

```bash
# Logs anzeigen (drücke Strg+C zum Beenden)
docker compose logs -f llama-server
```

**Suche nach:**
- ✅ `model loaded` — Modell erfolgreich geladen
- ✅ `success` — Server bereit
- ❌ `error` oder `OOM` — Modell zu groß für verfügbaren RAM

### Bei OOM-Fehlern (Out of Memory)

Wenn der Container mit "Out of Memory" abstürzt, ist das Modell zu groß. Versuche eine kleinere Quantisierung:

1. Wähle ein **Q3_K_M** oder **IQ3_XXS** Modell aus deinem Modelshelf
2. Kopiere es nach `/workspace/openmono.ai/models/` (Schritt 4)
3. Aktualisiere `MODEL_NAME` in `.env` (Schritt 5)
4. Starte neu: `docker compose up -d llama-server`

---

## Zusammenfassung aller Schritte

| Schritt | Aktion | Befehl |
|---------|--------|--------|
| 1 | AMD Kernel-Optimierung + Neustart | `sudo update-grub && sudo reboot` |
| 2 | Verzeichnis prüfen | `mkdir -p /workspace/openmono.ai/models` |
| 3 | Modelle auflisten | `find ~/Modelshelf -name "*Qwen3*gguf"` |
| 4 | Modell kopieren | `cp ~/Modelshelf/*.gguf /workspace/openmono.ai/models/` |
| 5 | .env aktualisieren | `nano docker/.env` |
| 6 | Docker Compose prüfen | `cat docker/docker-compose.override.yml` |
| 7 | Container starten | `docker compose up -d llama-server` |
| 8 | Logs prüfen | `docker compose logs -f llama-server` |

---

## Tuning-Optionen (optional)

Sollte die Performance nicht optimal sein, kannst du folgende Parameter in `docker-compose.override.yml` anpassen:

| Parameter | Beschreibung | Standard | Empfehlung |
|-----------|-------------|----------|------------|
| `--ctx-size` | Kontextgröße (Tokens) | 196608 | Halbiere bei OOM |
| `--cache-type-k/v` | KV-Cache Quantisierung | q8_0 | q4_0 spart RAM |
| `--n-gpu-layers` | Schichten auf GPU | 99 (alle) | Reduziere für CPU-Hilfe |
| `--threads` | CPU Threads | 8 | Passt automatisch an |
| `shm_size` | Shared Memory | 16gb | Erhöhe bei OOM |

---

*Diese Anleitung wurde erstellt für: AMD Ryzen 9 PRO 8945HS / Radeon 780M / Linux Mint 22.3 / 130 GB RAM*
