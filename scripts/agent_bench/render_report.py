#!/usr/bin/env python3
"""Render the agentic-coding benchmark JSONL into an HTML report.

Usage: render_report.py [--in results.jsonl] [--out report.html]
"""
import argparse
import json
import statistics
from collections import defaultdict
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent.parent

TASKS = {
    "t1_bugfix": ("Bugfix", "Root-Cause im geteilten Modul, Symptom nur auf einem Pfad gemeldet"),
    "t2_feature_test": ("Feature + Test", "parse_duration() bauen, eigene Unit-Tests schreiben und laufen lassen"),
    "t3_refactor": ("Refactoring", "Drei Copy-Paste-Exporter entdoppeln, Ausgabe byte-identisch"),
    "t4_context7": ("Context7-Doku", "pydantic v1 -> v2 portieren, aktuelle API per Context7 nachschlagen"),
    "t5_cli_feature": ("CLI-Feature", "stats-Subcommand über mehrere Dateien verdrahten"),
}
TASK_ORDER = list(TASKS)

# Speed + HumanEval pass@1 from the 14.07. model benchmark, for the "raw vs. agentic" contrast.
BASELINE = {
    "Qwen3.6-35B-A3B-MTP-UD-Q4_K_XL": (37.0, "8/8"),
    "Qwen3-Coder-30B-A3B-Instruct-UD-Q4_K_XL": (33.4, "8/8"),
    "ornith-1.0-35b-Q4_K_M": (28.3, "8/8"),
    "Qwen-AgentWorld-35B-A3B-UD-IQ3_S": (26.1, "7/8"),
}

# Run-specific prose. Defaults describe the 14.07. first run (pre-ACP-fix);
# pass --fazit/--execnote to render a different run without touching this file.
EXECNOTE = """<strong>Die wichtigste Zahl im ganzen Bericht.</strong>
  „ausgeführt / angefordert“ zählt aus dem Agenten-Log, wie viele der vom Modell angeforderten Tool-Calls
  tatsächlich gelaufen sind (<code class="inline">SSE tool_call:</code> gegen <code class="inline">Tool executing:</code>).
  <strong>Rund die Hälfte aller Tool-Calls wird nie ausgeführt</strong> — bei jedem Modell. Das ist kein
  Modellfehler, sondern der ACP-Permission-Handshake (siehe Fazit)."""

