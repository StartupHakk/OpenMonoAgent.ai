# Desktops (optional)

A bot can drive a desktop: a container with Xfce, a browser and the cua-driver MCP server (plan 7.7, phase 7). Desktops are off unless the team file asks (decision B5).

## Modes

```yaml
bots:
  - name: Scout
    desktop: own        # off (default), own, shared or pool
```

| Mode | Desktops | Rule |
|---|---|---|
| own | one per bot, named after it | only that bot uses it |
| shared | one, named `shared` | one bot at a time, with a lease |
| pool | `pool-1` to `pool-N` (`desktops.pool_size`, default 2) | the first free one is leased |

At most 8 desktops in all (`desktops.max`); a team file that needs more is refused. The health bot never gets one.

## Leases and idle stop

- A lease belongs to one bot and one piece of work. It ends when that work ends, or after `desktops.lease_minutes` (30) without a desktop action.
- A desktop with no lease and no action for `desktops.idle_minutes` (20) is stopped. Its home folder survives: `bots/BOT/desktop/` for an own desktop, `desktops/NAME/home/` otherwise.

## How a bot uses it

The bot sees two tools from its desktop server (the roster shim inside the bot container):

- `desktop_list_actions`: what the desktop can do and the arguments.
- `desktop_act`: one action, for example a screenshot, a click or typing.

Inside a turn the roster leases the desktop (starting it when needed) and the shim forwards the action to the desktop's MCP server on the internal desktop network. Each action is an MCP tool call, so it asks for approval like any other; Spencer can add team file auto rules for safe ones (for example `auto_allow: ["mcp__desktop__desktop_list_actions"]`).

A desktop browses only through the egress gate, with its owner's or lease holder's allowlist (none when it is free), and never reaches the gateway, the host or the LAN.

## Watching

noVNC is published on `127.0.0.1:76NN` only, with a view-only password per desktop in `secrets/desktops/NAME.vnc` (0600).

```bash
oma-bot desktops            # list, state, port and lease
oma-bot desktops show NAME  # the noVNC address and the view-only password
oma-bot desktops stop NAME
```

## The image

`docker/desktop/Dockerfile` builds `oma-roster-desktop` from `docker.io/trycua/xfce-cua` pinned by digest and the cua-driver package at a pinned version. Both are required build arguments, so an unpinned desktop is never built:

```bash
docker build -f docker/desktop/Dockerfile \
  --build-arg BASE_DIGEST=sha256:... --build-arg CUA_DRIVER_VERSION=... \
  -t oma-roster-desktop:latest docker/desktop
```

Open items for Spencer: the base image digest, the driver version, the driver's MCP command (`CUA_MCP_COMMAND`), and how the base image takes its VNC view-only password (the start script exports `VNC_VIEW_ONLY_PASSWORD` from the mounted secret).

## Settings (roster.json)

| Key | Default |
|---|---|
| desktops.max | 8 |
| desktops.pool_size | 2 |
| desktops.lease_minutes | 30 |
| desktops.idle_minutes | 20 |
| desktops.memory, desktops.cpu, desktops.pids | 2g, 1.5, 1024 |
| desktops.port_base | 7600 |
| desktops.no_vnc_port, desktops.mcp_port | 6901, 8000 (inside the container) |
| desktops.image | oma-roster-desktop:latest |
