// 信号制御盤（relaypanel.html）・電子連動装置（denshirendo.html）の内線電話機。右上の「電話」で出し入れする。盤の上のどこにでも置ける（上の帯を引っぱる）。
// ・駅は盤と同じ（?st=…。付けなければ TH71）。ログインは盤の右上と同じ（ログインした人なら誰でも出られる）
// ・一度開くと、しまっても電話機はつながったまま（着信があると「電話」のボタンが点滅し、電話機が出てくる）
// ・大きさは上の帯の ⊖ ⊕ か、右下の角を引っぱって変える（覚えておく。初めは画面の高さに収まる大きさ）
(() => {
  const params = new URLSearchParams(location.search);
  const ST = (window.GokkoPhoneStation || params.get("st") || "TH71").replace(/[^A-Za-z0-9]/g, "");   // 電子連動装置の画面は GokkoPhoneStation で駅を決める
  const button = document.getElementById("phoneBtn");
  if (!button || !window.GokkoExtPhone) return;

  const crewToken = () => GokkoCrew.token(); // 盤と同じ、このタブのログイン（crewsession.js）

  const style = document.createElement("style");
  style.textContent = `
    #extPhone { position: fixed; z-index: 45; display: none; filter: drop-shadow(0 8px 20px rgba(0,0,0,.55)); }
    #extPhone.open { display: block; }
    #extPhone .grab { display: flex; align-items: center; gap: 6px; background: #2a3330; color: #e6ece8; font-size: 12px; padding: 3px 6px 3px 10px;
                      border-radius: 8px 8px 0 0; cursor: move; touch-action: none; }
    #extPhone .grab span { flex: 1; }
    #extPhone .grab button { background: none; border: none; color: #e6ece8; font-size: 15px; cursor: pointer; padding: 0 4px; }
    #extPhone .grab .z { font-size: 14px; min-width: 22px; }
    #extPhone .size { position: absolute; right: -2px; bottom: -2px; width: 22px; height: 22px; cursor: nwse-resize; touch-action: none; z-index: 2;
                      background: linear-gradient(135deg, transparent 50%, rgba(230,236,232,.75) 50% 58%, transparent 58% 68%, rgba(230,236,232,.75) 68% 76%, transparent 76%); }
    #extPhone .gate { background: #1c2220; color: #e6ece8; padding: 14px; width: 332px; font-size: 13px; border-radius: 0 0 10px 10px; }
    #extPhone .gate button { font: inherit; font-size: 15px; padding: 8px 16px; margin-top: 8px; background: #2d6b45; color: #fff; border: 1px solid #8fc7a2; border-radius: 6px; cursor: pointer; }
    #extPhone .gate p { margin: 6px 0 0; color: #ffb7a8; }
    #phoneBtn.ringing { background: #b33a2e !important; animation: extRing .5s steps(2, start) infinite; }
    @keyframes extRing { 50% { background: #3a1a17; } }`;
  document.head.appendChild(style);

  const box = document.createElement("div");
  box.id = "extPhone";
  box.innerHTML = `<div class="grab"><span>内線電話（${ST}）</span><button class="z" data-z="-1" title="小さくする">⊖</button><button class="z" data-z="1" title="大きくする">⊕</button><button class="hide" title="しまう（つながったまま）">－</button></div>
    <div class="gate">この駅の内線電話機を開きます。<br>マイクを使います。右上でログインすると使えます。
      <div><button>電話機を開く</button></div><p></p></div><div class="body"></div><div class="size" title="引っぱって大きさを変える"></div>`;
  document.body.appendChild(box);
  const gate = box.querySelector(".gate"), body = box.querySelector(".body"), gateMsg = gate.querySelector("p");

  let pos = null, zoom = 0;
  try { pos = JSON.parse(localStorage.getItem("extPhonePos") || "null"); zoom = Number(localStorage.getItem("extPhoneZoom") || "0"); } catch (e) {}
  const ZMIN = 0.5, ZMAX = 1.8, BASE_H = 590; // BASE_H: 等倍のときの電話機の高さ（上の帯を含む、おおよそ）
  // 大きさ: 電話機の本体と、開く前の案内に zoom をかける（上の帯はそのまま）
  function setZoom(z, save) {
    zoom = Math.round(Math.min(ZMAX, Math.max(ZMIN, z)) * 100) / 100;
    body.style.zoom = zoom; gate.style.zoom = zoom;
    if (save) try { localStorage.setItem("extPhoneZoom", String(zoom)); } catch (e) {}
    place();
  }
  function place() {
    const w = box.offsetWidth || 424, h = box.offsetHeight || 520;
    const x = Math.min(Math.max(0, pos ? pos.x : innerWidth - w - 16), Math.max(0, innerWidth - w));
    const y = Math.min(Math.max(0, pos ? pos.y : 48), Math.max(0, innerHeight - 40));
    box.style.left = x + "px"; box.style.top = y + "px";
  }

  const phone = GokkoExtPhone.create({
    el: body, skin: "desk", kind: "station", station: ST, token: crewToken,
    onNeedLogin: () => { const l = document.getElementById("login"); if (l) l.classList.add("open"); }
  });

  function show(open) { box.classList.toggle("open", open); button.classList.toggle("open", open); if (open) place(); }
  button.onclick = () => show(!box.classList.contains("open"));
  box.querySelector(".grab .hide").onclick = () => show(false);
  box.querySelectorAll(".grab .z").forEach((b) => b.onclick = () => setZoom(zoom + 0.1 * Number(b.dataset.z), true));
  setZoom(zoom > 0 ? zoom : Math.min(1, (innerHeight - 60) / BASE_H), false);

  // 右下の角を引っぱって大きさを変える（横に動かした分だけ、縦横そろえて）
  const size = box.querySelector(".size");
  size.addEventListener("pointerdown", (e) => {
    e.preventDefault();
    const sx = e.clientX, sy = e.clientY, z0 = zoom, w0 = box.offsetWidth;
    size.setPointerCapture(e.pointerId);
    const move = (m) => { const dx = m.clientX - sx, dy = (m.clientY - sy) * 0.68, d = Math.abs(dx) > Math.abs(dy) ? dx : dy; setZoom(z0 * (w0 + d) / w0, false); };
    const up = () => { size.removeEventListener("pointermove", move); size.removeEventListener("pointerup", up); setZoom(zoom, true); };
    size.addEventListener("pointermove", move); size.addEventListener("pointerup", up);
  });
  gate.querySelector("button").onclick = async () => {
    gateMsg.textContent = "";
    try { await phone.start(); gate.style.display = "none"; place(); }
    catch (e) { gateMsg.textContent = e && e.message ? e.message : String(e); }
  };

  // 引っぱって動かす
  const grab = box.querySelector(".grab");
  grab.addEventListener("pointerdown", (e) => {
    if (e.target.tagName === "BUTTON") return;
    const sx = e.clientX, sy = e.clientY, ox = box.offsetLeft, oy = box.offsetTop;
    grab.setPointerCapture(e.pointerId);
    const move = (m) => { pos = { x: ox + m.clientX - sx, y: oy + m.clientY - sy }; place(); };
    const up = () => { grab.removeEventListener("pointermove", move); grab.removeEventListener("pointerup", up); try { localStorage.setItem("extPhonePos", JSON.stringify(pos)); } catch (e) {} };
    grab.addEventListener("pointermove", move); grab.addEventListener("pointerup", up);
  });
  addEventListener("resize", () => { if (box.classList.contains("open")) place(); });

  // 着信: ボタンを点滅させ、電話機を出す。ログインし直したら参加し直す
  let lastToken = crewToken(), wasRinging = false;
  setInterval(() => {
    const ringing = phone.running && phone.ringing;
    button.classList.toggle("ringing", ringing);
    if (ringing && !wasRinging) show(true);
    wasRinging = ringing;
    const t = crewToken();
    if (t !== lastToken) { lastToken = t; phone.rejoin(); }
  }, 300);
})();
