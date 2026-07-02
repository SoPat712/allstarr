// Public HTTP supervisor for the private Apple-runtime worker process.

#pragma once

#include <string>

#include <httplib.h>

namespace wrapper {

class Supervisor {
public:
    Supervisor(std::string argv0, std::string version, int worker_port);
    ~Supervisor();

    Supervisor(const Supervisor&) = delete;
    Supervisor& operator=(const Supervisor&) = delete;

    void mount(httplib::Server& svr);
    void stop_worker();

private:
    class Impl;
    Impl* impl_;
};

}  // namespace wrapper
