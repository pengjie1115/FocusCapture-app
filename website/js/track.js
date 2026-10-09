/* FocusCapture 官网统计打点 —— 同源部署，不需要配置地址。
 *
 * 做了什么：页面打开时给计数服务发一次打点；下载按钮被点击时再发一次。
 * 没做什么：不读 cookie、不取指纹、不塞任何标识；打点失败绝不影响页面（静默忽略）。
 *
 * 计数服务与官网是**同一个应用**（同源），所以默认用相对地址 "/api/hit"。
 * 若将来计数服务改为独立部署，把它的域名填进 COUNTER_BASE 即可（不要带结尾斜杠）。
 */
(function () {
  "use strict";

  var COUNTER_BASE = ""; // 留空 = 同源（当前方式）

  function hit(name) {
    try {
      var img = new Image();
      img.src = COUNTER_BASE + "/api/hit?p=" + encodeURIComponent(name) + "&_=" + Date.now();
    } catch (e) {
      /* 统计是附带动作，失败就失败，绝不打扰访客 */
    }
  }

  function currentPage() {
    var file = location.pathname.split("/").pop() || "";
    file = file.replace(/\.html?$/i, "").toLowerCase();
    return file || "index";
  }

  function init() {
    hit(currentPage());
    var link = document.querySelector('a[href$="FocusCapture.exe"]');
    if (link) {
      link.addEventListener("click", function () {
        hit("dl");
      });
    }
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
