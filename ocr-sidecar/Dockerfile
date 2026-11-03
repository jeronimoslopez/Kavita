# syntax=docker/dockerfile:1

FROM python:3.12-slim AS ndlocr
ARG NDLOCR_SHA=e57f7c6bd9cfb3de5f12f008e71ad2122bd44bfd
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/*
# The models live in the repo itself, so this fetch brings about 150 MB.
RUN git init /src \
    && git -C /src remote add origin https://github.com/ndl-lab/ndlocr-lite \
    && git -C /src fetch --depth 1 origin "$NDLOCR_SHA" \
    && git -C /src checkout FETCH_HEAD \
    && mkdir -p /opt/ndlocr-lite \
    && cp -r /src/src /src/LICENCE /src/LICENCE_DEPENDENCEIES /opt/ndlocr-lite/

FROM python:3.12-slim AS runtime
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    NDLOCR_SRC=/opt/ndlocr-lite/src \
    NDLOCR_COMMIT=e57f7c6 \
    OCR_THREADS=2
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY --from=ndlocr /opt/ndlocr-lite /opt/ndlocr-lite
COPY app.py engine.py ./
RUN useradd --system --create-home --uid 10001 ocr
USER ocr
EXPOSE 8000
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s \
    CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8000/health', timeout=4).status == 200 else 1)"
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8000", "--workers", "1"]

# docker build --target test -t kavita-ocr-sidecar:test . && docker run --rm kavita-ocr-sidecar:test
FROM runtime AS test
USER root
COPY requirements-dev.txt pytest.ini ./
RUN pip install --no-cache-dir -r requirements-dev.txt
COPY contract ./contract
COPY tests ./tests
USER ocr
CMD ["pytest", "-v"]

FROM runtime
