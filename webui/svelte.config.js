import adapter from "@sveltejs/adapter-static";

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
        "connect-src": ["self"],
        "font-src": ["self"],
        "form-action": ["self"],
        "img-src": ["self", "data:", "blob:"],
        "object-src": ["none"],
        "script-src": ["self"],
        "style-src": ["self", "unsafe-inline"],
      },
    },
  },
};

export default config;
