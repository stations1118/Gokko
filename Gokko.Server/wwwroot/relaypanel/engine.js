'use strict';
// 信号制御盤の画面（全駅で同じ）。盤の状態と連動の判断はサーバー（Services/RelayPanels.cs）が持ち、
// この画面は操作を送って、届いた状態を描くだけ。開いている全員に同じ状態が届くので、Aさんの操作は Bさんの画面にも出る。
// 駅のデータは relaypanel/stations/<駅ID>.json（/api/relay/<駅ID>/station で読む）。部品の絵は relaypanel/parts/。
//
// 操作: 左クリックでてこを左へ、右クリックでてこを右へ倒す。着点ボタン・接近ボタンはクリックで押す。
//       進路を取れるのは、管理画面でこの駅が「駅扱い」で、解放の鍵が R のときだけ。それ以外のときも、てこは倒せてボタンは押せるが、効果はない
//       解放の鍵: 鍵がなければクリックで刺す（ログインが要る）。刺さっていれば右クリックで R へ、左クリックで N へ（N で左クリックすると抜く）
// 音: てこ・ボタン・鍵・転換・接近の音を、サーバーから届いた出来事で鳴らす（全員に聞こえる）。音量は人ごと（この画面に覚える）
// 小窓: 右上の道具から tid.html などを開く。置き場所と大きさは人ごと（この画面に覚える）
(() => {
  const NS = 'http://www.w3.org/2000/svg';
  const PARTS = 'relaypanel/parts/';
  const svg = document.getElementById('panel');
  const params = new URLSearchParams(location.search);
  const ST = (params.get('st') || 'TH71').replace(/[^A-Za-z0-9]/g, '');
  const TEST = params.get('test') === '1';
  const KNOB_DEG = 38;

  const store = {
    get(k, d) { try { const v = localStorage.getItem(k); return v == null ? d : JSON.parse(v); } catch (e) { return d; } },
    set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch (e) {} },
    del(k) { try { localStorage.removeItem(k); } catch (e) {} },
  };

  // ---- 部品を置く道具 ----
  const el = (tag, attrs = {}, parent = svg) => {
    const e = document.createElementNS(NS, tag);
    for (const k in attrs) e.setAttribute(k, attrs[k]);
    parent.appendChild(e);
    return e;
  };
  const img = (name, x, y, w, h, parent, extra = {}) => el('image', Object.assign({ href: PARTS + name + '.svg', x, y, width: w, height: h }, extra), parent);
  const setImg = (e, name) => { const h = PARTS + name + '.svg'; if (e.getAttribute('href') !== h) e.setAttribute('href', h); };
  function knobGroup(parent, x, y) {
    const g = el('g', { class: 'knob', style: `transform-origin:${x}px ${y}px` }, parent);
    return g;
  }

  // 機械彫刻の字（kikai-glyphs.svg の輪郭）で書く。cx, cy は字の真ん中
  const glyphs = {};
  async function loadGlyphs() {
    try {
      const doc = new DOMParser().parseFromString(await (await fetch(PARTS + 'kikai-glyphs.svg')).text(), 'image/svg+xml');
      for (const p of doc.querySelectorAll('path[id^="g-"]')) glyphs[p.id.slice(2)] = { d: p.getAttribute('d'), adv: +p.getAttribute('data-advance') || 72 };
    } catch (e) {}
  }
  // maxW を渡すと、字の幅がそれを超えないように小さくする（てこの握りの丸に収める）
  function glyphText(s, cx, cy, size, fill, parent, maxW) {
    const chars = [...String(s)], adv = chars.reduce((w, c) => w + (glyphs[c]?.adv ?? 60), 0);
    if (maxW && adv * size / 100 > maxW) size = maxW * 100 / adv;
    const k = size / 100, width = adv * k;
    const g = el('g', { fill, 'pointer-events': 'none' }, parent);
    let x = cx - width / 2;
    for (const c of chars) {
      const gl = glyphs[c];
      if (gl) el('path', { d: gl.d, transform: `translate(${x.toFixed(1)} ${(cy + 36 * k).toFixed(1)}) scale(${k})` }, g);
      else { const t = el('text', { x: x + 30 * k, y: cy + 36 * k, 'font-size': size * 0.8, 'text-anchor': 'middle' }, g); t.textContent = c; }
      x += (gl?.adv ?? 60) * k;
    }
    return g;
  }

  // ---- 音（Web Audio で作る。録った音に差し替えるときは、ここの関数を置き換える） ----
  const Sound = (() => {
    let ctx = null, master = null, noiseBuf = null;
    let volume = store.get('relayVolume', 60);
    function init() {
      // iPhone・iPad は、消音スイッチが入っていると Web Audio が鳴らない。音楽と同じ扱い（playback）にして鳴らす（iOS 17 から）
      try { if (navigator.audioSession) navigator.audioSession.type = 'playback'; } catch (e) {}
      if (ctx) { if (ctx.state !== 'running') ctx.resume().catch(() => {}); return; }
      try { ctx = new (window.AudioContext || window.webkitAudioContext)(); } catch (e) { return; }
      ctx.onstatechange = () => showState();
      if (ctx.state !== 'running') ctx.resume().catch(() => {});
      master = ctx.createGain(); master.connect(ctx.destination);
      const n = ctx.sampleRate;
      noiseBuf = ctx.createBuffer(1, n, n);
      const d = noiseBuf.getChannelData(0);
      for (let i = 0; i < n; i++) d[i] = Math.random() * 2 - 1;
      setVolume(volume);
      loadFiles();
    }
    // 録った音（relaypanel/sounds/）。[ファイル, 頭の無音を飛ばす秒, 大きさ]。読めるまでと読めなかったときは合成の音で鳴らす
    const FILES = {
      keyIn:   ['key-in.wav',   0.02, 1],
      keyOut:  ['key-out.wav',  0.05, 1],
      keyTurn: ['key-turn.wav', 0.11, 1],
      // てこは、ユーザーが送った録音から BGM を取り除いて切り出したもの
      leverReverse: ['lever-reverse.wav', 0, 0.6],
      leverNormal:  ['lever-normal.wav',  0, 0.6],
      button:       ['button.wav',        0.55, 0.6],   // ユーザーが用意した音。押す音（0.57 秒）と離す音（0.77・0.83 秒）が入っている
    };
    const bufs = {};
    function loadFiles() {
      for (const [k, [file]] of Object.entries(FILES)) {
        fetch('relaypanel/sounds/' + file).then(r => r.ok ? r.arrayBuffer() : Promise.reject(r.status))
          .then(a => ctx.decodeAudioData(a)).then(b => { bufs[k] = b; }).catch(() => {});
      }
    }
    function playFile(k) {
      const b = bufs[k]; if (!b) return false;
      const [, skip, gain] = FILES[k];
      const s = ctx.createBufferSource(); s.buffer = b;
      const g = ctx.createGain(); g.gain.value = gain;
      s.connect(g); g.connect(master); s.start(ctx.currentTime, skip);
      return true;
    }
    function setVolume(v) {
      volume = v; store.set('relayVolume', v);
      if (master) master.gain.value = Math.pow(v / 100, 2) * 1.6;
    }
    const T = () => ctx.currentTime;
    function env(g, t, a, peak, decay) {
      g.gain.setValueAtTime(0.0001, t);
      g.gain.exponentialRampToValueAtTime(peak, t + a);
      g.gain.exponentialRampToValueAtTime(0.0001, t + a + decay);
    }
    function noise(t, dur, freq, q, peak, type = 'bandpass', freqEnd) {
      const s = ctx.createBufferSource(); s.buffer = noiseBuf;
      const f = ctx.createBiquadFilter(); f.type = type; f.frequency.setValueAtTime(freq, t); f.Q.value = q;
      if (freqEnd) f.frequency.exponentialRampToValueAtTime(freqEnd, t + dur);
      const g = ctx.createGain(); env(g, t, 0.002, peak, dur);
      s.connect(f); f.connect(g); g.connect(master);
      s.start(t, Math.random() * 0.5); s.stop(t + dur + 0.05);
    }
    function tone(t, freq, dur, peak, type = 'sine', freqEnd) {
      const o = ctx.createOscillator(); o.type = type; o.frequency.setValueAtTime(freq, t);
      if (freqEnd) o.frequency.exponentialRampToValueAtTime(freqEnd, t + dur);
      const g = ctx.createGain(); env(g, t, 0.003, peak, dur);
      o.connect(g); g.connect(master);
      o.start(t); o.stop(t + dur + 0.05);
    }
    // 止まっていたら動かし直す（ブラウザが止めることがある）。動いていなければ鳴らさない
    const ok = () => { if (ctx && ctx.state !== 'running') ctx.resume().catch(() => {}); return !!ctx && ctx.state === 'running'; };
    // 右上の「音」の字: 鳴らせないときは赤くして、押すように知らせる
    function showState() {
      const el = document.getElementById('sndState'); if (!el) return;
      const run = ctx && ctx.state === 'running';
      el.style.color = run ? '' : '#ff8f7a';
      el.title = run ? '音量（この画面だけ）。押すと試しの音が鳴る' : '音が止まっています。押すと鳴るようになります';
    }
    // 金属の小さな鳴り（倍音を少しずらして重ねる）
    function ring(t, base, parts, peak) {
      for (const [m, p, d] of parts) tone(t, base * m, d, peak * p, 'sine');
    }
    // てこ: 鋭い「パチッ」1つ。ユーザーが送った2つの音の間に寄せた
    //   1つめの音: 2〜16kHz に広がった当たりと、380〜850Hz のこもった響きが 0.05 秒ほど残る
    //   2つめの音: 8〜10kHz が強い明るい当たりで、金属の「シャン」とした響きが 0.15 秒ほど残る
    //   録った音（倒す: lever-reverse.wav、定位に戻す: lever-normal.wav）が読めていればそちらを鳴らす
    function lever(normal) {
      if (!ok()) return; if (playFile(normal ? 'leverNormal' : 'leverReverse')) return;
      const t = T(), v = 0.9 + Math.random() * 0.2;
      noise(t, 0.03, 2000, 0.7, 0.45 * v, 'highpass');
      noise(t, 0.016, 9500, 1, 0.4 * v);
      noise(t + 0.008, 0.025, 4000, 0.8, 0.16 * v, 'highpass');
      noise(t + 0.004, 0.13, 7000, 0.7, 0.07 * v, 'highpass');
      noise(t, 0.05, 700, 1.6, 0.13 * v);
      ring(t, 845, [[0.45, 0.8, 0.05], [0.645, 0.7, 0.045], [1, 1, 0.05]], 0.07 * v);
      ring(t, 8100, [[1, 1, 0.12], [1.13, 0.8, 0.1], [1.28, 0.6, 0.08]], 0.035 * v);
    }
    // 押しボタン: 録音に寄せた。指が触れる小さく高い「チ」と、16ms 後に押し込んで当たる音（600・940・2150Hz が響く）
    function button() {
      if (!ok()) return; if (playFile('button')) return;
      const t = T(), u = t + 0.016;
      noise(t, 0.006, 7000, 1, 0.22, 'highpass');
      noise(u, 0.022, 1800, 0.7, 0.45, 'highpass');
      noise(u, 0.03, 8000, 3, 0.18);
      noise(u, 0.04, 650, 2, 0.18);
      ring(u, 2150, [[0.284, 0.9, 0.05], [0.437, 0.7, 0.04], [1, 1, 0.06]], 0.08);
    }
    // 鍵を刺す・抜く: ユーザーが録った音（sounds/key-in.wav・key-out.wav）。読めないときは合成の「カリカリ・ゴツ」
    function keyIn(out) {
      if (!ok()) return; if (playFile(out ? 'keyOut' : 'keyIn')) return; const t = T();
      const pins = [0, 0.032, 0.062, 0.095, 0.125];
      noise(t, 0.15, 2600, 2.5, 0.05);
      pins.forEach((p, i) => {
        const u = t + p + Math.random() * 0.008;
        noise(u, 0.007, 2400 + i * (out ? -150 : 150), 3, 0.2);
        tone(u, 1900 + Math.random() * 300, 0.014, 0.03, 'triangle');
      });
      const end = t + (out ? 0 : 0.16);
      noise(end, 0.012, 1300, 2.5, 0.35);
      tone(end, 420, 0.04, 0.16, 'triangle', 300);
      ring(end, 1600, [[1, 1, 0.03], [2.7, 0.35, 0.018]], 0.05);
    }
    // 鍵を回す: ユーザーが録った音（sounds/key-turn.wav）。読めないときは合成の「ガチャッ」
    function keyTurn() {
      if (!ok()) return; if (playFile('keyTurn')) return; const t = T();
      noise(t, 0.01, 2000, 2, 0.2);
      tone(t, 1300, 0.015, 0.03, 'triangle');
      noise(t + 0.06, 0.016, 1100, 1.8, 0.45);
      tone(t + 0.06, 330, 0.05, 0.22, 'triangle', 220);
      ring(t + 0.06, 1450, [[1, 1, 0.035], [2.5, 0.4, 0.02]], 0.06);
    }
    // 継電器（リレー）が動く小さな「カチ」
    function relay(n = 1, gap = 0.04, peak = 0.16) {
      if (!ok()) return; const t = T();
      for (let i = 0; i < n; i++) { const u = t + i * gap + Math.random() * 0.01; noise(u, 0.008, 3800 + Math.random() * 1500, 3, peak); tone(u, 1500, 0.012, peak * 0.25, 'square'); }
    }
    // 転てつ器が動く: 遠くの転てつ機の音（低いうなり）
    function pointMotor() {
      if (!ok()) return; const t = T();
      const o = ctx.createOscillator(); o.type = 'sawtooth'; o.frequency.setValueAtTime(70, t); o.frequency.linearRampToValueAtTime(95, t + 0.3);
      const f = ctx.createBiquadFilter(); f.type = 'lowpass'; f.frequency.value = 380;
      const g = ctx.createGain(); g.gain.setValueAtTime(0.0001, t); g.gain.exponentialRampToValueAtTime(0.06, t + 0.15);
      g.gain.setValueAtTime(0.06, t + 1.0); g.gain.exponentialRampToValueAtTime(0.0001, t + 1.2);
      o.connect(f); f.connect(g); g.connect(master); o.start(t); o.stop(t + 1.25);
    }
    // 接近のブザー: 上り（館浜方）は「ペーッ ペーッ ペーッ」と切って鳴らし、下り（新大路方）は「ツーーーー」と続けて鳴らす
    //   wave: 音の形、f: 高さ、on/off: 鳴らす・休む長さ（秒。off が無ければ続けて鳴らす）、peak: 大きさ、pan: 左右
    const BUZZ = {
      up:   { wave: 'square', f: 740, lp: 1800, on: 0.42, off: 0.22, peak: 0.05, pan: -0.35 },
      down: { wave: 'sine',   f: 1050, lp: 4000, peak: 0.085, pan: 0.35 },
    };
    const buzz = {};
    function buzzer(id, on) {
      const b = buzz[id];
      if (on && !b && ok()) {
        const cfg = BUZZ[id] || BUZZ.down, t = T();
        const o = ctx.createOscillator(); o.type = cfg.wave; o.frequency.value = cfg.f;
        const lp = ctx.createBiquadFilter(); lp.type = 'lowpass'; lp.frequency.value = cfg.lp; lp.Q.value = 0.5;
        const g = ctx.createGain(); g.gain.setValueAtTime(0, t);
        if (!cfg.off) g.gain.setTargetAtTime(cfg.peak, t, 0.006);
        let out = g;
        if (ctx.createStereoPanner) { const p = ctx.createStereoPanner(); p.pan.value = cfg.pan; g.connect(p); out = p; }
        o.connect(lp); lp.connect(g); out.connect(master);
        o.start(t);
        buzz[id] = { g, o, cfg, next: t };
      } else if (!on && b) {
        const t = ctx.currentTime;
        b.g.gain.cancelScheduledValues(t); b.g.gain.setTargetAtTime(0, t, 0.008);
        b.o.stop(t + 0.08);
        delete buzz[id];
      }
    }
    // 切って鳴らす方は、少し先まで「鳴らす・休む」を並べておく
    setInterval(() => {
      if (!ctx) return;
      const t = T();
      for (const id in buzz) {
        const b = buzz[id], c = b.cfg;
        if (!c.off) continue;
        if (b.next < t) b.next = t;
        while (b.next < t + 0.4) {
          b.g.gain.setTargetAtTime(c.peak, b.next, 0.005);
          b.g.gain.setTargetAtTime(0, b.next + c.on, 0.008);
          b.next += c.on + c.off;
        }
      }
    }, 80);
    // 鳴っている方面を { up: true, down: false } のように受け取る
    function bell(map) {
      for (const id of new Set([...Object.keys(map), ...Object.keys(buzz)])) buzzer(id, !!map[id]);
    }
    function test() { init(); setTimeout(() => lever(false), 60); }
    return { init, test, showState, setVolume, get volume() { return volume; }, lever, button, keyIn, keyTurn, relay, pointMotor, bell };
  })();

  // ---- 右上の道具・小窓・ログイン ----
  const toastEl = document.getElementById('toast');
  let toastTimer = null;
  function toast(msg) {
    toastEl.textContent = msg; toastEl.classList.add('show');
    clearTimeout(toastTimer); toastTimer = setTimeout(() => toastEl.classList.remove('show'), 2600);
  }

  const vol = document.getElementById('vol');
  vol.value = Sound.volume;
  vol.addEventListener('input', () => Sound.setVolume(+vol.value));
  vol.addEventListener('change', () => Sound.test());   // つまみを離したら試しの音を鳴らす
  document.getElementById('sndState').onclick = () => Sound.test();
  setInterval(() => Sound.showState(), 1000);

  // 盤の上に置く物（小窓・時計）。置き場所は盤の座標（px, py）で覚え、盤を拡大・移動すると同じ尺度で一緒に動く。
  // w・h は盤の全体を出しているときの大きさ（画面の px）。拡大した分だけ transform で大きくする。人ごと・駅ごとにこの画面へ覚える
  const placed = new Set();
  const placeAll = () => { for (const f of placed) f(); };
  addEventListener('resize', () => placeAll());
  function screenOf(px, py) { const p = svg.createSVGPoint(); p.x = px; p.y = py; return p.matrixTransform(svg.getScreenCTM()); }
  const zoomNow = () => (zv ? zv.z : 1);
  const unitPx = () => svg.getScreenCTM().a;   // 盤の 1 単位が画面の何 px か
  // 引っぱって動かす・大きさを変える。o は {px, py, w, h}
  function dragMove(e, o, done) {
    const sx = e.clientX, sy = e.clientY, ox = o.px, oy = o.py, u = unitPx();
    document.body.classList.add('dragging');
    const move = ev => { o.px = ox + (ev.clientX - sx) / u; o.py = oy + (ev.clientY - sy) / u; placeAll(); };
    const up = () => { document.body.classList.remove('dragging'); removeEventListener('pointermove', move); removeEventListener('pointerup', up); done(); };
    addEventListener('pointermove', move); addEventListener('pointerup', up);
  }
  function dragSize(e, o, done, keepRatio) {
    const sx = e.clientX, sy = e.clientY, ow = o.w, oh = o.h, z = zoomNow();
    document.body.classList.add('dragging');
    const move = ev => {
      const dx = (ev.clientX - sx) / z, dy = (ev.clientY - sy) / z;
      if (keepRatio) o.w = ow + Math.max(dx, dy * ow / oh); else { o.w = ow + dx; o.h = oh + dy; }
      placeAll();
    };
    const up = () => { document.body.classList.remove('dragging'); removeEventListener('pointermove', move); removeEventListener('pointerup', up); done(); };
    addEventListener('pointermove', move); addEventListener('pointerup', up);
  }
  function putBox(box, o) {
    const p = screenOf(o.px, o.py), z = zoomNow();
    Object.assign(box.style, { left: p.x + 'px', top: p.y + 'px', width: o.w + 'px', height: o.h + 'px', transform: z === 1 ? '' : `scale(${z})`, transformOrigin: '0 0' });
  }
  // 画面の点を盤の座標に（新しく置くとき）
  function panelOf(x, y) { const p = svg.createSVGPoint(); p.x = x; p.y = y; return p.matrixTransform(svg.getScreenCTM().inverse()); }

  // 小窓（tid.html・ダイヤグラムなど）。どこにでも置け、大きさも変えられる
  const popKey = 'relayPops:' + ST;
  let pops = store.get(popKey, []), popZ = 40;
  const savePops = () => store.set(popKey, pops);
  function openPop(p, isNew) {
    const box = document.createElement('div');
    box.className = 'pop';
    const place = () => { p.w = Math.max(220, p.w); p.h = Math.max(120, p.h); putBox(box, p); };
    box.innerHTML = `<div class="bar"><span></span><input type="range" class="zoom" min="40" max="200" step="5" title="中の画面の大きさ"><button title="しまう（中の画面は動いたまま。無線モニターなら聞こえ続ける。上のボタンでまた出す）">－</button><button title="新しいタブで開く">↗</button><button title="閉じる">×</button></div><iframe></iframe><div class="grip" title="大きさを変える"></div>`;
    box.querySelector('span').textContent = p.title;
    const frame = box.querySelector('iframe');
    frame.src = p.url;
    // 中の画面の大きさ（%）。同じサーバーの画面なので、中の html に zoom をかける。読み直すたびにかけ直す
    const zoomIn = box.querySelector('.zoom');
    zoomIn.value = p.zoom || 100;
    const applyZoom = () => { try { frame.contentDocument.documentElement.style.zoom = (p.zoom || 100) / 100; } catch (e) {} };
    frame.addEventListener('load', applyZoom);
    zoomIn.addEventListener('input', () => { p.zoom = +zoomIn.value; applyZoom(); });
    zoomIn.addEventListener('change', savePops);
    zoomIn.addEventListener('pointerdown', e => e.stopPropagation());
    const [stowBtn, openBtn, closeBtn] = box.querySelectorAll('button');
    stowBtn.onclick = () => { box.classList.add('stowed'); markStowed(); };
    openBtn.onclick = () => window.open(p.url, '_blank');
    closeBtn.onclick = () => { box.remove(); placed.delete(place); pops = pops.filter(q => q !== p); savePops(); markStowed(); };
    box._pop = p;
    box.querySelector('.bar').addEventListener('pointerdown', e => { if (e.target.tagName !== 'BUTTON' && e.target.tagName !== 'INPUT') dragMove(e, p, savePops); });
    // 右下の角で大きさを変える（中の画面がマウスを取らないように、動かしている間は iframe を止める）
    box.querySelector('.grip').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); dragSize(e, p, savePops); });
    // 触った小窓を前へ（入れ替えると中の画面が読み直しになり、ボタンも押せなくなるので、重なりの順だけ変える）
    box.addEventListener('pointerdown', () => { box.style.zIndex = ++popZ; }, true);
    document.body.appendChild(box);
    placed.add(place); place();
    if (isNew) { pops.push(p); savePops(); }
  }
  // しまった小窓（中の画面は動いたまま）。同じ画面のボタンに印を付ける。しまったことは覚えない（読み直すと出る）
  function markStowed() {
    const urls = new Set([...document.querySelectorAll('.pop.stowed')].map(b => b._pop.url));
    const own = new Set([...document.querySelectorAll('#tools button[data-pop]')].map(b => b.dataset.pop));
    for (const b of document.querySelectorAll('#tools button[data-pop]')) b.classList.toggle('stowed', urls.has(b.dataset.pop));
    document.getElementById('popUrl').classList.toggle('stowed', [...urls].some(u => !own.has(u))); // 「ほか…」で開いたもの
  }
  // 初めの大きさは 720×420。ボタンに data-w・data-h があれば、その大きさ（画面の点で）
  // 同じ画面をしまってあれば、新しく開かずに、それを出す
  function newPop(url, title, w = 720, h = 420) {
    const stowed = [...document.querySelectorAll('.pop.stowed')].find(b => b._pop.url === url);
    if (stowed) { stowed.classList.remove('stowed'); stowed.style.zIndex = ++popZ; markStowed(); return; }
    const n = pops.length, at = panelOf(60 + n * 30, 60 + n * 30);
    openPop({ url, title, px: at.x, py: at.y, w: w / zoomNow(), h: h / zoomNow() }, true);
  }
  for (const b of document.querySelectorAll('#tools button[data-pop]')) b.onclick = () => newPop(b.dataset.pop, b.dataset.title, +b.dataset.w || 720, +b.dataset.h || 420);
  document.getElementById('popUrl').onclick = () => {
    // 「ほか…」で開いてしまった小窓があれば、先にそれを出す
    const own = new Set([...document.querySelectorAll('#tools button[data-pop]')].map(b => b.dataset.pop));
    const others = [...document.querySelectorAll('.pop.stowed')].filter(b => !own.has(b._pop.url));
    if (others.length) { for (const b of others) { b.classList.remove('stowed'); b.style.zIndex = ++popZ; } markStowed(); return; }
    const u = prompt('小窓で開く画面（このサーバーの中の画面。例: tid.html?names=1）', 'tid.html');
    if (!u) return;
    const url = new URL(u, location.href);
    if (url.origin !== location.origin) return toast('このサーバーの画面だけ開けます');
    newPop(url.pathname.slice(1) + url.search, url.pathname.slice(1));
  };

  // デジタル時計（ロープレの時刻。設定していなければこの PC の時刻）。枠は clock-face.svg（320×160）で、数字などの置き場所はその中の注記に合わせる。
  // 日付と曜日は、ロープレの日付がないので、この PC の今日
  // 大きさは前の 2 倍に（2026-11-17 ユーザーの指示）。前に覚えていた大きさがあれば、それを 2 倍にして引き継ぐ
  const clockKey = 'relayClock4:' + ST;
  const clockSet = store.get(clockKey, null) || (o => o ? { ...o, w: (o.w || 80) * 2 } : { on: true, px: null, py: null, w: 160 })(store.get('relayClock3:' + ST, null));
  const saveClock = () => store.set(clockKey, clockSet);
  let clockBox = null, clockPlace = null;
  function showClock() {
    const c = clockSet;
    if (c.px == null) { const at = panelOf(24, 24); c.px = at.x; c.py = at.y; }
    const box = document.createElement('div');
    box.id = 'clock';
    const svg = el('svg', { viewBox: '0 0 320 160' }, box);
    const part = (name, x, y, w, h) => el('image', { href: PARTS + name + '.svg', x, y, width: w, height: h, preserveAspectRatio: 'none' }, svg);
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
    clockPlace = () => { c.w = Math.max(48, c.w); c.h = c.w / 2; putBox(box, c); };
    box.addEventListener('pointerdown', e => {
      e.preventDefault();
      if (e.target === grip) dragSize(e, c, saveClock, true); else dragMove(e, c, saveClock);
    });
    const two = (pair, n, blankTens) => { pair[0](blankTens && n < 10 ? null : Math.floor(n / 10)); pair[1](n % 10); };
    const tick = () => {
      const s = Math.floor(GokkoClock.daySec(GokkoClock.now()) + 0.002) % 86400;
      two(D.h, Math.floor(s / 3600)); two(D.m, Math.floor(s / 60) % 60); two(D.s, s % 60);
      colon.style.opacity = (Date.now() % 1000) < 500 ? 1 : 0.25;   // 点は 1 秒ごとに点滅
      const today = new Date();
      two(D.mo, today.getMonth() + 1, true); two(D.d, today.getDate());
      setImg(wday, `clock-wday-${today.getDay()}`);
    };
    tick();
    box._timer = setInterval(tick, 250);
    document.body.appendChild(box);
    placed.add(clockPlace); clockPlace();
    clockBox = box;
  }
  function hideClock() { if (clockBox) { clearInterval(clockBox._timer); clockBox.remove(); placed.delete(clockPlace); clockBox = null; } }
  document.getElementById('clockBtn').onclick = () => {
    clockSet.on = !clockBox;
    clockSet.on ? showClock() : hideClock();
    saveClock();
  };
  // 盤ができてから置く（盤の座標が決まってから）
  function restorePlaced() {
    for (const p of pops) openPop(p, false);
    if (clockSet.on) showClock();
  }

  // ログイン（盤を扱う人みんな。社員番号とパスワード。乗務タブレットと同じ仕組み）。鍵は、さらに「信号扱い」の役職が要る（サーバーが決める）
  // 合い札はこのタブの中だけ（crewsession.js）。乗務メニューから開けば引き継ぎ、URL を直接開いたタブではログインするまで盤を扱えない
  {
    // 前の版でこのブラウザに覚えていた盤のログインは、乗務メニューのログインに移す（メニューにログインがなければ）。盤自身はもう使わない
    const old = store.get('relayCrew', null);
    try { if (old && old.id && old.token && !localStorage.getItem('gokkoTabletToken')) { localStorage.setItem('gokkoTabletId', old.id); localStorage.setItem('gokkoTabletToken', old.token); } } catch (e) {}
    store.del('relayCrew');
  }
  let crew = GokkoCrew.get();   // { id, token }
  const who = document.getElementById('who'), login = document.getElementById('login'), loginMsg = document.getElementById('loginMsg');
  const showWho = () => { who.textContent = crew ? `ID:${crew.id}` : 'ログイン'; };
  const askLogin = (msg) => { login.classList.add('open'); loginMsg.textContent = msg || ''; document.getElementById('loginId').focus(); };
  showWho();
  if (!crew) askLogin('盤を扱うには、社員番号でログインしてください');
  who.onclick = async () => {
    if (crew) {
      if (!confirm(`ID:${crew.id} をログアウトしますか？`)) return;
      try { await fetch('/api/tid/logout', { method: 'POST', headers: { 'X-Crew-Token': crew.token } }); } catch (e) {}
      crew = null; GokkoCrew.logout(); showWho(); return;
    }
    askLogin('');
  };
  document.getElementById('loginClose').onclick = () => login.classList.remove('open');
  GokkoCrew.discordButton(login, loginMsg); // Discord でログイン（戻ってきたら crewsession.js が受け取る）
  document.getElementById('loginGo').onclick = async () => {
    loginMsg.textContent = '';
    const id = document.getElementById('loginId').value.trim(), password = document.getElementById('loginPw').value;
    try {
      const res = await fetch('/api/tid/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id, password, passphrase: GokkoCrew.passphrase(login) }) });
      const j = await res.json();
      if (!res.ok) { loginMsg.textContent = j.message || 'ログインできません'; return; }
      crew = { id: j.id, token: j.token }; GokkoCrew.login(j.id, j.token); showWho();
      document.getElementById('loginPw').value = '';
      login.classList.remove('open');
    } catch (e) { loginMsg.textContent = 'サーバーにつながりません'; }
  };

  const startEl = document.getElementById('start');
  startEl.onclick = () => { Sound.init(); startEl.remove(); };   // ボタンの外を押しても閉じる
  // 音はブラウザが「人が触った」ときにしか鳴らし始めさせないので、触るたびに確かめる（止まっていれば動かし直す）
  for (const t of ['pointerdown', 'pointerup', 'touchend', 'keydown']) addEventListener(t, () => Sound.init(), true);

  // ---- 盤 ----
  document.addEventListener('contextmenu', e => { if (e.target.closest('#panel')) e.preventDefault(); });

  async function send(op, id, dir) {
    // ログインしていなければ送らない（サーバーも断る）。ログインの小窓を開く
    if (!crew) { askLogin('盤を扱うには、社員番号でログインしてください'); return { ok: false, message: 'ログインしてください' }; }
    try {
      const headers = { 'Content-Type': 'application/json', 'X-Crew-Token': crew.token };
      const res = await fetch(`/api/relay/${ST}/op`, { method: 'POST', headers, body: JSON.stringify({ op, id, dir }) });
      const j = await res.json();
      // 扱えない操作は、文を出さずに機械的に動かないだけ（てこ・鍵の向きは盤の状態のまま）。ログインが要るとき（合い札が使えなくなったときなど）だけ、ログインの小窓を開く
      if (!j.ok && j.message && j.message.includes('ログイン')) {
        if (j.message.startsWith('盤を扱うには')) { crew = null; GokkoCrew.clear(); showWho(); } // 合い札がもう使えない
        askLogin(j.message);
      }
      return j;
    } catch (e) { toast('サーバーにつながりません'); }
  }
  // マウスは 左クリック = L、右クリック = R。
  // スマホ（指）は右クリックがないので: てこ（mode 'lever'）は長押しで選んでから左右へなぞると L・R に倒れる。
  // 鍵（'key'）はタップで抜き差し、長押しで選んでから右へなぞると R、左へなぞると N に戻す。ボタン（'tap'）はタップで押す
  let lastTouch = 0, touchHint = false;
  const PRESS_MS = 350, SWIPE_PX = 28;
  // cap: 誤操作防止のキャップの名前（"s:12" など）。Shift＋クリックで、キャップをかぶせる・外す（てこは倒さない）
  function onClicks(target, fn, mode = 'lever', cap = null) {
    target.addEventListener('click', e => { e.preventDefault(); if (Date.now() - lastTouch <= 700) return; if (cap && e.shiftKey) { send('cap', cap); return; } fn('L'); });
    target.addEventListener('contextmenu', e => { e.preventDefault(); if (Date.now() - lastTouch <= 700) return; if (cap && e.shiftKey) { send('cap', cap); return; } fn('R'); });
    target.addEventListener('pointerdown', e => {
      if (e.pointerType === 'mouse') return;
      e.preventDefault(); e.stopPropagation();
      lastTouch = Date.now();
      const sx = e.clientX, sy = e.clientY, id = e.pointerId;
      let armed = false, moved = false, done = false;
      const mark = on => { target.style.stroke = on ? '#ffd94a' : ''; target.style.strokeWidth = on ? 5 : ''; };
      const timer = setTimeout(() => { if (mode !== 'tap' && !moved && !pinching) { armed = true; mark(true); navigator.vibrate?.(15); } }, PRESS_MS);
      const end = () => { clearTimeout(timer); mark(false); removeEventListener('pointermove', move); removeEventListener('pointerup', up); removeEventListener('pointercancel', end); };
      const move = ev => {
        if (ev.pointerId !== id || done) return;
        const dx = ev.clientX - sx, dy = ev.clientY - sy;
        if (pinching) { done = true; end(); return; }
        if (!armed) { if (Math.hypot(dx, dy) > 12) moved = true; return; }
        if (Math.abs(dx) > SWIPE_PX) { done = true; lastTouch = Date.now(); fn(dx > 0 ? 'R' : 'L'); end(); }
      };
      const up = ev => {
        if (ev.pointerId !== id) return;
        lastTouch = Date.now();
        if (!done && !armed && !moved && !pinching) {
          if (mode === 'tap') fn('L');
          else if (mode === 'key') fn('tap');
          else if (!touchHint) { touchHint = true; toast('てこは長押ししてから、左右へなぞって倒します'); }
        }
        end();
      };
      addEventListener('pointermove', move); addEventListener('pointerup', up); addEventListener('pointercancel', end);
    });
  }

  // 盤の拡大・縮小・移動。z は倍率（1 = 全体）、cx・cy は画面の真ん中に来る盤の点。人ごと・駅ごとにこの画面へ覚える
  let panelSize = [1920, 1080];
  const zoomKey = 'relayZoom:' + ST;
  let zv = store.get(zoomKey, null);
  function applyView() {
    const [W, H] = panelSize;
    if (!zv) zv = { z: 1, cx: W / 2, cy: H / 2 };
    zv.z = Math.max(1, Math.min(8, zv.z));
    const w = W / zv.z, h = H / zv.z;
    zv.cx = Math.max(w / 2, Math.min(W - w / 2, zv.cx)); zv.cy = Math.max(h / 2, Math.min(H - h / 2, zv.cy));
    svg.setAttribute('viewBox', `${zv.cx - w / 2} ${zv.cy - h / 2} ${w} ${h}`);
    placeAll();
  }
  const saveView = () => store.set(zoomKey, zv);
  // 画面の点（clientX, Y）が盤のどこか
  function toPanel(x, y) { const p = svg.createSVGPoint(); p.x = x; p.y = y; return p.matrixTransform(svg.getScreenCTM().inverse()); }
  function zoomAt(f, x, y) {
    const before = x == null ? null : toPanel(x, y);
    zv.z *= f; applyView();
    if (before) { const after = toPanel(x, y); zv.cx += before.x - after.x; zv.cy += before.y - after.y; applyView(); }
    saveView();
  }
  svg.addEventListener('wheel', e => { e.preventDefault(); zoomAt(e.deltaY < 0 ? 1.15 : 1 / 1.15, e.clientX, e.clientY); }, { passive: false });
  // 何もない所（てこ・ボタンの外）を引っぱると動かせる
  // スマホ: 2本の指でつまむと拡大・縮小（てこ・ボタンの上で始めてもよい）
  const touches = new Map();
  let pinching = false, pinch0 = null;
  svg.addEventListener('pointerdown', e => {
    if (e.pointerType !== 'touch') return;
    touches.set(e.pointerId, [e.clientX, e.clientY]);
    if (touches.size === 2) {
      pinching = true;
      const [a, b] = [...touches.values()];
      pinch0 = { d: Math.hypot(a[0] - b[0], a[1] - b[1]), z: zv.z };
    }
  }, true);
  addEventListener('pointermove', e => {
    if (!touches.has(e.pointerId)) return;
    touches.set(e.pointerId, [e.clientX, e.clientY]);
    if (!pinching || touches.size < 2) return;
    const [a, b] = [...touches.values()], d = Math.hypot(a[0] - b[0], a[1] - b[1]);
    zoomAt((pinch0.z * d / pinch0.d) / zv.z, (a[0] + b[0]) / 2, (a[1] + b[1]) / 2);
  });
  const touchEnd = e => { touches.delete(e.pointerId); if (touches.size === 0) pinching = false; };
  addEventListener('pointerup', touchEnd); addEventListener('pointercancel', touchEnd);
  svg.addEventListener('pointerdown', e => {
    if (e.button !== 0 || e.target.classList.contains('hit') || zv.z <= 1) return;
    const start = toPanel(e.clientX, e.clientY);
    document.body.classList.add('panning');
    const move = ev => { if (pinching) return; const p = toPanel(ev.clientX, ev.clientY); zv.cx -= p.x - start.x; zv.cy -= p.y - start.y; applyView(); };
    const up = () => { document.body.classList.remove('panning'); removeEventListener('pointermove', move); removeEventListener('pointerup', up); saveView(); };
    addEventListener('pointermove', move); addEventListener('pointerup', up);
  });
  document.getElementById('zoomIn').onclick = () => zoomAt(1.25);
  document.getElementById('zoomOut').onclick = () => zoomAt(1 / 1.25);
  document.getElementById('zoomFit').onclick = () => { zv = null; applyView(); saveView(); };

  let st = null, S = null, view = null, offset = 0, blink = false, flashUntil = 0;

  function build() {
    document.title = `${st.name} 信号制御盤`;
    svg.innerHTML = '';
    const [W, H] = st.size || [1920, 1080];   // 盤の大きさ（駅ごと。大道寺は 3180 幅）
    panelSize = [W, H];
    applyView();
    img('panel-bg', 0, 0, W, H, svg, { preserveAspectRatio: 'none' });
    img(st.face.replace(/^.*\/(.*)\.svg$/, '$1'), 0, 0, W, H, svg);
    const L = {
      lamps: el('g'), heads: el('g'), bases: el('g'), knobs: el('g'), buttons: el('g'), top: el('g'),
    };
    view = { track: [], heads: [], sig: {}, pt: {}, dest: {}, bells: {}, timers: [], area: {}, dirs: {}, key: null, allStop: null, caps: {} };

    // 線路の表示灯
    // 同じ場所に2行ある灯（両渡りの根もとなど）は1つの灯で、どちらかの行の条件が合えば点く
    const lampAt = {};
    for (const t of st.trackLamps) {
      const [x, y] = t.at, key = `${x},${y}`;
      if (lampAt[key]) { lampAt[key].ts.push(t); continue; }
      const e = img('track-lamp-off', x - 16, y - 10, 32, 20, L.lamps, t.rot ? { transform: `rotate(${t.rot} ${x} ${y})` } : {});
      view.track.push(lampAt[key] = { ts: [t], e });
      if (TEST) {
        const h = el('rect', { x: x - 20, y: y - 14, width: 40, height: 28, fill: 'transparent', class: 'hit', transform: t.rot ? `rotate(${t.rot} ${x} ${y})` : '' }, L.top);
        h.addEventListener('click', () => send('occ', t.c));
      }
    }

    // 信号表示灯（70% の大きさ）。at は柱の足（x）と灯の高さ（y）。左へ進む信号は 180° 回す。同じ柱の灯は同じ at に重ねる。
    // route（1つ）か routes（いくつか）の進路が進行のときに点く。主信号機は緑、入換信号機は緑、誘導信号機は橙、進路表示機（type route）は白
    for (const h of st.signalHeads) {
      const [x, y] = h.at, w = /-top$/.test(h.type) ? 89.6 : 67.2, hh = 33.6;   // 入換と同じ柱に並ぶ形（-top）は幅 89.6
      const e = h.dir > 0
        ? img(`signal-head-${h.type}-off`, x - 7, y - 16.8, w, hh, L.heads)
        : img(`signal-head-${h.type}-off`, x - w + 7, y - 16.8, w, hh, L.heads, { transform: `rotate(180 ${x - w / 2 + 7} ${y})` });
      const on = /^callon/.test(h.type) ? 'orange' : /^route/.test(h.type) ? 'white' : 'green';   // 進路表示機（route・route3・route4l など）は白
      view.heads.push({ h, e, on, routes: h.routes || (h.route ? [h.route] : []) });
    }

    const knobColor = { main: 'red', shuntSign: 'green', shunt: 'white', route: 'yellow', callOn: 'pink' };
    // k: 大きさ（1 = 104×100）。転てつ器の単独てこは 0.6
    // bk: 台だけの大きさ（方向てこは台だけ 60%）
    // cap: 誤操作防止のキャップの名前。キャップ（透明な筒）は握りと一緒には回さず、倒れた向きへずらして重みで下がった形に置く
    function lever(x, y, base, knob, label, onTurn, k = 1, bk = k, cap = null) {
      img(`lever-base-${base}`, x - 52 * bk, y - 70 * bk, 104 * bk, 100 * bk, L.bases);
      const g = knobGroup(L.knobs, x, y);
      img(`lever-knob-${knob}`, x - 52 * k, y - 70 * k, 104 * k, 100 * k, g);
      if (label) glyphText(label, x, y, 19 * k, '#c9cdcf', L.knobs, 17 * k);
      const hit = el('circle', { cx: x, cy: y - 18 * k, r: 46 * k, fill: 'transparent', class: 'hit' }, L.top);
      onClicks(hit, onTurn, 'lever', cap);
      let pos = 'N', capped = false, capE = null;
      if (cap) {
        capE = img('lever-cap', x - 52 * k, y - 70 * k, 104 * k, 108 * k, L.knobs, { style: 'display:none' });
        view.caps[cap] = on => { capped = on; place(); };
      }
      const place = () => {
        if (!capE) return;
        capE.style.display = capped ? '' : 'none';
        const dx = pos === 'R' ? 27.7 : pos === 'L' ? -27.7 : 0, dy = pos === 'N' ? 0 : 10.3;
        capE.setAttribute('transform', `translate(${dx * k} ${dy * k})`);
      };
      return p => { pos = p === 'L' || p === 'R' ? p : 'N'; g.style.transform = `rotate(${pos === 'L' ? -KNOB_DEG : pos === 'R' ? KNOB_DEG : 0}deg)`; place(); };
    }
    function lamp(color, x, y, parent = L.buttons, size = 40) {
      const e = img(`lamp-${color}-off`, x - size / 2, y - size / 2, size, size, parent);
      return on => setImg(e, `lamp-${color}-${on ? 'on' : 'off'}`);
    }

    // 発点てこ
    for (const l of st.signalLevers) {
      const [x, y] = l.at;
      const base = l.side === 'LR' ? 'LNR' : l.side === 'L' ? 'LN' : 'NR';
      const turn = lever(x, y, base, knobColor[l.kind] || 'red', l.label || l.n, dir => send('signal', l.n, dir), 1, 1, 's:' + l.n);
      view.sig[l.n] = { turn };
    }

    // 転てつ器てこ（N・自動（C）・R）と、上の灯（左が定位の緑、右が反位の橙）、鎖錠の小さな赤い灯。てこも灯も 60% の大きさ
    const PK = 0.6;
    for (const p of st.points) {
      const [x, y] = p.at;
      const turn = lever(x, y, 'NCR', 'black', p.n, dir => send('point', p.n, dir), PK, PK, 'p:' + p.n);
      view.pt[p.n] = { turn, n: lamp('green', x - 34 * PK, y - 102 * PK, L.buttons, 40 * PK), r: lamp('orange', x + 34 * PK, y - 102 * PK, L.buttons, 40 * PK), lock: lamp('red', x, y + 42 * PK, L.buttons, 22 * PK) };
    }

    // 着点ボタン（光らない）。押している間の絵に替える
    for (const b of st.destButtons) {
      const [x, y] = b.at;
      const e = img('dest-button', x - 33, y - 36, 66, 100, L.buttons);
      glyphText(b.label || b.id, x, y + 42, 18, '#e9ecea', L.buttons);
      const hit = el('circle', { cx: x, cy: y, r: 30, fill: 'transparent', class: 'hit' }, L.top);
      const press = () => { setImg(e, 'dest-button-pressed'); setTimeout(() => setImg(e, 'dest-button'), 160); };
      onClicks(hit, () => { press(); send('dest', b.id); }, 'tap', 'd:' + b.id);
      view.dest[b.id] = press;
      // キャップ（透明な筒）。重みで下がった形の絵を、白い頭の中心に合わせて重ねる
      const capE = img('dest-button-cap', x - 52, y - 52, 104, 122, L.buttons, { style: 'display:none' });
      view.caps['d:' + b.id] = on => { capE.style.display = on ? '' : 'none'; };
    }

    // 集中／駅扱の切換てこ（白）。倒せるが、今は効果がない。緑の灯は、盤で進路を取れるとき（管理画面で駅扱い、かつ解放の鍵が R）に点く
    for (const a of st.areas || []) {
      const turn = lever(a.lever[0], a.lever[1], 'NR', 'white', '', dir => send('area', a.id, dir));
      view.area[a.id] = { turn, lamp: a.lamp ? lamp('green', a.lamp[0], a.lamp[1]) : () => {} };
    }

    // 単線の方向てこ（黄、線路の上）。左（L）= 隣の駅から当駅へ、右（R）= 当駅から隣の駅へ。真ん中（N）はなく、いつもどちらかに倒れている。
    // 両わきの「＜」「＞」の灯（盤に埋め込み）は、区間が開いている向きの2本が黄色に点く。lampsL・lampsR は [x, y, 回す角度]
    for (const d of st.directions || []) {
      const [x, y] = d.at;
      const turn = lever(x, y, 'LR', 'yellow', '', dir => send('direction', d.n, dir), 1, 0.6);
      const bar = ([bx, by, rot]) => img('dir-lamp-off', bx - 20, by - 9, 40, 18, L.lamps, { transform: `rotate(${rot} ${bx} ${by})` });
      const ls = (d.lampsL || []).map(bar), rs = (d.lampsR || []).map(bar);
      view.dirs[d.n] = { turn, set(dir) { for (const e of ls) setImg(e, `dir-lamp-${dir === 'L' ? 'yellow' : 'off'}`); for (const e of rs) setImg(e, `dir-lamp-${dir === 'R' ? 'yellow' : 'off'}`); } };
    }

    // 一斉停止のてこ（赤、面の字は「停」）
    if (st.allStop) {
      const [x, y] = st.allStop.at;
      view.allStop = lever(x, y, 'NR', 'red', '停', dir => send('allStop', '', dir), 1, 1, 'allStop');
    }

    // 解放の鍵。上の灯は解放中（R）に赤、「正常」の灯は鍵が N のときに緑
    if (st.releaseKey) {
      const k = st.releaseKey, [x, y] = k.at;
      img('key-lock-NR', x - 52, y - 62, 104, 104, L.bases);
      const keyG = el('g', { class: 'keyin', style: `transform-origin:${x}px ${y}px;opacity:0;transform:scale(1.25)` }, L.knobs);
      const turnG = el('g', { class: 'keyturn', style: `transform-origin:${x}px ${y}px` }, keyG);
      img('key-switch-key', x - 44, y - 44, 88, 88, turnG);
      const hit = el('circle', { cx: x, cy: y, r: 46, fill: 'transparent', class: 'hit' }, L.top);
      onClicks(hit, dir => {
        if (dir === 'tap') {   // スマホ: タップで抜き差し
          if (!S.key.inserted) return send('key', 'insert');
          if (S.key.pos === 'R') return;   // R のままでは抜けない
          return send('key', 'remove');
        }
        if (!S.key.inserted) return send('key', 'insert');
        if (dir === 'R') return send('key', 'turn', 'R');
        if (S.key.pos === 'R') return send('key', 'turn', 'L');
        send('key', 'remove');
      });
      view.key = {
        set(inserted, pos) {
          keyG.style.opacity = inserted ? 1 : 0;
          keyG.style.transform = inserted ? 'scale(1)' : 'scale(1.25)';
          turnG.style.transform = `rotate(${pos === 'R' ? 45 : 0}deg)`;
        },
        lamp: lamp('red', k.lamp[0], k.lamp[1]),
        normal: k.normalLamp ? lamp('green', k.normalLamp[0], k.normalLamp[1]) : () => {},
      };
    }

    // 転てつ不良・停電・CTC故障。転てつ不良は、盤で取った進路がゲームに載らないまま列車が近づいたときに点く（停電・CTC故障はまだ点かない）
    view.status = (st.statusLamps || []).map(([x, y]) => lamp('red', x, y));

    // 接近ボタン（緑）。列車が接近区間に入ると点滅して鳴る。押すと音が止まり、列車がいる間は点いたまま
    for (const b of st.approachBells || []) {
      const [x, y] = b.at;
      const e = img('approach-button-green-off', x - 33, y - 37, 66, 66, L.buttons);
      const hit = el('circle', { cx: x, cy: y, r: 30, fill: 'transparent', class: 'hit' }, L.top);
      onClicks(hit, () => send('bell', b.id), 'tap');
      view.bells[b.id] = on => setImg(e, `approach-button-green-${on ? 'on' : 'off'}`);
    }

    // 時素の灯（30秒・60秒）
    for (const t of st.timers || []) view.timers.push({ sec: t.sec, set: lamp('yellow', t.at[0], t.at[1]) });
  }

  function flashArea() { flashUntil = Date.now() + 900; draw(); }

  function draw() {
    if (!S || !view) return;
    const occ = new Set(S.occ), route = new Set(S.route);
    const moving = new Set(S.moving);
    // 灯が線路のどちら側にあるか。when（{"41":"N","43":"R"}）か point・pos で書く。今の転てつ器の向きが全部合い、転換中でない灯だけ点く
    const condOk = t => Object.entries(t.when || (t.point ? { [t.point]: t.pos } : {})).every(([p, pos]) => S.point[p] === pos && !moving.has(p));
    for (const { ts, e } of view.track) {
      let s = 'off';
      const ok = ts.filter(condOk);
      if (ok.some(t => occ.has(t.c))) s = 'red';   // 在線も、列車がいる向きの線にだけ点く
      else if (ok.some(t => route.has(t.c))) s = 'white';   // 進路は白（ゲームの進路の一覧に出た進路の、列車がまだ通り終わっていない回路。盤で取った進路も自動の進路も。サーバーの LitCircuits）
      setImg(e, `track-lamp-${s}`);
      e.style.filter = s === 'white' ? 'sepia(0.4) saturate(1.5)' : '';   // 白は少し黄色を混ぜる（古い盤のくすんだ白）
    }
    const go = new Set(S.go), notInGame = new Set(S.notInGame || []);
    for (const { h, e, on, routes } of view.heads) {
      // 信号表示灯は、進行のときだけ点く（停止の赤は点かない）。ゲームに載っていない進路は点滅
      const lit = routes.some(r => go.has(r) && (!notInGame.has(r) || blink));
      setImg(e, `signal-head-${h.type}-${lit ? on : 'off'}`);
    }
    if (view.status[0]) view.status[0](notInGame.size > 0);
    for (const n in view.sig) {
      const v = view.sig[n];
      v.turn(S.sig[n]);
    }
    for (const n in view.pt) {
      const v = view.pt[n], mv = moving.has(n);
      v.turn(S.ptLever[n] === 'N' ? 'L' : S.ptLever[n] === 'R' ? 'R' : 'C');
      v.n(!mv && S.point[n] === 'N'); v.r(!mv && S.point[n] === 'R');
      v.lock(S.locked.includes(n));
    }
    const flashing = Date.now() < flashUntil;
    for (const id in view.area) {
      const v = view.area[id];
      v.turn(S.area[id] === 'R' ? 'R' : 'N');
      v.lamp(flashing ? blinkFast() : S.local);
    }
    if (view.allStop) view.allStop(S.allStop === 'R' ? 'R' : 'N');
    const caps = new Set(S.caps || []);
    for (const c in view.caps) view.caps[c](caps.has(c));
    for (const n in view.dirs) {
      // てこはこの駅の向き。灯はいま取れている区間方向（N = 決まっていないので点けない）
      const v = view.dirs[n], dir = (S.dir || {})[n] || 'L', actual = (S.dirActual || {})[n] ?? dir;
      v.turn(dir); v.set(actual);
    }
    if (view.key) {
      view.key.set(S.key.inserted, S.key.pos);
      view.key.lamp(S.key.inserted && S.key.pos === 'R');
      view.key.normal(!(S.key.inserted && S.key.pos === 'R'));
    }
    for (const id in view.bells) { const b = S.bells[id]; view.bells[id](b && ((b.ringing && blink) || (b.occ && !b.ringing))); }
    Sound.bell(Object.fromEntries(Object.entries(S.bells).map(([id, b]) => [id, !!b.ringing])));
    for (const t of view.timers) t.set(Object.values(S.timers).some(x => x.sec === t.sec));
  }
  const blinkFast = () => Math.floor(Date.now() / 150) % 2 === 0;

  // てこが定位に戻ったか（音を倒す・戻すで分ける）。S はこの知らせで届いた新しい状態
  //   s 発点 N（真ん中）/L/R、p 転てつ器の単独てこ N（左）/A（真ん中）/R（右）、a 切換 N/R、allStop 一斉停止 N/R
  //   d 方向てこは L と R のどちらも倒した位置なので、いつも倒す音
  function leverIsNormal(id) {
    if (id === 'allStop') return (S.allStop ?? 'N') === 'N';
    const n = id.slice(1), v = { s: S.sig, p: S.ptLever, a: S.area, d: S.dir }[id[0]]?.[n];
    if (id[0] === 'd') return false;
    if (id[0] === 'p') return v === 'A';
    return (v ?? 'N') === 'N';
  }
  function playEvents(events) {
    for (const ev of events) {
      switch (ev.type) {
        case 'lever': Sound.lever(leverIsNormal(ev.id)); break;
        case 'button': Sound.button(); if (ev.id[0] === 'd' && view.dest[ev.id.slice(1)]) view.dest[ev.id.slice(1)](); break;
        case 'keyIn': Sound.keyIn(false); break;
        case 'keyOut': Sound.keyIn(true); break;
        case 'keyTurn': Sound.keyTurn(); break;
        case 'pointStart': case 'pointEnd': break;   // 転てつ器の音は鳴らさない（ユーザーの指示）
        case 'route': break;   // 進路が引けた音は鳴らさない（ユーザーの指示）
        case 'release': Sound.relay(3, 0.05, 0.12); break;
      }
    }
  }

  function connect() {
    const es = new EventSource(`/api/relay/${ST}/events`);
    es.onmessage = e => {
      const m = JSON.parse(e.data);
      offset = m.now - Date.now();
      S = m.state;
      if (m.events?.length) playEvents(m.events);
      draw();
    };
    es.onerror = () => { toast('サーバーとの接続が切れました。つなぎ直しています'); };
  }

  setInterval(() => { blink = !blink; draw(); }, 450);
  setInterval(() => { if (Date.now() < flashUntil) draw(); }, 150);

  (async () => {
    await loadGlyphs();
    const res = await fetch(`/api/relay/${ST}/station`);
    if (!res.ok) { startEl.style.display = 'none'; window.relayPick?.(`駅 ${ST} の盤はありません。ほかの駅を選んでください`); return; }   // 盤のない駅: 駅を選ぶ画面を出す
    st = await res.json();
    build();
    restorePlaced();
    connect();
  })();
})();
