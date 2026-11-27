// 電子連動装置（denshirendo.html）の小窓と時計。信号制御盤（relaypanel/engine.js）と同じ道具を、画面の位置で置く版。
// ・下の帯の data-pop のボタンで小窓（在線位置・ダイヤグラム・乗務メニュー・無線モニター）を開く。「ほか…」はこのサーバーの好きな画面
// ・小窓は上の帯で動かし、右下の角で大きさを変える。－ でしまう（中の画面は動いたまま）。置き場所はこの画面ごとにこのブラウザへ覚える
// ・「時計」でデジタル時計（ロープレの時刻）を出す・しまう。絵は信号制御盤の時計と同じ（relaypanel/parts の clock-*）
(() => {
  const SCREEN = new URLSearchParams(location.search).get('screen') || 'TH61-TH59';
  const PARTS = 'relaypanel/parts/';
  const NS = 'http://www.w3.org/2000/svg';
  const store = {
    get(k, d) { try { const v = JSON.parse(localStorage.getItem(k)); return v ?? d; } catch (e) { return d; } },
    set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch (e) {} },
  };
  const tools = document.querySelectorAll('[data-pop]');

  // 引っぱって動かす・大きさを変える。o は {x, y, w, h}（画面の px）
  const clampIn = o => { o.x = Math.min(Math.max(o.x, -o.w + 80), innerWidth - 80); o.y = Math.min(Math.max(o.y, 0), innerHeight - 40); };
  function drag(e, o, put, done, mode) {
    const sx = e.clientX, sy = e.clientY, ox = o.x, oy = o.y, ow = o.w, oh = o.h;
    document.body.classList.add('dragging');
    const move = ev => {
      const dx = ev.clientX - sx, dy = ev.clientY - sy;
      if (mode === 'move') { o.x = ox + dx; o.y = oy + dy; clampIn(o); }
      else if (mode === 'ratio') { o.w = Math.max(96, ow + Math.max(dx, dy * ow / oh)); o.h = o.w / 2; }
      else { o.w = Math.max(220, ow + dx); o.h = Math.max(120, oh + dy); }
      put();
    };
    const up = () => { document.body.classList.remove('dragging'); removeEventListener('pointermove', move); removeEventListener('pointerup', up); done(); };
    addEventListener('pointermove', move); addEventListener('pointerup', up);
  }
  const putBox = (box, o) => Object.assign(box.style, { left: o.x + 'px', top: o.y + 'px', width: o.w + 'px', height: o.h + 'px' });

  // ---- 小窓 ----
  const popKey = 'denshiPops:' + SCREEN;
  let pops = store.get(popKey, []), popZ = 40;
  const savePops = () => store.set(popKey, pops);
  function openPop(p, isNew) {
    const box = document.createElement('div');
    box.className = 'pop';
    box.innerHTML = `<div class="bar"><span></span><input type="range" class="zoom" min="40" max="200" step="5" title="中の画面の大きさ"><button title="しまう（中の画面は動いたまま。下のボタンでまた出す）">－</button><button title="新しいタブで開く">↗</button><button title="閉じる">×</button></div><iframe></iframe><div class="grip" title="大きさを変える"></div>`;
    box.querySelector('span').textContent = p.title;
    const frame = box.querySelector('iframe');
    frame.src = p.url;
    const zoom = box.querySelector('.zoom');
    zoom.value = p.zoom || 100;
    const applyZoom = () => { try { frame.contentDocument.documentElement.style.zoom = (p.zoom || 100) / 100; } catch (e) {} };
    frame.addEventListener('load', applyZoom);
    zoom.addEventListener('input', () => { p.zoom = +zoom.value; applyZoom(); });
    zoom.addEventListener('change', savePops);
    zoom.addEventListener('pointerdown', e => e.stopPropagation());
    const [stowBtn, openBtn, closeBtn] = box.querySelectorAll('button');
    stowBtn.onclick = () => { box.classList.add('stowed'); markStowed(); };
    openBtn.onclick = () => window.open(p.url, '_blank');
    closeBtn.onclick = () => { box.remove(); pops = pops.filter(q => q !== p); savePops(); markStowed(); };
    box._pop = p;
    const put = () => putBox(box, p);
    box.querySelector('.bar').addEventListener('pointerdown', e => { if (e.target.tagName !== 'BUTTON' && e.target.tagName !== 'INPUT') drag(e, p, put, savePops, 'move'); });
    box.querySelector('.grip').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); drag(e, p, put, savePops, 'size'); });
    box.addEventListener('pointerdown', () => { box.style.zIndex = ++popZ; }, true);
    document.body.appendChild(box);
    clampIn(p); put();
    if (isNew) { pops.push(p); savePops(); }
  }
  function markStowed() {
    const urls = new Set([...document.querySelectorAll('.pop.stowed')].map(b => b._pop.url));
    const own = new Set([...tools].map(b => b.dataset.pop));
    for (const b of tools) b.classList.toggle('stowed', urls.has(b.dataset.pop));
    document.getElementById('popUrl')?.classList.toggle('stowed', [...urls].some(u => !own.has(u)));
  }
  function newPop(url, title, w = 720, h = 420) {
    const stowed = [...document.querySelectorAll('.pop.stowed')].find(b => b._pop.url === url);
    if (stowed) { stowed.classList.remove('stowed'); stowed.style.zIndex = ++popZ; markStowed(); return; }
    const n = pops.length;
    openPop({ url, title, x: 60 + n * 30, y: 60 + n * 30, w, h }, true);
  }
  for (const b of tools) b.onclick = () => newPop(b.dataset.pop, b.dataset.title, +b.dataset.w || 720, +b.dataset.h || 420);
  const popUrl = document.getElementById('popUrl');
  if (popUrl) popUrl.onclick = () => {
    const own = new Set([...tools].map(b => b.dataset.pop));
    const others = [...document.querySelectorAll('.pop.stowed')].filter(b => !own.has(b._pop.url));
    if (others.length) { for (const b of others) { b.classList.remove('stowed'); b.style.zIndex = ++popZ; } markStowed(); return; }
    const u = prompt('小窓で開く画面（このサーバーの中の画面。例: tid.html?names=1）', 'tid.html');
    if (!u) return;
    const url = new URL(u, location.href);
    if (url.origin !== location.origin) return;
    newPop(url.pathname.slice(1) + url.search, url.pathname.slice(1));
  };

  // ---- デジタル時計（ロープレの時刻。日付と曜日はこの PC の今日） ----
  const clockKey = 'denshiClock:' + SCREEN;
  const c = store.get(clockKey, { on: false, x: 24, y: 80, w: 240 });
  const saveClock = () => store.set(clockKey, c);
  let clockBox = null;
  function showClock() {
    const box = document.createElement('div');
    box.id = 'floatClock';
    const svg = document.createElementNS(NS, 'svg');
    svg.setAttribute('viewBox', '0 0 320 160');
    box.appendChild(svg);
    const part = (name, x, y, w, h) => {
      const e = document.createElementNS(NS, 'image');
      for (const [k, v] of Object.entries({ href: PARTS + name + '.svg', x, y, width: w, height: h, preserveAspectRatio: 'none' })) e.setAttribute(k, v);
      svg.appendChild(e); return e;
    };
    const setImg = (e, name) => { const h = PARTS + name + '.svg'; if (e.getAttribute('href') !== h) e.setAttribute('href', h); };
    part('clock-face', 0, 0, 320, 160);
    const digit = (x, y, w, h) => { const e = part('clock-digit-8', x, y, w, h); return v => v == null ? e.setAttribute('visibility', 'hidden') : (e.removeAttribute('visibility'), setImg(e, `clock-digit-${v}`)); };
    const D = {
      h: [digit(18, 18, 49, 84), digit(67, 18, 49, 84)],
      m: [digit(140.5, 18, 49, 84), digit(189.5, 18, 49, 84)],
      s: [digit(246.5, 54, 28, 48), digit(274.5, 54, 28, 48)],
      mo: [digit(24, 116, 15.75, 27), digit(41, 116, 15.75, 27)],
      d: [digit(72, 116, 15.75, 27), digit(89, 116, 15.75, 27)],
    };
    const colon = part('clock-colon', 116, 18, 24.5, 84);
    const wday = part('clock-wday-0', 268, 116, 27, 27);
    const grip = document.createElement('div');
    grip.className = 'grip';
    box.appendChild(grip);
    c.h = c.w / 2;
    const put = () => putBox(box, c);
    box.addEventListener('pointerdown', e => { e.preventDefault(); drag(e, c, put, saveClock, e.target === grip ? 'ratio' : 'move'); });
    const two = (pair, n, blankTens) => { pair[0](blankTens && n < 10 ? null : Math.floor(n / 10)); pair[1](n % 10); };
    const tick = () => {
      const s = Math.floor(GokkoClock.daySec(GokkoClock.now()) + 0.002) % 86400;
      two(D.h, Math.floor(s / 3600)); two(D.m, Math.floor(s / 60) % 60); two(D.s, s % 60);
      colon.style.opacity = (Date.now() % 1000) < 500 ? 1 : 0.25;
      const today = new Date();
      two(D.mo, today.getMonth() + 1, true); two(D.d, today.getDate());
      setImg(wday, `clock-wday-${today.getDay()}`);
    };
    tick();
    box._timer = setInterval(tick, 250);
    document.body.appendChild(box);
    clampIn(c); put();
    clockBox = box;
  }
  function hideClock() { if (clockBox) { clearInterval(clockBox._timer); clockBox.remove(); clockBox = null; } }
  const clockBtn = document.getElementById('clockBtn');
  if (clockBtn) clockBtn.onclick = () => { c.on = !clockBox; c.on ? showClock() : hideClock(); saveClock(); };

  for (const p of pops) openPop(p, false);
  if (c.on) showClock();
})();
