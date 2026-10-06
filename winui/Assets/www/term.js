(function () {
  var term = new Terminal({
    cursorBlink: true,
    fontSize: 14,
    fontFamily: '"Cascadia Mono", "Cascadia Code", Consolas, "Courier New", monospace',
    scrollback: 5000,
    allowProposedApi: false,
    theme: {
      background: "#1e1e2e",
      foreground: "#cdd6f4",
      cursor: "#f5e0dc",
      cursorAccent: "#1e1e2e",
      selectionBackground: "#45475a",
      black: "#45475a", red: "#f38ba8", green: "#a6e3a1", yellow: "#f9e2af",
      blue: "#89b4fa", magenta: "#f5c2e7", cyan: "#94e2d5", white: "#bac2de",
      brightBlack: "#585b70", brightRed: "#f38ba8", brightGreen: "#a6e3a1",
      brightYellow: "#f9e2af", brightBlue: "#89b4fa", brightMagenta: "#f5c2e7",
      brightCyan: "#94e2d5", brightWhite: "#a6adc8"
    }
  });

  var fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.open(document.getElementById("terminal"));
  try { fit.fit(); } catch (e) { }
  window.addEventListener("resize", function () { try { fit.fit(); } catch (e) { } });

  // 暴露给宿主以便自检/诊断 (只读用途)
  window.__novaTerm = term;
  window.__novaReceived = "";
  var diagDecoder = new TextDecoder("utf-8", { stream: true });

  function b64FromBytes(bytes) {
    var s = "", chunk = 0x8000;
    for (var i = 0; i < bytes.length; i += chunk) {
      s += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
    }
    return btoa(s);
  }

  term.onData(function (d) {
    var bytes = new TextEncoder().encode(d);
    window.chrome.webview.postMessage({ type: "input", data: b64FromBytes(bytes) });
  });

  term.onResize(function (size) {
    window.chrome.webview.postMessage({ type: "resize", cols: size.cols, rows: size.rows });
  });

  // 宿主 -> 网页。WebView2 通过 chrome.webview 的 message 事件投递宿主消息;
  // 同时监听 window.message 以兼容在普通浏览器/其它宿主中复用该页面。
  function onHostMessage(ev) {
    var m = ev.data;
    if (!m || typeof m !== "object") return;
    if (m.type === "output") {
      var bin = atob(m.data);
      var bytes = new Uint8Array(bin.length);
      for (var i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
      term.write(bytes);
      try {
        window.__novaReceived = (window.__novaReceived || "") + diagDecoder.decode(bytes, { stream: true });
        if (window.__novaReceived.length > 40000)
          window.__novaReceived = window.__novaReceived.slice(-20000);
      } catch (e) { }
    } else if (m.type === "focus") {
      term.focus();
    } else if (m.type === "reset") {
      term.reset();
    } else if (m.type === "fit") {
      try { fit.fit(); } catch (e) { }
    } else if (m.type === "setfont") {
      term.options.fontSize = m.size;
      try { fit.fit(); } catch (e) { }
    }
  }

  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener("message", onHostMessage);
  }
  window.addEventListener("message", onHostMessage);

  window.chrome.webview.postMessage({ type: "ready" });
})();
