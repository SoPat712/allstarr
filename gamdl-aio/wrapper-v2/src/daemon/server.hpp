#pragma once

#include <string>

#include <httplib.h>

#include "apple/auth.hpp"
#include "apple/loader.hpp"
#include "apple/runtime.hpp"

namespace wrapper {

struct ServerInfo {
    std::string version = "0.0.1";

    bool apple_init_enabled = true;
};

class Server {
public:
    Server(httplib::Server& svr,
           apple::Runtime& rt,
           apple::Loader& loader,
           apple::Account& account,
           ServerInfo info);

    void mount();

private:
    httplib::Server& svr_;
    apple::Runtime& rt_;
    apple::Loader& loader_;
    apple::Account& account_;
    ServerInfo info_;
};

}  // namespace wrapper
