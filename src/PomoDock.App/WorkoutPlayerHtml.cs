using System.Net;
using System.Text.Json;

namespace PomoDock.App;

/// <summary>
/// Full-bleed player (set / rest) as HTML so chrome can sit on top of the demo video.
/// WebView2 airspace blocks WPF overlays.
/// </summary>
internal static class WorkoutPlayerHtml
{
    internal static string Shell() => """
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8"/>
        <style>
          html,body{margin:0;width:100%;height:100%;background:#000;color:#fff;font-family:"Segoe UI",system-ui,sans-serif;overflow:hidden;user-select:none}
          #stage{position:absolute;inset:0;overflow:hidden;background:#000}
          #bg{position:absolute;left:50%;top:50%;width:100%;height:100%;object-fit:cover;object-position:center 40%;transform:translate(-50%,-50%) scale(1.4);transform-origin:center center;background:#000}
          #shade{position:absolute;inset:0;background:linear-gradient(180deg,rgba(0,0,0,.75) 0%,rgba(0,0,0,.18) 36%,rgba(0,0,0,.18) 55%,rgba(0,0,0,.86) 100%)}
          #ui{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;padding:3.2vmin 4.2vmin 3.6vmin;box-sizing:border-box}
          .top{width:100%;display:flex;align-items:center;justify-content:center;position:relative;min-height:8vmin;margin-bottom:1vmin}
          .back{position:absolute;left:0;width:9vmin;height:9vmin;max-width:48px;max-height:48px;border-radius:50%;border:1px solid rgba(255,255,255,.4);background:rgba(0,0,0,.4);color:#fff;font-size:5.5vmin;display:flex;align-items:center;justify-content:center;cursor:pointer}
          .head{text-align:center}
          .head .t{font-size:clamp(14px,3.4vmin,20px);font-weight:600;letter-spacing:.02em}
          .head .s{font-size:clamp(11px,2.6vmin,15px);opacity:.7;margin-top:2px}
          #title{margin-top:2.4vmin;font-size:clamp(26px,6.4vmin,44px);font-weight:400;text-align:center;line-height:1.15}
          #sub{margin-top:1.2vmin;font-size:clamp(15px,3.6vmin,22px);opacity:.88;text-align:center}
          #hint{margin-top:1.4vmin;font-size:clamp(16px,4vmin,24px);opacity:.92;text-align:center}
          .grow{flex:1;min-height:8px}
          #ring{width:min(46vmin,240px);height:min(46vmin,240px);border-radius:50%;border:2px solid #fff;display:flex;flex-direction:column;align-items:center;justify-content:center;cursor:pointer;background:transparent}
          #kg{font-size:clamp(13px,3.2vmin,18px);opacity:.9}
          #num{font-size:clamp(52px,16vmin,92px);font-weight:200;line-height:1;margin:1vmin 0}
          #act{font-size:clamp(14px,3.6vmin,20px);opacity:.88}
          .adj{width:100%;display:flex;justify-content:space-between;padding:2vmin 8vmin 0;box-sizing:border-box}
          .adj b{font-size:clamp(26px,7vmin,40px);font-weight:200;cursor:pointer;opacity:.85;width:12vmin;text-align:center}
          #next{margin-top:3vmin;text-align:center;font-size:clamp(12px,2.8vmin,16px);opacity:.55}
          #next .v{opacity:.95;font-size:clamp(14px,3.4vmin,20px);margin-top:4px}
          #dots{display:flex;gap:2.4vmin;margin-top:2.4vmin}
          #dots i{display:block;opacity:.75;font-style:normal;font-size:clamp(12px,3vmin,18px)}
        </style>
        </head>
        <body>
          <div id="stage"><video id="bg" muted loop playsinline autoplay></video></div>
          <div id="shade"></div>
          <div id="ui">
            <div class="top">
              <div class="back" id="back">‹</div>
              <div class="head"><div class="t" id="nav">Entrenamiento</div><div class="s" id="navs"></div></div>
            </div>
            <div id="title"></div>
            <div id="sub"></div>
            <div id="hint"></div>
            <div class="grow"></div>
            <div id="ring">
              <div id="kg"></div>
              <div id="num"></div>
              <div id="act"></div>
            </div>
            <div class="adj" id="adj"><b id="minus">−</b><b id="plus">+</b></div>
            <div id="next"><div>Siguiente ejercicio:</div><div class="v" id="nextv"></div></div>
            <div id="dots"></div>
          </div>
          <script>
            const v = document.getElementById('bg');
            let mode = 'set';
            let restLeft = 0;
            let restTimer = null;
            function send(o){ try { chrome.webview.postMessage(JSON.stringify(o)); } catch(e) {} }
            function playSrc(src){
              if (!src) return;
              if (v.getAttribute('data-src') === src) { v.play().catch(()=>{}); return; }
              v.setAttribute('data-src', src);
              v.src = src;
              v.play().catch(()=>{});
            }
            function dots(total, done){
              const el = document.getElementById('dots');
              el.innerHTML = '';
              for (let i=0;i<total;i++){
                const n = document.createElement('i');
                n.textContent = i < done ? '●' : '○';
                el.appendChild(n);
              }
            }
            function apply(msg){
              mode = msg.mode || 'set';
              if (msg.video) playSrc(msg.video);
              document.getElementById('nav').textContent = msg.nav || 'Entrenamiento';
              document.getElementById('navs').textContent = msg.navs || '';
              document.getElementById('title').textContent = msg.title || '';
              document.getElementById('sub').textContent = msg.sub || '';
              document.getElementById('hint').textContent = msg.hint || '';
              document.getElementById('kg').textContent = msg.kg || '';
              document.getElementById('num').textContent = msg.num || '';
              document.getElementById('act').textContent = msg.act || '';
              document.getElementById('nextv').textContent = msg.next || '';
              document.getElementById('adj').style.display = mode === 'set' ? 'flex' : 'none';
              document.getElementById('kg').style.display = mode === 'set' ? 'block' : 'none';
              dots(msg.sets || 0, msg.done || 0);
              if (restTimer) { clearInterval(restTimer); restTimer = null; }
              if (mode === 'rest') {
                restLeft = msg.rest || 0;
                document.getElementById('num').textContent = restLeft;
                restTimer = setInterval(() => {
                  restLeft -= 1;
                  if (restLeft <= 0) { clearInterval(restTimer); restTimer = null; send({cmd:'restDone'}); return; }
                  document.getElementById('num').textContent = restLeft;
                }, 1000);
              }
            }
            document.getElementById('back').onclick = () => send({cmd:'back'});
            document.getElementById('ring').onclick = () => send({cmd: mode === 'rest' ? 'skipRest' : 'doneSet'});
            document.getElementById('minus').onclick = (e) => { e.stopPropagation(); send({cmd:'kg', delta:-1}); };
            document.getElementById('plus').onclick = (e) => { e.stopPropagation(); send({cmd:'kg', delta:1}); };
            window.chrome.webview.addEventListener('message', ev => {
              const msg = typeof ev.data === 'string' ? JSON.parse(ev.data) : ev.data;
              apply(msg);
            });
            send({cmd:'ready'});
          </script>
        </body>
        </html>
        """;

    internal static string MessageJson(object payload) =>
        JsonSerializer.Serialize(payload);
}
