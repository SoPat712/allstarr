const STIRS = 8;
function createWater() {
  const canvas = document.createElement("canvas");
  const gl = canvas.getContext("webgl", { preserveDrawingBuffer: true, antialias: false });
  if (!gl) return null;
  const vs = "attribute vec2 a; void main(){ gl_Position = vec4(a, 0.0, 1.0); }";
  const fs = `precision highp float;
uniform vec2 res; uniform float t, seed; uniform vec3 c0, c1, c2, c3;
uniform vec4 stir[${STIRS}]; uniform float age[${STIRS}];
vec2 h2(vec2 p){ p = vec2(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3))); return -1.0 + 2.0 * fract(sin(p) * 43758.5453); }
float noise(vec2 p){ vec2 i = floor(p), f = fract(p), u = f * f * (3.0 - 2.0 * f);
  return mix(mix(dot(h2(i), f), dot(h2(i + vec2(1,0)), f - vec2(1,0)), u.x), mix(dot(h2(i + vec2(0,1)), f - vec2(0,1)), dot(h2(i + vec2(1,1)), f - vec2(1,1)), u.x), u.y); }
float fbm(vec2 p){ float v = 0.0, a = 0.5; mat2 m = mat2(1.6, 1.2, -1.2, 1.6); for (int i = 0; i < 4; i++){ v += a * noise(p); p = m * p; a *= 0.42; } return v; }
void main(){
  vec2 uv = gl_FragCoord.xy / res;
  vec2 p = uv * 0.85 + seed;
  for (int i = 0; i < ${STIRS}; i++){
    vec2 d = uv - stir[i].xy; float w = exp(-dot(d, d) / 0.05) * exp(-age[i] * 0.7);
    p -= stir[i].zw * w * 0.35;
    p += vec2(-d.y, d.x) * w * length(stir[i].zw) * 1.6;
  }
  vec2 q = vec2(fbm(p + vec2(0.0, 0.11 * t)), fbm(p + vec2(5.2, 1.3) - 0.09 * t));
  vec2 r = vec2(fbm(p + 1.7 * q + vec2(1.7, 9.2) + 0.13 * t), fbm(p + 1.7 * q + vec2(8.3, 2.8) - 0.1 * t));
  float f = fbm(p + 1.6 * r);
  vec3 col = c0;
  col = mix(col, c1, smoothstep(-0.05, 0.15, q.x));
  col = mix(col, c2, smoothstep(0.04, 0.22, r.y));
  col = mix(col, c3, smoothstep(0.16, 0.34, f) * 0.9);
  col = mix(col, c0, smoothstep(0.2, 0.5, -r.x) * 0.6);
  col += (fract(sin(dot(gl_FragCoord.xy, vec2(12.9898, 78.233))) * 43758.5453) - 0.5) * 0.03;
  gl_FragColor = vec4(col, 1.0);
}`;
  let buf: WebGLBuffer | null = null;
  const prog = gl.createProgram()!;
  const shaders: WebGLShader[] = [];
  for (const [type, src] of [[gl.VERTEX_SHADER, vs], [gl.FRAGMENT_SHADER, fs]] as [number, string][]) { const sh = gl.createShader(type)!; shaders.push(sh); gl.shaderSource(sh, src); gl.compileShader(sh);
    if (!gl.getShaderParameter(sh, gl.COMPILE_STATUS)) { dispose(); return null; } gl.attachShader(prog, sh); }
  gl.linkProgram(prog);
  if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) { dispose(); return null; }
  gl.useProgram(prog);
  buf = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, buf); gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
  const loc = gl.getAttribLocation(prog, "a"); gl.enableVertexAttribArray(loc); gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
  const u = (name: string) => gl.getUniformLocation(prog, name);
  const rgb = (hex: string) => { const n = parseInt(hex.slice(1), 16); return [(n >> 16 & 255) / 255, (n >> 8 & 255) / 255, (n & 255) / 255]; };
  function dispose() {
    if (buf) gl!.deleteBuffer(buf);
    shaders.forEach(sh => gl!.deleteShader(sh)); gl!.deleteProgram(prog);
    gl!.getExtension("WEBGL_lose_context")?.loseContext(); canvas.width = canvas.height = 0;
  }
  return { dispose, draw(S: number, colours: string[], seed: number, t: number) {
    if (gl.isContextLost()) return null;
    if (canvas.width !== S) { canvas.width = canvas.height = S; }
    gl.viewport(0, 0, S, S);
    gl.uniform2f(u("res"), S, S); gl.uniform1f(u("t"), t); gl.uniform1f(u("seed"), seed);
    ["c0", "c1", "c2", "c3"].forEach((name, i) => gl.uniform3fv(u(name), rgb(colours[i])));
    const s = new Float32Array(STIRS * 4), a = new Float32Array(STIRS).fill(99);
    gl.uniform4fv(u("stir"), s); gl.uniform1fv(u("age"), a);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    return canvas;
  } };
}


let shared: ReturnType<typeof createWater> | undefined, users = 0;
export function retainWater() {
  users++;
  return () => { if (--users === 0) { shared?.dispose(); shared = undefined; } };
}
export function water(S: number, colours: string[], seed: number, t: number) {
  if (shared === undefined) shared = createWater();
  return shared?.draw(S, colours, seed, t) ?? null;
}
