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
    sox \
    libsox-fmt-all \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
RUN python3 -m pip install --no-cache-dir -r requirements.txt

COPY usb_monitor.py .
COPY gui.py .
COPY main.py .
COPY start.sh .
RUN chmod +x start.sh

VOLUME ["/app/USB_Backups"]

ENV USB_DEBUG=1

CMD ["/bin/bash", "start.sh"]
