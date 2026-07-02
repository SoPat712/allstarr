// Account / login state for wrapper-v2.
//
// Apple-ID password sign-in flow driven through Apple's AuthenticateFlow. The
// flow is callback-based (Apple calls our credentialHandler inline
// from AuthenticateFlow::run()), so we run it on a dedicated worker
// thread and coordinate with the HTTP layer through a state machine
// plus a condvar that parks the credentialHandler while we wait for
// a 2FA code from POST /login/2fa.
//
// Threading model:
//   - One concurrent login flow at a time (the underlying RequestContext
//     is process-global; Apple's libs are not re-entrant on the auth
//     path).
//   - Account::instance() is a singleton so the static credentialHandler
//     trampoline (which has no user-data param) can reach state.
//   - All state mutation is mutex-protected; readers see consistent
//     snapshots.
//
// Persistence (mirrors upstream zhaarey/wrapper without -L):
//   - We never persist email/password. Apple writes mpl_db/kvs.sqlitedb
//     under WRAPPER_BASE_DIR when a real login has run at least once.
//   - On startup, try_restore_cached_session() may harvest storefront /
//     dev / music tokens from that persisted session (same as upstream
//     main() after skipping login()).
//   - Optional WRAPPER_APPLE_ID is only for labeling /me after restore;
//     it is not sent to Apple.

#pragma once

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <mutex>
#include <optional>
#include <string>
#include <thread>

#include "apple/abi.hpp"

namespace wrapper::apple {

class Loader;
class Runtime;

// Coarse-grained login state surfaced via /me.
enum class LoginState : std::uint8_t {
    LoggedOut,       // No credentials presented yet.
    InProgress,      // Worker thread is running AuthenticateFlow::run().
    Awaiting2FA,     // credentialHandler is parked waiting for a 6-digit code.
    Authenticated,   // flow.run() returned responseType=6 and tokens are cached.
    Failed,          // flow.run() reported an error; see last_error / last_code.
};

const char* to_string(LoginState s);

// Cached account-side data harvested after a successful flow.run().
// Returned (with secrets) only via Account::tokens(); a sanitized
// subset is exposed via Account::public_snapshot().
struct Tokens {
    std::string apple_id;
    std::string storefront;        // e.g. "143441" (US) or storefront/region string
    std::string dev_token;         // sf-api-token-service apiToken
    std::string music_user_token;  // createMusicToken result
    std::string dsid;              // decoded from dev_token JWT (empty if not extractable)
    std::chrono::system_clock::time_point logged_in_at{};
};

// Public-safe subset of Tokens for /me payloads.
struct AccountSnapshot {
    LoginState state = LoginState::LoggedOut;
    std::string apple_id;
    std::string storefront;
    std::string dsid;
    std::string music_user_token;
    std::string dev_token;
    std::chrono::system_clock::time_point logged_in_at{};

    // Set when state == Failed.
    std::string last_error;
    int last_error_code = 0;
};

class Account {
public:
    static Account& instance();

    // Detach (rather than join) any still-running worker at process
    // exit. The worker is likely sitting inside Apple's libs and we
    // can't safely cancel that synchronously - the OS will reap it.
    ~Account();

    // Returns false if a login is already in progress (InProgress or
    // Awaiting2FA). A fresh login request is allowed to replace an existing
    // authenticated snapshot, which lets clients recover from stale restored
    // Apple sessions without a separate DELETE /login. On true, the worker
    // thread has been started.
    bool start_login(const Loader& loader,
                     const Runtime& runtime,
                     std::string apple_id,
                     std::string password);

    // Hand a 2FA code to the parked credentialHandler. Returns false if
    // the state is not Awaiting2FA.
    bool submit_2fa(std::string code);

    // Reset to LoggedOut. If a worker thread is running, signals it to
    // exit at the next condvar wake. Tokens are wiped.
    void logout();

    // If state is LoggedOut and Apple already has a warm session on disk
    // (same pattern as upstream: init_ctx() without -L), harvest tokens
    // and become Authenticated. Returns false if no usable session.
    bool try_restore_cached_session(const Loader& loader, const Runtime& runtime);

    // Block (up to `timeout`) until state transitions out of InProgress.
    // Returns the resulting state. Used by POST /login and POST /login/2fa
    // to know when to send the HTTP response.
    LoginState wait_for_settled_state(std::chrono::milliseconds timeout);

    // Public-safe snapshot for /me.
    AccountSnapshot public_snapshot() const;

    LoginState state() const;

    // ---- Internal: only the credentialHandler trampoline should call these ----
    // Pull the current login attempt's credentials. Returns false if
    // there is no active login (e.g. the daemon never received /login).
    // If 2FA is required, this method blocks (up to a generous timeout)
    // until submit_2fa() is called or logout() aborts, then returns the
    // password+code concatenation upstream's flow expects.
    bool fetch_credentials_blocking(std::string* username,
                                    std::string* password,
                                    bool needs_2fa);

private:
    Account() = default;

    void worker_main();

    // Set by the worker after flow.run() finishes (success or failure).
    void finish_authenticated(Tokens t);
    void finish_failed(std::string message, int code);

    mutable std::mutex mu_;
    std::condition_variable cv_state_;       // notified on any state change
    std::condition_variable cv_2fa_;         // notified when 2FA code arrives or logout
    std::mutex restore_mu_;                  // serializes cached-session harvests
    std::atomic<LoginState> state_{LoginState::LoggedOut};

    // Inputs for the active login attempt.
    const Loader* loader_ = nullptr;
    const Runtime* runtime_ = nullptr;
    std::string apple_id_;
    std::string password_;
    std::string twofa_code_;
    bool twofa_submitted_ = false;
    bool aborted_ = false;

    // Outputs from a completed login.
    Tokens tokens_;
    std::string last_error_;
    int last_error_code_ = 0;

    std::thread worker_;
};

}  // namespace wrapper::apple
