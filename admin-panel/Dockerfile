FROM python:3.12-slim
WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY app.py .
COPY templates/ templates/
COPY static/ static/

# Note: this container runs as root (set in compose.yaml) because it needs
# access to /var/run/docker.sock to restart sibling containers.

EXPOSE 8081

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD python -c \
        "import urllib.request; urllib.request.urlopen('http://localhost:8081/api/health', timeout=3)"

CMD ["python", "app.py"]