FAZIT = """
<p><strong>ornith-1.0-35b ist im Agenten das beste Modell — und das Produktivmodell ist das schlechteste.</strong>
ornith löst 4 von 5 Aufgaben (97&nbsp;% Grader-Checks). Qwen3.6-35B-A3B-MTP, im Rohbenchmark vom 14.07. auf
Platz&nbsp;1 (37&nbsp;t/s, 8/8 HumanEval), löst im Agenten nur 2 von 5. Rohleistung sagt Agentenleistung nicht
vorher: alle vier Modelle haben 8/8 bzw. 7/8 auf HumanEval — im Agenten liegen sie zwischen 2/5 und 4/5.</p>

<p><strong>Der wahre Engpass ist nicht das Modell, sondern der ACP-Permission-Handshake.</strong> Über alle
Modelle hinweg wird <em>jeder zweite Tool-Call nie ausgeführt</em> (Ausführungsrate 47–60&nbsp;%). Grund: In der
ACP-Schleife führt ein erteiltes „allow“ das Tool <em>nicht</em> aus. Der Agent hängt stattdessen
<code class="inline">Permission granted by user. Re-issue the tool call to execute.</code> an und erwartet, dass das
Modell den <em>identischen</em> Call wiederholt — die Freigabe wird unter dem exakten Kommandotext gecacht
(<code class="inline">Bash|Execute: ls -la /workspace/</code>). Wiederholt das Modell den Call auch nur minimal
verändert, ist es eine neue Freigabe, und wieder passiert nichts. In der TUI gibt es dieses Problem nicht; es
trifft nur den ACP-/Headless-Pfad — also die VS-Code-Extension.</p>

<p><strong>Genau daran stirbt das MTP-Modell.</strong> In <code>t1_bugfix</code> schreibt es
<code>reproduce_bug.py</code> (Freigabe erteilt, Datei nie geschrieben), führt es aus, bekommt „Exit code: 2“,
und dreht dann 20 Calls lang im Kreis mit <code>ls</code>, <code>pwd</code>, <code>echo hello</code> — jedes Mal
minimal anders formuliert, jedes Mal ohne Ausführung. Das Modell debuggt ein Dateisystem, das es selbst nie
beschrieben hat. Am Ende meldet es Erfolg; der Workspace ist unverändert. <strong>Alle neun Fehlschläge in
diesem Lauf haben einen leeren <code>git diff</code></strong> — kein einziges Modell hat je falschen Code
geschrieben, sie haben schlicht gar nichts geschrieben.</p>

<p><strong>Die zweite Falle ist der Plan-Modus.</strong> Fünf der neun Fehlschläge folgen dem Muster
<code>EnterPlanMode → TodoWrite → ExitPlanMode → fertig</code>: Das Modell legt einen korrekten Plan vor und
beendet den Turn, um sich die Freigabe abzuholen. Ein Mensch würde jetzt „ja, mach“ sagen — der Benchmark
schickt keine zweite Nachricht. Das ist teils Messartefakt, teils echte Eigenschaft: Qwen3-Coder und AgentWorld
gehen deutlich häufiger in den Plan-Modus und bleiben dort stehen, ornith fängt einfach an zu arbeiten.</p>

<p><strong>Context7 wird nur von zwei Modellen wirklich benutzt.</strong> ornith (10 Calls) und AgentWorld
(9 Calls) schlagen die pydantic-v2-API tatsächlich nach und lösen <code>t4</code>. MTP und Qwen3-Coder rufen
Context7 <em>kein einziges Mal</em> auf; Qwen3-Coder rät die v2-API korrekt aus dem Gedächtnis und besteht
trotzdem, MTP scheitert. AgentWorld zeigt hier seine Trainingsprägung fürs Tool-Use: die mit Abstand beste
Ausführungsrate (60&nbsp;%) und nur 4 Tool-Fehler bei 75 Calls — es ist das <em>sauberste</em> Modell am Werkzeug,
verliert aber Aufgaben, weil es zu gern im Plan-Modus stehenbleibt.</p>

<p class="warn"><strong>Empfehlung.</strong> Für Agent-Betrieb heute: <code>ornith-1.0-35b-Q4_K_M</code>.
Für Chat und Rohdurchsatz bleibt <code>Qwen3.6-35B-A3B-MTP</code> vorn. Vor allem aber: den ACP-Handshake
reparieren — ein erteiltes „allow“ muss den anhängigen Tool-Call <em>ausführen</em>, statt das Modell zur
wortgleichen Wiederholung aufzufordern. Das ist ein Bug in
<code>AcpTurnRunner.AppendSyntheticToolMessages</code>, und er kostet jeden Agenten-Lauf rund die Hälfte
seiner Arbeit. Danach lohnt ein zweiter Durchlauf dieses Benchmarks — die Rangfolge dürfte sich deutlich
verschieben.</p>
"""


def de(x, nd=1):
    """German decimal comma."""
    return f"{x:,.{nd}f}".replace(",", " ").replace(".", ",")


def thou(n):
    """Thousands separator (thin space), no decimals."""
    return f"{n:,}".replace(",", "\u2009")


