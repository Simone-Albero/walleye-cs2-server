FROM python:3.12-slim
WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY scorer.py .

# Run as UID 1000 to match the host owner of the bind-mounted ./data directory.
# Using a non-system UID keeps the service unprivileged while still able to
# write players.json / matches.json on the host.
RUN groupadd -g 1000 scorer && useradd -u 1000 -g scorer -m scorer
USER scorer

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD python -c "import os; os.kill(1, 0)"

CMD ["python", "scorer.py"]
