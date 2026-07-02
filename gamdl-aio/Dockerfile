# Multi-stage Dockerfile for gamdl-aio
ARG RUNTIME_PLATFORM=linux/arm64
ARG BUILD_PLATFORM=linux/amd64

# -----------------------------------------------------------------------------
# Build Stage (always amd64 for Android NDK toolchain)
# -----------------------------------------------------------------------------
FROM --platform=linux/amd64 debian:13.2 AS build

ARG TARGET_ARCH=arm64-v8a
ARG CMAKE_BUILD_TYPE=Release
ARG NDK_VERSION=23
SHELL ["/bin/bash", "-c"]
ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        build-essential \
        ca-certificates \
        cmake \
        curl \
        git \
        jq \
        ninja-build \
        unzip \
    && if [[ "$TARGET_ARCH" == "arm64-v8a" ]]; then \
         apt-get install -y --no-install-recommends \
           gcc-aarch64-linux-gnu g++-aarch64-linux-gnu; \
       fi \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fSL -o /tmp/ndk.zip \
        "https://dl.google.com/android/repository/android-ndk-r${NDK_VERSION}b-linux.zip" && \
    unzip -q /tmp/ndk.zip -d /opt && \
    rm /tmp/ndk.zip
ENV ANDROID_NDK_HOME=/opt/android-ndk-r${NDK_VERSION}b

WORKDIR /app/wrapper-v2
COPY wrapper-v2/ /app/wrapper-v2

# Run system staging inside the build image to get linker64 & standard libs
RUN bash tools/stage-system.sh --arch "${TARGET_ARCH}"

# Compile daemon and wrapper launcher
RUN host_cc=(); \
    if [[ "$TARGET_ARCH" == "arm64-v8a" ]]; then \
      host_cc=( \
        -DCMAKE_C_COMPILER=aarch64-linux-gnu-gcc \
        -DCMAKE_CXX_COMPILER=aarch64-linux-gnu-g++ \
      ); \
    fi && \
    cmake -S . -B build -G Ninja \
        -DTARGET_ARCH="${TARGET_ARCH}" \
        "${host_cc[@]}" \
        -DCMAKE_BUILD_TYPE="${CMAKE_BUILD_TYPE}" && \
    cmake --build build -j

# -----------------------------------------------------------------------------
# Runtime Stage (FastAPI Server + Decryption Wrapper + Transcoder)
# -----------------------------------------------------------------------------
FROM --platform=${RUNTIME_PLATFORM} python:3.10-slim-bookworm

ENV DEBIAN_FRONTEND=noninteractive
ENV PYTHONUNBUFFERED=1

WORKDIR /app

# Install runtime packages: FFmpeg for transcoding, unzip/jq for extract-libs
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        ffmpeg \
        unzip \
        jq \
        ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Copy wrapper files
COPY --from=build /app/wrapper-v2/wrapper      /app/wrapper-v2/wrapper
COPY --from=build /app/wrapper-v2/rootfs       /app/wrapper-v2/rootfs
COPY --from=build /etc/ssl/certs/ca-certificates.crt /app/wrapper-v2/rootfs/etc/ssl/certs/ca-certificates.crt
COPY wrapper-v2/tools /app/wrapper-v2/tools
COPY wrapper-v2/vendor /app/wrapper-v2/vendor
COPY wrapper-v2/LIBS_VERSION.json /app/wrapper-v2/LIBS_VERSION.json

# Copy server assets
COPY requirements.txt /app/
COPY main.py /app/
COPY gamdl/ /app/gamdl/

# Install python dependencies
RUN pip install --no-cache-dir -r requirements.txt

EXPOSE 8000

# Set environment
ENV TARGET_ARCH=arm64-v8a
ENV HTTP_PORT=18080

CMD ["python", "main.py"]
