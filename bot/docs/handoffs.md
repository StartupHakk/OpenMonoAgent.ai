# Handoffs (phase 5, M5)

Every piece of work one bot hands to another is one assignment row in roster.db, with one lifecycle and one wake lane, whatever started it: Spencer's message, a routine run, another bot. A person's `oma-bot run` message and every routine run are root assignments, so work handed out during them reports back.

## Lifecycle

```
queued --admit--> running --turn ends--> completed | failed | cancelled
                    |  \--handed out work--> waiting --all children settled, or a child asks--> resume --> running
                    \--question for Spencer--> needs_input --answer--> resume --> running
```

Every status change is one transaction that writes the row and an event in `assignment_events` (append-only, enforced by triggers). `oma-bot audit tree ROOT_ID` replays a tree in order.

## Modes

| Mode | The assignee may | Reply |
|---|---|---|
| work | act within its own tools and the tree's level | resume (the requester runs once more with the results) |
| consult | read and answer only: the turn runs in plan mode and changing tools are gated | resume |
| notify | read it and maybe save a fact; changing tools are gated | none |

## Checks when work is handed out

All refusals are typed (`ancestor`, `depth`, `budget`, `fanout`, `tree_full`, `refused`, `not_found`, `disabled`, `parent_not_running`, `level`, `invalid`); none throws into a turn.

1. The requester's turn is running (a bot can only hand out work from its own current assignment; the roster knows which one from its turn record, not from the arguments).
2. No work back up the chain: the assignee is not the requester or any ancestor.
3. Depth at most 3 (`handoffs.max_depth`).
4. Tree budgets: 200,000 tokens, 30 turns, 2 hours (the resume turn owed after all children settle is never refused for budget).
5. At most 4 children per turn and 16 live assignments per tree.
6. Dedupe: the same brief (case, punctuation and spacing aside) to the same bot from the same parent and mode within 10 minutes joins the first, unless `rework` is set. The same request of 40 characters or more from another requester waits for one answer.
7. Level: the lower of the requester's level and the assignee's profile. A turn below the bot's own profile runs with changing tools gated. Routine and outside origin run at ask.
8. The assignee exists, is enabled, and lists the requesting bot in `accepts_from` (Spencer can always give work to his bots).

## Routing

`assign(to: "Builder")` by name, `to: "lead"` for the requester's team lead (or head-honcho), `to: "auto"` for the team file's keyword `routes:`, falling back to the lead. No model call decides routing.

## Turns

A turn is admitted only when the bot has no roster turn and one of its backends has an open gateway lane. A sleeping bot is woken first. The opening is written by the roster from the stored row (the brief and input are fenced as data, and imitations of the roster's markers are escaped). The resume turn of a parent carries a child's question first, then the results it has not read yet, with what the roster observed (tool calls), so the parent can check claims against evidence.

## Restarts

At service start, a turn that was running when the service stopped:

- had handed out work: it waits for that work, nothing of it runs again;
- had started an outward action (HostExec, Bash, Playbook, Agent or a non-roster MCP tool, seen on the stream, approved by the broker, or marked by the pre tool hook before it ran): it fails `interrupted` and Spencer is told once; it is never repeated;
- otherwise: it reruns once, told to check what it already did; a second cut fails it.

## Bot tools

`assign`, `consult`, `notify`, `assignment_status`, `ask` (Spencer by default, or `to: requester`), `answer` (a helper's question), `cancel` (work you handed out, and everything under it), `report` (one line to Spencer and the lead). `list_bots` shows who accepts work from whom.

## Commands

```
oma-bot run BOT "message" [--mode plan|build] [--wait-done SECONDS]
oma-bot assignments [--all] | show ID | cancel ID [--reason TEXT]
oma-bot audit tree ROOT_ID
oma-bot approvals --answer ID allow --text "the answer"    # answers a bot's question
```

The ten runtime invariants (I1 to I10, see Runtime/Invariants.cs) are checked after every scenario test.
