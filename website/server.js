/*
 * FocusCapture 官网服务 —— 静态页 + 计数，同一个应用（同源）。
 *
 * 为什么官网不再用平台内置静态托管：本工作区只能有一个应用，计数服务无法单独部署
 * （详见 .workbuddy/memory/PITFALLS.md「发布平台会把不同目录归到同一个应用」）。
 * 并进同一个应用后：无跨域、一次发布、少一个应用要管。
 *
 * 约束：
 *   - 必须监听 process.env.PORT 并绑 0.0.0.0
 *   - **不打请求日志** —— 这是「官网不收集访客信息」这条承诺的技术支撑，别加
 *   - 静态文件根目录 = 本文件所在目录（website/）
 *   - 大文件（downloads/FocusCapture.exe，约 250 MB）支持 Range，便于断点续传/暂停续下
 *   - 缓存一律 no-cache + ETag/Last-Modified：未变返 304、变了立刻生效。
 *     别退回 public,max-age=N —— 那会让改版后的一段时间里访客仍看到旧页（见 PITFALLS）
 */

"use strict";

const http = require("http");
const fs = require("fs");
const path = require("path");
const counter = require("./counter");

const PORT = Number(process.env.PORT) || 3000;
const ROOT = __dirname;

const MIME = {
  ".html": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".md": "text/markdown; charset=utf-8",
  ".txt": "text/plain; charset=utf-8",
  ".xml": "application/xml; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".jpg": "image/jpeg",
  ".jpeg": "image/jpeg",
  ".gif": "image/gif",
  ".webp": "image/webp",
  ".ico": "image/x-icon",
  ".exe": "application/octet-stream",
  ".zip": "application/zip",
};

// —— 静态访问拦截 ——
// 网站根目录里同时躺着「服务端代码」（server.js/counter.js/package.json）与「运行时数据」（data/ 里
// 有看板口令），这些默认都不能见光。2026-10-09 实测发现它们此前全都是 200 可下载 —— 口令等于公开。
// 规则一：路径里任何一段以 . 开头（.gitignore / .git / .env …）
// 规则二：运行时数据与依赖目录
// 规则三：服务端自己的文件（显式列出 —— 将来新增服务端文件必须同步加进来）
// 规则四：扩展名不在「网页资源」白名单里（防以后误放进来的杂项文件被直接发出去）
const DENY_DIRS = new Set(["data", "node_modules"]);
const DENY_FILES = new Set(["server.js", "counter.js", "package.json", "package-lock.json", "token.txt", "agents.md"]);
const WEB_EXT = new Set([
  ".html", ".css", ".js", ".md", ".txt", ".xml", ".svg",
  ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".exe", ".zip",
]);

function isServerOnly(relPath) {
  const segs = relPath.split("/").filter(Boolean);
  if (!segs.length) return false;
  if (segs.some((s) => s.charAt(0) === ".")) return true;
  if (DENY_DIRS.has(segs[0].toLowerCase())) return true;
  if (segs.length === 1 && DENY_FILES.has(segs[0].toLowerCase())) return true;
  return !WEB_EXT.has(path.extname(segs[segs.length - 1]).toLowerCase());
}

counter.load();

function head(res, code, headers) {
  res.writeHead(code, Object.assign({ "X-Content-Type-Options": "nosniff" }, headers || {}));
}

function fail(res, code, text) {
  head(res, code, { "Content-Type": "text/plain; charset=utf-8" });
  res.end(text);
}

// 单段 Range（bytes=start-end / bytes=start- / bytes=-suffix）。返回 null 表示按整文件发。
function parseRange(header, size) {
  if (!header) return null;
  const m = /^bytes=(\d*)-(\d*)$/.exec(String(header).trim());
  if (!m) return null;
  const hasStart = m[1] !== "";
  const hasEnd = m[2] !== "";
  if (!hasStart && !hasEnd) return null;
  let start;
  let end;
  if (!hasStart) {
    const suffix = Number(m[2]);
    if (!suffix) return null;
    start = Math.max(0, size - suffix);
    end = size - 1;
  } else {
    start = Number(m[1]);
    end = hasEnd ? Number(m[2]) : size - 1;
  }
  if (!Number.isFinite(start) || !Number.isFinite(end) || start > end || start >= size) return null;
  return { start, end: Math.min(end, size - 1) };
}

// 缓存策略：no-cache = 浏览器可以存，但每次使用前必须回来问一句。
// 配合下面的 ETag / Last-Modified：没变 → 304（几乎零流量）；变了 → 立刻拿到新内容。
// （2026-10-09 由 public,max-age=3600 改来：改版后一小时内访客会看到旧页，见 PITFALLS）
const CACHE_CONTROL = "no-cache";

// 强 ETag：大小 + 修改时间（含毫秒）。文件一动，ETag 必变。
function makeEtag(st) {
  return '"' + st.size.toString(16) + "-" + Math.floor(st.mtimeMs).toString(16) + '"';
}

