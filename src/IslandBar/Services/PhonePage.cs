using System.Net;

namespace IslandBar.Services
{
    /// <summary>手機掃 QR code 打開的網頁（不用裝 App；深色、中文、單手好按）</summary>
    internal static class PhonePage
    {
        public static string Html(string pc) => Template.Replace("%%PC%%", WebUtility.HtmlEncode(pc ?? "電腦"));

        private const string Template = """
<!doctype html>
<html lang="zh-Hant">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<meta name="color-scheme" content="dark">
<meta name="theme-color" content="#0e0e10">
<title>傳到 %%PC%%</title>
<style>
:root{--bg:#0e0e10;--card:#1b1b1f;--card2:#26262b;--line:#2c2c32;--dim:#8e8e96;--fg:#f2f2f5;--acc:#34d3c0;--warn:#ffb040;--bad:#ff6b6b}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
html,body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.45 -apple-system,BlinkMacSystemFont,"Segoe UI","PingFang TC","Noto Sans TC","Microsoft JhengHei",sans-serif}
main{max-width:560px;margin:0 auto;padding:max(16px,env(safe-area-inset-top)) 16px calc(28px + env(safe-area-inset-bottom))}
header{display:flex;align-items:center;gap:12px;margin:6px 2px 18px}
.logo{width:42px;height:42px;border-radius:21px;background:var(--acc);display:grid;place-items:center;flex:none}
.logo svg{width:22px;height:22px}
h1{font-size:18px;margin:0;font-weight:650;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
#st{margin:1px 0 0;font-size:13px;color:var(--dim);display:flex;align-items:center;gap:6px}
#st i{width:7px;height:7px;border-radius:4px;background:var(--acc);display:inline-block}
#st.off{color:var(--bad)}#st.off i{background:var(--bad)}
.card{background:var(--card);border:1px solid var(--line);border-radius:18px;padding:14px;margin-bottom:14px}
h2{font-size:13px;font-weight:600;color:var(--dim);margin:0 0 10px;letter-spacing:.02em}
.pick{display:flex;align-items:center;justify-content:center;gap:10px;width:100%;min-height:64px;border-radius:14px;background:var(--acc);color:#0e0e10;font-weight:650;font-size:16px;border:0;cursor:pointer}
.pick svg{width:22px;height:22px}
.row{display:flex;gap:10px;align-items:center;padding:10px 2px;border-top:1px solid var(--line)}
.row:first-child{border-top:0}
.ic{width:40px;height:40px;border-radius:10px;background:var(--card2);flex:none;display:grid;place-items:center;overflow:hidden;color:var(--dim);font-size:11px;font-weight:700}
.ic img{width:100%;height:100%;object-fit:cover}
.meta{flex:1;min-width:0}
.nm{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.sub{font-size:12.5px;color:var(--dim)}
.bar{height:4px;border-radius:2px;background:var(--card2);margin-top:6px;overflow:hidden}
.bar b{display:block;height:100%;width:0;background:var(--acc);transition:width .2s}
.ok{color:var(--acc)}.err{color:var(--bad)}
.btn{flex:none;border:0;border-radius:12px;background:var(--card2);color:var(--fg);padding:9px 14px;font-size:14px;text-decoration:none;cursor:pointer}
.btn.acc{background:var(--acc);color:#0e0e10;font-weight:600}
textarea{width:100%;min-height:84px;resize:vertical;border-radius:12px;border:1px solid var(--line);background:var(--bg);color:var(--fg);padding:10px 12px;font:inherit}
textarea:focus{outline:2px solid var(--acc);outline-offset:-1px}
.tx{white-space:pre-wrap;word-break:break-word;max-height:7.5em;overflow:hidden}
.empty{color:var(--dim);font-size:13.5px;margin:2px}
.send{display:flex;justify-content:flex-end;margin-top:10px;gap:8px;align-items:center}
.toast{position:fixed;left:50%;bottom:calc(24px + env(safe-area-inset-bottom));transform:translateX(-50%) translateY(20px);background:#2f2f35;color:var(--fg);padding:9px 16px;border-radius:20px;font-size:14px;opacity:0;transition:.25s;pointer-events:none}
.toast.on{opacity:1;transform:translateX(-50%)}
footer{color:var(--dim);font-size:12px;text-align:center;margin-top:6px}
</style>
</head>
<body>
<main>
  <header>
    <div class="logo"><svg viewBox="0 0 24 24" fill="none" stroke="#0e0e10" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M7 4v16M7 20l-3.5-3.5M7 20l3.5-3.5M17 20V4M17 4l-3.5 3.5M17 4l3.5 3.5"/></svg></div>
    <div style="min-width:0"><h1>傳到 %%PC%%</h1><p id="st"><i></i><span>已連線</span></p></div>
  </header>

  <section class="card">
    <button class="pick" id="pickBtn"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 16V4M12 4l-5 5M12 4l5 5M4 16v3a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-3"/></svg>選照片或檔案，傳到電腦</button>
    <input type="file" id="file" multiple hidden>
    <div id="ups"></div>
  </section>

  <section class="card">
    <h2>傳文字或網址</h2>
    <textarea id="txt" placeholder="貼上文字或網址（電腦收到會自動複製到剪貼簿）"></textarea>
    <div class="send"><span id="txtSt" class="sub"></span><button class="btn acc" id="sendTxt">傳到電腦</button></div>
  </section>

  <section class="card">
    <h2>電腦傳來的</h2>
    <div id="list"><p class="empty">在電腦上把檔案拖到「手機傳檔」卡片，或按「傳剪貼簿」，就會出現在這裡。</p></div>
  </section>
  <footer>只在同一個 Wi-Fi 裡傳，不經過網路上的任何伺服器。電腦那邊關掉傳檔，這個網頁就失效。</footer>
</main>
<div class="toast" id="toast"></div>
<script>
(function(){
  var B = location.pathname.replace(/\/+$/, '');
  var $ = function(id){ return document.getElementById(id); };
  function esc(s){ return String(s == null ? '' : s).replace(/[&<>"']/g, function(c){ return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]; }); }
  function size(b){ return b < 1024 ? b + ' B' : b < 1048576 ? (b/1024).toFixed(1) + ' KB' : b < 1073741824 ? (b/1048576).toFixed(1) + ' MB' : (b/1073741824).toFixed(2) + ' GB'; }
  function ext(n){ var m = /\.([A-Za-z0-9]{1,5})$/.exec(n || ''); return m ? m[1].toUpperCase() : 'FILE'; }
  var tt;
  function toast(t){ var e = $('toast'); e.textContent = t; e.classList.add('on'); clearTimeout(tt); tt = setTimeout(function(){ e.classList.remove('on'); }, 1800); }
  function copy(t){
    function fb(){ var a = document.createElement('textarea'); a.value = t; a.setAttribute('readonly',''); a.style.position = 'fixed'; a.style.top = '-1000px'; document.body.appendChild(a); a.select(); a.setSelectionRange(0, t.length); var ok = false; try { ok = document.execCommand('copy'); } catch(e){} a.remove(); toast(ok ? '已複製' : '請長按文字自己複製'); }
    if (navigator.clipboard && window.isSecureContext) navigator.clipboard.writeText(t).then(function(){ toast('已複製'); }, fb); else fb();
  }

  // ---- 上傳 ----
  var queue = [], busy = false;
  $('pickBtn').onclick = function(){ $('file').click(); };
  $('file').onchange = function(){ for (var i = 0; i < this.files.length; i++) add(this.files[i]); this.value = ''; next(); };
  function add(f){
    var row = document.createElement('div'); row.className = 'row';
    row.innerHTML = '<div class="ic">' + esc(ext(f.name)) + '</div><div class="meta"><div class="nm">' + esc(f.name) + '</div><div class="sub">等待中 · ' + size(f.size) + '</div><div class="bar"><b></b></div></div>';
    var ups = $('ups'); if (!ups.children.length) ups.style.marginTop = '8px';
    ups.insertBefore(row, ups.firstChild);
    if (/^image\//.test(f.type) && window.URL) { var img = new Image(); img.onload = function(){ URL.revokeObjectURL(img.src); }; img.src = URL.createObjectURL(f); var ic = row.querySelector('.ic'); ic.textContent = ''; ic.appendChild(img); }
    queue.push({ f: f, row: row });
  }
  function next(){
    if (busy || !queue.length) return;
    busy = true;
    var it = queue.shift(), sub = it.row.querySelector('.sub'), bar = it.row.querySelector('.bar b');
    var x = new XMLHttpRequest(), t0 = Date.now();
    x.open('POST', B + '/up?name=' + encodeURIComponent(it.f.name));
    x.upload.onprogress = function(e){
      if (!e.lengthComputable) return;
      var p = e.loaded / e.total, s = (Date.now() - t0) / 1000;
      bar.style.width = (p * 100).toFixed(1) + '%';
      sub.textContent = Math.round(p * 100) + '% · ' + size(e.loaded) + ' / ' + size(e.total) + (s > 1 ? ' · ' + size(e.loaded / s) + '/s' : '');
    };
    x.onload = function(){
      var r = {}; try { r = JSON.parse(x.responseText); } catch(e){}
      if (x.status === 200 && r.ok) { bar.style.width = '100%'; sub.innerHTML = '<span class="ok">✓ 已傳到電腦</span> · ' + size(it.f.size); }
      else { sub.innerHTML = '<span class="err">✕ ' + esc(r.error || ('失敗（' + x.status + '）')) + '</span>'; }
      busy = false; next();
    };
    x.onerror = function(){ sub.innerHTML = '<span class="err">✕ 連不到電腦</span>'; busy = false; next(); };
    x.send(it.f);
  }

  // ---- 文字 ----
  $('sendTxt').onclick = function(){
    var t = $('txt').value.trim(); if (!t) return;
    var x = new XMLHttpRequest();
    x.open('POST', B + '/text');
    x.setRequestHeader('Content-Type', 'text/plain;charset=utf-8');
    x.onload = function(){ if (x.status === 200) { $('txt').value = ''; $('txtSt').innerHTML = '<span class="ok">✓ 已傳到電腦（已複製到剪貼簿）</span>'; } else $('txtSt').innerHTML = '<span class="err">✕ 失敗</span>'; };
    x.onerror = function(){ $('txtSt').innerHTML = '<span class="err">✕ 連不到電腦</span>'; };
    x.send(t);
  };

  // ---- 電腦傳來的 ----
  var last = '', fails = 0;
  function render(s){
    var list = $('list');
    if (!s.offers || !s.offers.length) { list.innerHTML = '<p class="empty">在電腦上把檔案拖到「手機傳檔」卡片，或按「傳剪貼簿」，就會出現在這裡。</p>'; return; }
    var h = '';
    s.offers.forEach(function(o){
      var url = B + '/f/' + encodeURIComponent(o.id);
      if (o.kind === 'text') {
        h += '<div class="row"><div class="ic">T</div><div class="meta"><div class="tx">' + esc(o.text) + '</div></div><button class="btn" data-copy="' + esc(o.id) + '">複製</button></div>';
      } else {
        var ic = o.kind === 'image' ? '<img loading="lazy" src="' + url + '?inline=1" alt="">' : esc(ext(o.name));
        h += '<div class="row"><div class="ic">' + ic + '</div><div class="meta"><div class="nm">' + esc(o.name) + '</div><div class="sub">' + size(o.size) + '</div></div>' +
             (o.kind === 'image' || o.kind === 'video' || o.kind === 'pdf' ? '<a class="btn" href="' + url + '?inline=1" target="_blank" rel="noopener">開啟</a>' : '') +
             '<a class="btn acc" href="' + url + '" download="' + esc(o.name) + '">下載</a></div>';
      }
    });
    list.innerHTML = h;
    Array.prototype.forEach.call(list.querySelectorAll('[data-copy]'), function(b){
      b.onclick = function(){ var o = s.offers.filter(function(x){ return x.id === b.getAttribute('data-copy'); })[0]; if (o) copy(o.text); };
    });
  }
  function online(on){
    var st = $('st'); st.className = on ? '' : 'off';
    st.querySelector('span').textContent = on ? '已連線' : '連不到電腦（電腦可能關掉傳檔了，或手機換了網路）';
  }
  function poll(){
    var x = new XMLHttpRequest();
    x.open('GET', B + '/state', true);
    x.timeout = 6000;
    x.onload = function(){
      if (x.status !== 200) { fails++; if (fails > 1) online(false); return; }
      fails = 0; online(true);
      if (x.responseText !== last) { last = x.responseText; try { render(JSON.parse(last)); } catch(e){} }
    };
    x.onerror = x.ontimeout = function(){ fails++; if (fails > 1) online(false); };
    x.send();
  }
  poll(); setInterval(function(){ if (!document.hidden) poll(); }, 2000);
  document.addEventListener('visibilitychange', function(){ if (!document.hidden) poll(); });
})();
</script>
</body>
</html>
""";
    }
}
