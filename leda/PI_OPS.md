# Pi Operations Guide

Target: `tetrix@10.122.6.115`

---

## SSH In

```bash
ssh tetrix@10.122.6.115
```

---

## Check All Services

```bash
# Docker containers (SHILATE stack)
docker ps --format 'table {{.Names}}\t{{.Status}}'

# LED driver systemd service
systemctl status led-driver --no-pager
```

Expected output — all containers should show `Up`:

```
NAMES                       STATUS
shilate-leda-controller     Up ...
shilate-velocitas-app       Up ...
shilate-mqtt-kuksa-feeder   Up ...
shilate-mosquitto           Up ...
shilate-kuksa-databroker    Up ...
```

---

## Watch Live MQTT Traffic

Install client tools if not present:

```bash
sudo apt-get install -y mosquitto-clients
```

Subscribe to all `env0/` topics:

```bash
mosquitto_sub -h localhost -t 'env0/#' -v
```

| Topic | Published by | Meaning |
|-------|-------------|---------|
| `env0/vehicle/training/obs` | Unity | Observation vector sent to the model |
| `env0/leda/control/steer` | leda-controller | Steering command (−1 … +1) |
| `env0/leda/control/throttle` | leda-controller | Throttle command (0 … 1) |
| `env0/leda/control/brake` | leda-controller | Brake command (0 … 1) |
| `env0/vehicle/training/heartbeat` | Unity | 1 Hz keepalive (triggers GPIO18 LED) |
| `env0/vehicle/training/episode_end` | Unity | Episode summary JSON |

---

## Watch Container Logs

```bash
docker logs -f shilate-leda-controller
docker logs -f shilate-mqtt-kuksa-feeder
docker logs -f shilate-velocitas-app
```

---

## Test GPIO LEDs Without Unity

Requires `mosquitto-clients` (see above).

```bash
# Left LED on (GPIO 14) — full brightness
mosquitto_pub -h localhost -t 'env0/leda/control/steer' -m '{"value": -1.0}'

# Right LED on (GPIO 15) — full brightness
mosquitto_pub -h localhost -t 'env0/leda/control/steer' -m '{"value": 1.0}'

# Both off
mosquitto_pub -h localhost -t 'env0/leda/control/steer' -m '{"value": 0.0}'

# Flash heartbeat LED (GPIO 18)
mosquitto_pub -h localhost -t 'env0/vehicle/training/heartbeat' -m '{"value": 1}'
```

---

## Change the Inference Model

The model is mounted from `/home/tetrix/shilate/models/best_model.zip`.

**From your dev machine:**

```bash
# Copy a new checkpoint to the Pi
scp /path/to/your_model.zip tetrix@10.122.6.115:/home/tetrix/shilate/models/best_model.zip

# Restart the inference container to pick it up
ssh tetrix@10.122.6.115 "docker restart shilate-leda-controller"
```

**From the Pi directly:**

```bash
cp /path/to/your_model.zip /home/tetrix/shilate/models/best_model.zip
docker restart shilate-leda-controller
```

**Verify the model loaded correctly:**

```bash
docker logs shilate-leda-controller 2>&1 | head -10
```

Should show:
```
[INFO] Loading model from /model/best_model.zip (rays=21)
[INFO] Connected to MQTT broker at 127.0.0.1:1883 (prefix: env0)
[INFO] AI Driver running (model: /model/best_model.zip)
```

> The model must have been trained with `ray_count = 21` (observation size 24 = 21 rays + speed + steer + progress). Using a checkpoint trained with a different ray count will crash the container.

---

## Restart / Stop Everything

```bash
# Restart all Docker services
docker compose -f /home/tetrix/shilate/docker-compose.pi.yml restart

# Stop all Docker services
docker compose -f /home/tetrix/shilate/docker-compose.pi.yml down

# Restart LED driver
sudo systemctl restart led-driver

# Full redeploy (run from dev machine, in leda/)
./deploy-pi.sh
```
