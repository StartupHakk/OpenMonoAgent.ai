# Coding-Tools-Recherche — Juli 2026

Recherche zu Coding-Tools/Plugins für **Claude Code (Terminal)** und den **openmono-Agenten** (lokale Qwen3.6-Modelle), angewandt auf die vier Projekte. Quellen: GitHub, dev.to, Hacker News, Scott Logic, Reddit-Auswertungen (r/LocalLLaMA, r/ClaudeAI via Aggregatoren), Plugin-Rankings 2026. Direkte X-Suche ist ohne Login nicht zuverässig möglich — die X-Diskussionen sind über die zitierten Artikel abgedeckt.

---

## 1. Projekt-Analyse

| Projekt | Stack | Relevante Tool-Lücken |
|---|---|---|
| `engine` (2D-Browser-Gameengine) | TypeScript, WebGPU, pnpm-Monorepo, Vite 8, Vitest, Playwright, knip, Meta-Harness v2 | Aktuelle API-Docs (WebGPU/WGSL ändert sich schnell → Halluzinationsgefahr), Code-Minimalismus |
| `Off-Gritt-Clean` (App) | React Native, Jest, ESLint, Husky, eigenes Brand-Voice-/Design-System | Aktuelle RN-Docs, Disziplin gegen Over-Engineering |
| `openmono.ai` | C#/.NET 10, Docker, llama.cpp (Vulkan-Lane), eigener Coding-Agent mit MCP-Support | MCP-Server im Agent-Image (fehlte Node.js), Prompt-Disziplin für lokale Modelle |
| `paket-meta-harness-v2` | Doku/Skills-Scaffold | Keine — ist selbst ein Harness-Projekt |

**Wichtigster Befund:** Der openmono-Agent hat bereits volle MCP-Unterstützung (`mcp_servers` in `~/.openmono/settings.json`) und lädt `OPENMONO.md` als Projekt-Instruktionen. Damit sind **prompt-basierte Skills und MCP-Server die beiden Integrationswege** — alles, was rein auf Prompts basiert (Ponytail, Caveman), funktioniert in beiden Welten.

---

## 2. Caveman — geprüft ✅ empfehlenswert (mit Einschränkung)

