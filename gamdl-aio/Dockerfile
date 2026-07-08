# Multi-stage Dockerfile for gamdl-aio supporting both amd64 and arm64 natively
FROM debian:12.9 AS build

ARG TARGETPLATFORM
ARG TARGETARCH
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
    && if [[ "$TARGETARCH" == "arm64" ]]; then \
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

# Map TARGETARCH to Android NDK target arch
RUN target_ndk_arch="arm64-v8a"; \
    if [[ "$TARGETARCH" == "amd64" ]]; then \
      target_ndk_arch="x86_64"; \
    fi && \
    bash tools/stage-system.sh --arch "$target_ndk_arch" && \
    mkdir -p rootfs/system/lib64 && \
    cp "/opt/android-ndk-r${NDK_VERSION}b/sources/cxx-stl/llvm-libc++/libs/${target_ndk_arch}/libc++_shared.so" "rootfs/system/lib64/"

# Compile daemon and wrapper launcher
RUN host_cc=(); \
    target_ndk_arch="arm64-v8a"; \
    if [[ "$TARGETARCH" == "amd64" ]]; then \
      target_ndk_arch="x86_64"; \
    elif [[ "$TARGETARCH" == "arm64" && "$(uname -m)" != "aarch64" ]]; then \
      host_cc=( \
        -DCMAKE_C_COMPILER=aarch64-linux-gnu-gcc \
        -DCMAKE_CXX_COMPILER=aarch64-linux-gnu-g++ \
      ); \
    fi && \
    cmake -S . -B build -G Ninja \
        -DTARGET_ARCH="$target_ndk_arch" \
        "${host_cc[@]}" \
        -DCMAKE_BUILD_TYPE="${CMAKE_BUILD_TYPE}" && \
    cmake --build build -j

# -----------------------------------------------------------------------------
# Runtime Stage (FastAPI Server + Decryption Wrapper + Transcoder)
# -----------------------------------------------------------------------------
FROM python:3.10-slim-bookworm

ARG TARGETARCH

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
ENV TARGET_ARCH=${TARGETARCH}
ENV HTTP_PORT=18080

CMD ["python", "main.py"]
