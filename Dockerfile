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
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Base (lean) dependencies — always installed.
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Optional nanosuit voice stack (torch + coqui-tts, ~3 GB). Off by default to
# keep the image small; enable with:  docker build --build-arg INSTALL_VOICE=1
# or set it in docker-compose.yml under build.args.
ARG INSTALL_VOICE=0
COPY requirements-voice.txt .
RUN if [ "$INSTALL_VOICE" = "1" ] || [ "$INSTALL_VOICE" = "true" ]; then \
        pip install --no-cache-dir -r requirements-voice.txt; \
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
