cmake_minimum_required(VERSION 3.22)

# wrapper-v2 builds two binaries:
#   wrapper  - native-Linux chroot launcher (built with the host toolchain)
#   main     - the daemon, cross-compiled with the Android NDK so that it can
#              be exec'd inside a chroot whose dynamic loader is Android's
#              linker64 and whose .so dependencies are Apple Music's libs.
#
# These two targets cannot share a single CMake configuration because they
# target different sysroots, ABIs, and toolchains. The top-level project here
# only ever builds the launcher; it then invokes a sub-build for the daemon
# using the NDK toolchain file. This keeps each target's flags clean and
# avoids contaminating either build with the other's compiler.
#
# Usage:
#   cmake -S . -B build -DTARGET_ARCH=x86_64    # or arm64-v8a
#   cmake --build build -j
#
# Required input:
#   ANDROID_NDK_HOME or NDK_PATH must point at a usable NDK r23b install.

project(wrapper-v2 LANGUAGES C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

# -----------------------------------------------------------------------------
# Options
# -----------------------------------------------------------------------------
set(TARGET_ARCH "x86_64" CACHE STRING "Target ABI for the chrooted daemon (matches extract-libs/stage-system --arch)")
set_property(CACHE TARGET_ARCH PROPERTY STRINGS "x86_64" "arm64-v8a")

option(WRAPPER_BUILD_DAEMON "Drive the NDK sub-build for the chrooted daemon" ON)

# -----------------------------------------------------------------------------
# Locate the NDK (only required when WRAPPER_BUILD_DAEMON is ON)
# -----------------------------------------------------------------------------
if(WRAPPER_BUILD_DAEMON)
    if(DEFINED ENV{ANDROID_NDK_HOME} AND NOT DEFINED NDK_PATH)
        set(NDK_PATH "$ENV{ANDROID_NDK_HOME}")
    endif()
    if(NOT DEFINED NDK_PATH)
        message(FATAL_ERROR
            "NDK_PATH not set. Provide -DNDK_PATH=/path/to/android-ndk-r23b "
            "or export ANDROID_NDK_HOME, or pass -DWRAPPER_BUILD_DAEMON=OFF "
            "to skip the daemon sub-build.")
    endif()
    if(NOT EXISTS "${NDK_PATH}/build/cmake/android.toolchain.cmake")
        message(FATAL_ERROR "NDK_PATH '${NDK_PATH}' does not look like an NDK install.")
    endif()
endif()

# -----------------------------------------------------------------------------
# Host build: the chroot launcher
# -----------------------------------------------------------------------------
add_executable(wrapper src/launcher/wrapper.c)
target_compile_options(wrapper PRIVATE -Wall -Wextra -O2)
set_target_properties(wrapper PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}"
)

# Place the launcher at the repo root after build (matches upstream layout
# expected by entrypoint.sh and Docker COPY paths).
add_custom_command(TARGET wrapper POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        $<TARGET_FILE:wrapper>
        ${CMAKE_SOURCE_DIR}/wrapper
    COMMENT "Staging wrapper binary at repo root"
)

# -----------------------------------------------------------------------------
# Cross build: the chrooted daemon
# -----------------------------------------------------------------------------
# We invoke a fully separate CMake configure for src/daemon using the NDK
# toolchain file. This produces rootfs/system/bin/main.
if(WRAPPER_BUILD_DAEMON)
    set(_daemon_src    "${CMAKE_SOURCE_DIR}/src/daemon")
    set(_daemon_build  "${CMAKE_BINARY_DIR}/daemon-${TARGET_ARCH}")
    set(_daemon_output "${CMAKE_SOURCE_DIR}/rootfs/system/bin/main")

    if(TARGET_ARCH STREQUAL "x86_64")
        set(_android_abi "x86_64")
    elseif(TARGET_ARCH STREQUAL "arm64-v8a")
        set(_android_abi "arm64-v8a")
    else()
        message(FATAL_ERROR "TARGET_ARCH '${TARGET_ARCH}' is not supported (use x86_64 or arm64-v8a).")
    endif()

    file(MAKE_DIRECTORY "${_daemon_build}")

    # ANDROID_PLATFORM is intentionally low: we ship vendored bionic/AOSP
    # binaries from an Android 5.0-era system image (no __register_atfork,
    # no DT_GNU_HASH support in the linker). Targeting API 21 makes NDK
    # r23b emit code that runs against that older bionic. Apple's libs
    # ship libc++_shared.so built the same way, so ABIs line up.
    add_custom_target(daemon ALL
        COMMAND ${CMAKE_COMMAND}
            -S "${_daemon_src}"
            -B "${_daemon_build}"
            -G "${CMAKE_GENERATOR}"
            -DCMAKE_TOOLCHAIN_FILE=${NDK_PATH}/build/cmake/android.toolchain.cmake
            -DANDROID_ABI=${_android_abi}
            -DANDROID_PLATFORM=android-21
            -DANDROID_STL=c++_shared
            -DCMAKE_BUILD_TYPE=$<IF:$<CONFIG:Debug>,Debug,Release>
            -DLIBS_DIR=${CMAKE_SOURCE_DIR}/rootfs/system/lib64
        COMMAND ${CMAKE_COMMAND} --build "${_daemon_build}" -j
        COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_SOURCE_DIR}/rootfs/system/bin"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${_daemon_build}/main"
            "${_daemon_output}"
        BYPRODUCTS "${_daemon_output}"
        WORKING_DIRECTORY "${CMAKE_SOURCE_DIR}"
        VERBATIM
        COMMENT "Building daemon (NDK, ABI=${_android_abi}) -> ${_daemon_output}"
    )
endif()

# -----------------------------------------------------------------------------
# Diagnostics
# -----------------------------------------------------------------------------
message(STATUS "wrapper-v2:")
message(STATUS "  TARGET_ARCH         = ${TARGET_ARCH}")
message(STATUS "  WRAPPER_BUILD_DAEMON= ${WRAPPER_BUILD_DAEMON}")
if(WRAPPER_BUILD_DAEMON)
    message(STATUS "  NDK_PATH            = ${NDK_PATH}")
endif()
