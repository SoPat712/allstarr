#include "apple/tokens.hpp"

#include <chrono>
#include <cstdio>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <string>
#include <string_view>
#include <vector>

#include <nlohmann/json.hpp>

#include "apple/aarch64_sret_thunks.hpp"
#include "apple/auth.hpp"
#include "apple/loader.hpp"

namespace wrapper::apple::tokens {

namespace {

// Apple's URLResponse body occasionally contains bytes after the first JSON
// object (garbage or a second payload). nlohmann rejects trailing non-space.
// Upstream cJSON is sometimes more permissive; we parse only the first
// balanced `{...}` slice.
std::string normalize_apple_json_body(std::string_view raw) {
    std::string s(raw);
    if (s.size() >= 3 && static_cast<unsigned char>(s[0]) == 0xEF &&
        static_cast<unsigned char>(s[1]) == 0xBB &&
        static_cast<unsigned char>(s[2]) == 0xBF) {
        s.erase(0, 3);
    }
    if (s.size() >= 4 && s[0] == ')' && s[1] == ']' && s[2] == '}' &&
        s[3] == '\'') {
        const auto nl = s.find('\n');
        if (nl != std::string::npos) {
            s.erase(0, nl + 1);
        } else {
            s.erase(0, 4);
        }
    }
    return s;
}

std::string_view first_json_object(std::string_view s) {
    const std::size_t start = s.find('{');
    if (start == std::string_view::npos) return {};
    int depth = 0;
    bool in_str = false;
    bool esc = false;
    for (std::size_t j = start; j < s.size(); ++j) {
        const unsigned char c = static_cast<unsigned char>(s[j]);
        if (in_str) {
            if (esc) {
                esc = false;
            } else if (c == '\\') {
                esc = true;
            } else if (c == '"') {
                in_str = false;
            }
        } else {
            if (c == '"') {
                in_str = true;
            } else if (c == '{') {
                ++depth;
            } else if (c == '}') {
                --depth;
                if (depth == 0) {
                    return s.substr(start, j - start + 1);
                }
            }
        }
    }
    return {};
}

bool parse_apple_response_json(const std::string& body, nlohmann::json* out) {
    const std::string norm = normalize_apple_json_body(body);
    const std::string_view obj = first_json_object(norm);
    if (obj.empty()) {
        return false;
    }
    try {
        *out = nlohmann::json::parse(obj);
        return true;
    } catch (const std::exception&) {
        return false;
    }
}

// Read a std::__ndk1::basic_string out of Apple-land into a host string.
std::string read_std_string(const abi::std_string& s) {
    const bool is_long = (s._long.cap & 1u) != 0;
    if (is_long) {
        if (s._long.data == nullptr || s._long.size == 0) return {};
        return std::string(s._long.data, s._long.size);
    }
    const std::size_t len = s._short.mark >> 1;
    return std::string(s._short.str, len);
}

// HTTPMessage: manual __shared_ptr_emplace layout; +32 is the HTTPMessage
// object. The emplace/control-block storage must outlive this C++ stack frame:
// URLRequest / HTTPRequest can keep shared/weak references after run() returns,
// and a later request may release them. If this storage lives on the stack, the
// second token request can end up touching a reused/zeroed control block and
// crash through a null vtable (arm64 commonly reports fault_addr=0x10).
struct HTTPMessageHolder {
    static constexpr std::size_t kSize = 4096;
    std::uint8_t* buf = nullptr;
    abi::shared_ptr sp{};

