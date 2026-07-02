/*
 * wrapper-v2 chroot launcher.
 *
 * Sets up a minimal chroot inside ./rootfs/ that exposes the Android dynamic
 * linker (linker64) and Apple Music's native libs to a daemon binary at
 * /system/bin/main, then execs the daemon.
 *
 * Layout (/system/bin/linker64, /system/lib64) is the same for x86_64 and
 * arm64-v8a rootfs trees; the host binary must match the image arch (see Dockerfile).
 *
 * This is the host-Linux-side launcher. It is intentionally tiny and has no
 * dependency on the daemon's HTTP code or on the Apple libs.
 *
 * Differences vs upstream wrapper.c:
 *   - No gengetopt; the daemon reads configuration from the environment
 *     (WRAPPER_*). The launcher forwards argv unchanged (use --help only).
 *   - --base-dir handling is handled by the daemon itself; the launcher
 *     mkdir -p's WRAPPER_BASE_DIR (or the same default path as the daemon)
 *     plus mpl_db before exec.
 *   - SIGTERM is forwarded to the daemon (upstream only handled SIGINT).
 */

#define _GNU_SOURCE

#include <errno.h>
#include <fcntl.h>
#include <sched.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mount.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

extern char** environ;

#define CAP_SYS_ADMIN_IDX 21
#define CAP_SYS_ADMIN_BIT (1ULL << CAP_SYS_ADMIN_IDX)

#define ROOTFS         "./rootfs"
#define DAEMON_PATH    "/system/bin/main"
#define LINKER_PATH    "/system/bin/linker64"

/* Same default as daemon RuntimeConfig (runtime.hpp). */
static const char k_default_base_dir[] = "/data/data/com.apple.android.music/files";

static volatile pid_t g_child = -1;

static void on_signal(int sig) {
    if (g_child > 0) {
        kill(g_child, sig);
    }
}

static int has_cap_sys_admin(void) {
    FILE* fp = fopen("/proc/self/status", "r");
    if (!fp) return 0;

    char line[256];
    int found = 0;
    while (fgets(line, sizeof(line), fp)) {
        if (strncmp(line, "CapEff:", 7) == 0) {
            char* p = line + 7;
            while (*p == ' ' || *p == '\t') ++p;
            unsigned long long cap = strtoull(p, NULL, 16);
            found = (cap & CAP_SYS_ADMIN_BIT) ? 1 : 0;
            break;
        }
    }
    fclose(fp);
    return found;
}

static int ensure_dir(const char* path, mode_t mode) {
    if (mkdir(path, mode) == 0) return 0;
    if (errno == EEXIST) return 0;
    fprintf(stderr, "wrapper: mkdir %s: %s\n", path, strerror(errno));
    return -1;
}

static int ensure_dir_p(const char* path, mode_t mode) {
    if (path == NULL || *path == '\0') return -1;
    char buf[1024];
    size_t n = strlen(path);
    if (n >= sizeof(buf)) {
        fprintf(stderr, "wrapper: path too long for mkdir -p\n");
        return -1;
    }
    memcpy(buf, path, n + 1);
    for (char* p = buf + 1; *p; ++p) {
        if (*p == '/') {
            *p = '\0';
            if (ensure_dir(buf, mode) != 0) return -1;
            *p = '/';
        }
    }
    return ensure_dir(buf, mode);
}

