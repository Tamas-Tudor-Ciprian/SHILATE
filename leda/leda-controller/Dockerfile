FROM python:3.11-slim AS base

WORKDIR /app

# Install only MQTT client first (shared by all modes, tiny)
RUN pip install --no-cache-dir "paho-mqtt>=2.0,<3.0"

# Second stage: add ML deps only when needed
FROM base AS ml
RUN pip install --no-cache-dir \
    --trusted-host download.pytorch.org \
    --trusted-host download-r2.pytorch.org \
    torch --index-url https://download.pytorch.org/whl/cpu \
    && pip install --no-cache-dir \
    "stable-baselines3>=2.3,<3.0" \
    "gymnasium>=0.29,<1.0" \
    "numpy>=1.24,<2.0"

# --- Debug image (tiny: ~150 MB) ---
FROM base AS debug
COPY controller.py debug_drive.py ./
CMD ["python3", "debug_drive.py"]

# --- Train / Inference image (~900 MB) ---
FROM ml AS full
COPY . .
ENV MODE=train
ENV MQTT_HOST=127.0.0.1
ENV MQTT_PORT=1883
ENV NUM_ENVS=4
ENV TIMESCALE=5
ENV TOTAL_TIMESTEPS=100000
ENV MODEL_PATH=/tmp/shilate_model/best_model.zip

CMD ["sh", "-c", "\
    if [ \"$MODE\" = 'train' ]; then \
        python3 train.py --mqtt-host $MQTT_HOST --mqtt-port $MQTT_PORT \
            --num-envs $NUM_ENVS --timescale $TIMESCALE \
            --total-timesteps $TOTAL_TIMESTEPS; \
    elif [ \"$MODE\" = 'inference' ]; then \
        python3 ai_driver.py --mqtt-host $MQTT_HOST --mqtt-port $MQTT_PORT \
            --model $MODEL_PATH; \
    elif [ \"$MODE\" = 'debug' ]; then \
        python3 debug_drive.py --mqtt-host $MQTT_HOST --mqtt-port $MQTT_PORT; \
    else \
        echo \"Unknown MODE: $MODE (use debug|train|inference)\"; exit 1; \
    fi"]