    void init(void** vtable) {
        void* raw = nullptr;
        if (::posix_memalign(&raw, 16, kSize) != 0 || raw == nullptr) {
            std::fprintf(stderr, "tokens: HTTPMessageHolder allocation failed\n");
            std::fflush(stderr);
            std::abort();
        }
        buf = static_cast<std::uint8_t*>(raw);
        std::memset(buf, 0, kSize);
        *reinterpret_cast<void**>(buf) = vtable + 2;
        sp.obj      = buf + 32;
        sp.ctrl_blk = buf;
    }
};

// JWT base64url decoder. Returns an empty string on malformed input.
std::string base64url_decode(const std::string& in) {
    static const int8_t T[256] = {
        // 0..63 = legal char, else -1
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1, -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,62,-1,-1, 52,53,54,55,56,57,58,59,60,61,-1,-1,-1, 0,-1,-1,
        -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9,10,11,12,13,14, 15,16,17,18,19,20,21,22,23,24,25,-1,-1,-1,-1,63,
        -1,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40, 41,42,43,44,45,46,47,48,49,50,51,-1,-1,-1,-1,-1,
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1, -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1, -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1, -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1, -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    };
    std::string out;
    out.reserve(in.size() * 3 / 4);
    int bits = 0, val = 0;
    for (unsigned char c : in) {
        int v = T[c];
        if (v < 0) {
            if (c == '=' || c == '\r' || c == '\n') continue;
            return {};
        }
        val = (val << 6) | v;
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            out.push_back(static_cast<char>((val >> bits) & 0xff));
        }
    }
    return out;
}

void set_str_header(const Symbols& s, void* http_msg_obj,
                    const char* name, const char* value) {
    auto n = abi::make_string_view(name);
    auto v = abi::make_string_view(value);
    s.HTTPMessage_setHeader(http_msg_obj, &n, &v);
}

void set_url_param(const Symbols& s, void* url_req_obj,
                   const char* name, const char* value) {
    auto n = abi::make_string_view(name);
    auto v = abi::make_string_view(value);
    s.URLRequest_setRequestParameter(url_req_obj, &n, &v);
}

void construct_http_message(const Symbols& s,
                            HTTPMessageHolder& msg,
                            const char* url,
                            const char* method) {
    // HTTPMessage(url, method) takes std::string by value. Use real
    // std::string temporaries so libc++ constructs/destructs valid objects
    // instead of treating an abi::std_string view as an owned string.
    if (s.HTTPMessage_ctor_c1 != nullptr) {
        s.HTTPMessage_ctor_c1(msg.sp.obj, std::string(url), std::string(method));
    } else {
        s.HTTPMessage_ctor(msg.sp.obj, std::string(url), std::string(method));
    }
}

struct UrlRequestResult {
    std::string body;
    int  error_code = 0;
    std::string error_what;
    bool ok = false;
};

// Run an HTTPMessage through URLRequest with the given RequestContext
// and return the response body. Optional `params` populates GET-style
// query parameters via URLRequest::setRequestParameter.
UrlRequestResult run_request(
        const Symbols& s,
        abi::shared_ptr req_ctx,
        HTTPMessageHolder& msg,
        const std::vector<std::pair<std::string, std::string>>& params) {
    UrlRequestResult r;

    // Process-lifetime storage: URLRequest/HTTPRequest can retain references
    // beyond run(), so do not back this with a stack buffer.
    constexpr std::size_t kUrlReqSize = 8192;
    void* url_req_raw = nullptr;
    if (::posix_memalign(&url_req_raw, 16, kUrlReqSize) != 0 || url_req_raw == nullptr) {
        std::fprintf(stderr, "tokens: run_request: posix_memalign failed\n");
        std::fflush(stderr);
        return r;
    }
    std::uint8_t* url_req = static_cast<std::uint8_t*>(url_req_raw);
    std::memset(url_req, 0, kUrlReqSize);
    s.URLRequest_ctor(url_req, &msg.sp, &req_ctx);

    for (const auto& p : params) {
        set_url_param(s, url_req, p.first.c_str(), p.second.c_str());
    }

    aarch64_sret::urlrequest_run(url_req, s.URLRequest_run);

    abi::shared_ptr* err = s.URLRequest_error(url_req);
    if (err != nullptr && err->obj != nullptr) {
        r.error_code = s.SEC_errorCode(err->obj);
        const char* what = s.SEC_what(err->obj);
        if (what != nullptr) r.error_what = what;
        return r;
    }

    abi::shared_ptr* url_resp = s.URLRequest_response(url_req);
    if (url_resp == nullptr || url_resp->obj == nullptr) return r;
    abi::shared_ptr* inner = s.URLResponse_underlyingResponse(url_resp->obj);
    if (inner == nullptr || inner->obj == nullptr) return r;

    // Upstream pattern: at offset +48 from HTTPMessage there's a
    // pointer to a mediaplatform::Data whose bytes() yields the body.
    void* http_msg_obj = inner->obj;
    void** data_slot = reinterpret_cast<void**>(static_cast<std::uint8_t*>(http_msg_obj) + 48);
    void* data_obj = *data_slot;
    if (data_obj == nullptr) return r;
    const char* bytes = s.Data_bytes(data_obj);
    if (bytes == nullptr) return r;
    r.body = std::string(bytes);
    r.ok = true;
    return r;
}

// POST JSON body via URLRequest (uses Apple's signing headers on req_ctx).
std::string post_json(const Symbols& s,
                      abi::shared_ptr req_ctx,
                      HTTPMessageHolder& msg,
                      const std::string& body_json) {
    // Upstream uses a malloc'd + snprintf buffer and frees immediately after
    // setBodyData. libc++ std::string::data() is null-terminated, so we pass
    // the std::string buffer directly instead of copying into std::vector<char>
    // (which is NOT null-terminated and may cause strlen-based corruption).
    if (!body_json.empty()) {
        s.HTTPMessage_setBodyData(msg.sp.obj, body_json.data(), body_json.size());
    }
    auto r = run_request(s, req_ctx, msg, {});
    if (!r.ok) return {};
    return r.body;
}

}  // namespace

std::string harvest_storefront(const Symbols& s, abi::shared_ptr req_ctx) {
    abi::std_string out{};
    abi::shared_ptr null_url_bag{};
    aarch64_sret::request_context_store_front_identifier(
        &out, req_ctx.obj, &null_url_bag, s.RequestContext_storeFrontIdentifier);
    return read_std_string(out);
}