// If-None-Match 用弱比较：忽略 W/ 前缀，支持逗号列表与 '*'
function etagMatches(header, etag) {
  if (!header) return false;
  const h = String(header).trim();
  if (h === "*") return true;
  return h.split(",").some(function (part) {
    return part.trim().replace(/^W\//, "") === etag;
  });
}

// If-Modified-Since：HTTP 日期精度只到秒，故把文件时间向下取整到秒再比
function notModifiedSince(header, st) {
  if (!header) return false;
  const t = Date.parse(String(header).trim());
  if (!Number.isFinite(t)) return false;
  return Math.floor(st.mtimeMs / 1000) * 1000 <= t;
}

// If-Range：带校验值的续传请求，只有文件没变才允许只发一段；
// 变了就必须整文件重发，否则会把「旧文件的前半段 + 新文件的后半段」拼成一个坏包。
function ifRangeAllowsRange(header, etag, st) {
  if (!header) return true; // 没带 => 沿用既有行为
  const v = String(header).trim();
  if (v === "") return true;
  if (v.charAt(0) === '"' || v.indexOf("W/") === 0) return etagMatches(v, etag);
  return notModifiedSince(v, st);
}

function sendFile(req, res, filePath, st) {
  const size = st.size;
  const ext = path.extname(filePath).toLowerCase();
  const type = MIME[ext] || "application/octet-stream";
  const etag = makeEtag(st);
  const lastMod = st.mtime.toUTCString();
  const validators = {
    "Accept-Ranges": "bytes",
    "Cache-Control": CACHE_CONTROL,
    ETag: etag,
    "Last-Modified": lastMod,
  };

  let range = parseRange(req.headers.range, size);
  if (range && !ifRangeAllowsRange(req.headers["if-range"], etag, st)) range = null;

  if (range) {
    const len = range.end - range.start + 1;
    head(res, 206, Object.assign({}, validators, {
      "Content-Type": type,
      "Content-Length": len,
      "Content-Range": "bytes " + range.start + "-" + range.end + "/" + size,
    }));
    if (req.method === "HEAD") {
      res.end();
      return;
    }
    const stream = fs.createReadStream(filePath, { start: range.start, end: range.end });
    stream.on("error", () => res.destroy());
    stream.pipe(res);
    return;
  }

  // 没命中 Range：先判「未修改」——no-cache 下浏览器每次都会带校验值回来，这里是省流量的关键一步
  if (
    etagMatches(req.headers["if-none-match"], etag) ||
    (!req.headers["if-none-match"] && notModifiedSince(req.headers["if-modified-since"], st))
  ) {
    head(res, 304, { "Cache-Control": CACHE_CONTROL, ETag: etag, "Last-Modified": lastMod });
    res.end();
    return;
  }

  head(res, 200, Object.assign({}, validators, {
    "Content-Type": type,
    "Content-Length": size,
  }));
  if (req.method === "HEAD") {
    res.end();
    return;
  }
  const stream = fs.createReadStream(filePath);
  stream.on("error", () => res.destroy());
  stream.pipe(res);
}

const server = http.createServer((req, res) => {
  if (req.method !== "GET" && req.method !== "HEAD") {
    fail(res, 405, "method not allowed");
    return;
  }

  let u;
  try {
    u = new URL(req.url, "http://localhost");
  } catch (e) {
    fail(res, 400, "bad request");
    return;
  }

  // ① 计数端点优先（/api/hit、/_stats、/_stats.json）
  try {
    if (counter.handle(req, res, u)) return;
  } catch (e) {
    // 计数出错绝不能连累页面 —— 落到静态服务继续走
  }

  // ② 静态文件
  let rel;
  try {
    rel = decodeURIComponent(u.pathname);
  } catch (e) {
    fail(res, 400, "bad request");
    return;
  }
  if (rel === "/" || rel === "") rel = "/index.html";

  const target = path.resolve(ROOT, "." + path.posix.normalize(rel));
  // 防目录穿越：解析后必须仍在 ROOT 之下
  if (target !== ROOT && !target.startsWith(ROOT + path.sep)) {
    fail(res, 403, "forbidden");
    return;
  }

  // 服务端代码 / 运行时数据一律不可访问。用 404 而非 403 —— 不告诉对方「这里确实有东西」。
  const relPath = path.relative(ROOT, target).split(path.sep).join("/");
  if (relPath && isServerOnly(relPath)) {
    fail(res, 404, "not found");
    return;
  }

  let st;
  try {
    st = fs.statSync(target);
  } catch (e) {
    fail(res, 404, "not found");
    return;
  }

  let filePath = target;
  if (st.isDirectory()) {
    filePath = path.join(target, "index.html");
    try {
      st = fs.statSync(filePath);
    } catch (e) {
      fail(res, 404, "not found");
      return;
    }
  }
  if (!st.isFile()) {
    fail(res, 404, "not found");
    return;
  }

  sendFile(req, res, filePath, st);
});

server.listen(PORT, "0.0.0.0", function () {
  console.log(
    "[focuscapture-site] listening on " + PORT +
      " (static + counter, token: " + (counter.tokenConfigured() ? "configured" : "MISSING -> 看板已关闭") + ")"
  );
});

function shutdown() {
  try {
    counter.flush();
  } catch (e) {
    /* 退出前的尽力而为 */
  }
  process.exit(0);
}
process.on("SIGTERM", shutdown);
process.on("SIGINT", shutdown);
