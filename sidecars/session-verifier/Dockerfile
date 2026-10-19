FROM python:3.12.11-slim-bookworm@sha256:519591d6871b7bc437060736b9f7456b8731f1499a57e22e6c285135ae657bf7

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    CHROME_PATH=/usr/bin/chromium \
    PATH=/app/.venv/bin:$PATH

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates chromium xvfb tini \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --system --gid 10001 verifier \
    && useradd --system --uid 10001 --gid verifier --create-home --shell /usr/sbin/nologin verifier

WORKDIR /app
COPY pyproject.toml uv.lock /app/
RUN pip install --no-cache-dir uv==0.11.29 \
    && uv sync --frozen --no-dev --no-editable \
    && pip uninstall -y uv
COPY session_verifier /app/session_verifier
COPY entrypoint.sh /app/entrypoint.sh
USER verifier
EXPOSE 8000
ENTRYPOINT ["/usr/bin/tini", "-g", "--"]
CMD ["sh", "/app/entrypoint.sh"]
