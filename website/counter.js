/*
 * 官网计数模块 —— 与官网同一个应用（同源），由 website/server.js 调用。
 *
 * 这是计数逻辑的**唯一实现**；原 `website-counter/` 目录已改为薄壳引用本文件，
 * 不要再在那边维护一份逻辑（同一件事两处维护 = 改一处必漏一处）。
 *
 * 只做两件事：记「页面访问次数」与「下载按钮点击次数」。
 * 明确不收集：IP、User-Agent（仅瞬时读一下用于过滤爬虫，随即丢弃）、Referer、Cookie、任何可识别到人的信息。
 * 明确不落请求日志（本文件除被 server.js 调用外不产生任何输出）。
 *
 * 端点（刻意用不显眼的路径，页面里零入口、零链接）：
 *   GET /api/hit?p=<key>        计数 +1，返回 204（页面用 <img> 打点）
 *   GET /_stats?token=          浏览器看板（需口令）
 *   GET /_stats.json?token=     JSON 快照（需口令）
 *
 * 口令来源顺序：环境变量 COUNTER_TOKEN → data/token.txt → token.txt。
 * data/ 与 token.txt 都被 .gitignore 排除 —— 口令不进仓库。
 * 三处都没有口令 => 看板与 JSON 一律 404（**失败关闭**，绝不公开）。
 * 另：data/ 与 server.js / counter.js 已列进 server.js 的静态拦截名单，不会被当静态文件发出去。
 */

"use strict";

const fs = require("fs");
const path = require("path");

const ROOT = __dirname;
const DATA_DIR = path.join(ROOT, "data");
const DATA_FILE = path.join(DATA_DIR, "counts.json");

// 合法计数键白名单 —— 不在表里的一律忽略，防止被塞垃圾键把数据撑坏。
const PAGE_KEYS = new Set(["index", "features", "download", "pricing", "faq", "changelog", "dl"]);

// 爬虫/工具过滤：只用于「这次要不要计数」，匹配到就不计，且不记录该 UA。
const BOT_RE = /bot|crawl|spider|slurp|bingpreview|yandex|baidu|sogou|360spider|semrush|ahrefs|mj12|dotbot|petal|headless|phantom|python|curl|wget|go-http|okhttp|java\/|scrapy|facebookexternalhit|telegrambot|whatsapp|preview|monitor/i;

const LABELS = {
  index: "首页",
  features: "功能一览",
  download: "下载页",
  pricing: "价格",
  faq: "常见问题",
  changelog: "更新日志",
  dl: "下载点击",
};
const ORDER = ["index", "features", "download", "pricing", "faq", "changelog", "dl"];

let counts = { pages: {}, total: 0, firstSeen: "", lastSeen: "" };
let dirty = false;
let flushTimer = null;

function today() {
  const d = new Date();
  const pad = (n) => String(n).padStart(2, "0");
  return d.getFullYear() + "-" + pad(d.getMonth() + 1) + "-" + pad(d.getDate());
}

function load() {
  try {
    const obj = JSON.parse(fs.readFileSync(DATA_FILE, "utf8"));
    if (obj && typeof obj === "object") {
      counts.pages = obj.pages && typeof obj.pages === "object" ? obj.pages : {};
      counts.total = Number(obj.total) || 0;
      counts.firstSeen = typeof obj.firstSeen === "string" ? obj.firstSeen : "";
      counts.lastSeen = typeof obj.lastSeen === "string" ? obj.lastSeen : "";
      const sum = Object.keys(counts.pages).reduce((a, k) => a + (Number(counts.pages[k]) || 0), 0);
      if (!counts.total || counts.total < sum) counts.total = sum; // total 被改坏时以明细为准重算
    }
  } catch (e) {
    // 文件不存在或内容损坏 → 从零开始，绝不因此中断服务
  }
  if (!counts.firstSeen) counts.firstSeen = today();
}

function flush() {
  flushTimer = null;
  if (!dirty) return;
  dirty = false;
  try {
    fs.mkdirSync(DATA_DIR, { recursive: true });
    const tmp = DATA_FILE + ".tmp";
    fs.writeFileSync(tmp, JSON.stringify(counts, null, 2), "utf8");
    fs.renameSync(tmp, DATA_FILE); // 先写临时文件再改名 —— 中途挂掉不会留下半截 JSON
  } catch (e) {
    dirty = true; // 写失败保持脏标记，等下次触发再试
  }
}

// 合并写入：2 秒内的多次计数只落盘一次，避免高频访问反复写磁盘。
function scheduleFlush() {
  dirty = true;
  if (flushTimer) return;
  flushTimer = setTimeout(flush, 2000);
  if (flushTimer.unref) flushTimer.unref();
}

