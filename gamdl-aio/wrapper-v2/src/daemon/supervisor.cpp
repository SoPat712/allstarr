#include "supervisor.hpp"

#include <chrono>
#include <csignal>
#include <cstdio>
#include <cstdlib>
#include <exception>
#include <mutex>
#include <string>
#include <thread>
#include <utility>

#include <signal.h>
#include <sys/wait.h>
#include <unistd.h>

#include <nlohmann/json.hpp>

namespace wrapper {

namespace {

using nlohmann::json;
using namespace std::chrono_literals;

constexpr const char* kWorkerHost = "127.0.0.1";
constexpr int kMaxRecoveryAttempts = 5;
constexpr int kMaxConsecutiveWorkerStartFailures = kMaxRecoveryAttempts;
constexpr int kSupervisorFatalExitCode = 76;
constexpr auto kMaxRecoveryRetryDelay = 8s;

struct WorkerHttpResponse {
    bool transport_ok = false;
    int status = 502;
    std::string body;
    std::string content_type = "application/json";
    std::string error;
};

}  // namespace

class Supervisor::Impl {
public:
    Impl(std::string argv0, std::string version, int worker_port)
        : argv0_(std::move(argv0)),
          version_(std::move(version)),
          worker_port_(worker_port) {}

    ~Impl() {
        stop_worker();
    }

    void mount(httplib::Server& svr) {
        svr.Get("/health", [this](const httplib::Request&, httplib::Response& res) {
            WorkerHttpResponse wh = request("GET", "/health", "", "", 5s);
            json body = {
                {"status", "ok"},
                {"version", version_},
                {"mode", "supervisor"},
                {"worker", {
                    {"reachable", wh.transport_ok},
                    {"status", wh.transport_ok ? wh.status : 0},
                }},
            };
            if (!wh.transport_ok) body["worker"]["error"] = wh.error;
            res.status = 200;
            res.set_content(body.dump(), "application/json");
        });

        svr.Get("/me", [this](const httplib::Request& req, httplib::Response& res) {
            proxy_response(res, request("GET", req.target, "", "", 10s));
        });

        svr.Post("/login", [this](const httplib::Request& req, httplib::Response& res) {
            proxy_response(res, request_with_login_recovery(
                                    req.target, req.body,
                                    req.get_header_value("Content-Type")));
        });

        svr.Post("/login/2fa", [this](const httplib::Request& req, httplib::Response& res) {
            proxy_response(res, request("POST", req.target, req.body,
                                        req.get_header_value("Content-Type"), 65s));
        });

        svr.Delete("/login", [this](const httplib::Request& req, httplib::Response& res) {
            proxy_response(res, request("DELETE", req.target, "", "", 10s));
        });

        svr.Get("/playback", [this](const httplib::Request& req, httplib::Response& res) {
            proxy_response(res, request("GET", req.target, "", "", 45s));
        });

        svr.Post("/decrypt", [this](const httplib::Request& req, httplib::Response& res) {
            proxy_response(res, request_with_decrypt_recovery(
                                    req.target, req.body,
                                    req.get_header_value("Content-Type")));
        });

        svr.set_exception_handler([](const httplib::Request& req, httplib::Response& res,
                                      std::exception_ptr ep) {
            std::string what = "unknown";
            try {
                if (ep) std::rethrow_exception(ep);
            } catch (const std::exception& e) {
                what = e.what();
            } catch (...) {
            }
            std::fprintf(stderr, "supervisor: exception %s %s: %s\n",
                         req.method.c_str(), req.path.c_str(), what.c_str());
            json body = {{"error", "internal"}, {"detail", what}};
            res.status = 500;
            res.set_content(body.dump(), "application/json");
        });
    }

    void stop_worker() {
        std::lock_guard<std::mutex> lock(mu_);
        stop_locked();
    }

private:
    bool ensure_started() {
        std::lock_guard<std::mutex> lock(mu_);
        reap_locked();
        if (pid_ > 0 && wait_ready_locked(1s)) {
            return true;
        }
        if (pid_ > 0) {
            stop_locked();
        }
        const bool ok = start_locked() && wait_ready_locked(45s);
        note_worker_start_result_locked(ok);
        return ok;
    }

    bool restart() {
        std::lock_guard<std::mutex> lock(mu_);
        stop_locked();
        const bool ok = start_locked() && wait_ready_locked(45s);
        note_worker_start_result_locked(ok);
        return ok;
    }

    WorkerHttpResponse request(const std::string& method,
                               const std::string& path,
                               const std::string& body,
                               const std::string& content_type,
                               std::chrono::seconds read_timeout) {
        std::lock_guard<std::mutex> lock(request_mu_);
        return request_with_transport_recovery_locked(method, path, body, content_type,
                                                      read_timeout);
    }