std::string device_guid_string(const Symbols& s, abi::shared_ptr device_guid) {
    if (device_guid.obj == nullptr) return {};
    // DeviceGUID::guid() returns a (Data*, void*) pair into a 16-byte
    // hidden first arg. The first 8 bytes are the Data* whose bytes()
    // we want.
    void* ret[2] = {nullptr, nullptr};
    aarch64_sret::device_guid_guid(ret, device_guid.obj, s.DeviceGUID_guid);
    if (ret[0] == nullptr) return {};
    const char* bytes = s.Data_bytes(ret[0]);
    if (bytes == nullptr) return {};
    return std::string(bytes);
}

std::string harvest_dev_token(const Symbols& s, abi::shared_ptr req_ctx) {
    // Matches upstream get_dev_token(): GET + query params; JSON field is "token".
    HTTPMessageHolder msg;
    msg.init(s.vtable_HTTPMessage);
    construct_http_message(s, msg,
                           "https://sf-api-token-service.itunes.apple.com/apiToken",
                           "GET");

    std::vector<std::pair<std::string, std::string>> params;
    params.emplace_back("clientId", "musicAndroid");
    params.emplace_back("version", "1");

    auto r = run_request(s, req_ctx, msg, params);
    if (!r.ok || r.body.empty()) return {};

    nlohmann::json j;
    if (!parse_apple_response_json(r.body, &j)) return {};
    if (j.contains("token") && j["token"].is_string()) {
        return j["token"].get<std::string>();
    }
    return {};
}

std::string harvest_music_user_token(const Symbols& s,
                                     abi::shared_ptr req_ctx,
                                     const std::string& guid,
                                     const std::string& dev_token) {
    HTTPMessageHolder msg;
    msg.init(s.vtable_HTTPMessage);
    construct_http_message(s, msg,
                           "https://play.itunes.apple.com/WebObjects/MZPlay.woa/wa/createMusicToken",
                           "POST");

    set_str_header(s, msg.sp.obj, "Content-Type", "application/json; charset=UTF-8");
    set_str_header(s, msg.sp.obj, "Expect", "");
    set_str_header(s, msg.sp.obj, "X-Apple-Requesting-Bundle-Id", "com.apple.android.music");
    set_str_header(s, msg.sp.obj, "X-Apple-Requesting-Bundle-Version",
                   "Music/4.9 Android/10 model/Samsung S9 build/7663313 (dt:66)");

    auto now_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::system_clock::now().time_since_epoch()).count();
    std::string body;
    body.reserve(512);
    body += "{\"guid\":\"";   body += guid;
    body += "\",\"assertion\":\""; body += dev_token;
    body += "\",\"tcc-acceptance-date\":\""; body += std::to_string(now_ms);
    body += "\"}";

    std::string resp_body = post_json(s, req_ctx, msg, body);
    if (resp_body.empty()) return {};

    nlohmann::json j;
    if (!parse_apple_response_json(resp_body, &j)) return {};
    if (j.contains("music_token") && j["music_token"].is_string()) {
        return j["music_token"].get<std::string>();
    }
    return {};
}

std::optional<std::string> extract_dsid_from_jwt(const std::string& jwt) {
    auto dot1 = jwt.find('.');
    if (dot1 == std::string::npos) return std::nullopt;
    auto dot2 = jwt.find('.', dot1 + 1);
    if (dot2 == std::string::npos) return std::nullopt;
    std::string payload_b64 = jwt.substr(dot1 + 1, dot2 - dot1 - 1);
    std::string payload = base64url_decode(payload_b64);
    if (payload.empty()) return std::nullopt;
    try {
        auto j = nlohmann::json::parse(payload);
        if (j.contains("dsid")) {
            const auto& v = j["dsid"];
            if (v.is_string()) return v.get<std::string>();
            if (v.is_number_integer()) return std::to_string(v.get<long long>());
            if (v.is_number_unsigned()) return std::to_string(v.get<unsigned long long>());
        }
        // some Apple tokens spell the claim "iss" or "sub" - try those too
        for (const char* k : {"sub", "iss"}) {
            if (j.contains(k) && j[k].is_string()) {
                auto cand = j[k].get<std::string>();
                // dsid looks like "1234567890" - return only if numeric
                bool numeric = !cand.empty();
                for (char c : cand) numeric = numeric && (c >= '0' && c <= '9');
                if (numeric) return cand;
            }
        }
    } catch (...) {
        // fall through
    }
    return std::nullopt;
}

bool harvest_all(const Symbols& s,
                 abi::shared_ptr req_ctx,
                 abi::shared_ptr device_guid,
                 Tokens* out) {
    out->storefront = harvest_storefront(s, req_ctx);
    if (out->storefront.empty()) return false;

    out->dev_token = harvest_dev_token(s, req_ctx);
    if (out->dev_token.empty()) return false;

    std::string guid = device_guid_string(s, device_guid);
    if (guid.empty()) return false;

    out->music_user_token = harvest_music_user_token(s, req_ctx, guid, out->dev_token);
    if (out->music_user_token.empty()) return false;

    if (auto d = extract_dsid_from_jwt(out->dev_token)) {
        out->dsid = std::move(*d);
    }
    return true;
}

}  // namespace wrapper::apple::tokens
