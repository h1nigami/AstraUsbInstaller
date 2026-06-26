FROM python:3.12-slim-bookworm

RUN apt-get update && apt-get install -y --no-install-recommends \
    util-linux \
    udev \
    mount \
    usbutils \
    dosfstools \
    ntfs-3g \
    e2fsprogs \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY usb_monitor.py .
COPY main.py .

VOLUME ["/app/USB_Backups"]

ENV USB_DEBUG=1

CMD ["python", "main.py"]