def esc(s):
    return (str(s).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;"))


def load(path):
    rows = [json.loads(l) for l in Path(path).read_text().splitlines() if l.strip()]
    by_model = defaultdict(dict)
    for r in rows:
        by_model[r["model"]][r["task"]] = r
    return by_model


def agg(runs):
    """runs: {task: row} for one model."""
    rs = list(runs.values())
    solved = sum(1 for r in rs if r.get("passed"))
    return dict(
        solved=solved,
        n=len(rs),
        score=statistics.mean([r.get("score", 0) for r in rs]) if rs else 0,
        wall=sum(r.get("wall_s", 0) for r in rs),
        llm=sum(r.get("assistant_msgs", 0) for r in rs),
        tools=sum(r.get("tool_calls", 0) for r in rs),
        fails=sum(r.get("tool_fail", 0) for r in rs),
        perms=sum(r.get("permissions", 0) for r in rs),
        gen=sum(r.get("gen_tokens", 0) for r in rs),
        prompt=sum(r.get("prompt_tokens", 0) for r in rs),
        tok_s=statistics.mean([r["gen_tok_s"] for r in rs if r.get("gen_tok_s")]) or 0,
        ctx7=sum(r.get("context7_used", 0) for r in rs),
    )


def fail_rate(a):
    return 100 * a["fails"] / a["tools"] if a["tools"] else 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="inp", default=str(REPO / "docs" / "agent-bench-results-2026-07-14.jsonl"))
    ap.add_argument("--out", default=str(REPO / "docs" / "openmono-agent-benchmark-2026-07-14.html"))
    ap.add_argument("--execrate", default=str(REPO / "docs" / "agent-bench-execrate-2026-07-14.json"))
    ap.add_argument("--fazit", default="", help="HTML file replacing the built-in Fazit")
    ap.add_argument("--execnote", default="", help="HTML file replacing the built-in exec-rate note")
    ap.add_argument("--tag", default="", help="suffix for title/eyebrow, e.g. 'Lauf 2 · nach ACP-Fix'")
    args = ap.parse_args()

    by_model = load(args.inp)
    stats = {m: agg(runs) for m, runs in by_model.items()}
    execrate = json.loads(Path(args.execrate).read_text()) if Path(args.execrate).exists() else {}
    # rank: solved desc, then avg score desc, then wall asc
    ranked = sorted(stats, key=lambda m: (-stats[m]["solved"], -stats[m]["score"], stats[m]["wall"]))

    # ---- table 1: overall
    t1 = []
    for i, m in enumerate(ranked, 1):
        a = stats[m]
        speed, he = BASELINE.get(m, (0, "—"))
        cls = ' class="top"' if i <= 1 else ""
        sc = "q100" if a["solved"] == a["n"] else ("qlow" if a["solved"] <= a["n"] // 2 else "")
        t1.append(
            f'<tr{cls}><td>{i}</td><td><code>{esc(m)}</code></td>'
            f'<td class="num {sc}">{a["solved"]}/{a["n"]}</td>'
            f'<td class="num">{de(100 * a["score"])} %</td>'
            f'<td class="num">{de(a["wall"] / 60)}</td>'
            f'<td class="num">{a["llm"]}</td>'
            f'<td class="num">{a["tools"]}</td>'
            f'<td class="num">{a["fails"]} · {de(fail_rate(a))} %</td>'
            f'<td class="num">{thou(a["gen"])}</td>'
            f'<td class="num">{de(a["tok_s"])}</td>'
            f'<td class="num">{he} · {de(speed)}</td></tr>')

    # ---- table 2: per task
    t2 = []
    for m in ranked:
        cells = []
        for t in TASK_ORDER:
            r = by_model[m].get(t)
            if not r:
                cells.append('<td class="num">—</td>')
                continue
            ok = r.get("passed")
            mark = "✔" if ok else "✘"
            klass = "q100" if ok else "qlow"
            extra = "" if r.get("status") == "done" else f' <span class="tag">{esc(r.get("status"))}</span>'
            cells.append(f'<td class="num {klass}">{mark} {de(r.get("wall_s", 0) / 60)} min{extra}</td>')
        t2.append(f'<tr><td><code>{esc(m)}</code></td>' + "".join(cells) + "</tr>")

    # ---- table 3: tool behaviour
    t3 = []
    for m in ranked:
        a = stats[m]
        failed = defaultdict(int)
        used = defaultdict(int)
        for r in by_model[m].values():
            for k, v in (r.get("failed_tools") or {}).items():
                failed[k] += v
            for k, v in (r.get("tools") or {}).items():
                used[k] += v
        top = ", ".join(f"{k} {v}" for k, v in sorted(used.items(), key=lambda x: -x[1])[:4])
        bad = ", ".join(f"{k} {v}" for k, v in sorted(failed.items(), key=lambda x: -x[1])) or "—"
        e = execrate.get(m, {})
        rate = e.get("exec_rate")
        rcls = "q100" if (rate or 0) >= 60 else "qlow"
        rtxt = (f'{e["executed"]}/{e["intents"]} · {rate} %' if rate is not None else "—")
        t3.append(
            f'<tr><td><code>{esc(m)}</code></td>'
            f'<td class="num {rcls}">{rtxt}</td>'
            f'<td class="num">{a["perms"]}</td>'
            f'<td class="num">{a["ctx7"]}</td>'
            f'<td><code>{esc(top)}</code></td>'
            f'<td><code>{esc(bad)}</code></td></tr>')

    # ---- table 4: grader detail for failures
    t4 = []
    for m in ranked:
        for t in TASK_ORDER:
            r = by_model[m].get(t)
            if not r or r.get("passed"):
                continue
            t4.append(
                f'<tr><td><code>{esc(m)}</code></td><td>{esc(TASKS[t][0])}</td>'
                f'<td class="num">{de(100 * r.get("score", 0))} %</td>'
                f'<td>{esc((r.get("grade_detail") or r.get("error") or "")[:220])}</td></tr>')
    if not t4:
        t4.append('<tr><td colspan="4">Keine Fehlschläge.</td></tr>')

    task_rows = "".join(
        f'<tr><td><code>{t}</code></td><td>{esc(TASKS[t][0])}</td><td>{esc(TASKS[t][1])}</td></tr>'
        for t in TASK_ORDER)

    total_tasks = sum(stats[m]["n"] for m in ranked)
    total_solved = sum(stats[m]["solved"] for m in ranked)
    total_min = sum(stats[m]["wall"] for m in ranked) / 60

    html = TEMPLATE.format(
        chips=(f'<span class="chip">Modelle <strong>{len(ranked)}</strong></span>'
               f'<span class="chip">Aufgaben <strong>{len(TASK_ORDER)} × agentisch</strong></span>'
               f'<span class="chip">Läufe <strong>{total_tasks}</strong></span>'
               f'<span class="chip">gelöst <strong>{total_solved}/{total_tasks}</strong></span>'
               f'<span class="chip">Agent-Zeit <strong>{de(total_min, 0)} min</strong></span>'),
        t1="".join(t1), t2="".join(t2), t3="".join(t3), t4="".join(t4),
        task_rows=task_rows,
        task_head="".join(f'<th class="num">{esc(TASKS[t][0])}</th>' for t in TASK_ORDER),
        fazit=Path(args.fazit).read_text() if args.fazit else FAZIT,
        execnote=Path(args.execnote).read_text() if args.execnote else EXECNOTE,
        tag=f" · {args.tag}" if args.tag else "",
    )
    Path(args.out).write_text(html, encoding="utf-8")
    print(f"wrote {args.out}")


TEMPLATE = """<title>OpenMono · Agentic-Coding-Benchmark 14.07.2026{tag}</title>
<style>
  :root {{
    --bg: #f6f7f2; --surface: #ffffff; --ink: #1d2418; --ink-soft: #55604b; --line: #dde2d3;
    --accent: #3c7a1c; --accent-ink: #2e5f15; --warn-bg: #faf3e0; --warn-line: #e3cf9a;
    --warn-ink: #6e5410; --chip-bg: #eaeee0;
  }}
  @media (prefers-color-scheme: dark) {{
    :root {{
      --bg: #13160f; --surface: #1a1f13; --ink: #e4ead8; --ink-soft: #9aa688;
      --line: #2c3421; --accent: #a3ff66; --accent-ink: #b5ff85;
      --warn-bg: #241d0c; --warn-line: #4a3c14; --warn-ink: #e0b64d; --chip-bg: #232a18;
    }}
  }}
  :root[data-theme="dark"] {{
    --bg: #13160f; --surface: #1a1f13; --ink: #e4ead8; --ink-soft: #9aa688;
    --line: #2c3421; --accent: #a3ff66; --accent-ink: #b5ff85;
    --warn-bg: #241d0c; --warn-line: #4a3c14; --warn-ink: #e0b64d; --chip-bg: #232a18;
  }}
  :root[data-theme="light"] {{
    --bg: #f6f7f2; --surface: #ffffff; --ink: #1d2418; --ink-soft: #55604b;
    --line: #dde2d3; --accent: #3c7a1c; --accent-ink: #2e5f15;
    --warn-bg: #faf3e0; --warn-line: #e3cf9a; --warn-ink: #6e5410; --chip-bg: #eaeee0;
  }}
  * {{ box-sizing: border-box; }}
  body {{ margin: 0; background: var(--bg); color: var(--ink);
    font-family: system-ui, "Segoe UI", Roboto, sans-serif; font-size: 16px; line-height: 1.6; }}
  .page {{ max-width: 980px; margin: 0 auto; padding: 48px 24px 80px; }}
  .eyebrow {{ font-family: ui-monospace, Menlo, Consolas, monospace; font-size: 12px;
    letter-spacing: 0.14em; text-transform: uppercase; color: var(--accent); margin: 0 0 12px; }}
  h1 {{ font-family: ui-monospace, Menlo, Consolas, monospace; font-size: clamp(26px, 5vw, 34px);
    font-weight: 700; letter-spacing: -0.01em; line-height: 1.2; margin: 0 0 10px; text-wrap: balance; }}
  .lede {{ color: var(--ink-soft); margin: 0 0 20px; max-width: 68ch; }}
  .chips {{ display: flex; flex-wrap: wrap; gap: 8px; margin: 0 0 8px; }}
  .chip {{ font-family: ui-monospace, Menlo, Consolas, monospace; font-size: 12px; padding: 4px 10px;
    border-radius: 999px; background: var(--chip-bg); color: var(--ink-soft);
    border: 1px solid var(--line); white-space: nowrap; }}
  .chip strong {{ color: var(--ink); font-weight: 600; }}
  hr.rule {{ border: 0; border-top: 1px solid var(--line); margin: 36px 0; }}
  h3.section {{ font-family: ui-monospace, Menlo, Consolas, monospace; font-size: 13px;
    letter-spacing: 0.12em; text-transform: uppercase; color: var(--ink-soft); margin: 0 0 14px; }}
  .tablewrap {{ overflow-x: auto; border: 1px solid var(--line); border-radius: 10px; }}
  table {{ border-collapse: collapse; width: 100%; font-size: 14px; background: var(--surface); }}
  th, td {{ text-align: left; padding: 7px 12px; border-top: 1px solid var(--line); vertical-align: top; }}
  td.num, th.num {{ text-align: right; font-variant-numeric: tabular-nums;
    font-family: ui-monospace, Menlo, Consolas, monospace; font-size: 13px; }}
  thead th {{ border-top: 0; font-family: ui-monospace, Menlo, Consolas, monospace; font-size: 12px;
    letter-spacing: 0.08em; text-transform: uppercase; color: var(--ink-soft); font-weight: 600; }}
  td code {{ font-family: ui-monospace, Menlo, Consolas, monospace; font-size: 12.5px;
    color: var(--accent-ink); }}
  tr.top td {{ font-weight: 600; }}
  .tag {{ font-family: ui-monospace, Menlo, Consolas, monospace; font-size: 11px; padding: 1px 7px;
    border-radius: 999px; background: var(--chip-bg); border: 1px solid var(--line);
    color: var(--warn-ink); white-space: nowrap; }}
  td.q100 {{ color: var(--accent-ink); font-weight: 600; }}
  td.qlow {{ color: var(--warn-ink); }}
  .note {{ background: var(--surface); border: 1px solid var(--line); border-left: 3px solid var(--accent);
    border-radius: 8px; padding: 10px 14px; font-size: 14.5px; color: var(--ink-soft); margin: 0 0 12px; }}
  .note strong {{ color: var(--ink); }}
  .warn {{ background: var(--warn-bg); border: 1px solid var(--warn-line);
    border-left: 3px solid var(--warn-ink); border-radius: 8px; padding: 10px 14px;
    font-size: 14.5px; color: var(--warn-ink); margin: 0 0 12px; }}
  code.inline {{ font-family: ui-monospace, Menlo, Consolas, monospace; font-size: 0.88em;
    background: var(--chip-bg); border: 1px solid var(--line); border-radius: 5px;
    padding: 1px 6px; white-space: nowrap; }}
  .foot {{ margin-top: 36px; font-size: 13px; color: var(--ink-soft);
    font-family: ui-monospace, Menlo, Consolas, monospace; }}
  .fazit p {{ margin: 0 0 12px; }}
  .fazit strong {{ color: var(--ink); }}
</style>

<div class="page">
  <p class="eyebrow">openmono.ai · Agentic-Coding-Benchmark · 14.07.2026{tag}</p>
  <h1>Welches lokale Modell codet im Agenten wirklich am besten?</h1>
  <p class="lede">Der Benchmark vom 14.07. hat Modelle <em>direkt</em> gegen <code class="inline">llama-server</code> gemessen — ein Prompt, eine Antwort. Dieser Lauf misst etwas anderes: die vier besten Modelle arbeiten <strong>im openmono-Agenten</strong> (ACP, headless, alle Tools scharf) an echten Coding-Aufgaben in einem frischen Workspace — lesen Dateien, greppen, patchen, führen Tests aus, holen Doku über Context7. Bestanden ist eine Aufgabe nur, wenn ein <strong>versteckter Grader</strong> den Workspace hinterher ausführt und alle Checks grün sind. Den Grader sieht das Modell nie.</p>
  <div class="chips">{chips}</div>

  <hr class="rule">

  <h3 class="section">Die fünf Aufgaben</h3>
  <div class="tablewrap">
    <table>
      <thead><tr><th>ID</th><th>Typ</th><th>Was das Modell tun muss</th></tr></thead>
      <tbody>{task_rows}</tbody>
    </table>
  </div>
  <p class="note" style="margin-top:12px"><strong>Aufbau:</strong> pro Modell und Aufgabe ein frischer, git-initialisierter Workspace mit <code class="inline">OPENMONO.md</code> (Ponytail- und Caveman-Sektionen aktiv). Der Agent läuft headless im Container (<code class="inline">OPENMONO_ACP_ONLY=1</code>) gegen die Vulkan-Lane. Jede Permission-Anfrage wird über die Turn-API freigegeben — genau wie ein Mensch, der auf „allow“ klickt.</p>

  <hr class="rule">

  <h3 class="section">Gesamtwertung</h3>
  <div class="tablewrap">
    <table>
      <thead>
        <tr><th>#</th><th>Modell</th><th class="num">gelöst</th><th class="num">Ø Score</th>
        <th class="num">Zeit (min)</th><th class="num">LLM-Calls</th><th class="num">Tool-Calls</th>
        <th class="num">Tool-Fehler</th><th class="num">Gen-Tokens</th><th class="num">Ø t/s</th>
        <th class="num">HumanEval · t/s (14.07.)</th></tr>
      </thead>
      <tbody>{t1}</tbody>
    </table>
  </div>
  <p class="note" style="margin-top:12px"><strong>Ø Score</strong> ist der Anteil bestandener Grader-Checks (Teilpunkte), <strong>gelöst</strong> zählt nur Aufgaben, bei denen <em>alle</em> Checks grün sind. <strong>Tool-Fehler</strong> = Tool-Calls, die der Agent als fehlgeschlagen zurückmeldet (falscher Pfad, kaputter Patch, fehlgeschlagenes Kommando).</p>

  <hr class="rule">

  <h3 class="section">Ergebnis pro Aufgabe (✔/✘ · Wanduhrzeit)</h3>
  <div class="tablewrap">
    <table>
      <thead><tr><th>Modell</th>{task_head}</tr></thead>
      <tbody>{t2}</tbody>
    </table>
  </div>

  <hr class="rule">

  <h3 class="section">Tool-Verhalten · Ausführungsrate</h3>
  <div class="tablewrap">
    <table>
      <thead><tr><th>Modell</th><th class="num">ausgeführt / angefordert</th><th class="num">Permissions</th>
      <th class="num">Context7-Calls</th><th>meistgenutzte Tools</th><th>fehlgeschlagene Tools</th></tr></thead>
      <tbody>{t3}</tbody>
    </table>
  </div>
  <p class="warn" style="margin-top:12px">{execnote}</p>

  <hr class="rule">

  <h3 class="section">Woran die Fehlschläge scheiterten</h3>
  <div class="tablewrap">
    <table>
      <thead><tr><th>Modell</th><th>Aufgabe</th><th class="num">Score</th><th>Grader-Befund</th></tr></thead>
      <tbody>{t4}</tbody>
    </table>
  </div>

  <hr class="rule">

  <h3 class="section">Fazit</h3>
  <div class="fazit">{fazit}</div>

  <p class="foot">Harness: <code>scripts/agent_bench/run_agent_bench.py</code> · Aufgaben und Grader: <code>scripts/agent_bench/tasks/</code> · Rohdaten: <code>docs/agent-bench-results-2026-07-14.jsonl</code></p>
</div>
"""

if __name__ == "__main__":
    main()
