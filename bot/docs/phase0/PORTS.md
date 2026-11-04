# Port map (phase 0, confirmed from the plan)

| Port | Bind | Use |
|---|---|---|
| 7477 | 127.0.0.1 | Roster API and local web view |
| 7478 | Docker bridge gateway address only (for example 172.17.0.1). Never 127.0.0.1 in bot settings (L3) | Roster gateway |
| 7500 to 7599 | 127.0.0.1:75NN to container 7475 | ACP per bot |
| 7600 to 7699 | 127.0.0.1 | noVNC per desktop |
| 3128 | roster network only | Egress gate |
| ~/.omabot/run/roster.sock | unix 0600 | MCP shim to roster service, one socket per bot under run/bots/<bot>/ |
| ~/.omabot/run/supervisor.sock | unix 0600 | Roster service to supervisor, never mounted in a container |

OMA's own ports are left alone: 7474 inference, 7475 ACP inside containers, 7476 agent-host, 47480 web gateway.