function bump(key) {
  counts.pages[key] = (Number(counts.pages[key]) || 0) + 1;
  counts.total = (Number(counts.total) || 0) + 1;
  counts.lastSeen = today();
  scheduleFlush();
}

function readToken() {
  const fromEnv = String(process.env.COUNTER_TOKEN || "").trim();
  if (fromEnv) return fromEnv;
  const candidates = [path.join(DATA_DIR, "token.txt"), path.join(ROOT, "token.txt")];
  for (const file of candidates) {
    try {
      const t = String(fs.readFileSync(file, "utf8")).trim();
      if (t) return t;
    } catch (e) {
      // 文件不存在就试下一个；两个都没有则口令为空（看板公开，仅适合还没配口令时）
    }
  }
  return "";
}
const TOKEN = readToken();

// 口令是否已配置。未配置时看板**一律关闭**（失败关闭）——
// 宁可你看不到数据，也不让看板在「忘放口令文件」时变成公开页面（2026-10-09 改）。
function tokenConfigured() {
  return !!TOKEN;
}

function esc(s) {
  return String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
}

function dashboard(extra, showTable) {
  let rows = "";
  if (showTable) {
    const keys = ORDER.concat(Object.keys(counts.pages).filter((k) => ORDER.indexOf(k) < 0));
    rows = keys
      .map((k) => '<tr><td>' + esc(LABELS[k] || k) + '</td><td class="n">' + (Number(counts.pages[k]) || 0) + "</td></tr>")
      .join("");
    rows += '<tr><td><strong>合计</strong></td><td class="n"><strong>' + (Number(counts.total) || 0) + "</strong></td></tr>";
  }
  return [
    '<!DOCTYPE html><html lang="zh-CN"><head><meta charset="UTF-8">',
    '<meta name="viewport" content="width=device-width,initial-scale=1">',
    '<meta name="robots" content="noindex,nofollow"><title>官网计数</title><style>',
    'body{background:#0f1216;color:#e7ecf3;font-family:"Microsoft YaHei","PingFang SC",sans-serif;',
    'line-height:1.7;margin:0;padding:44px 24px}main{max-width:520px;margin:0 auto}',
    'h1{font-size:22px;margin:0 0 6px}.dim{color:#9aa7b4;font-size:13px;margin:0 0 4px}',
    'table{width:100%;border-collapse:collapse;margin-top:22px;font-size:15px}',
    'td{border-bottom:1px solid #2a313c;padding:9px 0}td.n{text-align:right;color:#a3e635;font-weight:600}',
    'code{background:#161b22;border:1px solid #2a313c;border-radius:5px;padding:1px 6px}',
    "</style></head><body><main>",
    "<h1>FocusCapture 官网计数</h1>",
    '<p class="dim">只统计「量」：不记录 IP、浏览器标识、来源页面、Cookie。爬虫与工具请求不计入。</p>',
    extra,
    showTable
      ? "<table>" +
        rows +
        '</table><p class="dim" style="margin-top:16px">统计自 ' +
        esc(counts.firstSeen || "-") +
        " 起，最近一次计数 " +
        esc(counts.lastSeen || "-") +
        "。<br>「下载点击」记的是按钮被点击的次数，不等于下载完成的份数。</p>"
      : "",
    "</main></body></html>",
  ].join("");
}

/**
 * 处理计数相关请求。已处理返回 true，否则返回 false（交给静态文件服务）。
 */
function handle(req, res, u) {
  const p = u.pathname;

  if (p === "/api/hit") {
    const key = String(u.searchParams.get("p") || "").trim().toLowerCase();
    const ua = String(req.headers["user-agent"] || ""); // 只用于本次判断，不写入任何地方
    if (PAGE_KEYS.has(key) && !BOT_RE.test(ua)) bump(key);
    res.writeHead(204, { "Cache-Control": "no-store" });
    res.end();
    return true;
  }

  if (p === "/_stats.json") {
    // 口令未配置 => 一律 404（失败关闭）；口令配了但对不上 => 同样 404，不提示「口令错了」
    if (!TOKEN || u.searchParams.get("token") !== TOKEN) {
      res.writeHead(404, { "Content-Type": "text/plain; charset=utf-8" });
      res.end("not found");
      return true;
    }
    res.writeHead(200, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
    res.end(JSON.stringify(counts, null, 2));
    return true;
  }

  if (p === "/_stats") {
    const ok = !!TOKEN && u.searchParams.get("token") === TOKEN;
    res.writeHead(200, { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" });
    res.end(ok ? dashboard("", true) : dashboard("<p>需要口令：在本地址后加 <code>?token=你的口令</code></p>", false));
    return true;
  }

  return false;
}

module.exports = { handle, load, flush, tokenConfigured };
