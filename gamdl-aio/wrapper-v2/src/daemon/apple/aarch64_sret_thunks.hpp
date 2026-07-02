// AArch64 AAPCS places the hidden C++ "struct return" pointer in x8 and the
// implicit `this` pointer in x0. Our function-pointer typedefs follow the x86_64
// SysV layout (sret in rdi, `this` in rsi, ...), so a plain call would pass the
// wrong registers. These thunks reorder into x8/x0/x1/... before blr.
//
// The callee address MUST live in a register other than x0–x8 while we set x8
// (sret) and argument registers. If "blr %[callee]" shared a register with the sret
// slot, Clang could place the target in x8, then `mov x8, %[sret]` would corrupt it
// and we would branch into the return buffer (SIGSEGV at a small address).
// Fix: load the target into x9 first, then set x8/x0–x7, then `blr x9`.
//
// On x86_64, call through the raw function pointer unchanged.

#pragma once

#include <cstdint>

#include "apple/abi.hpp"

namespace wrapper::apple::aarch64_sret {

#if defined(__aarch64__) || defined(__AARCH64EL__)

#define WRAPPER_A64_CLOB_CALL                                               \
    "memory", "cc", "x0", "x1", "x2", "x3", "x4", "x5", "x6", "x7", "x8", \
        "x9", "x10", "x11", "x12", "x13", "x14", "x15", "x16", "x17", "lr"

inline void device_guid_instance(abi::shared_ptr* out, abi::fn_DeviceGUID_instance fn) {
    void* callee = reinterpret_cast<void*>(fn);
    asm volatile(
        "mov x9, %[callee]\n\t"
        "mov x8, %[out]\n\t"
        "blr x9\n\t"
        :
        : [callee]"r"(callee), [out]"r"(out)
        : WRAPPER_A64_CLOB_CALL);
}

inline void make_shared_request_context(abi::shared_ptr*              out,
                                        abi::std_string*              arg,
                                        abi::fn_make_shared_RequestContext fn) {
    void* callee = reinterpret_cast<void*>(fn);
    asm volatile(
        "mov x9, %[callee]\n\t"
        "mov x8, %[out]\n\t"
        "mov x0, %[arg]\n\t"
        "blr x9\n\t"
        :
        : [callee]"r"(callee), [out]"r"(out), [arg]"r"(arg)
        : WRAPPER_A64_CLOB_CALL);
}

inline void make_shared_android_presentation_interface(
    abi::shared_ptr*                                out,
    abi::fn_make_shared_AndroidPresentationInterface fn) {
    void* callee = reinterpret_cast<void*>(fn);
    asm volatile(
        "mov x9, %[callee]\n\t"
        "mov x8, %[out]\n\t"
        "blr x9\n\t"
        :
        : [callee]"r"(callee), [out]"r"(out)
        : WRAPPER_A64_CLOB_CALL);
}

inline void make_shared_authenticate_flow(abi::shared_ptr*              out,
                                          abi::shared_ptr*              req_ctx,
                                          abi::fn_make_shared_AuthenticateFlow fn) {
    void* callee = reinterpret_cast<void*>(fn);
    asm volatile(
        "mov x9, %[callee]\n\t"
        "mov x8, %[out]\n\t"
        "mov x0, %[req]\n\t"
        "blr x9\n\t"
        :
        : [callee]"r"(callee), [out]"r"(out), [req]"r"(req_ctx)
        : WRAPPER_A64_CLOB_CALL);
}

inline void device_guid_configure(void*                         cfg_ret,
                                  void*                         this_,
                                  abi::std_string*              a1,
                                  abi::std_string*              a2,
                                  const unsigned int*           a3,
                                  const std::uint8_t*         a4,
                                  abi::fn_DeviceGUID_configure fn) {
    void* callee = reinterpret_cast<void*>(fn);
    asm volatile(
        "mov x9, %[callee]\n\t"
        "mov x8, %[sret]\n\t"
        "mov x0, %[ths]\n\t"
        "mov x1, %[a1]\n\t"
        "mov x2, %[a2]\n\t"
        "mov x3, %[a3]\n\t"
        "mov x4, %[a4]\n\t"
        "blr x9\n\t"
        :
        : [callee]"r"(callee),
          [sret]"r"(cfg_ret),
          [ths]"r"(this_),
          [a1]"r"(a1),
          [a2]"r"(a2),
          [a3]"r"(a3),
          [a4]"r"(a4)
        : WRAPPER_A64_CLOB_CALL);
}

inline void request_context_init(void*              rci_ret,
                                 void*              this_,
                                 abi::shared_ptr*   cfg,
                                 abi::fn_RequestContext_init fn) {
    void* callee = reinterpret_cast<void*>(fn);
    asm volatile(
        "mov x9, %[callee]\n\t"
        "mov x8, %[sret]\n\t"
        "mov x0, %[ths]\n\t"
        "mov x1, %[cfg]\n\t"
        "blr x9\n\t"
        :
        : [callee]"r"(callee),
          [sret]"r"(rci_ret),
          [ths]"r"(this_),
          [cfg]"r"(cfg)
        : WRAPPER_A64_CLOB_CALL);
}

inline void device_guid_guid(void* hidden_pair, void* this_, abi::fn_DeviceGUID_guid fn) {
    void* callee = reinterpret_cast<void*>(fn);
    asm volatile(
        "mov x9, %[callee]\n\t"
        "mov x8, %[sret]\n\t"
        "mov x0, %[ths]\n\t"
        "blr x9\n\t"
        :
        : [callee]"r"(callee), [sret]"r"(hidden_pair), [ths]"r"(this_)
        : WRAPPER_A64_CLOB_CALL);
}

inline void request_context_store_front_identifier(
    abi::std_string*                        out,
    void*                                   this_,
    abi::shared_ptr*                        url_bag,
    abi::fn_RequestContext_storeFrontIdentifier fn) {
    void* callee = reinterpret_cast<void*>(fn);
    asm volatile(
        "mov x9, %[callee]\n\t"
        "mov x8, %[sret]\n\t"
        "mov x0, %[ths]\n\t"
        "mov x1, %[bag]\n\t"
        "blr x9\n\t"
        :
        : [callee]"r"(callee),
          [sret]"r"(out),
          [ths]"r"(this_),
          [bag]"r"(url_bag)
        : WRAPPER_A64_CLOB_CALL);
}

inline void purchase_response_items(abi::std_vector* out,
                                    void*             this_,
                                    abi::fn_PurchaseResponse_items fn) {
    void* callee = reinterpret_cast<void*>(fn);
    asm volatile(
        "mov x9, %[callee]\n\t"
        "mov x8, %[sret]\n\t"
        "mov x0, %[ths]\n\t"
        "blr x9\n\t"
        :
        : [callee]"r"(callee), [sret]"r"(out), [ths]"r"(this_)
        : WRAPPER_A64_CLOB_CALL);
}

// Eight std::string parameters (seven S8_ in the mangled name): x1..x7 in regs,
// eighth on stack at [sp]. Matches zhaarey agent-arm64.js and
// WorldObservationLog/wrapper arm64 import.h.
inline void svfoot_get_persistent_key(abi::shared_ptr*    ret,
                                      void*               fh,
                                      abi::std_string*    a1,
                                      abi::std_string*    a2,
                                      abi::std_string*    a3,
                                      abi::std_string*    a4,
                                      abi::std_string*    a5,
                                      abi::std_string*    a6,
                                      abi::std_string*    a7,
                                      abi::std_string*    a8_stack,
                                      abi::fn_SVFootHillSessionCtrl_getPersistentKey fn) {
    void* callee = reinterpret_cast<void*>(fn);
    asm volatile(
        "mov x9, %[callee]\n\t"
        "mov x8, %[sret]\n\t"
        "mov x0, %[ths]\n\t"
        "mov x1, %[a1]\n\t"
        "mov x2, %[a2]\n\t"
        "mov x3, %[a3]\n\t"
        "mov x4, %[a4]\n\t"
        "mov x5, %[a5]\n\t"
        "mov x6, %[a6]\n\t"
        "mov x7, %[a7]\n\t"
        "sub sp, sp, #0x10\n\t"
        "mov x10, %[a8]\n\t"
        "str x10, [sp]\n\t"
        "blr x9\n\t"
        "add sp, sp, #0x10\n\t"
        :
        : [callee]"r"(callee),
          [sret]"r"(ret),
          [ths]"r"(fh),
          [a1]"r"(a1),
          [a2]"r"(a2),
          [a3]"r"(a3),
          [a4]"r"(a4),
          [a5]"r"(a5),
          [a6]"r"(a6),
          [a7]"r"(a7),
          [a8]"r"(a8_stack)
        : WRAPPER_A64_CLOB_CALL);
}

// Seven std::string parameters (six S8_): all in x1..x7; no stack argument.
inline void svfoot_get_persistent_key_7str(abi::shared_ptr* ret,
                                           void*                  fh,
                                           abi::std_string*       adam,
                                           abi::std_string*       key_uri,
                                           abi::std_string*       key_format,
                                           abi::std_string*       key_format_ver,
                                           abi::std_string*       server_uri,
                                           abi::std_string*       protocol_type,
                                           abi::std_string*       fps_cert,
                                           abi::fn_SVFootHillSessionCtrl_getPersistentKey7 fn) {
    void* callee = reinterpret_cast<void*>(fn);
    asm volatile(
        "mov x9, %[callee]\n\t"
        "mov x8, %[sret]\n\t"
        "mov x0, %[ths]\n\t"
        "mov x1, %[a1]\n\t"
        "mov x2, %[a2]\n\t"
        "mov x3, %[a3]\n\t"
        "mov x4, %[a4]\n\t"
        "mov x5, %[a5]\n\t"
        "mov x6, %[a6]\n\t"
        "mov x7, %[a7]\n\t"
        "blr x9\n\t"
        :
        : [callee]"r"(callee),
          [sret]"r"(ret),
          [ths]"r"(fh),
          [a1]"r"(adam),
          [a2]"r"(key_uri),
          [a3]"r"(key_format),
          [a4]"r"(key_format_ver),
          [a5]"r"(server_uri),
          [a6]"r"(protocol_type),
          [a7]"r"(fps_cert)
        : WRAPPER_A64_CLOB_CALL);
}

inline void svfoot_decrypt_context(abi::shared_ptr*                 ret,
                                   void*                            fh,
                                   void*                            persist,
                                   abi::fn_SVFootHillSessionCtrl_decryptContext fn) {
    void* callee = reinterpret_cast<void*>(fn);
    asm volatile(
        "mov x9, %[callee]\n\t"
        "mov x8, %[sret]\n\t"
        "mov x0, %[ths]\n\t"
        "mov x1, %[pk]\n\t"
        "blr x9\n\t"
        :
        : [callee]"r"(callee),
          [sret]"r"(ret),
          [ths]"r"(fh),
          [pk]"r"(persist)
        : WRAPPER_A64_CLOB_CALL);
}

// Keep an x8 scratch slot for URLRequest::run() on AArch64. Some Apple C++
// entry points in this area return non-trivial values via the hidden result
// register, and the scratch slot is harmless for void-returning builds.
inline void urlrequest_run(void* this_, abi::fn_URLRequest_run fn) {
    void* callee = reinterpret_cast<void*>(fn);
    // 32-byte scratch buffer for the sret return value (we discard it).
    alignas(8) std::uint8_t scratch[32] = {};
    void* sret = reinterpret_cast<void*>(scratch);
    asm volatile(
        "mov x9, %[callee]\n\t"
        "mov x8, %[sret]\n\t"
        "mov x0, %[ths]\n\t"
        "blr x9\n\t"
        :
        : [callee]"r"(callee), [sret]"r"(sret), [ths]"r"(this_)
        : WRAPPER_A64_CLOB_CALL);
}

#undef WRAPPER_A64_CLOB_CALL

#else

inline void device_guid_instance(abi::shared_ptr* out, abi::fn_DeviceGUID_instance fn) {
    fn(out);
}

inline void make_shared_request_context(abi::shared_ptr*              out,
                                        abi::std_string*              arg,
                                        abi::fn_make_shared_RequestContext fn) {
    fn(out, arg);
}

inline void make_shared_android_presentation_interface(
    abi::shared_ptr*                                out,
    abi::fn_make_shared_AndroidPresentationInterface fn) {
    fn(out);
}

inline void make_shared_authenticate_flow(abi::shared_ptr*              out,
                                          abi::shared_ptr*              req_ctx,
                                          abi::fn_make_shared_AuthenticateFlow fn) {
    fn(out, req_ctx);
}

inline void device_guid_configure(void*                         cfg_ret,
                                  void*                         this_,
                                  abi::std_string*              a1,
                                  abi::std_string*              a2,
                                  const unsigned int*           a3,
                                  const std::uint8_t*           a4,
                                  abi::fn_DeviceGUID_configure fn) {
    fn(cfg_ret, this_, a1, a2, a3, a4);
}

inline void request_context_init(void*              rci_ret,
                                 void*              this_,
                                 abi::shared_ptr*   cfg,
                                 abi::fn_RequestContext_init fn) {
    fn(rci_ret, this_, cfg);
}

inline void device_guid_guid(void* hidden_pair, void* this_, abi::fn_DeviceGUID_guid fn) {
    fn(hidden_pair, this_);
}

inline void request_context_store_front_identifier(
    abi::std_string*                        out,
    void*                                   this_,
    abi::shared_ptr*                        url_bag,
    abi::fn_RequestContext_storeFrontIdentifier fn) {
    fn(out, this_, url_bag);
}

inline void purchase_response_items(abi::std_vector* out,
                                    void*             this_,
                                    abi::fn_PurchaseResponse_items fn) {
    fn(out, this_);
}

inline void svfoot_get_persistent_key(abi::shared_ptr*    ret,
                                      void*               fh,
                                      abi::std_string*    a1,
                                      abi::std_string*    a2,
                                      abi::std_string*    a3,
                                      abi::std_string*    a4,
                                      abi::std_string*    a5,
                                      abi::std_string*    a6,
                                      abi::std_string*    a7,
                                      abi::std_string*    a8,
                                      abi::fn_SVFootHillSessionCtrl_getPersistentKey fn) {
    fn(ret, fh, a1, a2, a3, a4, a5, a6, a7, a8);
}

inline void svfoot_get_persistent_key_7str(abi::shared_ptr* ret,
                                           void*            fh,
                                           abi::std_string* adam,
                                           abi::std_string* key_uri,
                                           abi::std_string* key_format,
                                           abi::std_string* key_format_ver,
                                           abi::std_string* server_uri,
                                           abi::std_string* protocol_type,
                                           abi::std_string* fps_cert,
                                           abi::fn_SVFootHillSessionCtrl_getPersistentKey7 fn) {
    fn(ret, fh, adam, key_uri, key_format, key_format_ver, server_uri, protocol_type, fps_cert);
}

inline void svfoot_decrypt_context(abi::shared_ptr*                 ret,
                                   void*                            fh,
                                   void*                            persist,
                                   abi::fn_SVFootHillSessionCtrl_decryptContext fn) {
    fn(ret, fh, persist);
}

inline void urlrequest_run(void* this_, abi::fn_URLRequest_run fn) {
    fn(this_);
}

#endif

}  // namespace wrapper::apple::aarch64_sret
