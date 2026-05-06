# Closed Network Setup (No Internet Required)

OpenMono supports fully air-gapped deployments where the agent box and inference box communicate on a closed network without internet access.

## Overview

There are three ways to connect the agent box to the inference box on a closed network:

1. **Direct Connection** (simplest) — agent connects directly to inference box IP
2. **Local Relay (frps)** — self-hosted relay server when direct connection isn't possible
3. **Reverse SSH Tunnel** — alternative when frp is blocked

---

## Option 1: Direct Connection (Simplest)

No additional software needed. The agent box connects directly to the inference box's IP address.

### Prerequisites

- Both boxes on the same network
- Inference box port 7474 reachable (no firewall blocking)
- `llama-server` configured to bind to `0.0.0.0` (default in Docker setup)

### Steps

**On the Inference Box:**

```bash
# Install inference components only
openmono setup --inference

# Start llama-server
openmono start

# Note the IP address
ip addr show
# Look for inet address (e.g., 192.168.1.100)
```

**On the Agent Box:**

```bash
# Install agent components only
openmono setup --agent

# Configure to use inference box
openmono config set llm.endpoint http://<inference-box-ip>:7474

# Get the API key from inference box
# (Found in docker/.env after running openmono start)
openmono config set llm.api_key <llama_api_key_from_inference_box>

# Run the agent
openmono agent
```

### Verification

```bash
# On agent box, test connectivity
curl http://<inference-box-ip>:7474/health
```

---

## Option 2: Local Relay (frps) for NAT/Firewall

Use this when the inference box can't accept inbound connections (NAT, firewall). A third machine acts as a relay server on the local network.

### Architecture

```
Agent Box ────▶ Local Relay Server ────▶ Inference Box
                (frps)                   (frpc tunnel)
```

### Steps

**Step 1: Set up Local Relay Server**

Choose a machine on the network to act as the relay (can be the agent box, inference box, or a separate machine):

```bash
# On the relay server machine
openmono frps setup

# The script will:
# - Install frps (frp server)
# - Generate a relay token
# - Show the server's IP addresses
# - Save credentials to ~/.openmono/local-relay.json
```

Output will show:
```
✓ Local frps relay is ready!
Server IP addresses:
  - 192.168.1.50

On the INFERENCE box, run:
  openmono tunnel setup --local --frps-address=192.168.1.50

Relay Token (for inference box):
  omr_local_a1b2c3d4e5f6...
```

**Step 2: Configure Inference Box to Use Local Relay**

```bash
# On the inference box
openmono tunnel setup --local --frps-address=<relay-server-ip>

# The script will:
# - Read credentials from relay server (~/.openmono/local-relay.json)
# - Install frpc (frp client)
# - Configure tunnel to relay server
# - Start frpc service
# - Restart llama-server with new API key
```

Output will show:
```
✓ frp tunnel connected to 192.168.1.50:7000
✓ LLAMA_API_KEY stored in docker/.env
✓ Local endpoint: http://192.168.1.50:4747

ON THE AGENT BOX, run:

  openmono config set llm.endpoint  http://192.168.1.50:4747
  openmono config set llm.api_key   <llama_api_key>
```

**Step 3: Configure Agent Box**

```bash
# On the agent box
openmono config set llm.endpoint http://<relay-server-ip>:4747
openmono config set llm.api_key <llama_api_key_from_inference_box>

# Run the agent
openmono agent
```

### Managing the Local Relay

```bash
# Check relay status
openmono frps status

# View relay logs
openmono frps logs

# Stop/start relay
openmono frps stop
openmono frps start

# On inference box, manage tunnel
openmono tunnel status
openmono tunnel stop
openmono tunnel start
```

---

## Option 3: Reverse SSH Tunnel (Alternative)

If frp is blocked but SSH is allowed, use SSH reverse tunneling.

### Steps

**On the Relay Server:**

```bash
# Ensure SSH server is running
sudo systemctl status sshd

# Note the server's IP
ip addr show
```

**On the Inference Box:**

```bash
# Create reverse SSH tunnel to relay server
ssh -R 7474:localhost:7474 user@<relay-server-ip> -N -f

# -R: reverse tunnel (remote port forwarding)
# 7474:localhost:7474: forward remote port 7474 to local port 7474
# -N: no remote command
# -f: run in background
```

**On the Agent Box:**

```bash
# Connect through the relay server
openmono config set llm.endpoint http://<relay-server-ip>:7474
openmono config set llm.api_key <llama_api_key>

openmono agent
```

### Managing SSH Tunnel

```bash
# On inference box, find tunnel process
ps aux | grep "ssh -R"

# Kill tunnel
pkill -f "ssh -R 7474"
```

---

## Troubleshooting

### Direct Connection Issues

**Problem:** Agent can't connect to inference box
- Check firewall: `sudo ufw status` or `sudo iptables -L`
- Verify llama-server is listening: `ss -tlnp | grep 7474`
- Test connectivity: `curl http://<ip>:7474/health`

### Local Relay Issues

**Problem:** frpc can't connect to frps
- Verify relay server IP is correct
- Check frps is running: `sudo systemctl status frps`
- Check firewall on relay server (port 7000 by default)
- View logs: `sudo journalctl -u frps -f` (server) or `sudo journalctl -u frpc -f` (client)

**Problem:** Agent can't reach relay endpoint
- Verify the remote port (default 4747) is accessible
- Check if relay server has multiple IPs (try each one)

### General

**Check llama-server health:**
```bash
curl http://<llama-server-ip>:7474/health
curl http://<llama-server-ip>:7474/props
```

**View llama-server logs:**
```bash
cd docker && docker compose logs llama-server
```

---

## Comparison Table

| Method | Complexity | Requires Extra Machine | Works with NAT | Internet Required |
|--------|-------------|----------------------|----------------|------------------|
| Direct Connection | Low | No | No | No |
| Local Relay (frps) | Medium | Optional* | Yes | No |
| SSH Reverse Tunnel | Medium | Optional* | Yes | No |

*Can run frps on inference box or agent box instead of separate machine

---

## Files Created

When using local relay, these files are created:

- **Relay Server:** `~/.openmono/local-relay.json` — relay credentials
- **Relay Server:** `/etc/frp/frps.toml` — frps configuration
- **Inference Box:** `~/.openmono/relay.json` — tunnel credentials (if using `--local`)
- **Inference Box:** `/etc/frp/frpc.toml` — frpc configuration
- **Inference Box:** `docker/.env` — contains LLAMA_API_KEY

---

## Next Steps

After setting up connectivity:

1. Run `openmono agent` to start the coding agent
2. See [SETUP.md](SETUP.md) for daily commands and slash commands
3. See [PLAYBOOKS.md](PLAYBOOKS.md) for automated workflows