- **Repo:** [JuliusBrussee/caveman](https://github.com/juliusbrussee/caveman) — ~89.000 Stars, 5.100 Forks, MIT, aktive Entwicklung (v1.9.1, Juli 2026)
- **Was es ist:** Rein prompt-basierter Skill, der die Agent-*Antworten* radikal kürzt („why use many token when few token do trick"). Code, Befehle und Fehlermeldungen werden nie verändert — nur Prosa. Stufen: `lite`/`full`/`ultra`, abschaltbar per `/caveman off`.
- **Reputation:** Sehr gut. Wird in Rankings als „bisschen Gimmick, spart aber real Tokens" eingeordnet. Das Repo ist selbst ehrlich: ~65 % weniger **Output**-Tokens, aber +1–1,5k **Input**-Tokens pro Turn Overhead; bei ohnehin knappen Antworten kann der Netto-Effekt klein oder negativ sein.
- **Für dich konkret:**
  - **Claude Code (Abo):** weniger Output = spürbar mehr Usage aus dem Plan.
  - **openmono/Qwen lokal:** weniger Output-Tokens = **schnellere Antworten** (bei deinen 37 t/s auf Qwen3.6-35B direkt spürbar). Kostenlos ist es eh — hier zählt nur die Zeit.
  - Nachteil: Berichte werden knapper/englischer Telegrammstil. Wenn dich das stört: `/caveman off`.
- **Achtung Verwechslung:** `caveman-code` (getcaveman.dev, gleicher Autor) ist ein **eigener Terminal-Agent** — nicht installiert, du hast mit openmono schon einen. `wilpel/caveman-compression` ist ein drittes, unabhängiges Projekt.

## 3. Ponytail — geprüft ✅ klar empfehlenswert

- **Repo:** [DietrichGebert/ponytail](https://github.com/DietrichGebert/ponytail) — ~82.500 Stars (44k in 9 Tagen), MIT, v4.8.4 (Juni 2026), 15+ Plattformen
- **Was es ist:** „Lazy senior dev"-Ruleset: Vor jedem Code muss der Agent die **Ladder** durchsteigen (Braucht es das? → Schon im Codebase? → Stdlib? → Plattform-Feature? → Vorhandene Dependency? → Einzeiler? → erst dann Minimal-Code). Befehle: `/ponytail lite|full|ultra|off`, `/ponytail-review`, `/ponytail-audit`, `/ponytail-debt`.
- **Reputation:** Breit positiv, und die Kritik wurde vorbildlich behandelt: [Scott Logic](https://blog.scottlogic.com/2026/06/16/ponytail-yagni-and-the-problem-with-prompt-benchmarks.html) zerlegte den ersten Benchmark (Baseline war künstlich geschwätzig) — der Autor akzeptierte das und maß neu mit echten Headless-Agent-Runs auf einem realen Repo: **~54 % weniger Code, ~20 % billiger, ~27 % schneller, 100 % Security-Checks bestanden.** Sicherheits-Guardrails (Validierung, Error-Handling, A11y werden nie übersprungen) sind explizit Teil des Skills.
- **Offene Frage aus der Community** ([dev.to](https://dev.to/yashddesai/ponytail-the-ai-coding-skill-taking-github-by-storm-and-the-one-question-nobodys-answered-yet-46mc)): Respektiert es etablierte **Design-Systeme**? Es könnte z. B. natives `<input type="date">` statt der Projekt-Komponente wählen. → Für **Off-Gritt** relevant: deine CLAUDE.md verlangt schon das Lesen von `docs/design/` — das fängt das ab, trotzdem bei UI-Arbeit im Auge behalten.
- **Passung:** Deckt sich fast 1:1 mit Regel 1 deiner Meta-Harness v2 („Einfachste funktionierende Lösung, keine ungebetenen Abstraktionen") — Ponytail ist die durchsetzbare, portierbare Version davon.
- **Caveat für lokale Modelle:** Instruction-Following-Skills brauchen Modelle, die Anweisungen zuverlässig befolgen. Bei 3B-Modellen inkonsistent; **Qwen3.6-35B-A3B ist groß genug**, aber erwarte etwas weniger Konsequenz als bei Claude.

## 4. Weitere Top-Tools (recherchiert, bewertet)

**Installiert:**
- **Context7 (MCP)** — [Upstash](https://github.com/upstash/context7), in allen 2026er-Rankings top-3 (~349k Installs im offiziellen Verzeichnis). Liefert versionsgenaue, aktuelle Library-Docs in den Kontext statt halluzinierter APIs. **Genau dein Schmerzpunkt**: WebGPU/WGSL (engine), React Native (Off-Gritt), und für Qwen lokal noch wichtiger, weil ältere Trainingsdaten.

**Bewusst NICHT installiert (mit Begründung):**
- **Superpowers** (~752k Installs) — mächtiges Workflow-Paket (Brainstorming, TDD, Subagenten), aber es überlappt und **kollidiert mit deiner Meta-Harness v2** (eigene Gates, Profile, Arbeitsdisziplin). Zwei konkurrierende Workflow-Regelwerke machen Agenten schlechter, nicht besser.
- **Exa Search MCP** — bestes Agent-Web-Search-MCP, aber API-Key + kostenpflichtig; Claude Code hat hier schon WebSearch.
- **GitHub MCP** — `gh` CLI ist bereits da und leichter.
- **Playwright MCP** — engine nutzt Playwright direkt über `pnpm e2e`, und Claude-in-Chrome ist hier schon eingerichtet. Doppelt hält nicht besser (Tool-Budget!).
- **Claude Mem / Memory-Plugins** — Claude Code hat in diesem Setup bereits persistentes Memory.
- **LSP-Plugins (offiziell)** — für TS/C# eine Überlegung wert, wenn dich Typ-Fehler-Feedback-Schleifen nerven; erstmal beobachten. Quellen: [claudefa.st Ranking](https://claudefa.st/blog/tools/mcp-extensions/best-addons), [Composio](https://composio.dev/content/top-claudecode-plugins), [Nimbalyst](https://nimbalyst.com/blog/best-claude-code-mcp-servers/), [buildtolaunch „11 getestet, 4 behalten"](https://buildtolaunch.substack.com/p/best-claude-code-plugins-tested-review).

Merkregel aus allen Rankings 2026: **3–6 gut gewählte Server/Skills schlagen 15** — jedes Tool kostet Kontext-Budget und Latenz.

---

## 5. Was wurde installiert & integriert

**Claude Code (User-Scope, gilt für alle Projekte):**
1. ✅ Plugin `ponytail@ponytail` (Marketplace DietrichGebert/ponytail)
2. ✅ Plugin `caveman@caveman` (Marketplace JuliusBrussee/caveman)
3. ✅ MCP-Server `context7` (`npx -y @upstash/context7-mcp`) — Status: Connected

**openmono-Agent:**
4. ✅ `~/.openmono/settings.json`: `mcp_servers.context7` ergänzt (global, gilt in jedem Projekt; das Verzeichnis wird in den Container gemountet)
5. ✅ `docker/Dockerfile.agent`: `nodejs` + `npm` ins Runtime-Image (nötig, damit npx-MCP-Server im Container starten; Image-Rebuild siehe unten)
6. ✅ `OPENMONO.md` (openmono.ai **und** engine): Sektionen „Minimalism (Ponytail ladder)" und „Output style (Caveman)" — kompakte Prompt-Fassung der beiden Skills, da openmono kein Plugin-System hat, aber OPENMONO.md in jeden System-Prompt lädt. Für lokale Modelle bewusst kurz gehalten (Kontext-Budget).

**Steuerung:** In Claude Code `/ponytail off` bzw. `/caveman off`, falls ein Skill im Weg ist. In openmono die jeweilige Sektion aus OPENMONO.md löschen.

**Hinweis:** Die Änderungen an Dockerfile.agent/OPENMONO.md sind **uncommitted** auf dem Branch `vulkan-lane` — committen, wenn du zufrieden bist.

---

## 6. Empfehlungen / nächste Schritte

1. **Ponytail im engine-Alltag testen** — bei UI-/Komponentenarbeit in Off-Gritt auf die Design-System-Frage achten.
2. **Caveman 1 Woche fahren**, dann entscheiden: bei Claude Code lohnt es über die Usage-Limits, bei openmono über die Antwortzeit. Stört der Stil → `/caveman lite` statt `off`.
3. **Context7 in openmono ausprobieren**: nach dem Image-Rebuild im Agenten fragen „use context7 to check the current WebGPU API for …".
4. Optional später: offizielles **TypeScript-LSP-Plugin** für engine, **security-guidance** für Off-Gritt vor App-Store-Releases.

## Quellen

- https://github.com/DietrichGebert/ponytail · https://github.com/juliusbrussee/caveman
- https://blog.scottlogic.com/2026/06/16/ponytail-yagni-and-the-problem-with-prompt-benchmarks.html
- https://dev.to/yashddesai/ponytail-the-ai-coding-skill-taking-github-by-storm-and-the-one-question-nobodys-answered-yet-46mc
- https://nimbalyst.com/blog/best-claude-code-mcp-servers/ · https://claudefa.st/blog/tools/mcp-extensions/best-addons
- https://composio.dev/content/best-mcp-servers-claude-code-codex · https://buildtolaunch.substack.com/p/best-claude-code-plugins-tested-review
- https://www.kdnuggets.com/top-7-coding-models-you-can-run-locally-in-2026 (r/LocalLLaMA-Auswertung Qwen3.6)
- https://knightli.com/en/2026/06/24/ponytail-ai-agent-coding-plugin-guide/ · https://www.alphamatch.ai/blog/ponytail-ai-coding-skill-2026
