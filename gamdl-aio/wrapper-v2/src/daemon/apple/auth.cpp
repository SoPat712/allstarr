#include "apple/auth.hpp"

#include <cstdlib>
#include <cstdio>
#include <cstring>
#include <utility>

#include <sys/stat.h>
#include <unistd.h>

#include "apple/aarch64_sret_thunks.hpp"
#include "apple/loader.hpp"
#include "apple/runtime.hpp"
#include "apple/tokens.hpp"

namespace wrapper::apple {

namespace {

void unlink_quiet(const char* path) {
    if (path == nullptr || *path == '\0') return;
    (void)::unlink(path);
}

// Apple writes mpl_db/kvs.sqlitedb the first time AuthenticateFlow succeeds.
// Without that file, RequestContext::storeFrontIdentifier on arm64 dereferences
// uninitialized internal state at offset 0x10 and SIGSEGVs (x86_64 returns an
// empty string instead). Gate the cached-session probe on this file so a cold
// `./data` mount does not crash the daemon at startup.
bool warm_session_present(const std::string& base_dir) {
    if (base_dir.empty()) return false;
    const std::string kvs = base_dir + "/mpl_db/kvs.sqlitedb";
    struct stat st{};
    if (::stat(kvs.c_str(), &st) != 0) return false;
    return S_ISREG(st.st_mode) && st.st_size > 0;
}

}  // namespace

const char* to_string(LoginState s) {
    switch (s) {
        case LoginState::LoggedOut:     return "logged_out";
        case LoginState::InProgress:    return "in_progress";
        case LoginState::Awaiting2FA:   return "awaiting_2fa";
        case LoginState::Authenticated: return "authenticated";
        case LoginState::Failed:        return "failed";
    }
    return "unknown";
}

Account& Account::instance() {
    static Account a;
    return a;
}

Account::~Account() {
    if (worker_.joinable()) {
        worker_.detach();
    }
}

bool Account::start_login(const Loader& loader,
                          const Runtime& runtime,
                          std::string apple_id,
                          std::string password) {
    std::unique_lock<std::mutex> g(mu_);
    LoginState s = state_.load(std::memory_order_acquire);
    if (s == LoginState::InProgress || s == LoginState::Awaiting2FA) {
        return false;
    }

    // Drain a previous worker thread, if any. join() outside the lock
    // would risk re-entering Account from the worker; we take the
    // hit of joining under the lock since the worker has already
    // transitioned out of the busy states. This also allows POST /login
    // to refresh an existing Authenticated snapshot after a stale restore.
    if (worker_.joinable()) {
        worker_.join();
    }

    loader_   = &loader;
    runtime_  = &runtime;
    apple_id_ = std::move(apple_id);
    password_ = std::move(password);
    twofa_code_.clear();
    twofa_submitted_ = false;
    aborted_ = false;
    tokens_ = Tokens{};
    last_error_.clear();
    last_error_code_ = 0;

    state_.store(LoginState::InProgress, std::memory_order_release);
    cv_state_.notify_all();

    worker_ = std::thread(&Account::worker_main, this);
    return true;
}

bool Account::submit_2fa(std::string code) {
    std::lock_guard<std::mutex> g(mu_);
    if (state_.load(std::memory_order_acquire) != LoginState::Awaiting2FA) {
        return false;
    }
    twofa_code_ = std::move(code);
    twofa_submitted_ = true;
    state_.store(LoginState::InProgress, std::memory_order_release);
    cv_state_.notify_all();
    cv_2fa_.notify_all();
    return true;
}

bool Account::try_restore_cached_session(const Loader& loader, const Runtime& runtime) {
    std::lock_guard<std::mutex> restore_guard(restore_mu_);

    if (!loader.ok() || !runtime.initialized()) {
        return false;
    }

    std::string bd = runtime.base_dir();
    bool warm = warm_session_present(bd);
    if (!warm) {
        std::fprintf(stderr,
                     "auth: no warm Apple session at %s/mpl_db/kvs.sqlitedb; "
                     "skipping cached-session restore\n",
                     bd.c_str());
        return false;
    }

    {
        std::lock_guard<std::mutex> g(mu_);
        if (state_.load(std::memory_order_acquire) != LoginState::LoggedOut) {
            return false;
        }
        if (worker_.joinable()) {
            worker_.join();
        }
        loader_   = &loader;
        runtime_  = &runtime;
        password_.clear();
        twofa_code_.clear();
        twofa_submitted_ = false;
        aborted_         = false;
        tokens_          = Tokens{};
        last_error_.clear();
        last_error_code_ = 0;
        state_.store(LoginState::InProgress, std::memory_order_release);
        cv_state_.notify_all();
    }

    const Symbols& s         = loader.sym();
    auto           req_ctx = runtime.request_ctx_copy();
    if (req_ctx.obj == nullptr) {
        std::lock_guard<std::mutex> g(mu_);
        loader_  = nullptr;
        runtime_ = nullptr;
        state_.store(LoginState::LoggedOut, std::memory_order_release);
        cv_state_.notify_all();
        return false;
    }
    auto device_guid = runtime.device_guid_copy();
    Tokens t;
    if (!tokens::harvest_all(s, req_ctx, device_guid, &t)) {
        std::fprintf(stderr,
                     "auth: cached-session restore found Apple session files "
                     "but token harvest failed\n");
        std::lock_guard<std::mutex> g(mu_);
        loader_  = nullptr;
        runtime_ = nullptr;
        state_.store(LoginState::LoggedOut, std::memory_order_release);
        cv_state_.notify_all();
        return false;
    }

    if (const char* label = std::getenv("WRAPPER_APPLE_ID")) {
        if (label[0] != '\0') {
            t.apple_id = label;
        }
    }

    finish_authenticated(std::move(t));
    return true;
}

void Account::logout() {
    {
        std::lock_guard<std::mutex> g(mu_);
        aborted_ = true;
        cv_2fa_.notify_all();
        cv_state_.notify_all();
    }
    if (worker_.joinable()) {
        worker_.join();
    }
    std::lock_guard<std::mutex> g(mu_);
    state_.store(LoginState::LoggedOut, std::memory_order_release);
    apple_id_.clear();
    password_.clear();
    twofa_code_.clear();
    twofa_submitted_ = false;
    aborted_ = false;
    tokens_ = Tokens{};
    last_error_.clear();
    last_error_code_ = 0;
    cv_state_.notify_all();
}

LoginState Account::wait_for_settled_state(std::chrono::milliseconds timeout) {
    std::unique_lock<std::mutex> g(mu_);
    cv_state_.wait_for(g, timeout, [this] {
        auto s = state_.load(std::memory_order_acquire);
        return s != LoginState::InProgress;
    });
    return state_.load(std::memory_order_acquire);
}

LoginState Account::state() const {
    return state_.load(std::memory_order_acquire);
}

AccountSnapshot Account::public_snapshot() const {
    std::lock_guard<std::mutex> g(mu_);
    AccountSnapshot snap;
    snap.state = state_.load(std::memory_order_acquire);
    snap.apple_id = apple_id_;
    if (snap.state == LoginState::Authenticated) {
        snap.storefront        = tokens_.storefront;
        snap.dsid              = tokens_.dsid;
        snap.music_user_token  = tokens_.music_user_token;
        snap.dev_token         = tokens_.dev_token;
        snap.logged_in_at      = tokens_.logged_in_at;
    }
    if (snap.state == LoginState::Failed) {
        snap.last_error      = last_error_;
        snap.last_error_code = last_error_code_;
    }
    return snap;
}

bool Account::fetch_credentials_blocking(std::string* username,
                                        std::string* password,
                                        bool needs_2fa) {
    std::unique_lock<std::mutex> g(mu_);

    if (!needs_2fa) {
        *username = apple_id_;
        *password = password_;
        return !apple_id_.empty();
    }

    // Park here as Awaiting2FA. submit_2fa() or logout() will wake us.
    state_.store(LoginState::Awaiting2FA, std::memory_order_release);
    cv_state_.notify_all();

    constexpr auto k2faTimeout = std::chrono::minutes(5);
    cv_2fa_.wait_for(g, k2faTimeout, [this] {
        return twofa_submitted_ || aborted_;
    });

    state_.store(LoginState::InProgress, std::memory_order_release);
    cv_state_.notify_all();

    *username = apple_id_;
    if (aborted_ || !twofa_submitted_) {
        // No code arrived in time. Returning empty creds lets the flow
        // resolve cleanly with an auth error rather than hang.
        *password = password_;
        return false;
    }

    // Upstream's quirk: AuthenticateFlow on Android expects the 2FA
    // code to be appended to the password field rather than delivered
    // as a separate input.
    *password = password_ + twofa_code_;
    return true;
}

void Account::finish_authenticated(Tokens t) {
    std::lock_guard<std::mutex> g(mu_);
    tokens_ = std::move(t);
    tokens_.logged_in_at = std::chrono::system_clock::now();
    apple_id_ = tokens_.apple_id;
    // Wipe the password from memory now that the flow is done.
    password_.assign(password_.size(), '\0');
    password_.clear();
    twofa_code_.assign(twofa_code_.size(), '\0');
    twofa_code_.clear();
    twofa_submitted_ = false;
    state_.store(LoginState::Authenticated, std::memory_order_release);
    cv_state_.notify_all();
}

void Account::finish_failed(std::string message, int code) {
    std::lock_guard<std::mutex> g(mu_);
    last_error_ = std::move(message);
    last_error_code_ = code;
    password_.assign(password_.size(), '\0');
    password_.clear();
    twofa_code_.assign(twofa_code_.size(), '\0');
    twofa_code_.clear();
    twofa_submitted_ = false;
    state_.store(LoginState::Failed, std::memory_order_release);
    cv_state_.notify_all();
}

void Account::worker_main() {
    // Worker thread: drive AuthenticateFlow::run() to completion, then
    // harvest tokens or record the failure. All Apple calls below are
    // unchecked: a crash inside Apple's libs takes the daemon down
    // rather than masking the failure.
    if (loader_ == nullptr || !loader_->ok() || runtime_ == nullptr || !runtime_->initialized()) {
        finish_failed("runtime not initialized", -1);
        return;
    }
    const Symbols& s = loader_->sym();
    auto req_ctx = runtime_->request_ctx_copy();
    if (req_ctx.obj == nullptr) {
        finish_failed("request context unavailable", -2);
        return;
    }

    /* Upstream login() removes these before each AuthenticateFlow so stale
       session files do not confuse the flow. */
    {
        std::string bd = runtime_->base_dir();
        if (!bd.empty()) {
            unlink_quiet((bd + "/STOREFRONT_ID").c_str());
            unlink_quiet((bd + "/MUSIC_TOKEN").c_str());
        }
    }

    abi::shared_ptr flow;
    aarch64_sret::make_shared_authenticate_flow(&flow, &req_ctx,
                                                s.make_shared_AuthenticateFlow);
    if (flow.obj == nullptr) {
        finish_failed("make_shared<AuthenticateFlow> returned null", -3);
        return;
    }
    s.AuthenticateFlow_run(flow.obj);

    abi::shared_ptr* resp = s.AuthenticateFlow_response(flow.obj);
    if (resp == nullptr || resp->obj == nullptr) {
        finish_failed("AuthenticateFlow::response() returned null", -4);
        return;
    }

    int resp_type = s.AR_responseType(resp->obj);
    if (resp_type != 6) {
        std::string msg;
        int code = 0;

        abi::shared_ptr* err = s.AR_error(resp->obj);
        if (err != nullptr && err->obj != nullptr) {
            code = s.SEC_errorCode(err->obj);
            const char* what = s.SEC_what(err->obj);
            if (what != nullptr) msg = what;
        }
        if (msg.empty()) {
            abi::std_string* customer = s.AR_customerMessage(resp->obj);
            if (customer != nullptr) {
                bool is_long = (customer->_long.cap & 1) != 0;
                const char* p = is_long ? customer->_long.data
                                        : customer->_short.str;
                if (p && *p) msg = p;
            }
        }
        if (msg.empty()) {
            msg = "Apple returned response type " + std::to_string(resp_type);
        }
        finish_failed(std::move(msg), code);
        return;
    }

    Tokens t;
    t.apple_id = apple_id_;
    if (!tokens::harvest_all(s, runtime_->request_ctx_copy(),
                             runtime_->device_guid_copy(), &t)) {
        finish_failed("token harvest failed after flow.run() succeeded", -5);
        return;
    }
    finish_authenticated(std::move(t));
}

}  // namespace wrapper::apple