int main(int argc, char* argv[], char* envp[]) {
    (void)envp;
    struct sigaction sa = {0};
    sa.sa_handler = on_signal;
    sigaction(SIGINT,  &sa, NULL);
    sigaction(SIGTERM, &sa, NULL);

    if (ensure_dir(ROOTFS "/dev", 0755) != 0) return 1;

    int fd = open(ROOTFS "/dev/urandom", O_CREAT | O_RDWR, 0666);
    if (fd < 0) {
        fprintf(stderr, "wrapper: open %s/dev/urandom: %s\n", ROOTFS, strerror(errno));
        return 1;
    }
    close(fd);

    if (mount("/dev/urandom", ROOTFS "/dev/urandom", NULL, MS_BIND, NULL) != 0) {
        fprintf(stderr, "wrapper: bind-mount /dev/urandom: %s\n", strerror(errno));
        return 1;
    }

    if (chdir(ROOTFS) != 0) {
        fprintf(stderr, "wrapper: chdir " ROOTFS ": %s\n", strerror(errno));
        return 1;
    }
    if (chroot("./") != 0) {
        fprintf(stderr, "wrapper: chroot: %s (need CAP_SYS_CHROOT or root)\n", strerror(errno));
        return 1;
    }

    if (ensure_dir("/proc", 0755) != 0) return 1;

    chmod(LINKER_PATH, 0755);
    chmod(DAEMON_PATH, 0755);

    if (has_cap_sys_admin()) {
        if (unshare(CLONE_NEWPID) != 0) {
            fprintf(stderr, "wrapper: unshare(CLONE_NEWPID): %s\n", strerror(errno));
            return 1;
        }
    }

    g_child = fork();
    if (g_child < 0) {
        fprintf(stderr, "wrapper: fork: %s\n", strerror(errno));
        return 1;
    }

    if (g_child > 0) {
        int status = 0;
        if (waitpid(g_child, &status, 0) < 0) {
            fprintf(stderr, "wrapper: waitpid: %s\n", strerror(errno));
            return 1;
        }
        if (WIFEXITED(status))   return WEXITSTATUS(status);
        if (WIFSIGNALED(status)) return 128 + WTERMSIG(status);
        return 1;
    }

    if (mount("proc", "/proc", "proc", 0, NULL) != 0) {
        fprintf(stderr, "wrapper: mount proc: %s\n", strerror(errno));
        return 1;
    }

    const char* base_dir = getenv("WRAPPER_BASE_DIR");
    if (base_dir == NULL || base_dir[0] == '\0') {
        base_dir = k_default_base_dir;
    }
    if (ensure_dir_p(base_dir, 0777) != 0) return 1;
    char db_dir[1024];
    if (snprintf(db_dir, sizeof(db_dir), "%s/mpl_db", base_dir) >= (int)sizeof(db_dir)) {
        fprintf(stderr, "wrapper: mpl_db path too long\n");
        return 1;
    }
    if (ensure_dir_p(db_dir, 0777) != 0) return 1;

    /* Bionic DNS resolver behavior; upstream wrapper sets this in init(). */
    setenv("ANDROID_DNS_MODE", "local", 1);

    /* Bionic internals (timezone, SSL cert paths, etc.) expect these. */
    setenv("ANDROID_DATA", "/data", 0);
    setenv("ANDROID_ROOT", "/system", 0);

    /* Point libcurl / OpenSSL at the CA bundle so SSL verification works. */
    setenv("SSL_CERT_FILE", "/etc/ssl/certs/ca-certificates.crt", 0);
    setenv("CURL_CA_BUNDLE", "/etc/ssl/certs/ca-certificates.crt", 0);

    /* Pass environ so setenv() changes are visible (execve's envp is static). */
    /* execve leaves argv[0] as the host path (e.g. /app/wrapper); set argv[0]
     * to the daemon so bionic diagnostics and any argv[0]-based logic match. */
    const int max_args = 256;
    if (argc >= max_args) {
        fprintf(stderr, "wrapper: too many arguments (%d max)\n", max_args - 1);
        return 1;
    }
    char* fixed_argv[max_args + 1];
    fixed_argv[0] = (char*)DAEMON_PATH;
    for (int i = 1; i < argc; ++i) {
        fixed_argv[i] = argv[i];
    }
    /* argv list must be NULL-terminated (C null pointer, not C++ nullptr). */
    fixed_argv[argc > 0 ? argc : 1] = 0;

    execve(DAEMON_PATH, fixed_argv, environ);
    fprintf(stderr, "wrapper: execve %s: %s\n", DAEMON_PATH, strerror(errno));
    return 1;
}
