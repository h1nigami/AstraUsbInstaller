FROM python:3.12-slim-bookworm

RUN apt-get update && apt-get install -y --no-install-recommends \
    util-linux \
    udev \
    mount \
    usbutils \
    dosfstools \
    ntfs-3g \
    e2fsprogs \
    python3 \
    python3-pip \
    python3-tk \
    espeak-ng \
    espeak-ng-data \
    alsa-utils \
    x11-utils \
    xdg-utils \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Base (lean) dependencies — always installed.
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Optional nanosuit voice stack (torch + coqui-tts, ~3 GB). Off by default to
# keep the image small (CI builds the Dockerfile directly and stays lean);
# enable with:  docker build --build-arg INSTALL_VOICE=1
# docker-compose.yml passes INSTALL_VOICE=auto -> install only on x86_64,
# skip on ARM (Jetson) where the native wheels fail to build.
ARG INSTALL_VOICE=0
COPY requirements-voice.txt .
RUN arch="$(uname -m)"; \
    if [ "$INSTALL_VOICE" = "auto" ]; then \
        case "$arch" in \
            x86_64|amd64) INSTALL_VOICE=1 ;; \
            *) INSTALL_VOICE=0 ;; \
        esac; \
    fi; \
    if [ "$INSTALL_VOICE" = "1" ] || [ "$INSTALL_VOICE" = "true" ]; then \
        echo "Installing voice stack (arch=$arch)"; \
        pip install --no-cache-dir -r requirements-voice.txt; \
    else \
        echo "Skipping voice stack (arch=$arch, INSTALL_VOICE=$INSTALL_VOICE)"; \
    fi

COPY usb_monitor.py .
COPY gui.py .
COPY main.py .
COPY start.sh .
RUN sed -i 's/\r$//' start.sh && chmod +x start.sh

VOLUME ["/app/USB_Backups", "/app/data"]

ENV USB_DEBUG=1
# Accept the XTTS (CPML) model license non-interactively, and keep the large
# voice models in the mounted /app/data volume so they download only once.
ENV COQUI_TOS_AGREED=1
ENV TTS_HOME=/app/data/tts

CMD ["/bin/bash", "start.sh"]