    WorkerHttpResponse request_with_decrypt_recovery(const std::string& path,
                                                     const std::string& body,
                                                     const std::string& content_type) {
        std::lock_guard<std::mutex> lock(request_mu_);
        WorkerHttpResponse last;
        for (int attempt = 1; attempt <= kMaxRecoveryAttempts; ++attempt) {
            last = request_with_transport_recovery_locked("POST", path, body,
                                                          content_type, 50s);
            if (last.transport_ok && !should_restart_after_decrypt(last)) {
                return last;
            }
            if (!last.transport_ok || attempt == kMaxRecoveryAttempts) {
                return last;
            }

            std::fprintf(stderr,
                         "supervisor: restarting worker after decrypt response "
                         "status=%d (attempt %d/%d)\n",
                         last.status, attempt, kMaxRecoveryAttempts);
            if (!restart_after_delay("decrypt response", attempt)) {
                WorkerHttpResponse out;
                out.error = "worker restart failed after decrypt response";
                return out;
            }
        }

        return last;
    }

    WorkerHttpResponse request_with_login_recovery(const std::string& path,
                                                   const std::string& body,
                                                   const std::string& content_type) {
        std::lock_guard<std::mutex> lock(request_mu_);
        WorkerHttpResponse last;
        for (int attempt = 1; attempt <= kMaxRecoveryAttempts; ++attempt) {
            last = request_with_transport_recovery_locked("POST", path, body,
                                                          content_type, 35s);
            if (last.transport_ok && !should_restart_after_login(last)) {
                return last;
            }
            if (!last.transport_ok || attempt == kMaxRecoveryAttempts) {
                return last;
            }

            std::fprintf(stderr,
                         "supervisor: restarting worker after login response "
                         "status=%d (attempt %d/%d)\n",
                         last.status, attempt, kMaxRecoveryAttempts);
            if (!restart_after_delay("login response", attempt)) {
                WorkerHttpResponse out;
                out.error = "worker restart failed after login response";
                return out;
            }
        }

        return last;
    }

    WorkerHttpResponse request_with_transport_recovery_locked(
        const std::string& method,
        const std::string& path,
        const std::string& body,
        const std::string& content_type,
        std::chrono::seconds read_timeout) {
        WorkerHttpResponse out;
        for (int attempt = 1; attempt <= kMaxRecoveryAttempts; ++attempt) {
            if (!ensure_started()) {
                out.error = "worker did not become ready";
            } else {
                out = request_once(method, path, body, content_type, read_timeout);
                if (out.transport_ok) return out;
            }

            if (attempt == kMaxRecoveryAttempts) {
                return out;
            }

            std::fprintf(stderr,
                         "supervisor: worker transport failed (%s), restarting "
                         "(attempt %d/%d)\n",
                         out.error.c_str(), attempt, kMaxRecoveryAttempts);
            const std::string previous_error = out.error;
            if (!restart_after_delay("transport error", attempt)) {
                WorkerHttpResponse restart_error;
                restart_error.error = "worker restart failed after transport error: "
                                    + previous_error;
                return restart_error;
            }
        }
        return out;
    }

    bool restart_after_delay(const char* reason, int failed_attempt) {
        auto delay = std::chrono::seconds(1 << (failed_attempt - 1));
        if (delay > kMaxRecoveryRetryDelay) {
            delay = kMaxRecoveryRetryDelay;
        }
        std::fprintf(stderr, "supervisor: waiting %lds before retry after %s\n",
                     static_cast<long>(delay.count()), reason);
        std::this_thread::sleep_for(delay);
        return restart();
    }

    bool start_locked() {
        if (pid_ > 0) return true;

        pid_t child = fork();
        if (child < 0) {
            std::fprintf(stderr, "supervisor: fork worker failed\n");
            return false;
        }
        if (child == 0) {
            setenv("WRAPPER_HOST", kWorkerHost, 1);
            setenv("WRAPPER_PORT", std::to_string(worker_port_).c_str(), 1);
            setenv("WRAPPER_MODE", "worker", 1);
            execl(argv0_.c_str(), argv0_.c_str(), static_cast<char*>(nullptr));
            std::fprintf(stderr, "supervisor: exec worker failed: %s\n", argv0_.c_str());
            _exit(127);
        }

        pid_ = child;
        std::fprintf(stderr, "supervisor: started worker pid=%ld port=%d\n",
                     static_cast<long>(pid_), worker_port_);
        return true;
    }

    void stop_locked() {
        if (pid_ <= 0) return;

        const pid_t old = pid_;
        kill(old, SIGTERM);
        for (int i = 0; i < 30; ++i) {
            int status = 0;
            pid_t r = waitpid(old, &status, WNOHANG);
            if (r == old) {
                pid_ = -1;
                return;
            }
            usleep(100000);
        }

        kill(old, SIGKILL);
        int status = 0;
        (void)waitpid(old, &status, 0);
        pid_ = -1;
    }

