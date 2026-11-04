# Rooms

A room is a shared thread where Spencer and several bots talk (plan 7.14). Rooms are defined in the team file and their messages live in roster.db.

```yaml
rooms:
  - { name: planning, team: Core, members: [head-honcho, Scout, Builder], responder: lead, brief: "Release planning." }
```

## Who answers

| Responder | Rule |
|---|---|
| mentions (default) | Only the bots named with @Name answer; nobody answers a message that names nobody |
| lead | The room's team lead answers (or head-honcho, or the first member) and may hand out work |
| everyone | Each member answers once, in order |

Each member turn is a root assignment on that bot's room session, so the runtime's limits, audit and approvals all apply. The bot gets the room brief and the messages new to it as data, and its reply is posted for it. A reply that names other members with @Name starts their turns.

## Limits

| Limit | Value |
|---|---|
| Bot posts per message from Spencer | 6 |
| Chained mentions between bots | 3 hops |
| Room turn | 10 minutes |
| Room queue | one member turn at a time, in order |
| Bot posts per hour | `max_posts_per_hour` (30) |

A turn started by Spencer's message runs at the bot's own level. A turn started by another bot's mention runs at the ask level, so text one bot writes cannot make another bot change anything without approval. Room turns do not feed memory capture.

## Using a room

```bash
oma-bot room list
oma-bot room post planning "@Scout what changed since Monday?"
oma-bot room show planning
```

The web view has the same thread on its Rooms page. Bots that are members can use `room_post` and `room_read`.
