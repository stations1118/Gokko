// 内線電話（ブラウザの電話機）。信号制御盤（relaypanel.html）の卓上電話機と、スマホ（radio.html）の携帯電話で共通。
// ・接続は /extension（SignalR）。station: その駅の電話機（ログインした人なら誰でも）、handset: PC で参加している本人の受話器
// ・音声は列車無線と同じ形式（Opus 16kHz・モノラル・40ms・24kbps）。libopus-wasm で圧縮・展開する
// ・呼出音・呼出中の音（リングバック）・保留音・キャッチホンの音・ボタンの音（DTMF）は、ここで作る（音のファイルは使わない）
// ・受けた声と、回線の向こうから聞こえる音（呼出中の音・保留音など）は、電話の帯域に絞って鳴らす（voicefx.js）。電話機のベルはそのまま
// 使い方: const phone = GokkoExtPhone.create({ el, skin: "desk" | "keitai", kind: "station" | "handset", station, token: () => "…",
//          host: 列車無線の音を共有するとき { ctx, out, opus, tap(fn) }（省くと自分でマイクを開く）, onNeedLogin })
//        await phone.start()（ボタンを押したときに呼ぶ。音とマイクを開く）、phone.stop()
(() => {
  const OPUS_URL = "https://cdn.jsdelivr.net/npm/libopus-wasm@0.4.1/dist/index.js";
  const SIGNALR_URL = "https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.7/signalr.min.js";
  const RATE = 16000, FRAME = 640, BITRATE = 24000, MAX_PACKET = 400;
  const GATE = 0.02, GATE_HOLD = 400;
  const DIAL_WAIT = 2500; // 卓上: 番号を押し終えてからかかるまで（ms）
  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const toBase64 = (b) => { let s = ""; for (let i = 0; i < b.length; i++) s += String.fromCharCode(b[i]); return btoa(s); };
  const fromBase64 = (t) => { const s = atob(t); const b = new Uint8Array(s.length); for (let i = 0; i < s.length; i++) b[i] = s.charCodeAt(i); return b; };
  const mmss = (sec) => `${String(Math.floor(sec / 60)).padStart(2, "0")}:${String(sec % 60).padStart(2, "0")}`;

  function loadScript(src) {
    return new Promise((ok, ng) => { const s = document.createElement("script"); s.src = src; s.onload = ok; s.onerror = () => ng(new Error("読み込めません: " + src)); document.head.appendChild(s); });
  }

  // ---- マイクと音（自分で開くとき。列車無線の画面と同じ作り） ----
  const CAPTURE = `
    class Capture extends AudioWorkletProcessor {
      constructor() {
        super();
        this.ratio = sampleRate / ${RATE};
        const w = 2 * Math.PI * Math.min(7000, sampleRate * 0.45) / sampleRate, q = Math.SQRT1_2;
        const a = Math.sin(w) / (2 * q), c = Math.cos(w), a0 = 1 + a;
        this.b0 = (1 - c) / 2 / a0; this.b1 = (1 - c) / a0; this.b2 = this.b0; this.a1 = -2 * c / a0; this.a2 = (1 - a) / a0;
        this.x1 = this.x2 = this.y1 = this.y2 = 0; this.pos = 0; this.prev = 0;
        this.out = new Float32Array(${FRAME}); this.n = 0;
      }
      process(inputs) {
        const ch = inputs[0] && inputs[0][0];
        if (!ch) return true;
        for (let i = 0; i < ch.length; i++) {
          const x = ch[i];
          const y = this.b0 * x + this.b1 * this.x1 + this.b2 * this.x2 - this.a1 * this.y1 - this.a2 * this.y2;
          this.x2 = this.x1; this.x1 = x; this.y2 = this.y1; this.y1 = y;
          while (this.pos <= 1) {
            this.out[this.n++] = this.prev + (y - this.prev) * this.pos;
            this.pos += this.ratio;
            if (this.n === ${FRAME}) { this.port.postMessage(this.out, [this.out.buffer]); this.out = new Float32Array(${FRAME}); this.n = 0; }
          }
          this.pos -= 1; this.prev = y;
        }
        return true;
      }
    }
    registerProcessor("gokko-ext-capture", Capture);`;

  async function openOwnAudio() {
    const ctx = new (window.AudioContext || window.webkitAudioContext)();
    await ctx.resume();
    const out = ctx.createGain(); out.connect(ctx.destination);
    const stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true, channelCount: 1 } });
    const opus = await import(OPUS_URL);
    const url = URL.createObjectURL(new Blob([CAPTURE], { type: "application/javascript" }));
    await ctx.audioWorklet.addModule(url);
    URL.revokeObjectURL(url);
    const node = new AudioWorkletNode(ctx, "gokko-ext-capture", { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [1] });
    const taps = new Set();
    node.port.onmessage = (e) => { for (const f of taps) f(e.data); };
    const sink = ctx.createGain(); sink.gain.value = 0;
    ctx.createMediaStreamSource(stream).connect(node); node.connect(sink); sink.connect(ctx.destination);
    return {
      ctx, out, opus,
      tap(fn) { taps.add(fn); return () => taps.delete(fn); },
      close() {
        try { stream.getTracks().forEach((t) => t.stop()); } catch (e) {}
        try { node.disconnect(); } catch (e) {}
        try { ctx.close(); } catch (e) {}
      }
    };
  }

  // ---- 見た目 ----
  const CSS = `
  .xp { font-family: "BIZ UDGothic", "Meiryo", system-ui, sans-serif; user-select: none; -webkit-user-select: none; touch-action: manipulation; color: #222; }
  .xp button { font: inherit; cursor: pointer; -webkit-tap-highlight-color: transparent; }
  .xp button:disabled { cursor: default; opacity: .45; }
  .xp .err { color: #c0392b; font-size: 12px; min-height: 1.3em; }

  /* 卓上の電話機（駅・司令）。事務所のビジネスホン（縦長・左に受話器）の形 */
  .xp-desk { width: 360px; background: linear-gradient(160deg, #f6f5f1, #e0ded8 65%, #cfccc4); color: #2b2b2b; border-radius: 24px 24px 34px 34px;
             padding: 12px 14px 16px 10px; box-shadow: 0 12px 28px rgba(0,0,0,.55), inset 0 2px 0 #fff, inset 0 -3px 0 rgba(0,0,0,.14);
             display: grid; grid-template-columns: 74px 1fr; gap: 0 12px; }
  .xp-desk .cradle { position: relative; border-radius: 30px; background: linear-gradient(90deg, #bdb9b1, #aeaaa2); box-shadow: inset 0 3px 9px rgba(0,0,0,.38); min-height: 500px; }
  .xp-desk .cradle::before, .xp-desk .cradle::after { content: ""; position: absolute; left: 26px; width: 22px; height: 10px; border-radius: 3px; background: #8f8b84; }
  .xp-desk .cradle::before { top: 40px; } .xp-desk .cradle::after { bottom: 40px; }
  .xp-desk .hs { position: absolute; inset: 3px 2px; z-index: 1; border-radius: 34px; cursor: pointer; transition: transform .15s, box-shadow .15s;
                 background: linear-gradient(90deg, #d4d1ca, #f7f6f3 42%, #cbc8c0); box-shadow: 0 4px 8px rgba(0,0,0,.35), inset 0 0 0 1px #a9a59d;
                 display: flex; flex-direction: column; align-items: center; justify-content: space-between; padding: 16px 0; }
  .xp-desk .hs.off { transform: translate(-10px, -16px) rotate(-7deg); box-shadow: 0 20px 24px rgba(0,0,0,.45), inset 0 0 0 1px #a9a59d; }
  .xp-desk .hs i { width: 46px; height: 46px; border-radius: 50%; background: radial-gradient(circle, #6d6a64 1.6px, transparent 2.2px) 0 0 / 7px 7px, linear-gradient(#e7e5e0, #cfccc5);
                   box-shadow: inset 0 2px 4px rgba(0,0,0,.3); }
  .xp-desk .hs b { font-size: 11px; color: #7b776f; writing-mode: vertical-rl; letter-spacing: .35em; font-weight: normal; }
  .xp-desk .panel { display: flex; flex-direction: column; gap: 7px; min-width: 0; }
  .xp-desk .head { display: flex; align-items: center; gap: 6px; font-size: 9.5px; letter-spacing: .18em; color: #78746c; }
  .xp-desk .head .sp { flex: 1; }
  .xp-desk .lcd { background: linear-gradient(#c9cec4, #b3b9ad); color: #1d221a; border: 3px solid #4a4944; border-radius: 4px; height: 92px; overflow: hidden;
                  box-shadow: inset 0 2px 6px rgba(0,0,0,.32); padding: 3px 7px; font-family: "BIZ UDGothic", "MS Gothic", monospace; font-size: 12px; line-height: 15px; }
  .xp-desk .lcd .ic { display: flex; gap: 7px; height: 15px; font-size: 11px; }
  .xp-desk .lcd .ic .t { margin-left: auto; font-family: Consolas, "Courier New", monospace; font-weight: bold; letter-spacing: .06em; }
  .xp-desk .lcd .num { font-family: Consolas, "Courier New", monospace; font-size: 26px; font-weight: bold; letter-spacing: .14em; height: 30px; line-height: 30px; white-space: nowrap; overflow: hidden; }
  .xp-desk .lcd .tx { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; height: 15px; }
  .xp-desk .lcd .blink { animation: xp-blink 1s steps(2, start) infinite; }
  .xp-desk .ctl { display: flex; align-items: center; gap: 4px; white-space: nowrap; font-size: 9.5px; color: #5d5952; }
  .xp-desk .ctl .sp { flex: 1; }
  .xp-desk .ctl label { display: flex; align-items: center; gap: 2px; white-space: nowrap; }
  .xp-desk .flat { background: linear-gradient(#fbfbf9, #d3d0c9); border: 1px solid #8a867e; border-radius: 4px; height: 15px; padding: 0; box-shadow: 0 2px 0 #8a867e; color: #333; font-size: 9px; line-height: 13px; }
  .xp-desk .flat:active, .xp-desk .pad button:active, .xp-desk .bot button:active { transform: translateY(2px); box-shadow: none; }
  .xp-desk .ctl .flat { width: 34px; }
  .xp-desk .ctl .flat.crew { width: 38px; }
  .xp-desk .ctl .flat.on { background: linear-gradient(#ffe7a8, #e9b949); }
  .xp-desk .memo { display: grid; grid-template-columns: 8px 1fr; gap: 5px; }
  .xp-desk .grille { border-radius: 4px; background: repeating-linear-gradient(#6f6b64 0 2px, transparent 2px 6px); opacity: .55; }
  .xp-desk .ots { display: grid; grid-template-columns: repeat(4, 1fr); gap: 2px 5px; background: #fdfdf9; border: 1px solid #b7b3ab; border-radius: 3px; padding: 3px 4px 5px; }
  .xp-desk .ot { min-width: 0; }
  .xp-desk .ot .nm { display: flex; align-items: center; gap: 2px; height: 14px; font-size: 10px; color: #23324a; }
  .xp-desk .ot .nm span { overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
  .xp-desk .ot .led { width: 6px; height: 6px; }
  .xp-desk .ot .flat { width: 100%; display: block; }
  .xp-desk .fnl, .xp-desk .fn { display: grid; grid-template-columns: repeat(4, 1fr); gap: 0 6px; }
  .xp-desk .fnl span { font-size: 10px; text-align: center; color: #4d4943; letter-spacing: .1em; white-space: nowrap; overflow: hidden; }
  .xp-desk .fn .flat { height: 17px; }
  .xp-desk .pad { display: grid; grid-template-columns: repeat(3, 48px); gap: 6px 14px; justify-content: center; margin-top: 2px; }
  .xp-desk .pad button { height: 32px; background: linear-gradient(#ffffff, #dedbd4); color: #222; border: 1px solid #8a867e; border-radius: 50% / 46%;
                         font-size: 17px; font-weight: bold; box-shadow: 0 2px 0 #8a867e; padding: 0; }
  .xp-desk .bot { display: grid; grid-template-columns: repeat(4, 1fr); gap: 6px; margin-top: 2px; }
  .xp-desk .bot button { height: 22px; background: linear-gradient(#ffffff, #d8d5ce); color: #333; border: 1px solid #8a867e; border-radius: 11px; font-size: 10.5px;
                         box-shadow: 0 2px 0 #8a867e; padding: 0; white-space: nowrap; }
  .xp-desk .bot button.on { background: linear-gradient(#ffe7a8, #e9b949); }
  .xp .led { width: 9px; height: 9px; border-radius: 50%; background: #2a2c2e; border: 1px solid #111; flex: none; }
  .xp-desk .led { background: #8d8a83; border-color: #5f5c56; width: 8px; height: 8px; }
  .xp .led.g { background: #3fd26b; box-shadow: 0 0 6px #3fd26b; }
  .xp .led.r { background: #ff4a3d; box-shadow: 0 0 6px #ff4a3d; }
  .xp .led.a { background: #ffb02e; box-shadow: 0 0 5px #ffb02e; }
  .xp .led.blink { animation: xp-blink .5s steps(2, start) infinite; }
  .xp-desk .err { color: #b03a2e; }
  @keyframes xp-blink { 50% { opacity: .15; } }

  /* 携帯電話（ガラケー。乗務員・司令員のスマホ） */
  .xp-keitai { width: 250px; margin: 0 auto; display: flex; flex-direction: column; align-items: center; }
  .xp-keitai .top { width: 100%; background: linear-gradient(135deg, #3a5a8c, #1f3558 60%, #2b4672); border-radius: 26px 26px 10px 10px; padding: 14px 14px 18px;
                    box-shadow: 0 6px 18px rgba(0,0,0,.5), inset 0 1px 0 rgba(255,255,255,.25); position: relative; }
  .xp-keitai .top::before { content: ""; display: block; width: 46px; height: 5px; border-radius: 3px; background: #0d1626; margin: 0 auto 10px; }
  .xp-keitai .scr { background: linear-gradient(#f7fbff, #dfeaf6); border: 4px solid #0d1626; border-radius: 6px; height: 210px; padding: 4px 6px; color: #10213a;
                    font-size: 13px; display: flex; flex-direction: column; overflow: hidden; }
  .xp-keitai .bar { display: flex; justify-content: space-between; font-size: 10.5px; color: #33507a; border-bottom: 1px solid #b6c8dc; padding-bottom: 2px; }
  .xp-keitai .ant { letter-spacing: -1px; }
  .xp-keitai .main { flex: 1; display: flex; flex-direction: column; justify-content: center; text-align: center; gap: 2px; }
  .xp-keitai .main .big { font-size: 19px; font-weight: bold; }
  .xp-keitai .main .peer { font-size: 15px; }
  .xp-keitai .main .num { font-size: 26px; font-weight: bold; letter-spacing: .08em; min-height: 1.2em; }
  .xp-keitai .main .blink { animation: xp-blink 1s steps(2, start) infinite; color: #c0392b; }
  .xp-keitai .list { flex: 1; overflow: hidden; font-size: 12.5px; text-align: left; }
  .xp-keitai .list div { padding: 1px 4px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .xp-keitai .list .sel { background: #2f6fd0; color: #fff; border-radius: 3px; }
  .xp-keitai .list .off { color: #8a9bb2; }
  .xp-keitai .soft { display: flex; justify-content: space-between; font-size: 11px; color: #fff; background: #33507a; margin: 0 -6px -4px; padding: 2px 6px; }
  .xp-keitai .hinge { width: 88%; height: 14px; background: linear-gradient(#0d1626, #3a5a8c 50%, #0d1626); border-radius: 7px; margin: -2px 0; position: relative; z-index: 1; }
  .xp-keitai .bot { width: 100%; background: linear-gradient(135deg, #3a5a8c, #1f3558 60%, #2b4672); border-radius: 10px 10px 30px 30px; padding: 14px 14px 22px;
                    box-shadow: 0 6px 18px rgba(0,0,0,.5); display: flex; flex-direction: column; gap: 8px; }
  .xp-keitai .nav { display: grid; grid-template-columns: 1fr 74px 1fr; gap: 6px; align-items: center; }
  .xp-keitai .nav .col { display: flex; flex-direction: column; gap: 6px; }
  .xp-keitai .k { background: linear-gradient(#e9eef5, #b9c6d6); color: #10213a; border: 0; border-radius: 12px; padding: 7px 0; font-size: 12px; font-weight: bold;
                  box-shadow: 0 2px 0 #0d1626; }
  .xp-keitai .k:active, .xp-keitai .ring button:active { transform: translateY(2px); box-shadow: none; }
  .xp-keitai .k.call { color: #1d8a3e; font-size: 13px; }
  .xp-keitai .k.end { color: #c0392b; font-size: 13px; }
  .xp-keitai .ring { position: relative; width: 74px; height: 74px; border-radius: 50%; background: linear-gradient(#e9eef5, #9fb0c6); box-shadow: 0 2px 0 #0d1626; }
  .xp-keitai .ring button { position: absolute; border: 0; background: transparent; color: #10213a; font-size: 12px; font-weight: bold; padding: 0; line-height: 1; white-space: nowrap; }
  .xp-keitai .ring .u { left: 24px; top: 3px; width: 26px; height: 16px; }
  .xp-keitai .ring .d { left: 24px; bottom: 3px; width: 26px; height: 16px; }
  .xp-keitai .ring .c { left: 17px; top: 19px; width: 40px; height: 36px; border-radius: 50%; background: linear-gradient(#ffffff, #c9d5e4); box-shadow: 0 1px 0 #0d1626; font-size: 11px; }
  .xp-keitai .pad { display: grid; grid-template-columns: repeat(3, 1fr); gap: 7px; }
  .xp-keitai .pad .k { font-size: 17px; padding: 6px 0; border-radius: 14px / 12px; }
  .xp-keitai .pad .k small { display: block; font-size: 8px; font-weight: normal; color: #4a5f7c; }
  .xp-keitai .opts { display: flex; align-items: center; gap: 8px; color: #dfe8f3; font-size: 12px; }
  .xp-keitai .opts input { flex: 1; }
  .xp-keitai .err { color: #ffb4a8; text-align: center; }
  `;
  let cssAdded = false;
  function addCss() {
    if (cssAdded) return;
    cssAdded = true;
    const s = document.createElement("style"); s.textContent = CSS; document.head.appendChild(s);
  }

  const PAD = [["1", ""], ["2", "ABC"], ["3", "DEF"], ["4", "GHI"], ["5", "JKL"], ["6", "MNO"], ["7", "PQRS"], ["8", "TUV"], ["9", "WXYZ"], ["*", ""], ["0", ""], ["#", ""]];
  const DTMF = { "1": [697, 1209], "2": [697, 1336], "3": [697, 1477], "4": [770, 1209], "5": [770, 1336], "6": [770, 1477], "7": [852, 1209], "8": [852, 1336],
    "9": [852, 1477], "*": [941, 1209], "0": [941, 1336], "#": [941, 1477] };

  function create(opts) {
    addCss();
    const el = opts.el, skin = opts.skin || "desk";
    let audio = null, ownAudio = false, encoder = null, untap = null, line = null, lineOut = null; // line: 電話の帯域にする（受けた声・回線の音）
    let conn = null, closed = true, state = null, joinedName = "";
    let buffer = "", message = "", messageUntil = 0, dirIndex = -1, showDir = false;
    let muted = false, gateUntil = 0, offHook = false;
    // 卓上の電話機だけ: ワンタッチの上下（0 = 上、1 = 下）、［転送］を押して転送先を待っているか、番号を押し終わるのを待つタイマー
    let onePage = 0, transferArmed = false, dialTimer = null;
    let volume = 1, lastNumber = "";
    try { volume = Number(localStorage.getItem("gokkoExtVolume") || "1"); lastNumber = localStorage.getItem("gokkoExtRedial") || ""; } catch (e) {}
    if (!(volume >= 0 && volume <= 2)) volume = 1;
    if (!/^[0-9*#]{1,12}$/.test(lastNumber)) lastNumber = "";
    const voices = new Map();
    const tones = { ring: { next: 0, nodes: [] }, back: { next: 0, nodes: [] }, hold: { next: 0, nodes: [] }, wait: { next: 0, nodes: [] }, dial: { next: 0, nodes: [] } };
    let lastSig = "", timer = null, sliding = false;
    window.addEventListener("pointerup", () => { if (sliding) { sliding = false; render(); } });

    // ---- 状態の読み方 ----
    const calls = () => (state && state.calls) || [];
    const active = () => calls().find((c) => c.status === "talking" || c.status === "outgoing") || null;
    const incoming = () => calls().find((c) => c.status === "incoming") || null;
    const held = () => calls().filter((c) => c.status === "held");
    const directory = () => (state && state.directory) || [];
    const crew = () => (state && state.crew) || [];
    // 一覧で選んでかける相手: 卓上は乗務員だけ（司令・駅はワンタッチで固定）、携帯は司令・駅・乗務員をみな
    const book = () => (skin !== "keitai" ? crew() : directory().concat(crew()));
    const isCrew = (d) => d.kind === "driver" || d.kind === "conductor";
    const usable = () => !!state && state.kind && state.kind !== "replaced" && state.canUse;

    function say(text, ms = 3500) { message = text || ""; messageUntil = performance.now() + ms; render(); }

    async function invoke(name, ...args) {
      if (!conn || conn.state !== signalR.HubConnectionState.Connected) { say("つながっていません"); return null; }
      try {
        const r = await conn.invoke(name, ...args);
        if (r && !r.ok) say(r.message);
        return r;
      } catch (e) { say("送れませんでした: " + e.message); return null; }
    }

    // ---- 操作 ----
    // 卓上: 受話器を上げて（スピーカでも）番号を押すと、押し終えて少し待つか # でかかる（転送先も同じ）
    const desk = skin !== "keitai";
    const dialing = () => desk && offHook && !active() && !incoming();
    function fire() {
      clearTimeout(dialTimer); dialTimer = null;
      if (!buffer) return;
      if (transferArmed) transfer(); else if (dialing()) call();
    }
    function press(d) {
      tone(DTMF[d], 0.09, 0.08);
      const waiting = dialing() || transferArmed;
      if (waiting && d === "#" && buffer) { fire(); return; }
      if (buffer.length < 12) buffer += d;
      showDir = false;
      if (waiting) { clearTimeout(dialTimer); dialTimer = setTimeout(fire, DIAL_WAIT); }
      render();
    }
    async function call() {
      const inc = incoming(), act = active();
      if (buffer) {
        const n = buffer; buffer = "";
        clearTimeout(dialTimer); dialTimer = null;
        const r = await invoke("ExtDial", n);
        if (r && r.ok) {
          lastNumber = n;
          try { localStorage.setItem("gokkoExtRedial", n); } catch (e) {}
          say(r.message, 2000);
        }
        return;
      }
      if (showDir && dirIndex >= 0 && book()[dirIndex]) {
        buffer = book()[dirIndex].number; showDir = false;
        if (transferArmed) return transfer();
        if (desk) offHook = true;
        return call();
      }
      if (inc) { await invoke("ExtAnswer", inc.id); return; }
      if (!act && held().length) { await invoke("ExtHold", held()[0].id, false); return; }
      say(skin === "keitai" ? "番号を押すか、電話帳から選んでください" : "番号を押すか、ワンタッチ・［乗務員］の一覧から選んでください");
    }
    async function end() {
      const act = active();
      if (buffer) { buffer = ""; clearTimeout(dialTimer); dialTimer = null; render(); return; }
      if (transferArmed) { transferArmed = false; render(); return; }
      if (showDir) { showDir = false; render(); return; }
      if (act) { await invoke("ExtHangup", act.id); return; }
      if (held().length) { await invoke("ExtHangup", held()[0].id); return; }
    }
    async function hold() {
      const act = active();
      if (act && act.status === "talking") { await invoke("ExtHold", act.id, true); return; }
      if (held().length) { await invoke("ExtHold", held()[0].id, false); return; }
      say("保留にできる通話がありません");
    }
    async function transfer() {
      const act = active();
      if (!act || act.status !== "talking") { transferArmed = false; say(desk ? "通話中に［転送］を押して、転送先の番号を押します" : "通話中に、番号を押してから「転送」を押します"); return; }
      if (act.isPage) { say("一斉呼出は転送できません"); return; }
      if (!buffer) {
        // 卓上: ［転送］→ 転送先の番号（もう一度［転送］で元の通話に戻る）
        if (desk) { transferArmed = !transferArmed; say(transferArmed ? "転送先の番号を押してください" : "", transferArmed ? 15000 : 0); return; }
        say("転送先の番号を押してから「転送」を押します"); return;
      }
      const n = buffer; buffer = ""; transferArmed = false;
      clearTimeout(dialTimer); dialTimer = null;
      const r = await invoke("ExtTransfer", act.id, n);
      if (r && r.ok) say(r.message);
    }
    async function page() {
      const act = active();
      if (act && act.isPage) { await invoke("ExtHangup", act.id); return; }
      const r = await invoke("ExtPage");
      if (r && r.ok) say(r.message);
    }
    async function oneTouch(number) {
      buffer = number;
      if (transferArmed) { await transfer(); return; }
      if (desk) offHook = true;
      await call();
    }
    async function redial() {
      if (!lastNumber) { say("再呼できる番号がありません"); return; }
      if (active()) { say("通話中は再呼できません"); return; }
      buffer = lastNumber; offHook = true;
      await call();
    }
    function hook() {
      // 受話器（スピーカ）を上げる: 着信に出る・押してあった番号にかける・保留中の通話に戻る。何もなければ発信音
      // 置く: 話している通話を切る。押しかけの番号は消す
      const act = active(), inc = incoming();
      transferArmed = false;
      if (act) { end(); offHook = false; render(); return; }
      if (inc) { offHook = true; call(); return; }
      if (offHook) { offHook = false; buffer = ""; clearTimeout(dialTimer); dialTimer = null; render(); return; }
      offHook = true;
      if (buffer || held().length) call(); else render();
    }
    function setVolume(v) {
      volume = Math.round(Math.min(2, Math.max(0, v)) * 100) / 100;
      if (lineOut) lineOut.gain.value = volume;
      try { localStorage.setItem("gokkoExtVolume", String(volume)); } catch (e) {}
      say(`受話音量 ${Math.round(volume * 100)}%`, 1500);
    }
    function moveDir(step) {
      const list = book();
      if (!list.length) { say(desk ? "いま参加している乗務員はいません" : "電話帳は空です"); return; }
      showDir = true;
      dirIndex = dirIndex < 0 ? 0 : (dirIndex + step + list.length) % list.length;
      render();
    }

    // ---- 音 ----
    function ctxOk() { return audio && audio.ctx && audio.ctx.state !== "closed"; }
    // viaLine: 回線の向こうから聞こえる音（受話器から出る音）。電話の帯域に絞る
    function tone(freqs, seconds, gain, at, list, viaLine) {
      if (!ctxOk() || !freqs) return;
      const ctx = audio.ctx, t = at ?? ctx.currentTime + 0.01;
      const g = ctx.createGain();
      g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(gain, t + 0.008);
      g.gain.setValueAtTime(gain, t + seconds - 0.012); g.gain.linearRampToValueAtTime(0, t + seconds);
      g.connect(viaLine && line ? line.input : audio.out);
      for (const f of freqs) {
        const o = ctx.createOscillator(); o.frequency.value = f; o.type = "sine"; o.connect(g); o.start(t); o.stop(t + seconds + 0.02);
        if (list) list.push(o);
      }
    }
    // くり返す音を、先の分まで少しずつ並べる
    function loop(name, on, period, schedule) {
      const t = tones[name];
      if (!on || !ctxOk()) {
        if (t.next) { for (const o of t.nodes) { try { o.stop(); } catch (e) {} } t.nodes = []; t.next = 0; }
        return;
      }
      const now = audio.ctx.currentTime;
      if (t.next < now) t.next = now + 0.05;
      t.nodes = t.nodes.filter((o) => o.__end > now);
      while (t.next < now + 0.5) {
        const list = [];
        schedule(t.next, list);
        for (const o of list) { o.__end = t.next + period; t.nodes.push(o); }
        t.next += period;
      }
    }
    function ringDesk(at, list) { for (let i = 0; i < 16; i++) tone([i % 2 ? 1300 : 1600], 0.0625, 0.16, at + i * 0.0625, list); } // 1秒鳴って2秒休む
    function ringKeitai(at, list) {
      const notes = [1318.5, 1568, 2093, 1568, 1318.5, 1568, 2093, 2637];
      notes.forEach((f, i) => tone([f], 0.11, 0.15, at + i * 0.13, list));
    }
    function ringBack(at, list) { tone([392, 408], 1.0, 0.07, at, list, true); } // 400Hz を 16Hz でゆらした呼出音（1秒鳴って2秒休む）
    function holdMusic(at, list) {
      // 保留音（短い旋律のくり返し。オリジナル）
      const m = [[523.3, .3], [659.3, .3], [784, .3], [659.3, .3], [698.5, .3], [880, .3], [784, .6], [587.3, .3], [698.5, .3], [880, .3], [784, .3], [659.3, .6]];
      let t = at; for (const [f, d] of m) { tone([f, f * 2], d * 0.9, 0.045, t, list, true); t += d; }
    }
    function waitBeep(at, list) { tone([440], 0.12, 0.08, at, list, true); tone([440], 0.12, 0.08, at + 0.25, list, true); }

    function updateSounds() {
      const act = active(), inc = incoming();
      loop("ring", !!inc && !act && usable(), 3, skin === "keitai" ? ringKeitai : ringDesk);
      loop("wait", !!inc && !!act, 4, waitBeep);
      loop("back", !!act && act.status === "outgoing", 3, ringBack);
      loop("hold", !!act && act.status === "talking" && act.peerHeld, 4.2, holdMusic);
      // 内線発信音（ツーツーツー）: 卓上で受話器を上げて、まだ番号を押していないとき
      loop("dial", dialing() && !buffer && usable(), 0.5, (at, list) => tone([400], 0.25, 0.04, at, list, true));
      if (line) line.setLine(!!act && act.status === "talking" && !act.peerHeld); // 話している間だけ回線の「サー」
    }

    // ---- 音声 ----
    function onFrame(pcm) {
      let peak = 0;
      for (let i = 0; i < pcm.length; i++) peak = Math.max(peak, Math.abs(pcm[i]));
      const now = performance.now();
      if (peak >= GATE) gateUntil = now + GATE_HOLD;
      const act = active();
      if (!act || act.status !== "talking" || muted || now >= gateUntil || !encoder || !conn || conn.state !== signalR.HubConnectionState.Connected) return;
      let packet;
      try { packet = encoder.encodeFloat(pcm, { maxPacketBytes: MAX_PACKET }); } catch (e) { return; }
      if (!packet || packet.length === 0) return;
      conn.send("ExtSendAudio", { sessionId: act.id, senderId: "", opus: toBase64(packet) }).catch(() => {});
    }

    function receive(packet) {
      if (!state || !ctxOk() || !packet || !packet.opus || packet.senderId === state.selfId) return;
      const call = calls().find((c) => c.id === packet.sessionId && c.status === "talking");
      if (!call || !call.parties.some((p) => p.id === packet.senderId)) return;
      const bytes = fromBase64(packet.opus);
      if (bytes.length === 0 || bytes.length > MAX_PACKET) return;
      let v = voices.get(packet.senderId);
      if (!v) {
        v = { decoder: null, waiting: [bytes], next: 0, last: performance.now() };
        voices.set(packet.senderId, v);
        audio.opus.createDecoder({ sampleRate: RATE, channels: 1 }).then((d) => { v.decoder = d; for (const b of v.waiting.splice(0)) play(v, b); }).catch(() => voices.delete(packet.senderId));
        return;
      }
      if (!v.decoder) { if (v.waiting.length < 10) v.waiting.push(bytes); return; }
      play(v, bytes);
    }

    function play(v, bytes) {
      if (!ctxOk() || !line) return;
      let pcm;
      try { pcm = v.decoder.decodeFloat(bytes); } catch (e) { return; }
      if (!pcm || !pcm.length) return;
      const ctx = audio.ctx, buf = ctx.createBuffer(1, pcm.length, RATE);
      buf.getChannelData(0).set(pcm);
      const src = ctx.createBufferSource(); src.buffer = buf; src.connect(line.input);
      const t = ctx.currentTime;
      if (v.next < t + 0.02 || v.next > t + 0.6) v.next = t + 0.12;
      src.start(v.next); v.next += buf.duration; v.last = performance.now();
    }

    // ---- 接続 ----
    async function connect() {
      if (!window.signalR) await loadScript(SIGNALR_URL);
      conn = new signalR.HubConnectionBuilder().withUrl("/extension")
        .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (c) => closed ? null : Math.min(10000, [0, 1000, 2000, 5000][c.previousRetryCount] ?? 10000) })
        .build();
      conn.on("ReceiveExtensionState", (s) => { state = s; onState(); });
      conn.on("ReceiveExtensionAudio", receive);
      conn.onreconnecting(() => say("つなぎ直しています…", 60000));
      conn.onreconnected(() => { say(""); join(); });
      conn.onclose(() => { if (!closed) { state = null; say("切れました。電話機を開き直してください", 600000); } });
      await conn.start();
      await join();
    }

    async function join() {
      const token = opts.token ? opts.token() : "";
      if (!token) { state = null; say("ログインしてください", 600000); opts.onNeedLogin && opts.onNeedLogin(); return false; }
      const r = await conn.invoke("ExtJoin", token, opts.kind, opts.station || "");
      if (!r.ok) { state = null; say(r.message, 600000); if (/ログイン/.test(r.message)) opts.onNeedLogin && opts.onNeedLogin(); return false; }
      joinedName = r.message || "";
      say("");
      return true;
    }

    let lastIncoming = null, lastActive = null;
    function onState() {
      const inc = incoming(), act = active();
      if (lastActive && !act && !held().some((c) => c.id === lastActive)) { tone([480, 620], 0.25, 0.08, undefined, null, true); offHook = false; } // 切れた
      lastIncoming = inc ? inc.id : null;
      lastActive = act ? act.id : null;
      if (act) offHook = true;
      if (!act || act.status !== "talking") transferArmed = false;
      if (state && state.kind === "replaced") { closed = true; conn && conn.stop().catch(() => {}); }
      updateSounds();
      render();
    }

    // ---- 描き方 ----
    function statusLines() {
      const act = active(), inc = incoming(), hs = held();
      const now = performance.now();
      const note = now < messageUntil ? message : "";
      if (!state || !state.kind) return { big: state ? "使えません" : "未接続", sub: (state && state.reason) || note || (closed ? "電話機を開いてください" : "つないでいます…"), note: "" };
      if (state.kind === "replaced") return { big: "切れました", sub: state.reason, note: "" };
      let big = "待受", sub = !state.canUse ? state.reason : !desk ? "番号を押してください" : offHook ? "番号を押してください（# ですぐかかる）" : "受話器を上げて番号を押してください", blink = false;
      if (act && act.status === "outgoing") { big = act.isPage ? "一斉呼出中" : "呼出中"; sub = act.peer + (act.peerNumber ? `（${act.peerNumber}）` : ""); }
      else if (act) { big = (act.isPage ? "一斉 " : "通話中 ") + mmss(act.seconds); sub = act.peer + (act.peerHeld ? " 〔相手が保留中〕" : ""); }
      else if (inc) { big = inc.isPage ? "一斉呼出 着信" : "着信"; sub = inc.peer + (inc.peerNumber ? `（${inc.peerNumber}）` : "") + (inc.transferredBy ? ` ← ${inc.transferredBy}から転送` : ""); blink = true; }
      else if (hs.length) { big = "保留中"; sub = hs[0].peer; }
      const extra = [];
      if (inc && act) extra.push(`着信あり: ${inc.peer}`);
      if (hs.length && (act || inc)) extra.push(`保留 ${hs.length}件`);
      if (buffer) extra.push(`番号 ${buffer}`);
      return { big, sub, blink, note: note || extra.join("　") };
    }

    function signature() {
      return JSON.stringify([state, buffer, message && performance.now() < messageUntil ? message : "", showDir, dirIndex, muted, offHook, onePage, transferArmed, volume, calls().length ? Math.floor(performance.now() / 1000) : new Date().getMinutes()]);
    }

    function render() {
      if (sliding) return;
      const sig = signature();
      if (sig === lastSig) return;
      lastSig = sig;
      if (skin === "keitai") renderKeitai(); else renderDesk();
    }

    function renderDesk() {
      const s = statusLines(), act = active(), inc = incoming(), hs = held();
      const me = state && state.kind ? `${state.label}　内線 ${state.number}` : "内線電話";
      // ［乗務員］の一覧を開いているとき: 液晶に選んでいる人を出す（▲▼で選び、［応答］でかける・［終話］で閉じる）
      if (showDir && dirIndex >= book().length) dirIndex = book().length - 1;
      const pick = showDir ? book()[dirIndex] : null;
      if (showDir) {
        const n = book().length;
        s.big = pick ? `乗務員 ${dirIndex + 1}/${n}` : "乗務員 0人";
        s.sub = pick ? pick.label : "いま参加している乗務員はいません";
        s.blink = false;
        s.note = transferArmed ? "▲▼で選び［応答］で転送" : "▲▼で選び［応答］でかける";
      }
      const talking = act && act.status === "talking";
      const time = talking ? mmss(act.seconds) : new Date().toLocaleTimeString("ja-JP", { hour: "2-digit", minute: "2-digit" });
      const cur = act || inc || hs[0];
      const num = pick ? pick.number : buffer || (cur && cur.peerNumber) || "";
      // 液晶の上の段の印: ◁ = 受話器を上げている（スピーカ）、♫ = 保留中、H = 転送の番号待ち、大 = 受話音量を上げている
      const icons = [offHook && !act ? "◁" : "", hs.length ? "♫保留" : "", transferArmed ? "H 転送" : "", volume > 1.01 ? "大" : "", muted ? "消音" : ""].filter(Boolean);
      const talkLed = act ? "led g" : hs.length ? "led g blink" : "led";
      const ringingNumbers = new Set(calls().filter((c) => c.status === "incoming").map((c) => c.peerNumber));
      const talkingNumbers = new Set(calls().filter((c) => c.status !== "incoming").map((c) => c.peerNumber));
      const list = directory(), cells = [];
      for (let i = 0; i < 16; i++) {
        const d = list[onePage * 16 + i];
        if (!d) { cells.push(`<div class="ot"><div class="nm"><i class="led"></i><span></span></div><button class="flat" disabled></button></div>`); continue; }
        const led = ringingNumbers.has(d.number) ? "led r blink" : talkingNumbers.has(d.number) ? "led r" : d.present ? "led g" : "led";
        cells.push(`<div class="ot" title="${esc(d.label)}（${esc(d.number)}）${d.present ? "" : " — 出られる人がいません"}"><div class="nm"><i class="${led}"></i><span>${esc(d.label)}</span></div>`
          + `<button class="flat" data-one="${esc(d.number)}" ${d.present ? "" : "disabled"}></button></div>`);
      }
      const isDispatcher = state && state.kind === "dispatcher";
      el.innerHTML = `<div class="xp xp-desk">
        <div class="cradle"><div class="hs ${offHook || act ? "off" : ""}" data-act="hook" title="受話器（押すと上げる・置く）"><i></i><b></b><i></i></div></div>
        <div class="panel">
          <div class="head"><span>GOKKO</span><span class="sp"></span><span>着信</span><i class="${inc ? "led r blink" : "led"}"></i></div>
          <div class="lcd">
            <div class="ic">${icons.map((t) => `<span>${esc(t)}</span>`).join("")}<span class="t">${esc(time)}</span></div>
            <div class="num">${esc(num)}</div>
            <div class="tx ${s.blink ? "blink" : ""}">${esc(s.big)}${s.sub ? "　" + esc(s.sub) : ""}</div>
            <div class="tx">${esc(s.note || me)}</div>
            <div class="tx">${s.note ? esc(me) : ""}</div>
          </div>
          <div class="ctl">
            <label title="ワンタッチの上の段">上<i class="${onePage === 0 ? "led a" : "led"}"></i></label>
            <label title="ワンタッチの下の段">下<i class="${onePage === 1 ? "led a" : "led"}"></i></label>
            <button class="flat" data-act="page2" title="ワンタッチの上・下を切り替える">上下</button>
            <button class="flat crew ${showDir ? "on" : ""}" data-act="crewlist" title="乗務員（運転士・車掌）の一覧。列番と社員番号。▲▼で選び［応答］でかける">乗務員</button>
            <label title="通話ランプ（点滅 = 保留中）" style="margin-left:6px">通話<i class="${talkLed}"></i></label>
            <span class="sp"></span><span>音量</span>
            <button class="flat" data-act="voldown" title="受話音量を下げる（乗務員の一覧では次の人）">▼</button>
            <button class="flat" data-act="volup" title="受話音量を上げる（乗務員の一覧では前の人）">▲</button>
          </div>
          <div class="memo"><div class="grille"></div><div class="ots" title="ワンタッチ（押すとかける）。緑: 出られる人がいる、赤の点滅: 着信中">${cells.join("")}</div></div>
          <div class="fnl"><span>${isDispatcher ? (act && act.isPage ? "一斉終了" : "一斉呼出") : ""}</span><span>応答</span><span>${buffer ? "消去" : "終話"}</span><span>${muted ? "マイク切" : "マイク"}</span></div>
          <div class="fn">
            <button class="flat" data-act="page" ${isDispatcher ? "" : "disabled"} title="全駅を一斉に呼び出す（司令だけ）"></button>
            <button class="flat" data-act="call" title="着信に出る・押した番号にかける"></button>
            <button class="flat" data-act="end" title="通話を切る・押した番号を消す"></button>
            <button class="flat" data-act="mute" title="こちらの声を送らない"></button>
          </div>
          <div class="pad">${PAD.map(([d]) => `<button data-key="${d}">${d}</button>`).join("")}</div>
          <div class="bot">
            <button data-act="hook" class="${offHook && !act ? "on" : ""}" title="受話器を取らずに話す・かける（スピーカ）">◁ スピーカ</button>
            <button data-act="redial" title="最後にかけた番号にかけ直す">再呼</button>
            <button data-act="transfer" class="${transferArmed ? "on" : ""}" title="通話中に押して、転送先の番号を押す（もう一度押すと元の通話に戻る）">転送</button>
            <button data-act="hold" class="${hs.length && !talking ? "on" : ""}" title="通話中: 相手を待たせる。保留中: 元の通話に戻る">♫ 保留</button>
          </div>
        </div>
      </div>`;
      wire();
    }

    function renderKeitai() {
      const s = statusLines(), act = active(), inc = incoming();
      const clock = new Date().toLocaleTimeString("ja-JP", { hour: "2-digit", minute: "2-digit" });
      const ant = state && state.kind && state.kind !== "replaced" ? "▂▄▆█" : "圏外";
      let body;
      if (showDir) {
        const list = book();
        const start = Math.max(0, Math.min(dirIndex - 4, list.length - 9));
        body = `<div class="list">${list.slice(start, start + 9).map((d, i) =>
          `<div class="${start + i === dirIndex ? "sel" : ""} ${d.present ? "" : "off"}">${isCrew(d) ? `${esc(d.label)}　${esc(d.number)}` : `${esc(d.number)}　${esc(d.label)}`}</div>`).join("") || "<div>電話帳は空です</div>"}</div>`;
      } else {
        body = `<div class="main">
          <div class="big ${s.blink ? "blink" : ""}">${esc(s.big)}</div>
          <div class="peer">${esc(s.sub)}</div>
          <div class="num">${esc(buffer)}</div>
          <div style="font-size:11px;color:#33507a">${esc(s.note)}</div>
        </div>`;
      }
      const leftSoft = showDir ? "戻る" : act && act.status === "talking" ? "転送" : "電話帳";
      const rightSoft = act && act.status === "talking" ? "保留" : held().length ? "保留解除" : muted ? "マイク入" : "マイク切";
      el.innerHTML = `<div class="xp xp-keitai">
        <div class="top"><div class="scr">
          <div class="bar"><span class="ant">${ant}</span><span>${state && state.kind ? esc(state.label) : "内線"}</span><span>${clock}</span></div>
          ${body}
          <div class="soft"><span>${leftSoft}</span><span>${showDir ? "発信" : inc && !act ? "応答" : "メニュー"}</span><span>${rightSoft}</span></div>
        </div></div>
        <div class="hinge"></div>
        <div class="bot">
          <div class="nav">
            <div class="col"><button class="k" data-act="lsoft">${leftSoft}</button><button class="k call" data-act="call">📞 通話</button></div>
            <div class="ring"><button class="u" data-act="up">▲</button><button class="c" data-act="center">決定</button><button class="d" data-act="down">▼</button></div>
            <div class="col"><button class="k" data-act="rsoft">${rightSoft}</button><button class="k end" data-act="end">終話</button></div>
          </div>
          <div class="pad">${PAD.map(([d, l]) => `<button class="k" data-key="${d}">${d}<small>${l || "&nbsp;"}</small></button>`).join("")}</div>
          <div class="opts"><span>音量</span><input type="range" min="0" max="2" step="0.05" value="${volume}" data-vol>${muted ? "<b>消音中</b>" : ""}</div>
        </div>
      </div>`;
      wire();
    }

    function wire() {
      // 通話中は毎秒描き直すので、押した瞬間（pointerdown）に動かす（click だと、押している間に描き直すと取りこぼす）
      const on = (b, fn) => { b.onpointerdown = (e) => { if (e.button > 0 || b.disabled) return; e.preventDefault(); fn(); }; };
      el.querySelectorAll("[data-key]").forEach((b) => on(b, () => press(b.dataset.key)));
      el.querySelectorAll("[data-one]").forEach((b) => on(b, () => oneTouch(b.dataset.one)));
      const acts = {
        call, end, hold, transfer, page, hook, redial,
        page2: () => { onePage = onePage ? 0 : 1; render(); },
        volup: () => (desk && showDir ? moveDir(-1) : setVolume(volume + 0.25)),
        voldown: () => (desk && showDir ? moveDir(1) : setVolume(volume - 0.25)),
        crewlist: () => { if (showDir) { showDir = false; render(); } else { buffer = ""; dirIndex = -1; moveDir(0); } },
        mute: () => { muted = !muted; render(); },
        up: () => moveDir(-1), down: () => moveDir(1),
        center: () => (showDir ? call() : incoming() && !active() ? call() : moveDir(0)),
        lsoft: () => { if (showDir) { showDir = false; render(); } else if (active() && active().status === "talking") transfer(); else moveDir(0); },
        rsoft: () => { const a = active(); if ((a && a.status === "talking") || held().length) hold(); else { muted = !muted; render(); } }
      };
      el.querySelectorAll("[data-act]").forEach((b) => on(b, () => acts[b.dataset.act] && acts[b.dataset.act]()));
      const v = el.querySelector("[data-vol]");
      if (v) v.onpointerdown = () => { sliding = true; };
      if (v) v.oninput = () => { volume = Number(v.value); try { localStorage.setItem("gokkoExtVolume", String(volume)); } catch (e) {} };
    }

    function onKey(e) {
      if (closed || /INPUT|TEXTAREA|SELECT/.test((e.target && e.target.tagName) || "")) return;
      if (opts.keyboard === false) return;
      if (DTMF[e.key]) { press(e.key); e.preventDefault(); }
      else if (e.key === "Backspace" && buffer) { buffer = buffer.slice(0, -1); render(); e.preventDefault(); }
    }

    // ---- 開く・閉じる ----
    async function start() {
      if (!closed) return;
      closed = false;
      say("つないでいます…", 60000);
      try {
        if (!window.isSecureContext) throw new Error("https:// で開いてください（マイクは https でないと使えません）");
        if (opts.host) audio = await opts.host();
        else { audio = await openOwnAudio(); ownAudio = true; }
        if (!window.GokkoVoiceFx) await loadScript("/voicefx.js");
        line = GokkoVoiceFx.phone(audio.ctx);
        lineOut = audio.ctx.createGain(); lineOut.gain.value = volume;
        line.output.connect(lineOut); lineOut.connect(audio.out);
        encoder = await audio.opus.createEncoder({ sampleRate: RATE, channels: 1, frameSize: FRAME, application: audio.opus.Application.Voip,
          signal: audio.opus.Signal.Voice, bitrate: BITRATE, vbr: true, complexity: 5 });
        untap = audio.tap(onFrame);
        await connect();
      } catch (e) {
        stop();
        say((e && e.name === "NotAllowedError") ? "マイクの使用が許可されませんでした" : "開けませんでした: " + (e && e.message || e), 600000);
        throw e;
      }
      timer = setInterval(() => {
        updateSounds(); render();
        const now = performance.now();
        for (const [id, v] of voices) if (v.decoder && now - v.last > 30000) { try { v.decoder.free(); } catch (e) {} voices.delete(id); }
      }, 200);
      document.addEventListener("keydown", onKey);
    }

    function stop() {
      closed = true;
      try { conn && conn.stop(); } catch (e) {}
      conn = null; state = null;
      for (const k of Object.keys(tones)) loop(k, false);
      if (untap) { untap(); untap = null; }
      try { encoder && encoder.free(); } catch (e) {}
      encoder = null;
      for (const v of voices.values()) { try { v.decoder && v.decoder.free(); } catch (e) {} }
      voices.clear();
      if (line) { line.close(); line = null; }
      if (lineOut) { try { lineOut.disconnect(); } catch (e) {} lineOut = null; }
      if (ownAudio && audio) audio.close();
      audio = null; ownAudio = false;
      clearInterval(timer); timer = null;
      document.removeEventListener("keydown", onKey);
      render();
    }

    // ログインし直したとき（盤の右上のログイン）に参加し直す
    async function rejoin() { if (!closed && conn && conn.state === signalR.HubConnectionState.Connected) await join(); }

    render();
    return { start, stop, rejoin, get running() { return !closed; }, get ringing() { return !!incoming(); }, resume: () => audio && audio.ctx.resume() };
  }

  window.GokkoExtPhone = { create };
})();