    void reap_locked() {
        if (pid_ <= 0) return;
        int status = 0;
        pid_t r = waitpid(pid_, &status, WNOHANG);
        if (r == pid_) {
            std::fprintf(stderr, "supervisor: worker exited status=%d\n", status);
            pid_ = -1;
        }
    }

    bool wait_ready_locked(std::chrono::seconds timeout) {
        const auto deadline = std::chrono::steady_clock::now() + timeout;
        while (std::chrono::steady_clock::now() < deadline) {
            reap_locked();
            if (pid_ <= 0) return false;
            WorkerHttpResponse r = request_once_unlocked("GET", "/health", "", "", 2s);
            if (r.transport_ok) return true;
            usleep(200000);
        }
        return false;
    }

    void note_worker_start_result_locked(bool ok) {
        if (ok) {
            consecutive_worker_start_failures_ = 0;
            return;
        }
        ++consecutive_worker_start_failures_;
        std::fprintf(stderr,
                     "supervisor: worker start/restart failed (%d/%d)\n",
                     consecutive_worker_start_failures_,
                     kMaxConsecutiveWorkerStartFailures);
        if (consecutive_worker_start_failures_ >= kMaxConsecutiveWorkerStartFailures) {
            std::fprintf(stderr,
                         "supervisor: worker failed too many times; exiting supervisor\n");
            std::fflush(stderr);
            std::_Exit(kSupervisorFatalExitCode);
        }
    }

    WorkerHttpResponse request_once(const std::string& method,
                                    const std::string& path,
                                    const std::string& body,
                                    const std::string& content_type,
                                    std::chrono::seconds read_timeout) {
        std::lock_guard<std::mutex> lock(mu_);
        reap_locked();
        if (pid_ <= 0) {
            WorkerHttpResponse out;
            out.error = "worker is not running";
            return out;
        }
        return request_once_unlocked(method, path, body, content_type, read_timeout);
    }

    WorkerHttpResponse request_once_unlocked(const std::string& method,
                                             const std::string& path,
                                             const std::string& body,
                                             const std::string& content_type,
                                             std::chrono::seconds read_timeout) {
        httplib::Client cli(kWorkerHost, worker_port_);
        cli.set_connection_timeout(2, 0);
        cli.set_read_timeout(static_cast<time_t>(read_timeout.count()), 0);
        cli.set_write_timeout(10, 0);

        auto finish = [](httplib::Result res) {
            WorkerHttpResponse out;
            if (!res) {
                out.error = "httplib error " + std::to_string(static_cast<int>(res.error()));
                return out;
            }

            out.transport_ok = true;
            out.status = res->status;
            out.body = res->body;
            auto ct = res->get_header_value("Content-Type");
            if (!ct.empty()) out.content_type = std::move(ct);
            return out;
        };

        if (method == "GET") {
            return finish(cli.Get(path));
        }
        if (method == "DELETE") {
            return finish(cli.Delete(path));
        }
        if (method == "POST") {
            const std::string ct = content_type.empty() ? "application/octet-stream" : content_type;
            return finish(cli.Post(path, body, ct));
        }

        WorkerHttpResponse out;
        out.error = "unsupported proxy method";
        return out;
    }

    static bool should_restart_after_decrypt(const WorkerHttpResponse& r) {
        if (!r.transport_ok) return true;
        if (r.status != 502 && r.status != 500 && r.status != 504) return false;
        if (r.body.find("decrypt_failed") != std::string::npos) return true;
        if (r.body.find("FairPlay") != std::string::npos) return true;
        if (r.body.find("CKC") != std::string::npos) return true;
        if (r.body.find("KDCanProcess") != std::string::npos) return true;
        return false;
    }

    static bool should_restart_after_login(const WorkerHttpResponse& r) {
        if (!r.transport_ok) return true;
        if (r.status != 401) return false;
        return r.body.find("Apple returned response type 4") != std::string::npos;
    }

    static void proxy_response(httplib::Response& res, const WorkerHttpResponse& wr) {
        if (!wr.transport_ok) {
            res.status = 503;
            json body = {
                {"error", "worker_unavailable"},
                {"detail", wr.error},
            };
            res.set_content(body.dump(), "application/json");
            return;
        }
        res.status = wr.status;
        res.set_content(wr.body, wr.content_type);
    }

    std::string argv0_;
    std::string version_;
    int worker_port_ = 18080;
    pid_t pid_ = -1;
    int consecutive_worker_start_failures_ = 0;
    std::mutex mu_;
    std::mutex request_mu_;
};

Supervisor::Supervisor(std::string argv0, std::string version, int worker_port)
    : impl_(new Impl(std::move(argv0), std::move(version), worker_port)) {}

Supervisor::~Supervisor() {
    delete impl_;
}

void Supervisor::mount(httplib::Server& svr) {
    impl_->mount(svr);
}

void Supervisor::stop_worker() {
    impl_->stop_worker();
}

}  // namespace wrapper
