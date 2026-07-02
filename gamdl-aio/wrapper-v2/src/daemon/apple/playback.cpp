#include "apple/playback.hpp"

#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include <nlohmann/json.hpp>

#include "apple/abi.hpp"
#include "apple/aarch64_sret_thunks.hpp"
#include "apple/loader.hpp"
#include "apple/runtime.hpp"

namespace wrapper::apple {

namespace {

// Forward declaration so we can construct a libc++ std::function whose
// mangled signature contains storeservicescore::URLResponse. Apple ships
// the same libc++ runtime as the NDK we cross-compile with, so the
// std::function and std::shared_ptr we build here are binary-compatible
// with the one URLRequest::setURLResponsePreprocessor expects.
struct URLResponseFwd;

using URLResponseSp     = std::shared_ptr<URLResponseFwd>;
using URLRespPreprocFn  = std::function<void(const URLResponseSp&)>;

constexpr unsigned long kCFStringEncodingUTF8 = 0x08000100;
constexpr long          kCFNumberSInt64Type   = 4;
constexpr long          kCFNumberFloat64Type  = 6;

// CFAbsoluteTime is seconds since 2001-01-01 00:00:00 UTC. Unix epoch
// (1970-01-01) is 978307200 seconds earlier.
constexpr double kCFAbsoluteTimeIntervalSince1970 = 978307200.0;

// Standard base64 alphabet (no URL-safe variant). Used to encode CFData
// payloads so the JSON document stays string-friendly.
std::string base64_encode(const unsigned char* data, std::size_t len) {
    static const char tbl[] =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string ret;
    ret.reserve(((len + 2) / 3) * 4);
    unsigned int val  = 0;
    int          valb = -6;
    for (std::size_t i = 0; i < len; ++i) {
        val = (val << 8) + data[i];
        valb += 8;
        while (valb >= 0) {
            ret.push_back(tbl[(val >> valb) & 0x3F]);
            valb -= 6;
        }
    }
    if (valb > -6) {
        ret.push_back(tbl[((val << 8) >> (valb + 8)) & 0x3F]);
    }
    while (ret.size() % 4 != 0) ret.push_back('=');
    return ret;
}

// Read a CFString out as UTF-8. Tries the fast pointer-aliased path first
// (CFStringGetCStringPtr) and falls back to a copy via CFStringGetCString
// for strings whose internal encoding is not UTF-8.
std::string read_cf_string(const Symbols& s, const void* str) {
    if (str == nullptr) return std::string();
    const char* fast = s.CFStringGetCStringPtr(str, kCFStringEncodingUTF8);
    if (fast != nullptr) return std::string(fast);
    long len = s.CFStringGetLength(str);
    if (len <= 0) return std::string();
    long max = s.CFStringGetMaximumSizeForEncoding(len, kCFStringEncodingUTF8) + 1;
    std::vector<char> buf(static_cast<std::size_t>(max), 0);
    if (s.CFStringGetCString(str, buf.data(), max, kCFStringEncodingUTF8) == 0) {
        return std::string();
    }
    return std::string(buf.data());
}

// Recursive CFPropertyList -> nlohmann::json walker. Handles the seven
// PList types Apple actually uses in its store responses; anything else
// falls through to JSON null. CFData is base64-encoded; CFDate is
// rendered as ISO-8601 UTC.
nlohmann::json cf_to_json(const Symbols& s, const void* cf) {
    if (cf == nullptr) return nullptr;
    const unsigned long tid = s.CFGetTypeID(cf);

    if (tid == s.CFDictionaryGetTypeID()) {
        nlohmann::json obj = nlohmann::json::object();
        long n = s.CFDictionaryGetCount(cf);
        if (n > 0) {
            std::vector<const void*> keys(static_cast<std::size_t>(n));
            std::vector<const void*> vals(static_cast<std::size_t>(n));
            s.CFDictionaryGetKeysAndValues(cf, keys.data(), vals.data());
            for (long i = 0; i < n; ++i) {
                if (keys[i] == nullptr) continue;
                if (s.CFGetTypeID(keys[i]) != s.CFStringGetTypeID()) {
                    // Apple's MZ-protocol plists only ever use string keys;
                    // skipping a non-string key is the closest sane mapping.
                    continue;
                }
                std::string k = read_cf_string(s, keys[i]);
                if (k.empty()) continue;
                obj[k] = cf_to_json(s, vals[i]);
            }
        }
        return obj;
    }
    if (tid == s.CFArrayGetTypeID()) {
        nlohmann::json arr = nlohmann::json::array();
        long n = s.CFArrayGetCount(cf);
        for (long i = 0; i < n; ++i) {
            const void* el = s.CFArrayGetValueAtIndex(cf, i);
            arr.push_back(cf_to_json(s, el));
        }
        return arr;
    }
    if (tid == s.CFStringGetTypeID()) {
        return read_cf_string(s, cf);
    }
    if (tid == s.CFNumberGetTypeID()) {
        if (s.CFNumberIsFloatType(cf) != 0) {
            double v = 0;
            s.CFNumberGetValue(cf, kCFNumberFloat64Type, &v);
            return v;
        }
        std::int64_t v = 0;
        s.CFNumberGetValue(cf, kCFNumberSInt64Type, &v);
        return v;
    }
    if (tid == s.CFBooleanGetTypeID()) {
        return s.CFBooleanGetValue(cf) != 0;
    }
    if (tid == s.CFDataGetTypeID()) {
        const unsigned char* p = s.CFDataGetBytePtr(cf);
        long n = s.CFDataGetLength(cf);
        if (p == nullptr || n <= 0) return std::string();
        return base64_encode(p, static_cast<std::size_t>(n));
    }
    if (tid == s.CFDateGetTypeID()) {
        double abs_t = s.CFDateGetAbsoluteTime(cf);
        double unix_secs = abs_t + kCFAbsoluteTimeIntervalSince1970;
        std::time_t t = static_cast<std::time_t>(unix_secs);
        std::tm tm_utc{};
#if defined(_WIN32)
        gmtime_s(&tm_utc, &t);
#else
        gmtime_r(&t, &tm_utc);
#endif
        char buf[32];
        std::strftime(buf, sizeof(buf), "%Y-%m-%dT%H:%M:%SZ", &tm_utc);
        return std::string(buf);
    }
    if (tid == s.CFNullGetTypeID()) {
        return nullptr;
    }
    return nullptr;
}

// PurchaseItem is a thin wrapper: its ctor stores a retained CFDictionaryRef
// at object offset 0 (libstoreservicescore PurchaseItem(__CFDictionary const*)).
// In the pinned APK, PurchaseItem::dictionary() const is compiled as "return this"
// (disassembly: mov %rdi,%rax; ret) — not the plist pointer. CFGetTypeID(this)
// is undefined and often yields 0. The real dispatch plist is *(void**)item.
void* purchase_item_cfdictionary(const Symbols& s, void* item_obj) {
    if (item_obj == nullptr) return nullptr;
    void* from_member = *static_cast<void**>(item_obj);
    if (s.PurchaseItem_dictionary != nullptr) {
        void* ret = s.PurchaseItem_dictionary(item_obj);
        if (ret != nullptr && ret != item_obj
            && s.CFGetTypeID(ret) == s.CFDictionaryGetTypeID()) {
            return ret;
        }
    }
    return from_member;
}

// Format-string buy parameters that match upstream wrapper main.c's
// get_m3u8_method_download. The "S" productType + "SUBS" pricing
// parameters are what tells the storefront we want the subscriber
// download dispatch (full plist with every variant), not the streaming
// HLS asset only.
std::string make_buy_parameters(const std::string& adam_id) {
    // adam_id is validated as digits-only by the caller; concatenate
    // directly to avoid sprintf format-string risk.
    std::string out;
    out.reserve(64 + adam_id.size());
    out.append("salableAdamId=");
    out.append(adam_id);
    out.append("&price=0&pricingParameters=SUBS&productType=S");
    return out;
}

}  // namespace

PlaybackResult fetch_playback_json(const Loader& loader,
                                   Runtime&      runtime,
                                   std::string   adam_id) {
    PlaybackResult out;

    if (!loader.ok()) {
        out.error = "Apple libs not loaded";
        return out;
    }
    if (!runtime.initialized()) {
        out.error = "Apple runtime not initialized";
        return out;
    }
    if (adam_id.empty()) {
        out.error = "adam_id is required";
        return out;
    }
    for (char c : adam_id) {
        if (c < '0' || c > '9') {
            out.error = "adam_id must be a numeric string";
            return out;
        }
    }

    const Symbols& s = loader.sym();

    abi::shared_ptr req_ctx = runtime.request_ctx_copy();
    if (req_ctx.obj == nullptr) {
        out.error = "RequestContext unavailable";
        return out;
    }

    // Captured dict + mutex outlive the lambda copies that std::function
    // makes; the lambda holds them by pointer so all copies share state.
    struct Capture {
        std::mutex mu;
        void*      cf_dict = nullptr;  // CFDictionaryRef (retained)
        int        hits    = 0;
    };
    Capture cap;

    URLRespPreprocFn preproc = [cap_ptr = &cap, &s](const URLResponseSp& sp) {
        void* url_resp = static_cast<void*>(sp.get());
        if (url_resp == nullptr) return;
        if (s.URLResponse_protocolDictionary == nullptr) return;
        void* dict = s.URLResponse_protocolDictionary(url_resp);
        if (dict == nullptr) return;
        // CFRetain so the dict outlives Apple's URLResponse, which may be
        // dropped immediately after the preprocessor returns.
        void* retained = s.CFRetain(dict);
        std::lock_guard<std::mutex> g(cap_ptr->mu);
        if (cap_ptr->cf_dict != nullptr) {
            // Multiple hits should not happen for a single PurchaseRequest
            // run, but if it did we'd leak the previous; release first.
            s.CFRelease(cap_ptr->cf_dict);
        }
        cap_ptr->cf_dict = retained != nullptr ? retained : dict;
        ++cap_ptr->hits;
    };

    // PurchaseRequest is heavy-ish; upstream uses malloc(1024) and never frees.
    // Keep one 8 KiB thread-local buffer and skip destruction, matching the
    // upstream lifetime model while leaving extra room for arm64 object size
    // and alignment.
    alignas(16) static thread_local std::uint8_t pr_buf[8192];
    std::memset(pr_buf, 0, sizeof(pr_buf));

    s.PurchaseRequest_ctor(pr_buf, &req_ctx);
    std::uint8_t one = 1;
    s.PurchaseRequest_setProcessDialogActions(pr_buf, one);

    auto url_bag_key = abi::make_string_view("subDownload");
    s.PurchaseRequest_setURLBagKey(pr_buf, &url_bag_key);

    std::string buy = make_buy_parameters(adam_id);
    auto buy_view   = abi::make_string_view(buy.c_str());
    s.PurchaseRequest_setBuyParameters(pr_buf, &buy_view);

    // PurchaseRequest derives from URLRequest with URLRequest as the first
    // base (offset 0 - confirmed by upstream's pattern of calling URLRequest
    // methods on a PurchaseRequest* directly). Passing pr_buf as the
    // URLRequest::setURLResponsePreprocessor `this` is therefore safe.
    s.URLRequest_setURLResponsePreprocessor(pr_buf, &preproc);

    s.PurchaseRequest_run(pr_buf);

    // After run() returns, the preprocessor has either captured the dict
    // or didn't fire (the request failed before reaching the parsed-response
    // stage). Either way, no more async invocations are possible.
    void* dict = nullptr;
    {
        std::lock_guard<std::mutex> g(cap.mu);
        dict = cap.cf_dict;
        cap.cf_dict = nullptr;
    }

    // Pull the PurchaseResponse for two things:
    //   (1) Surface Apple's StoreErrorCondition (if any) for diagnostics.
    //   (2) Fallback: when the preprocessor didn't fire (PurchaseRequest::run
    //       bypasses URLRequest::_performActionsForResponse), walk
    //       PurchaseResponse::items() and serialize each item's dictionary
    //       into a synthetic { "songList": [...] } root dict.
    void*       purchase_response_obj = nullptr;
    std::string apple_err;
    if (s.PurchaseRequest_response != nullptr) {
        abi::shared_ptr* resp_sp = s.PurchaseRequest_response(pr_buf);
        if (resp_sp != nullptr && resp_sp->obj != nullptr) {
            purchase_response_obj = resp_sp->obj;
            if (s.PurchaseResponse_error != nullptr) {
                abi::shared_ptr* err_sp =
                    s.PurchaseResponse_error(purchase_response_obj);
                if (err_sp != nullptr && err_sp->obj != nullptr) {
                    int code = (s.SEC_errorCode != nullptr)
                                 ? s.SEC_errorCode(err_sp->obj) : 0;
                    const char* what = (s.SEC_what != nullptr)
                                         ? s.SEC_what(err_sp->obj) : nullptr;
                    apple_err = "Apple store error code=" + std::to_string(code);
                    if (what != nullptr && *what != '\0') {
                        apple_err += ": ";
                        apple_err += what;
                    }
                }
            }
        }
    }

    // Preferred path: preprocessor captured the full protocol dict.
    if (dict != nullptr) {
        out.body = cf_to_json(s, dict);
        s.CFRelease(dict);
        out.ok = true;
        return out;
    }

    // Fallback: walk PurchaseResponse::items() and build the JSON document
    // directly (one cf_to_json per item, then wrap in {"songList": [...]}).
    // This is the expected path - PurchaseRequest::run does not fire
    // URLRequest::_performActionsForResponse.
    if (purchase_response_obj != nullptr
        && s.PurchaseResponse_items != nullptr) {
        abi::std_vector items{};
        aarch64_sret::purchase_response_items(&items, purchase_response_obj,
                                              s.PurchaseResponse_items);

        const auto* begin = static_cast<abi::shared_ptr*>(items.begin);
        const auto* end   = static_cast<abi::shared_ptr*>(items.end);
        const std::size_t count = (end > begin)
            ? static_cast<std::size_t>(end - begin) : 0;

        nlohmann::json song_list = nlohmann::json::array();
        for (std::size_t i = 0; i < count; ++i) {
            void* item_obj = begin[i].obj;
            if (item_obj == nullptr) {
                continue;
            }
            void* item_dict = purchase_item_cfdictionary(s, item_obj);
            if (item_dict == nullptr) continue;
            song_list.push_back(cf_to_json(s, item_dict));
        }

        // Note: we deliberately leak `items` (a std::vector with N shared_ptr
        // elements). Calling its destructor would require the vector's dtor
        // symbol; same shortcut upstream wrapper takes with similar return-
        // by-value results. ~16 bytes per item.

        if (!song_list.empty()) {
            out.body = nlohmann::json::object();
            out.body["songList"] = std::move(song_list);
            out.ok = true;
            return out;
        }
    }

    if (!apple_err.empty()) {
        out.error = std::move(apple_err);
    } else if (purchase_response_obj == nullptr) {
        out.error = "PurchaseRequest produced no response object "
                    "(likely an HTTP error before parsing)";
    } else {
        out.error = "PurchaseResponse has no items and no error "
                    "(Apple returned an empty subDownload payload)";
    }
    return out;
}

}  // namespace wrapper::apple
