FROM python:3.12.11-slim-bookworm@sha256:519591d6871b7bc437060736b9f7456b8731f1499a57e22e6c285135ae657bf7

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    APPLE_GATEWAY_DATA_ROOT=/data \
    PATH=/app/.venv/bin:$PATH

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --system --uid 10001 --home-dir /nonexistent --shell /usr/sbin/nologin gateway

WORKDIR /app
COPY pyproject.toml uv.lock README.md /app/
COPY apple_gateway /app/apple_gateway
RUN pip install --no-cache-dir uv==0.11.29 \
    && uv sync --frozen --no-dev --no-editable \
    && pip uninstall -y uv

RUN install -d -o gateway -g gateway /data
USER gateway
EXPOSE 8000
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/api/health', timeout=3)"
CMD ["uvicorn", "apple_gateway.app:app", "--host", "0.0.0.0", "--port", "8000", "--no-access-log"]
