#pragma once

#include <optional>
#include <string>

#include "apple/abi.hpp"

namespace wrapper::apple {

struct Symbols;
struct Tokens;

namespace tokens {

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
