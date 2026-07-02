// Token-harvest helpers.
//
// Post-login, Apple's `RequestContext` carries the iTunes-side auth
// state (DSID + signed device blobs). We use that to:
//   - Read the storefront identifier directly from RequestContext.
//   - GET sf-api-token-service.itunes.apple.com/apiToken?clientId=musicAndroid
//     &version=1 for the developer token (JWT; JSON key `token`).
//   - POST to play.itunes.apple.com/.../createMusicToken for the
//     Music User Token (MusicKit-side token derived from this login).
//   - Decode the `dsid` claim out of the dev-token JWT.
//
// Everything goes through Apple's URLRequest so requests are
// transparently signed with X-Dsid / X-Token / X-iTunes-Storefront
// headers; we don't have to reimplement that. The URL signing
// machinery lives in libstoreservicescore.so and consumes the same
// RequestContext that AuthenticateFlow just populated.

#pragma once

#include <optional>
#include <string>

#include "apple/abi.hpp"

namespace wrapper::apple {

struct Symbols;
struct Tokens;

namespace tokens {

// One-shot orchestrator used by the worker thread. Populates the
// storefront / dev_token / music_user_token / dsid fields on `out`
// (the caller is responsible for apple_id and logged_in_at). Returns
// false on the first hard failure with stderr-logged context.
bool harvest_all(const Symbols& s,
                 abi::shared_ptr req_ctx,
                 abi::shared_ptr device_guid,
                 Tokens* out);

// Individual stages, exposed for testing and finer-grained reuse.
std::string harvest_storefront(const Symbols& s, abi::shared_ptr req_ctx);
std::string harvest_dev_token(const Symbols& s, abi::shared_ptr req_ctx);
std::string harvest_music_user_token(const Symbols& s,
                                     abi::shared_ptr req_ctx,
                                     const std::string& guid,
                                     const std::string& dev_token);
std::string device_guid_string(const Symbols& s, abi::shared_ptr device_guid);
std::optional<std::string> extract_dsid_from_jwt(const std::string& jwt);

}  // namespace tokens

}  // namespace wrapper::apple
