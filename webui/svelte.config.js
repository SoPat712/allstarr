import adapter from "@sveltejs/adapter-static";

// Dev-only allowance so Impeccable live mode can load. Guarded by NODE_ENV.
const __impeccableLiveDev =
  process.env.NODE_ENV === "development" ? ["http://localhost:8400"] : [];

/** @type {import("@sveltejs/kit").Config} */
const config = {
  kit: {
    adapter: adapter({
      pages: "build",
      assets: "build",
      fallback: "index.html",
      strict: true,
    }),
    router: {
      type: "hash",
    },
    csp: {
      mode: "hash",
      directives: {
        "default-src": ["self"],
        "base-uri": ["self"],
        "connect-src": ["self", ...__impeccableLiveDev],
        "font-src": ["self"],
        "form-action": ["self"],
        "img-src": ["self", "data:", "blob:"],
        "object-src": ["none"],
        "script-src": ["self", ...__impeccableLiveDev],
        "style-src": ["self", "unsafe-inline"],
      },
    },
  },
};

export default config;
