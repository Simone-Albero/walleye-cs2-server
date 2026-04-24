FROM python:3.12-slim
WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY scorer.py .

# Run as a non-root user.
RUN groupadd -r scorer && useradd -r -g scorer scorer
USER scorer

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD python -c "import os; os.kill(1, 0)"

CMD ["python", "scorer.py"]
