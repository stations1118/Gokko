// ダイヤグラムの◆の窓と、出発順序・走行順の編集（管理画面 admin.html と、司令用管理画面 diasettings.html で共通）。
// 使い方: GokkoDia.openPopup({ box, train, marker, diagram, act, onClose }) / GokkoDia.orderEditor({ el, act })
//         GokkoDia.trainEditor({ el, act })（列車別設定）
(() => {
  const TYPE_COLORS = { "普通": "var(--text)", "準急": "#1e6fd9", "区急": "#18a33a", "急行": "#f08a00", "快急": "#b04fd0", "特急": "#d62828", "回送": "#888" };

  function esc(s) {
    return String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  }

  // 時刻はロープレ時刻で出す（gameclock.js。ロープレ時刻がなければ、この PC の時計のまま）
  function hhmm(ms, sec) {
    return GokkoClock.fmt(ms, sec);
  }

  const eta = (t) => (t ? hhmm(new Date(t).getTime(), true) : "--");

  // ---- 出発順序（駅）・走行順（単線区間）の表。seq は列番の並び（▲▼で入れ替える） ----
  function stationOrderHtml(ord, seq) {
    const info = Object.fromEntries(ord.trains.map((t) => [t.train, t]));
    const rows = seq.map((tr, i) => {
      const t = info[tr] || {};
      const trackSel = t.changeable
        ? `<select data-track-train="${esc(tr)}" data-track-station="${esc(ord.station)}">
             ${t.options.map((o) => `<option value="${o}" ${o === t.track ? "selected" : ""}>${o}番線</option>`).join("")}
             <option value="">所定に戻す</option></select>`
        : `${t.track == null ? "通過" : t.track + "番線"}`;
      return `<tr>
        <td>${i + 1}</td>
        <td style="color:${TYPE_COLORS[t.type] || "inherit"}"><b>${esc(tr)}</b> ${esc(t.type || "")}</td>
        <td class="muted">${esc(t.direction || "")}・${esc(t.stop || "")}</td>
        <td>${trackSel}</td>
        <td class="muted">${eta(t.eta)}</td>
        <td style="white-space:nowrap" title="発車時刻を手で決める（この30秒前まで出発を開通させません）">${t.departable || t.departAt
          ? `発 <input type="time" step="1" data-depart-train="${esc(tr)}" data-depart-station="${esc(ord.station)}" value="${t.departAt ? hhmm(Date.parse(t.departAt), true) : ""}" style="width:7.5em">${
              t.departAt ? `<button data-depart-clear="${esc(tr)}" data-depart-station="${esc(ord.station)}" title="発車時刻の指定を外す">×</button>` : ""}` : ""}</td>
        <td style="white-space:nowrap"><button data-up="${i}" ${i === 0 ? "disabled" : ""}>▲</button><button data-down="${i}" ${i === seq.length - 1 ? "disabled" : ""}>▼</button></td>
      </tr>${t.warning ? `<tr><td></td><td colspan="5" style="color:#e07000; font-size:12px">⚠ ${esc(t.warning)}</td></tr>` : ""}`;
    }).join("");
    return `<div style="margin:4px 0"><b>${esc(ord.name)}の出発順序</b>
        <span class="muted">（${ord.fixedOrder ? "指定中" : "自動・見込み順"}${ord.loopTrack ? "、待避線 " + esc(ord.loopTrack) : ""}）</span></div>
      ${seq.length === 0 ? `<div class="muted">この駅を通る列車がいません</div>` : `<table style="font-size:12px">${rows}</table>`}
      <div class="row" style="margin:6px 0 0">
        <button data-order="set" class="primary" ${seq.length < 2 ? "disabled" : ""}>この順で決定</button>
        <button data-order="clear" ${ord.fixedOrder ? "" : "disabled"}>自動に戻す</button></div>`;
  }

  // 入力したロープレ時刻 → 実際の時刻（ISO。サーバーに送る）
  function timeToIso(v) {
    return GokkoClock.isoFromGame(v);
  }

  function sectionOrderHtml(sec, seq) {
    const info = Object.fromEntries(sec.trains.map((t) => [t.train, t]));
    const rows = seq.map((tr, i) => {
      const t = info[tr] || {};
      return `<tr>
        <td>${i + 1}</td>
        <td style="color:${TYPE_COLORS[t.type] || "inherit"}"><b>${esc(tr)}</b> ${esc(t.type || "")}</td>
        <td class="muted">${esc(t.direction || "")}・${esc(t.from || "")}から</td>
        <td class="muted">${eta(t.eta)}</td>
        <td style="white-space:nowrap"><button data-sup="${i}" ${i === 0 ? "disabled" : ""}>▲</button><button data-sdown="${i}" ${i === seq.length - 1 ? "disabled" : ""}>▼</button></td>
      </tr>`;
    }).join("");
    return `<div style="margin:4px 0"><b>${esc(sec.name)}（単線）の走行順</b>
        <span class="muted">（${sec.fixedOrder ? "指定中" : "自動・見込み順"}）</span></div>
      ${seq.length === 0 ? `<div class="muted">この区間を通る列車がいません</div>` : `<table style="font-size:12px">${rows}</table>`}
      <div class="row" style="margin:6px 0 0">
        <button data-sorder="set" class="primary" ${seq.length < 2 ? "disabled" : ""}>この順で決定</button>
        <button data-sorder="clear" ${sec.fixedOrder ? "" : "disabled"}>自動に戻す</button></div>`;
  }

  const swap = (arr, i, j) => { [arr[i], arr[j]] = [arr[j], arr[i]]; };

  // その向き（"上り" / "下り"）の列車だけにした駅の出発順序（出発順序は上下別に持つ）
  const dirOrder = (o, d) => o && { ...o, name: `${o.name} ${d}`, direction: d,
    fixedOrder: d === "上り" ? o.fixedUp : o.fixedDown, trains: o.trains.filter((t) => t.direction === d) };

  // 順序の表のボタン・番線の選択。処理したら true
  async function handleOrderEvent(ev, ctx) {
    const btn = ev.target.closest("button");
    if (btn) {
      if (btn.dataset.up || btn.dataset.down) {
        const i = Number(btn.dataset.up ?? btn.dataset.down);
        swap(ctx.seq, i, btn.dataset.up ? i - 1 : i + 1);
        ctx.redraw();
        return true;
      }
      if (btn.dataset.sup || btn.dataset.sdown) {
        const i = Number(btn.dataset.sup ?? btn.dataset.sdown);
        swap(ctx.sseq, i, btn.dataset.sup ? i - 1 : i + 1);
        ctx.redraw();
        return true;
      }
      if (btn.dataset.order) {
        ctx.done();
        await ctx.act("POST", "/route/order", { station: ctx.ord.station, direction: ctx.ord.direction, trains: btn.dataset.order === "set" ? ctx.seq : null });
        return true;
      }
      if (btn.dataset.sorder) {
        ctx.done();
        await ctx.act("POST", "/route/section-order", { a: ctx.sec.a, b: ctx.sec.b, trains: btn.dataset.sorder === "set" ? ctx.sseq : null });
        return true;
      }
    }
    return false;
  }

  // ---- 後運用（終点で折り返して次になる列車）。◆の窓と列車別設定で共通 ----
  // 差し替えると、もともとその列車に折り返すはずだった列車が、こちらのもとの後運用を受け持つ（運行番号ごと入れ替わる）。取消で元に戻す
  function turnHtml(t, unyo, pick) {
    if (!t) return "";
    const now = t.kind === "折返" && t.number
      ? `<b class="mono">${esc(t.number)}</b> <span class="muted">${esc((t.time || "").slice(0, 5))}${esc(t.how || "")}</span>`
      : t.kind ? esc(t.kind) : "なし";
    const cands = (t.candidates || []).filter((c) => c.train !== t.number);
    const opts = `<option value="">差し替える列車を選ぶ</option>` + cands.map((c) =>
      `<option value="${esc(c.train)}" ${c.train === pick ? "selected" : ""}>${esc(c.train)} ${esc((c.time || "").slice(0, 5))}発 ${esc(c.type || "")}${
        c.unyo != null ? `・${c.unyo}運行` : ""}${c.prev ? `（いまは ${esc(c.prev)} の後運用）` : ""}</option>`).join("");
    // 直接折返: 入換で折り返す列車を、終点の番線（大道寺の 3番線）に着けて、入換せずにそのまま後運用で出す（遅れを取り戻すとき）
    const direct = t.direct != null
      ? `<div class="row" style="margin:4px 0 0"><span style="color:#e07000">${t.direct}番線で直接折返（入換なし）</span></div>`
      : (t.directTracks || []).length
        ? `<div class="row" style="margin:4px 0 0">${t.directTracks.map((k) =>
            `<button data-turn-direct="${k}" title="終点の ${k}番線に着けて、入換せずにそのまま ${esc(t.number || "")} で折り返す。着く列車と ${esc(t.number || "")} の番線を ${k}番線に指定します（取消で元に戻す）">${k}番線で直接折返</button>`).join("")}</div>`
        : "";
    return `<div>後運用 ${now}${unyo != null ? ` <span class="muted">（${unyo}運行）</span>` : ""}${
        t.changed && t.direct == null ? ` <span style="color:#e07000">差し替え中（元 ${esc(t.original || "なし")}）</span>` : ""}</div>${direct}
      <div class="row" style="margin:4px 0 0">${cands.length
        ? `<select data-turn-pick="1">${opts}</select><button data-turn-set="1" class="primary" ${pick ? "" : "disabled"}>差し替え</button>`
        : `<span class="muted">終点から出る列車で、差し替えられるものはありません</span>`}
        ${t.cancelable ? `<button data-turn-cancel="1" title="いちばん新しい差し替えを取り消して、元の後運用に戻す">取消</button>` : ""}</div>`;
  }

  // ---- 途中折返し（◆の窓。この駅で打ち切って、反対向きに出る列車に折り返す） ----
  // 大道寺方面から来た下りを、新野崎 3・4番線、浜園 2番線、藤江・水越・高見沢で上りに折り返すなど。選んだ列車のこの駅より手前は走らない
  function midTurnHtml(t, stationId, pick) {
    if (!t) return "";
    const mw = t.midway;
    if (mw && mw.station === stationId)
      return `<div class="grp"><div class="ttl">途中折返し</div><div><span style="color:#e07000">この駅で打ち切り、${mw.track}番線で <b class="mono">${esc(mw.next)}</b> に折り返します</span></div>
        <div class="row" style="margin:4px 0 0"><button data-turn-cancel="1" title="途中折返しを取り消して、終点まで走らせる（打ち切りと番線の指定も外す）">取消</button></div></div>`;
    const mt = (t.midTurns || []).find((x) => x.station === stationId);
    if (!mt) return "";
    const opts = `<option value="">折り返す列車を選ぶ</option>` + mt.candidates.map((c) =>
      `<option value="${esc(c.train)}" ${c.train === pick ? "selected" : ""}>${esc(c.train)} ${esc(c.time || "")}発 ${esc(c.type || "")}${c.unyo != null ? `・${c.unyo}運行` : ""}</option>`).join("");
    return `<div class="grp"><div class="ttl">途中折返し（この駅で打ち切り、反対向きの列車として出す。選んだ列車のこの駅より手前は走りません）</div>
      <div class="row" style="margin:0"><select data-mid-pick="1">${opts}</select>${mt.tracks.map((k) =>
        `<button data-mid-track="${k}" class="primary" ${pick ? "" : "disabled"} title="${k}番線に着けて、選んだ列車に折り返す">${k}番線で折返し</button>`).join("")}</div></div>`;
  }

  async function handleTrackChange(ev, ctx) {
    const s = ev.target.closest("select[data-track-train]");
    if (!s) return false;
    ctx.done();
    await ctx.act("POST", "/route/track", { train: s.dataset.trackTrain, station: s.dataset.trackStation, track: s.value === "" ? null : Number(s.value) });
    return true;
  }

  // ---- ◆の窓（別ウィンドウ） ----
  // 前は図の上に重ねて出していたが、開いている間は図の描き直しが止まる（中身も古いまま）・終日表示でスクロールしているとずれる・
  // 図の枠で下が切れる、などがあったので、在線位置（tid.html）と同じように別ウィンドウに出す。
  // ウィンドウの中身は開いた側（この画面）が書き、毎秒の更新で、その列車・駅の最新の状態に描き直す（操作しても閉じない）。
  // ブラウザがポップアップを止めたとき（アプリに埋め込んだ画面など）は、画面の右下に固定した枠（中身は同じ）に出す
  let popup = null; // { win, train, stationId, act, api, onClose, seq, sseq, dirty, error, pressing, diagram, ctx }

  // 枠に出したときは win.gokkoFrame がその枠（閉じる = 枠を外す）
  const winClosed = (w) => !w || w.closed || (w.gokkoFrame && !w.gokkoFrame.isConnected);
  function winClose(w) {
    try { if (w.gokkoFrame) w.gokkoFrame.remove(); else if (!w.closed) w.close(); } catch (e) {}
  }

  function closePopup() {
    if (!popup) return;
    const p = popup;
    popup = null;
    winClose(p.win);
    p.onClose && p.onClose();
  }
  const popupOpen = () => !!popup && !winClosed(popup.win);
  window.addEventListener("pagehide", () => { if (popup) winClose(popup.win); });

  // ポップアップを開けないときの、画面の右下の枠
  function framedWindow() {
    const box = document.createElement("div");
    box.style.cssText = "position:fixed; right:16px; bottom:16px; z-index:40; width:min(500px, calc(100vw - 32px)); height:min(640px, calc(100vh - 32px));"
      + " display:flex; flex-direction:column; background:var(--panel, #fff); border:1px solid var(--line, #ccc); border-radius:8px; box-shadow:0 4px 16px rgba(0,0,0,.3); overflow:hidden";
    box.innerHTML = `<div style="display:flex; align-items:center; padding:4px 8px; border-bottom:1px solid var(--line, #ccc); font-size:12px; color:var(--muted, #666)">
        <span style="flex:1">列車の扱い（ポップアップが止められているので、この画面の中に出しています）</span>
        <button data-frame-close="1" title="閉じる" style="padding:0 8px">×</button></div>
      <iframe title="列車の扱い" style="flex:1; border:0; width:100%; background:var(--bg, #fff)"></iframe>`;
    document.body.appendChild(box);
    box.querySelector("[data-frame-close]").addEventListener("click", () => closePopup());
    const win = box.querySelector("iframe").contentWindow;
    win.gokkoFrame = box;
    return win;
  }

  // 別ウィンドウを用意する（前に開いたものがあれば使い回す。この画面を読み直す前に開いたものは、中身を書き直す）
  function popupWindow() {
    let win = popup && !winClosed(popup.win) ? popup.win : null;
    if (!win) {
      try { win = window.open("", "gokkoMarker", "popup=yes,width=520,height=660"); } catch (e) { win = null; }
      if (!win) win = framedWindow();
    }
    if (win.gokkoOwner !== window) {
      const frame = win.gokkoFrame;
      const styles = [...document.querySelectorAll("style")].map((s) => s.outerHTML).join("");
      const d = win.document;
      d.open();
      d.write(`<!doctype html><html lang="ja"><head><meta charset="utf-8"><title>列車の扱い</title>${styles}
        <style>body { padding: 12px; } table { border-collapse: collapse; } td { padding: 2px 6px; vertical-align: middle; }
          .grp { margin: 10px 0 0; border-top: 1px solid var(--line); padding-top: 8px; } .grp > .ttl { font-size: 12px; color: var(--muted); margin-bottom: 4px; }</style>
        </head><body><div id="m"></div></body></html>`);
      d.close();
      win.gokkoOwner = window;
      if (frame) win.gokkoFrame = frame;
      const el = d.getElementById("m");
      el.addEventListener("click", onPopupClick);
      el.addEventListener("change", onPopupChange);
      el.addEventListener("pointerdown", () => { if (popup) popup.pressing = true; });
      win.addEventListener("pointerup", () => setTimeout(() => { if (popup) popup.pressing = false; }, 0));
      d.addEventListener("keydown", (ev) => { if (ev.key === "Escape") closePopup(); });
    }
    return win;
  }

  /**
   * ◆の窓を開く（別ウィンドウ）。train: 列番、marker: DiagramView の markers の1つ、diagram: /diagram の結果、
   * act(method, path, body): 実行して描き直す関数、api: 実行だけする関数（あればエラーをウィンドウに出す）、onClose: 閉じたとき
   */
  function openPopup({ train, marker: m, diagram, act, api, onClose }) {
    const win = popupWindow();
    const prev = popup;
    popup = { win, train, stationId: m.stationId, act, api, onClose, seq: null, sseq: null, dirty: false, error: "", pressing: false, turnPick: "" };
    if (prev && prev.onClose) prev.onClose(); // 前に選んでいた◆の印を消す
    updatePopup(diagram, true);
    win.focus();
    return win;
  }

  // 実行する。api があれば直接呼んでエラーをウィンドウに出し、act で画面ごと読み直す（GET /diagram なので何も変えない）
  async function popupRun(method, path, body) {
    const p = popup;
    if (!p) return;
    if (p.api) {
      try { await p.api(method, path, body); p.error = ""; } catch (e) { p.error = e.message; }
      await p.act("GET", "/diagram");
      if (popup === p) updatePopup(p.diagram, true);
    } else await p.act(method, path, body);
  }

  function popupCtx(p, d) {
    const tr = (d.trains || []).find((t) => t.train === p.train);
    const m = tr && tr.markers.find((x) => x.stationId === p.stationId);
    if (!m) return { tr, m: null };
    const all = (d.orders || []).find((o) => o.station === m.stationId);
    const ord = tr.direction ? dirOrder(all, tr.direction) : all; // 選んだ列車の向きの出発順序だけ
    const sec = m.singleNext ? (d.sections || []).find((s) => (s.a === m.stationId && s.b === m.singleNext) || (s.b === m.stationId && s.a === m.singleNext)) : null;
    // 並べ替えている途中（決定する前）は、並びを読み直さない
    if (!p.dirty || p.seq == null) {
      p.seq = ord ? ord.trains.map((t) => t.train) : [];
      p.sseq = sec ? sec.trains.map((t) => t.train) : [];
    }
    return { tr, m, ord, sec };
  }

  // 毎秒の更新で呼ぶ。押している途中・選んでいる途中は描き直さない（押している途中のボタンが消えないように）
  function updatePopup(d, force) {
    if (!popup || !d) return;
    const p = popup;
    if (winClosed(p.win)) { popup = null; p.onClose && p.onClose(); return; }
    p.diagram = d;
    const doc = p.win.document, el = doc.getElementById("m");
    if (!el) return;
    if (!force && (p.pressing || (doc.activeElement && ["SELECT", "INPUT"].includes(doc.activeElement.tagName)))) return;
    const c = popupCtx(p, d);
    p.ctx = c;
    const html = popupHtml(p, c);
    if (el.dataset.html !== html) { el.innerHTML = html; el.dataset.html = html; }
    const title = `${p.train} ${c.m ? c.m.name : ""}｜列車の扱い`;
    if (doc.title !== title) doc.title = title;
  }

  function popupHtml(p, { tr, m, ord, sec }) {
    const err = p.error ? `<div style="color:var(--danger); margin-bottom:6px">${esc(p.error)}</div>` : "";
    if (!m) return `${err}<div style="margin-bottom:8px; font-size:16px"><b>${esc(p.train)}</b></div>
      <div class="muted">${tr ? "この駅はもう過ぎたか、扱わない駅になりました" : "この列車は図にいません（運転を終えたか、在線を共有していません）"}。図の◆を押して選び直してください。</div>
      <div class="row" style="margin-top:10px"><button data-close="1">閉じる</button></div>`;
    const color = TYPE_COLORS[tr.type] || "inherit";
    const btn = (data, on, text, title, cls) => `<button ${data} class="${on ? cls || "primary" : ""}"${title ? ` title="${esc(title)}"` : ""}>${text}</button>`;
    const stopNow = m.stopOverride || "";
    const stopBtns = [["", "所定"], ["停車", "臨時停車"], ["通過", "臨時通過"], ["打ち切り", "打ち切り"]]
      .filter(([v]) => v !== "打ち切り" || !m.terminal || stopNow === "打ち切り")
      .map(([v, l]) => btn(`data-stop="${v}"`, stopNow === v, l,
        v === "打ち切り" ? "この駅で運転を打ち切る（サーバーの扱いだけ。この先の駅の進路は取らない）" : v ? "サーバーの扱いだけ。ゲームの停車駅は変わりません" : "臨時の指定を外す")).join("");
    return `${err}<div style="margin-bottom:4px; font-size:16px"><b style="color:${color}">${esc(tr.train)}</b>
          <span class="muted" style="font-size:13px">${esc(tr.type || "")}・${esc(tr.direction || "")}</span>
          <b style="margin-left:6px">${esc(m.name)}</b></div>
        <div>${m.track == null ? "通過" : m.track + "番線"} <span class="muted">（${esc(m.trackSource)}）・${esc(m.source)} ${hhmm(new Date(m.t).getTime(), true)}</span></div>
        <div class="muted" style="font-size:12px">${esc(m.status)}</div>
        <div class="grp"><div class="ttl">番線</div>
          ${m.changeable ? `<div class="row" style="margin:0">${m.options.map((o) => btn(`data-pick="${o}"`, o === m.track, `${o}番線`)).join("")}
              ${btn(`data-pick=""`, false, "所定に戻す")}</div>`
            : `<div class="muted">${(m.options || []).length > 1 ? "列車がもうこの駅の進路に入っているので、変えられません" : "この駅で選べる番線はありません"}</div>`}</div>
        ${tr.turn ? `<div class="grp"><div class="ttl">後運用（遅れたときに、終点から別の運用につなぐ。運行番号ごと入れ替わります）</div>${turnHtml(tr.turn, tr.unyo, p.turnPick)}</div>` : ""}
        ${midTurnHtml(tr.turn, m.stationId, p.midPick)}
        ${stopNow === "打ち切り" && window.GokkoRinji && !(tr.turn && tr.turn.midway && tr.turn.midway.station === m.stationId) // 途中折返しで打ち切っている駅では、特発臨は立てない
          ? GokkoRinji.popupHtml(p, tr, m, () => updatePopup(p.diagram, true)) : ""}
        <div class="grp"><div class="ttl">停車・通過（いまは${esc(m.stopType || "")}${stopNow ? `・${esc(stopNow)}の指定中` : ""}）</div>
          <div class="row" style="margin:0">${stopBtns}</div></div>
        ${m.draggable || m.departAt ? `<div class="grp"><div class="ttl">発車時刻（この30秒前まで出発を開通させません。図の◆を横にずらしても決められます）</div>
          <div class="row" style="margin:0">発 <input type="time" step="1" data-depart="1" value="${m.departAt ? hhmm(Date.parse(m.departAt), true) : ""}" style="width:8em">
            ${m.departAt ? btn(`data-depart-off="1"`, false, "指定を外す") : ""}</div></div>` : ""}
        ${m.manual && m.manual.length ? `<div class="grp"><div class="ttl">駅扱い（連動に問題がなければ、抑止・待ちを無視して開通）</div>
          <div class="row" style="margin:0">${m.manual.map((x) => `<button data-manual="${esc(x.action)}" data-label="${esc(x.label)}">${esc(x.label)}</button>`).join("")}</div></div>` : ""}
        <div class="grp"><div class="ttl">抑止・開通</div><div class="row" style="margin:0">
          ${btn(`data-hold="arrive"`, m.holdArrive, m.holdArrive ? "場内抑止中（解除）" : "場内抑止", "場内信号を出させない。開通済みでも、列車がまだ接近していなければ取り消す", "danger")}
          ${btn(`data-hold="depart"`, m.holdDepart, m.holdDepart ? "出発抑止中（解除）" : "出発抑止", "出発信号を出させない。開通済みでも、列車がまだ接近していなければ取り消す", "danger")}
          <button class="primary" data-act="force" title="抑止を外し、安全を確認でき次第、自動で開通させる">自動開通</button></div></div>
        ${ord ? `<div class="grp">${stationOrderHtml(ord, p.seq)}</div>` : ""}
        ${sec ? `<div class="grp">${sectionOrderHtml(sec, p.sseq)}</div>` : ""}
        <div class="row" style="margin-top:12px"><button data-close="1">閉じる</button></div>`;
  }

  async function onPopupClick(ev) {
    const p = popup;
    const btn = ev.target.closest("button");
    if (!p || !btn) return;
    if (btn.dataset.close) { closePopup(); return; }
    if (window.GokkoRinji && await GokkoRinji.popupEvent(ev, p, rinjiCtx(p))) return; // 特発臨（tokuhatsu-rinji.js）
    const c = p.ctx || {};
    // 出発順序・走行順の▲▼と決定（並べ替えは、決定するまでウィンドウの中だけ）
    const ctx = { ord: c.ord, sec: c.sec, seq: p.seq, sseq: p.sseq, act: popupRun,
      redraw: () => { p.dirty = true; updatePopup(p.diagram, true); }, done: () => { p.dirty = false; } };
    if (await handleOrderEvent(ev, ctx)) return;
    // 出発順序の表の発車時刻の「×」
    if (btn.dataset.departClear && btn.dataset.departStation) { await popupRun("POST", "/route/open-at", { train: btn.dataset.departClear, station: btn.dataset.departStation, time: null }); return; }
    // 後運用の差し替え・取消
    if (btn.dataset.turnSet && p.turnPick) { const next = p.turnPick; p.turnPick = ""; await popupRun("POST", "/route/turn", { train: p.train, next }); return; }
    if (btn.dataset.turnCancel) { p.turnPick = ""; await popupRun("POST", "/route/turn", { train: p.train, next: null }); return; }
    if (btn.dataset.turnDirect) { p.turnPick = ""; await popupRun("POST", "/route/turn", { train: p.train, next: null, direct: Number(btn.dataset.turnDirect) }); return; }
    const m = c.m;
    if (!m) return;
    if (btn.dataset.midTrack && p.midPick) { const next = p.midPick; p.midPick = ""; await popupRun("POST", "/route/turn", { train: p.train, next, station: m.stationId, direct: Number(btn.dataset.midTrack) }); return; }
    const body = { train: p.train, station: m.stationId };
    if (btn.dataset.act === "force") await popupRun("POST", "/route/force", body);
    else if (btn.dataset.stop !== undefined) await popupRun("POST", "/route/stop", { ...body, kind: btn.dataset.stop || null });
    else if (btn.dataset.hold) {
      const on = btn.dataset.hold === "arrive" ? m.holdArrive : m.holdDepart;
      await popupRun("POST", "/route/hold", { ...body, hold: !on, kind: btn.dataset.hold });
    } else if (btn.dataset.manual) await popupRun("POST", "/route/manual", { ...body, action: btn.dataset.manual, label: btn.dataset.label });
    else if (btn.dataset.pick !== undefined) await popupRun("POST", "/route/track", { ...body, track: btn.dataset.pick === "" ? null : Number(btn.dataset.pick) });
    else if (btn.dataset.departOff) await popupRun("POST", "/route/open-at", { ...body, time: null });
  }

  // 特発臨の欄（◆の窓で「打ち切り」を選んだとき）に渡すもの
  const rinjiCtx = (p) => ({
    win: p.win,
    redraw: () => { if (popup === p) updatePopup(p.diagram, true); },
    refresh: async () => { await p.act("GET", "/diagram"); if (popup === p) updatePopup(p.diagram, true); }
  });

  async function onPopupChange(ev) {
    const p = popup;
    if (!p) return;
    if (window.GokkoRinji && await GokkoRinji.popupEvent(ev, p, rinjiCtx(p))) return; // 特発臨（tokuhatsu-rinji.js）
    if (await handleTrackChange(ev, { done: () => {}, act: popupRun })) return;
    const tp = ev.target.closest("select[data-turn-pick]");
    if (tp) { p.turnPick = tp.value; tp.blur(); updatePopup(p.diagram, true); return; }
    const mp = ev.target.closest("select[data-mid-pick]");
    if (mp) { p.midPick = mp.value; mp.blur(); updatePopup(p.diagram, true); return; }
    // 発車時刻: この◆の駅（data-depart）か、出発順序の表の列車（data-depart-train）
    const inp = ev.target.closest("input[data-depart], input[data-depart-train]");
    if (!inp) return;
    const train = inp.dataset.departTrain || p.train, station = inp.dataset.departStation || (p.ctx && p.ctx.m && p.ctx.m.stationId);
    const time = timeToIso(inp.value);
    if (!station || (inp.value && !time)) return;
    inp.blur();
    await popupRun("POST", "/route/open-at", { train, station, time });
  }

  // ---- 順序の設定画面 ----
  // タブ「出発順序（上下別）」: 上り・下りを左右に並べ、それぞれ駅を選んで、その向きの列車だけを並べ替える。
  // タブ「単線区間の走行順（上下とも）」: 区間を選んで、上下の列車をまとめて並べ替える。並べ替えている間は描き直さない
  function orderEditor({ el, act }) {
    const DIRS = ["上り", "下り"];
    let diagram = null;
    let tab = "dep";
    try { tab = localStorage.getItem("gokkoOrderTab") || "dep"; } catch (e) {}
    const pick = { 上り: "", 下り: "", sec: "" };
    const seq = { 上り: [], 下り: [], sec: [] };
    const dirty = { 上り: false, 下り: false, sec: false };

    const station = (id) => (diagram && diagram.orders || []).find((o) => o.station === id) || null;
    const section = (k) => (diagram && diagram.sections || []).find((s) => `${s.a}|${s.b}` === k) || null;

    function load(col) {
      if (col === "sec") { const s = section(pick.sec); seq.sec = s ? s.trains.map((t) => t.train) : []; return; }
      const o = dirOrder(station(pick[col]), col);
      seq[col] = o ? o.trains.map((t) => t.train) : [];
    }

    function colHtml(d) {
      const opts = (diagram.orders || []).map((o) => {
        const n = o.trains.filter((t) => t.direction === d).length;
        const fixed = d === "上り" ? o.fixedUp : o.fixedDown;
        return `<option value="${esc(o.station)}" ${pick[d] === o.station ? "selected" : ""}>${esc(o.name)}（${n}本${fixed ? "・指定中" : ""}）</option>`;
      }).join("");
      const o = dirOrder(station(pick[d]), d);
      return `<div data-col="${d}" style="min-width:0">
          <div class="row"><b>${d}</b><select data-pick-col="${d}"><option value="">駅を選ぶ</option>${opts}</select>
            ${dirty[d] ? `<span class="muted">並べ替え中</span>` : ""}</div>
          ${o ? stationOrderHtml(o, seq[d]) : ""}</div>`;
    }

    function secHtml() {
      const opts = (diagram.sections || []).map((s) => `<option value="${esc(s.a)}|${esc(s.b)}" ${pick.sec === `${s.a}|${s.b}` ? "selected" : ""}>${esc(s.name)}（${s.trains.length}本${s.fixedOrder ? "・指定中" : ""}）</option>`).join("");
      const s = section(pick.sec);
      return `<div data-col="sec">
          <div class="row"><select data-pick-col="sec"><option value="">単線区間を選ぶ</option>${opts}</select>
            ${dirty.sec ? `<span class="muted">並べ替え中</span>` : ""}</div>
          ${s ? sectionOrderHtml(s, seq.sec) : ""}</div>`;
    }

    function draw() {
      if (!diagram) return;
      const tabBtn = (id, label) => `<button data-tab="${id}" class="${tab === id ? "primary" : ""}">${label}</button>`;
      el.innerHTML = `<div class="row">${tabBtn("dep", "出発順序（上下別）")}${tabBtn("sec", "単線区間の走行順（上下とも）")}</div>
        ${tab === "dep"
          ? `<div style="display:grid; gap:16px; grid-template-columns:repeat(auto-fit, minmax(320px, 1fr))">${DIRS.map(colHtml).join("")}</div>`
          : secHtml()}`;
    }

    el.addEventListener("change", async (ev) => {
      const s = ev.target.closest("select[data-pick-col]");
      if (s) { const c = s.dataset.pickCol; pick[c] = s.value; dirty[c] = false; load(c); draw(); return; }
      const inp = ev.target.closest("input[data-depart-train]");
      if (inp) {
        const time = timeToIso(inp.value);
        if (inp.value && !time) return;
        await act("POST", "/route/open-at", { train: inp.dataset.departTrain, station: inp.dataset.departStation, time });
        inp.blur();
        return;
      }
      const t = ev.target.closest("select[data-track-train]");
      if (t) {
        const c = t.closest("[data-col]")?.dataset.col;
        if (c) dirty[c] = false;
        await act("POST", "/route/track", { train: t.dataset.trackTrain, station: t.dataset.trackStation, track: t.value === "" ? null : Number(t.value) });
      }
    });

    el.addEventListener("click", async (ev) => {
      const btn = ev.target.closest("button");
      if (!btn) return;
      if (btn.dataset.tab) {
        tab = btn.dataset.tab;
        try { localStorage.setItem("gokkoOrderTab", tab); } catch (e) {}
        draw();
        return;
      }
      if (btn.dataset.departClear) { await act("POST", "/route/open-at", { train: btn.dataset.departClear, station: btn.dataset.departStation, time: null }); return; }
      const c = btn.closest("[data-col]")?.dataset.col;
      if (!c) return;
      const up = btn.dataset.up ?? btn.dataset.sup, down = btn.dataset.down ?? btn.dataset.sdown;
      if (up !== undefined || down !== undefined) {
        const i = Number(up ?? down);
        swap(seq[c], i, up !== undefined ? i - 1 : i + 1);
        dirty[c] = true;
        draw();
        return;
      }
      if (btn.dataset.sorder && c === "sec") {
        const s = section(pick.sec);
        dirty.sec = false;
        if (s) await act("POST", "/route/section-order", { a: s.a, b: s.b, trains: btn.dataset.sorder === "set" ? seq.sec : null });
        return;
      }
      if (btn.dataset.order && c !== "sec") {
        const o = station(pick[c]);
        dirty[c] = false;
        if (!o) return;
        // その向きだけを決める・自動に戻す（反対向きの順序はそのまま）
        await act("POST", "/route/order", { station: o.station, direction: c, trains: btn.dataset.order === "set" ? [...seq[c]] : null });
      }
    });

    return {
      update(d) {
        diagram = d;
        if (el.contains(document.activeElement) && ["SELECT", "INPUT"].includes(document.activeElement.tagName)) return;
        for (const c of [...DIRS, "sec"]) if (!dirty[c]) load(c);
        // 見込み時刻・状態は毎秒変わるので、比べるときは除く（押している途中のボタンが消えないように）
        const key = JSON.stringify([tab, pick, dirty, DIRS.map((x) => station(pick[x])), section(pick.sec)],
          (k, v) => (k === "eta" || k === "status" ? undefined : v));
        if (el.dataset.key === key) return;
        el.dataset.key = key;
        draw();
      }
    };
  }

  // ---- 列車別設定（列車を選んで、この先の駅ごとに番線・抑止・自動開通・駅扱いを操作する） ----
  function trainEditor({ el, act }) {
    let trains = [];
    let pick = "";
    let turnPick = ""; // 後運用の差し替えで選んだ列車
    let pressing = false; // ボタンを押している間は描き直さない（押している途中のボタンが消えないように）

    function table(t) {
      return `<div class="scroll" style="overflow-x:auto"><table>
        <thead style="white-space:nowrap"><tr><td class="muted">駅</td><td class="muted">停車</td><td class="muted">番線</td><td class="muted">決め方</td><td class="muted">状態</td><td></td></tr></thead>
        <tbody>${t.stations.map((s) => {
          const st = esc(s.stationId);
          const opts = [`<option value="">（指定なし）</option>`].concat(s.options.map((o) =>
            `<option value="${o}" ${s.track === o && s.source === "集中" ? "selected" : ""}>${o}番線</option>`)).join("");
          const manual = s.manual.length > 0
            ? `<select data-manual-station="${st}"><option value="">駅扱い…</option>${s.manual.map((x) =>
                `<option value="${esc(x.action)}">${esc(x.label)}</option>`).join("")}</select>` : "";
          return `<tr>
            <td>${esc(s.name)}</td>
            <td style="white-space:nowrap">${esc(s.stopType)} <select data-stop-station="${st}" title="臨時停車・臨時通過・打ち切り（サーバーの扱いだけ。ゲームの停車駅は変わりません）">${
              [["", "所定"], ["停車", "臨時停車"], ["通過", "臨時通過"], ["打ち切り", "打ち切り"]].map(([v, l]) =>
                `<option value="${v}" ${(s.stopOverride || "") === v ? "selected" : ""}>${l}</option>`).join("")}</select></td>
            <td>${s.track ?? "-"} ${s.options.length > 0 ? `<select data-track-station="${st}">${opts}</select>` : ""}</td>
            <td class="muted">${esc(s.source)}${s.locked ? "・確定" : ""}</td>
            <td>${esc(s.status)}</td>
            <td style="white-space:nowrap">
              <button data-hold="arrive" data-station="${st}" data-on="${s.holdArrive ? 1 : 0}" class="${s.holdArrive ? "danger" : ""}">${s.holdArrive ? "場内抑止中" : "場内抑止"}</button>
              <button data-hold="depart" data-station="${st}" data-on="${s.holdDepart ? 1 : 0}" class="${s.holdDepart ? "danger" : ""}">${s.holdDepart ? "出発抑止中" : "出発抑止"}</button>
              <button class="primary" data-force="${st}">自動開通</button>
              ${manual}
            </td></tr>`;
        }).join("")}</tbody></table></div>`;
    }

    function draw() {
      const t = trains.find((x) => x.train === pick);
      const opts = `<option value="">列車を選ぶ</option>` + trains.map((x) =>
        `<option value="${esc(x.train)}" ${x.train === pick ? "selected" : ""}>${esc(x.train)}（${esc(x.type)}・${esc(x.direction)}・${esc(x.position)}）</option>`).join("");
      el.innerHTML = `<div class="row"><select data-pick-train>${opts}</select>
          ${trains.length === 0 ? `<span class="muted">在線共有モードで運転中の列車はいません</span>` : ""}</div>
        ${t && t.turn ? `<div style="margin:6px 0 8px">${turnHtml(t.turn, t.unyo, turnPick)}</div>` : ""}${t ? table(t) : ""}`;
    }

    el.addEventListener("pointerdown", () => { pressing = true; });
    window.addEventListener("pointerup", () => { setTimeout(() => { pressing = false; }, 0); });
    el.addEventListener("change", async (ev) => {
      const s = ev.target.closest("select");
      if (!s) return;
      if (s.dataset.pickTrain !== undefined) { pick = s.value; turnPick = ""; el.dataset.key = ""; draw(); return; }
      if (!pick) return;
      if (s.dataset.turnPick) { turnPick = s.value; s.blur(); el.dataset.key = ""; draw(); return; }
      if (s.dataset.manualStation && s.value) {
        await act("POST", "/route/manual", { train: pick, station: s.dataset.manualStation, action: s.value, label: s.options[s.selectedIndex].text });
      } else if (s.dataset.stopStation) {
        await act("POST", "/route/stop", { train: pick, station: s.dataset.stopStation, kind: s.value || null });
      } else if (s.dataset.trackStation) {
        await act("POST", "/route/track", { train: pick, station: s.dataset.trackStation, track: s.value === "" ? null : Number(s.value) });
      }
      s.blur();
    });
    el.addEventListener("click", async (ev) => {
      const b = ev.target.closest("button");
      if (!b || !pick) return;
      if (b.dataset.turnSet && turnPick) { const next = turnPick; turnPick = ""; el.dataset.key = ""; await act("POST", "/route/turn", { train: pick, next }); }
      else if (b.dataset.turnCancel) { turnPick = ""; el.dataset.key = ""; await act("POST", "/route/turn", { train: pick, next: null }); }
      else if (b.dataset.turnDirect) { turnPick = ""; el.dataset.key = ""; await act("POST", "/route/turn", { train: pick, next: null, direct: Number(b.dataset.turnDirect) }); }
      else if (b.dataset.hold) await act("POST", "/route/hold", { train: pick, station: b.dataset.station, hold: b.dataset.on !== "1", kind: b.dataset.hold });
      else if (b.dataset.force) await act("POST", "/route/force", { train: pick, station: b.dataset.force });
    });

    return {
      update(list) {
        trains = list || [];
        if (pick && !trains.some((x) => x.train === pick)) pick = "";
        if (pressing || el.contains(document.activeElement) && document.activeElement.tagName === "SELECT") return;
        const key = JSON.stringify([pick, trains.map((x) => [x.train, x.position]), trains.find((x) => x.train === pick)]);
        if (el.dataset.key === key) return;
        el.dataset.key = key;
        draw();
      }
    };
  }

  // ---- ダイヤグラム（管理画面とダイヤグラムの設定画面で共通） ----
  // 実績（実線）と見込み（点線）。◆は進路を扱う駅を通過する（停車駅は着く）予想時刻。
  // ◆を押すと別ウィンドウ（番線・停車／通過・発車時刻・駅扱い・抑止・順序）。押してから 6px 動かすまでは押しただけとみなす。◆を横にずらすと、その駅の発車時刻を決める（スジずらし。ダブルクリックで解除）。
  // 詳細表示では、◆を縦にずらすと、その駅の番線を変える（変えられる番線の段は緑。列車がもうその駅の進路に入っていれば変えられない）。
  // 上の「全部・上りのみ・下りのみ」で、表示する列車を絞る。
  // 「詳細表示」: 進路を扱う駅を番線ごとの段に分け、列車はその駅で使う番線の段に引く。同じ番線に同じ時間帯に2本いれば、赤く示す（番線の競合）。
  // 1分の幅・駅と駅の間隔・（詳細表示では）番線の段の高さ・表示する時間の範囲を変えられ、拡大・縮小もできる（Ctrl+ホイールでも）。
  // 設定は通常表示と詳細表示で別々に、見る人ごとにブラウザに残す。通常表示を「元に戻す」にしているときは、これまでどおり画面の幅に合わせる
  // ダイヤ運転中は、時刻表にあって走っていない列車の計画の線（スジ）を薄く引く（api を渡したときだけ。「計画の線」で消せる）
  function diagramView({ box, tip, filter, act, api, readOnly = false }) {
    const RIGHT = 12, TOP = 18, BOTTOM = 24;
    const DEFAULTS = {
      // 通常表示（これまでの図: 幅 1000 に過去10分・先30分。駅の間隔は 17）
      n: { minPx: (1000 - 92 - RIGHT) / 40, stationPx: 17, trackPx: 14, pastMin: 10, futureMin: 30 },
      d: { minPx: 22, stationPx: 34, trackPx: 14, pastMin: 10, futureMin: 30 }
    };
    const LIMITS = { minPx: [4, 120], stationPx: [8, 200], trackPx: [8, 60], pastMin: [0, 120], futureMin: [5, 240] };
    let view = { split: false, n: { ...DEFAULTS.n }, d: { ...DEFAULTS.d } };
    try {
      const saved = JSON.parse(localStorage.getItem("gokkoDiagramView2") || "{}");
      view = { split: !!saved.split, n: { ...DEFAULTS.n, ...saved.n }, d: { ...DEFAULTS.d, ...saved.d } };
    } catch (e) {}
    const cur = () => (view.split ? view.d : view.n);
    // 計画の線を出すか（通常表示・詳細表示それぞれに残す。書かれていなければ出す。DEFAULTS に入れないのは「元に戻す」と画面の幅合わせに関わらないように）
    const planOn = () => cur().plan !== false;
    const dashOn = () => cur().dash === true; // 運行のつなぎ線を点線にするか
    // 読むだけの図（readOnly。diagramview.html）: ◆を出さず、操作できない。「時刻表だけ」にすると、実績・見込みを出さず、時刻表の計画の線だけを引く
    const simpleOn = () => readOnly && cur().simple === true;
    // 計画の線を引くか（ふだんはダイヤ運転中だけ。読むだけの図はいつでも。時刻表だけのときは必ず）
    const planWanted = () => !!api && (diaMode || readOnly) && (planOn() || simpleOn());
    // 終日表示: ロープレ時刻の 3時〜26時 を横に並べ、スクロールして過去の実績・先の計画を見る（「現在時刻」で今に戻る）。いつも終日表示
    const DAY_FROM = 3 * 3600, DAY_LEN = 23 * 3600;
    const dayOn = () => true;
    let jumpNow = dayOn(); // 次に描いたあと、今の位置までスクロールする
    let lastL = null; // 前に描いたときの配置
    let plan = null, planLoading = false, diaMode = false, shuntSec = {};
    // 後運用を差し替えると /diagram の turnVersion が増えるので、計画の線（つなぎ線）を読み直す。読み終わるまでは前の線のまま
    let planVer = null, wantVer = null;
    async function loadPlan() {
      if ((plan && planVer === wantVer) || planLoading || !api) return;
      planLoading = true;
      const ver = wantVer;
      try {
        await GokkoClock.ready;
        const res = await api("GET", "/diagram/plan");
        shuntSec = res.shuntSeconds || {};
        plan = res.trains.map((t) => ({ ...t, direction: t.stops[t.stops.length - 1].y < t.stops[0].y ? "上り" : "下り" }));
        planVer = ver;
      } catch (e) { /* 読めなければ次の更新でまた読む */ }
      planLoading = false;
      if (plan && diagram) { box.dataset.html = ""; render(diagram); }
    }
    const isDefault = (v, def) => Object.keys(def).every((k) => v[k] === def[k]);
    let diagram = null;
    let dragging = null;
    let shown = "全部";
    try { shown = localStorage.getItem("gokkoDiagramFilter") || "全部"; } catch (e) {}
    box.style.overflowX = "auto";

    const clamp = (k, v) => Math.round(Math.max(LIMITS[k][0], Math.min(LIMITS[k][1], v)));
    function save() { try { localStorage.setItem("gokkoDiagramView2", JSON.stringify(view)); } catch (e) {} }
    // 描き直し。終日表示では、まん中に見えていた時刻がまん中のままになるようにスクロールを合わせる（拡大・縮小しても見ている所がずれない）
    function redraw() {
      let keep = null;
      if (lastL && lastL.day && dayOn() && !jumpNow) keep = lastL.t(box.scrollLeft + box.clientWidth / 2); // 前に描いたときの配置で読む
      save(); drawFilter(); box.dataset.html = ""; render(diagram);
      if (keep != null && diagram) box.scrollLeft = layout(diagram).x(keep) - box.clientWidth / 2;
    }
    // 今の時刻を、見えている幅の左から 1/4 あたりに出す
    function scrollToNow() {
      if (!diagram) return;
      const L = layout(diagram);
      box.scrollLeft = Math.max(0, L.x(L.now) - (box.clientWidth - L.left) / 4 - L.left);
    }

    const step = (k, label, unit) => `<span class="dg-set" title="${label}">${label}
        <button data-dg-step="${k}" data-dg-dir="-1">−</button><b>${Math.round(cur()[k])}</b>${unit}<button data-dg-step="${k}" data-dg-dir="1">＋</button></span>`;
    // ボタンは、列車・表示・時間・大きさ・計画の線のグループに分けて、枠で囲んで並べる
    const group = (label, body) => `<span style="display:inline-flex; flex-wrap:wrap; align-items:center; gap:4px; border:1px solid var(--line); border-radius:8px; padding:3px 8px">
        <span style="font-size:11px; color:var(--muted); margin-right:2px">${label}</span>${body}</span>`;
    const toggle = (data, on, text, title) => `<button ${data} class="${on ? "primary" : ""}" title="${title}">${text}</button>`;
    function drawFilter() {
      if (!filter) return;
      filter.innerHTML = [
        group("列車", ["全部", "上りのみ", "下りのみ"].map((f) => toggle(`data-filter="${f}"`, f === shown, f, `${f}の列車を出す`)).join("")),
        group("表示", toggle(`data-dg-detail="1"`, view.split, "詳細表示", "番線ごとの段に分け、番線の競合を見る")),
        group("時間", `<button data-dg-now="1" title="今の時刻までスクロールする（3時〜26時を並べた図。図の何もない所をつかんで横に動かせる）">現在時刻</button>`),
        group("大きさ", `<button data-dg-zoom="1.25" title="1分の幅・駅の間隔${view.split ? "・番線の段" : ""}をまとめて大きく（Ctrl+ホイールでも）">拡大</button>
          <button data-dg-zoom="0.8" title="まとめて小さく">縮小</button>
          ${step("minPx", "1分の幅", "px")} ${step("stationPx", "駅の間隔", "px")} ${view.split ? step("trackPx", "番線の段", "px") : ""}
          <button data-dg-reset="1" title="大きさの設定を最初に戻す">元に戻す</button>`),
        readOnly ? group("内容", toggle(`data-dg-simple="0"`, !simpleOn(), "実績と見込み", "走っている列車の実績（実線）と見込み（点線）、時刻表の計画の線を出す")
          + toggle(`data-dg-simple="1"`, simpleOn(), "時刻表だけ", "実績を出さず、時刻表の計画の線だけを引く")) : "",
        (diaMode || readOnly) && api && !simpleOn() ? group("計画", toggle(`data-dg-plan="1"`, planOn(), "計画の線", "時刻表にあって、いま走っていない列車の計画の線を薄く出す")
          + (planOn() ? toggle(`data-dg-dash="1"`, dashOn(), "つなぎ線を点線", "折り返しの運行どうしをつなぐ線を点線にする（重なった線を見分けやすく）") : ""))
          : simpleOn() ? group("計画", toggle(`data-dg-dash="1"`, dashOn(), "つなぎ線を点線", "折り返しの運行どうしをつなぐ線を点線にする（重なった線を見分けやすく）")) : ""
      ].join("");
    }
    if (filter) {
      filter.addEventListener("click", (ev) => {
        const b = ev.target.closest("button");
        if (!b) return;
        const v = cur();
        if (b.dataset.filter) {
          shown = b.dataset.filter;
          try { localStorage.setItem("gokkoDiagramFilter", shown); } catch (e) {}
        } else if (b.dataset.dgStep) {
          const k = b.dataset.dgStep, dir = Number(b.dataset.dgDir);
          const inc = k === "pastMin" || k === "futureMin" ? 5 : Math.max(1, Math.round(v[k] * 0.15));
          v[k] = clamp(k, v[k] + dir * inc);
        } else if (b.dataset.dgZoom) zoom(Number(b.dataset.dgZoom));
        else if (b.dataset.dgReset) view[view.split ? "d" : "n"] = { ...DEFAULTS[view.split ? "d" : "n"], plan: v.plan, day: v.day, dash: v.dash, simple: v.simple };
        else if (b.dataset.dgPlan) v.plan = !planOn();
        else if (b.dataset.dgDash) v.dash = !dashOn();
        else if (b.dataset.dgSimple) v.simple = b.dataset.dgSimple === "1";
        else if (b.dataset.dgDay) { v.day = !dayOn(); jumpNow = v.day; }
        else if (b.dataset.dgNow) { scrollToNow(); return; }
        else if (b.dataset.dgDetail) { view.split = !view.split; jumpNow = dayOn(); }
        else return;
        redraw();
      });
    }
    function zoom(f) {
      const v = cur();
      for (const k of ["minPx", "stationPx", "trackPx"]) v[k] = clamp(k, v[k] * f);
    }
    box.addEventListener("wheel", (ev) => {
      if (!ev.ctrlKey) return;
      ev.preventDefault();
      zoom(ev.deltaY < 0 ? 1.1 : 1 / 1.1);
      redraw();
    }, { passive: false });
    drawFilter();

    // 縦の並び。駅ごとに、名前の線（y）と、番線の段（rows: 番線 → y）、上端・下端（駅間の線をつなぐところ）
    function layout(d) {
      const stations = [...d.stations].sort((a, b) => a.y - b.y);
      const c = cur();
      const left = view.split ? 132 : 92;
      const rowsOf = (s) => (view.split && s.control && s.tracks && s.tracks.length > 0 ? s.tracks : []);
      const band = {};
      // 単線区間（藤江〜水越など）の駅と駅の間は、駅間のスペースを空ける（番線の段が続いて見にくくならないように。列番もここに書く）
      const zoneOf = new Map(stations.map((s) => [s.id, s.y]));
      const singles = (d.sections || []).map((x) => [zoneOf.get(x.a), zoneOf.get(x.b)])
        .filter(([a, b]) => a != null && b != null).map(([a, b]) => [Math.min(a, b), Math.max(a, b)]);
      const singleGap = (z1, z2) => singles.some(([a, b]) => a <= z1 && z2 <= b);
      const extra = view.split ? 3 * c.trackPx : c.stationPx;
      let y = TOP, prev = null;
      for (const s of stations) {
        if (prev && singleGap(prev.y, s.y)) y += extra;
        prev = s;
        const tracks = rowsOf(s);
        const rows = {};
        // 番線の段がある駅（詳細表示）: 段だけを並べ、駅名は段の真ん中の横に書く（駅の線は引かない）
        tracks.forEach((t, i) => { rows[t] = y + i * c.trackPx; });
        const bottom = tracks.length ? y + (tracks.length - 1) * c.trackPx : y;
        band[s.y] = { s, y: tracks.length ? (y + bottom) / 2 : y, rows, top: y, bottom };
        y = bottom + c.stationPx;
      }
      const height = y - c.stationPx + BOTTOM;
      // 駅と駅の間（駅の線・番線の段にかからない所）。上の駅のいちばん下の段から、下の駅の線まで。
      // 通常表示は駅の間隔が狭いので、進路を扱わない駅（細い線）はまたいでよいことにし、扱う駅と扱う駅の間で数える
      const ordered = stations.map((s) => band[s.y])
        .filter((b, i, all) => view.split || b.s.control || i === 0 || i === all.length - 1);
      const pad = (b) => (Object.keys(b.rows).length ? c.trackPx / 2 : 0); // 番線の段のある駅は、段を囲む帯の外から
      const gaps = ordered.slice(1).map((b, i) => [ordered[i].bottom + pad(ordered[i]), b.top - pad(b)]);
      const zones = stations.map((s) => s.y);
      const now = new Date(d.now).getTime();
      const day = dayOn(), sp = GokkoClock.speed(), minPx = c.minPx; // あとで設定が変わっても、この配置の幅のまま
      // 終日表示の左端: 今の運用日の 3時（3時前なら前の日の 3時。26時までが1日）
      const since = ((GokkoClock.daySec(now) - DAY_FROM) % 86400 + 86400) % 86400;
      const t0 = day ? now - since / sp * 1000 : now - c.pastMin * 60000;
      const t1 = day ? t0 + DAY_LEN / sp * 1000 : now + c.futureMin * 60000;
      const w = left + (t1 - t0) / 60000 * c.minPx + RIGHT;
      // 時刻表の時刻（ロープレ時刻のその日の秒）→ 実際の時刻。終日表示はその運用日の中（3時前は 24時過ぎ）、ふだんは今にいちばん近いその時刻
      const gameMs = (sec) => {
        if (!day) return GokkoClock.msFromDaySec(sec);
        const s = sec % 86400 < DAY_FROM ? sec % 86400 + 86400 : sec;
        return t0 + (s - DAY_FROM) / sp * 1000;
      };
      // ゾーンと番線から縦の位置。駅ちょうどなら番線の段（番線が分からなければ駅の線）、駅間なら前後の駅の端をつなぐ
      function yOf(z, track) {
        const b = band[z];
        if (b) return track != null && b.rows[String(track)] != null ? b.rows[String(track)] : b.y;
        let i = zones.findIndex((x) => x > z);
        if (i <= 0) return band[zones[i < 0 ? zones.length - 1 : 0]].y;
        const a = band[zones[i - 1]], c = band[zones[i]];
        return a.bottom + (z - zones[i - 1]) / (zones[i] - zones[i - 1]) * (c.top - a.bottom);
      }
      return {
        stations, band, gaps, left, w, height, now, t0, t1, y: yOf, minPx: c.minPx, day, gameMs,
        fit: !day && !view.split && isDefault(view.n, DEFAULTS.n), // 通常表示を元のままにしているときは、画面の幅に合わせる
        x: (t) => left + (new Date(t).getTime() - t0) / 60000 * minPx,
        t: (x) => t0 + (x - left) / minPx * 60000
      };
    }

    const visible = (tr) => shown === "全部" || (shown === "上りのみ" ? tr.direction === "上り" : tr.direction === "下り");

    // 列車ごとの、駅にいる時間（着〜発。通過は一瞬）と、その駅の番線
    function occupancy(d) {
      const list = [];
      for (const tr of d.trains) {
        const tracks = trackMap(tr);
        const pts = [...tr.history, ...tr.forecast].map((p) => ({ t: new Date(p.t).getTime(), y: p.y, track: p.row ?? tracks[p.y] }));
        let cur = null;
        for (const p of pts) {
          const atStation = Number.isInteger(p.y) && p.y % 2 === 0;
          if (cur && cur.y === p.y && cur.track === p.track) { cur.end = Math.max(cur.end, p.t); cur.start = Math.min(cur.start, p.t); continue; }
          if (cur) list.push(cur);
          cur = atStation ? { train: tr.train, tr, y: p.y, track: p.track, start: p.t, end: p.t } : null;
        }
        if (cur) list.push(cur);
      }
      return list.filter((o) => o.track != null);
    }

    // 同じ駅・同じ番線に、時間の重なる2本（番線の競合）
    function conflicts(d) {
      const occ = occupancy(d);
      const out = [];
      for (let i = 0; i < occ.length; i++)
        for (let j = i + 1; j < occ.length; j++) {
          const a = occ[i], b = occ[j];
          if (a.train === b.train || a.y !== b.y || a.track !== b.track) continue;
          const start = Math.max(a.start, b.start), end = Math.min(a.end, b.end);
          if (start <= end) out.push({ y: a.y, track: a.track, trains: [a.train, b.train], start, end });
        }
      return out;
    }

    // 段の名前: 数字なら「N番線」、入換線などは「X線」
    const rowName = (t) => (/^\d+$/.test(String(t)) ? `${t}番線` : `${t}線`);

    function trackMap(tr) {
      const m = {};
      for (const x of tr.tracks || []) m[x.y] = String(x.track);
      for (const x of tr.markers) if (x.track != null) m[x.y] = String(x.track);
      return m;
    }

    // 計画の線: 時刻表にあって、いま走っていない列車（途中で列番が変わる列車は、変わる前の列番も走り終えたものとする）。
    // 細く薄い実線で、走っている列車の線・◆・競合の帯の下に描く。押したりはできない。
    // 走っている列車がこのあと受け持つ列車（途中で変わったあとの列番・折り返したあとの運行）と、そこまでのつなぎ線は、少し濃く描く
    const gameSec = (v) => {
      const m = /^(\d{1,2}):(\d{2})(?::(\d{2}))?$/.exec(v || "");
      return m ? Number(m[1]) * 3600 + Number(m[2]) * 60 + Number(m[3] || 0) : null;
    };
    function planLines(d, L) {
      const runBy = new Map(d.trains.map((t) => [t.train, t]));
      const running = new Set(runBy.keys());
      for (const t of plan) if (t.next && runBy.has(t.next)) running.add(t.train);
      const byNo = new Map(plan.map((t) => [t.train, t]));
      // 走っている列車のこのあと（まだ走っていないもの）
      const own = new Set();
      for (const name of runBy.keys()) {
        const t = byNo.get(name);
        for (const n of t ? [t.next, t.turn && t.turn.number] : []) if (n && byNo.has(n) && !running.has(n)) own.add(n);
      }
      const clip = `clip-path="url(#dg-plan-clip)" pointer-events="none"`;
      const out = [`<defs><clipPath id="dg-plan-clip"><rect x="${L.left}" y="0" width="${L.w - RIGHT - L.left}" height="${L.height}"/></clipPath></defs>`,
        `<g ${clip} opacity="0.32">`];
      const outOwn = [`<g ${clip} opacity="0.65">`];
      for (const t of plan) {
        if (running.has(t.train) || !visible(t)) continue;
        const pts = [];
        // 途中の駅で列番が変わる列車（1178 → 回1178 など）は、その駅の停車を次の列番の線だけに引く（二重にならないように）
        const nx = t.next && byNo.get(t.next), last = t.stops[t.stops.length - 1];
        const handOver = nx && nx.stops[0].y === last.y;
        for (const s of t.stops)
          for (const v of handOver && s === last ? [s.arr || s.dep] : s.arr && s.dep && s.arr !== s.dep ? [s.arr, s.dep] : [s.dep || s.arr]) {
            const sec = gameSec(v);
            if (sec != null) pts.push({ t: L.gameMs(sec), y: L.y(s.y, s.track) });
          }
        // 時刻は今からいちばん近いその時刻に置く（今から12時間ほど離れた列車だけ線が切れるが、画面には出ない）
        if (pts.length < 2 || pts[pts.length - 1].t < L.t0 || pts[0].t > L.t1) continue;
        planLine(own.has(t.train) ? outOwn : out, pts, TYPE_COLORS[t.type] || "var(--text)", t.train, L);
      }
      // 運行のつなぎ: 終点に着いてから、折り返して次の列車として出るまで、その番線の上を横線でつなぐ。
      // 入換で番線が変わるときは、入換の時刻で次の番線に移る。折り返したあとの列車がもう走っていれば引かない（路線の外で折り返す列車は、つなげない）。
      // 走っている列車からのつなぎは、終点に着く見込み（遅れていればその分あと。もう着いていれば今）から引く
      const links = [];
      for (const a of plan) {
        const b = a.turn && byNo.get(a.turn.number);
        if (!b) continue;
        const end = a.stops[a.stops.length - 1], start = b.stops[0];
        if (end.y !== start.y) continue;
        const ta = gameSec(end.arr || end.dep), tb = gameSec(start.dep || start.arr);
        if (ta == null || tb == null) continue;
        let x0 = L.gameMs(ta);
        const x1 = L.gameMs(tb), ra = runBy.get(a.train);
        if (ra) {
          const fl = ra.forecast[ra.forecast.length - 1];
          x0 = Math.max(x0, fl && fl.y === end.y ? new Date(fl.t).getTime() : L.now);
        }
        if (x1 < x0) continue;
        const turnSec = a.turn.how === "入換" ? gameSec(a.turn.time) : null;
        const xm = turnSec != null ? Math.min(x1, Math.max(x0, L.gameMs(turnSec))) : x1;
        links.push({ a, b, end, start, x0, x1, xm, via: turnSec != null ? a.turn.via : null });
      }
      // 引上線を通る入換（新野崎の5番線、大道寺のY線など）: 着いてから1分ほどで引上線へ。ただし引上線に前の列車がいれば、その列車が出たら。
      // 時刻表の入換の時刻は、引上線から出る番線へ入る時刻
      // 入換は、かかる時間（Stations.json の shuntSeconds。書いていなければ 75 秒）で斜めに引く。入換の時刻はその動き出し
      const sp = GokkoClock.speed(), minute = 60000 / sp, lastLeave = new Map();
      const move = (y, from, to) => ((shuntSec[y] || {})[`${from}>${to}`] ?? 75) * 1000 / sp;
      for (const k of links) {
        k.dOut = k.via != null ? move(k.end.y, k.end.track, k.via) : 0;
        k.dIn = move(k.end.y, k.via ?? k.end.track, k.start.track);
      }
      for (const k of links.filter((k) => k.via != null).sort((p, q) => p.x0 - q.x0)) {
        const key = `${k.end.y}:${k.via}`;
        k.xp = Math.max(k.x0 + minute, lastLeave.get(key) ?? -Infinity);
        k.xm = Math.max(k.xm, k.xp + k.dOut);
        lastLeave.set(key, k.xm + k.dIn); // 出る番線に入りきったら、引上線は空く
      }
      for (const { a, b, end, start, x0, x1, xm, xp, via, dOut, dIn } of links) {
        const ra = runBy.get(a.train);
        if ((running.has(a.train) && !ra) || running.has(b.train) || (!visible(a) && !visible(b)) || x1 < L.t0 || x0 > L.t1) continue;
        const ya = L.y(end.y, end.track), yb = L.y(start.y, start.track);
        const here = ra && ra.forecast[0];
        let pts;
        if (via != null && here && here.y === end.y && here.row != null && String(here.row) === String(via)) {
          // もう引上線に入っている: 今から、引上線 → 出る番線
          const yv = L.y(end.y, via), xi = Math.min(Math.max(xm, L.now), x1);
          pts = [{ t: L.now, y: yv }, { t: xi, y: yv }, { t: Math.min(xi + dIn, x1), y: yb }, { t: x1, y: yb }];
        } else if (via != null) {
          const yv = L.y(end.y, via), xi = Math.min(xm, x1), xe = Math.min(xi + dIn, x1);
          pts = [{ t: x0, y: ya }, { t: xp, y: ya }, { t: Math.min(xp + dOut, xi), y: yv }, { t: xi, y: yv }, { t: xe, y: yb }, { t: x1, y: yb }];
        } else if (ya !== yb) pts = [{ t: x0, y: ya }, { t: xm, y: ya }, { t: Math.min(xm + dIn, x1), y: yb }, { t: x1, y: yb }];
        else pts = [{ t: x0, y: ya }, { t: x1, y: yb }];
        (ra ? outOwn : out).push(`<polyline points="${pts.map((p) => `${L.x(p.t).toFixed(1)},${p.y.toFixed(1)}`).join(" ")}" fill="none" stroke="${TYPE_COLORS[a.type] || "var(--text)"}" stroke-width="1.6"${dashOn() ? ` stroke-dasharray="3 3"` : ""}/>`);
      }
      out.push(`</g>`, ...outOwn, `</g>`);
      return out.join("");
    }
    function planLine(out, pts, color, name, L) {
      out.push(`<polyline points="${pts.map((p) => `${L.x(p.t).toFixed(1)},${p.y.toFixed(1)}`).join(" ")}" fill="none" stroke="${color}" stroke-width="1.1"/>`);
      // 列番は、見えている範囲のいちばん左の点のそば
      const first = pts.find((p) => p.t >= L.t0 && p.t <= L.t1);
      if (first) out.push(`<text x="${L.x(first.t) + 4}" y="${first.y - 3}" font-size="9" fill="${color}">${esc(name)}</text>`);
    }

    function render(d) {
      if (!d) return;
      diagram = d;
      if (!!d.diaMode !== diaMode) { diaMode = !!d.diaMode; drawFilter(); }
      wantVer = d.turnVersion ?? null;
      if (planWanted()) loadPlan();
      if (box.isConnected) updatePopup(d); // ◆の窓（別ウィンドウ）も最新に
      if (dragging) return; // ずらしている間は描き直さない
      const L = layout(d);
      // 終日表示では、駅名の列を左に固定した別の図にする（横にスクロールしても駅名が見えるように）
      const svgOpen = `<svg data-dg-main="1" viewBox="0 0 ${L.w} ${L.height}" ${L.fit ? `width="100%"` : `width="${L.w}" height="${L.height}"`} style="display:block${L.day ? `; flex:none; margin-left:-${L.left}px` : ""}">`;
      const parts = L.day ? [`<div style="display:flex; width:max-content">`] : [svgOpen];
      const labels = [];
      for (const s of L.stations) {
        const b = L.band[s.y];
        const rowTracks = Object.keys(b.rows);
        if (rowTracks.length) {
          // 駅の構内: 番線の段を囲む薄い帯（駅間と見分けられるように）と、段ごとの線。駅名は段の真ん中の横
          const pad = cur().trackPx / 2;
          parts.push(`<rect x="${L.left}" y="${b.top - pad}" width="${L.w - RIGHT - L.left}" height="${b.bottom - b.top + 2 * pad}" fill="var(--line)" opacity="0.18" pointer-events="none"/>`);
          labels.push(`<text x="${L.left - 46}" y="${b.y + 4}" text-anchor="end" font-size="11" fill="var(--text)" font-weight="600">${esc(s.name)}</text>`);
          labels.push(`<line x1="${L.left - 42}" x2="${L.left - 42}" y1="${b.top - pad + 2}" y2="${b.bottom + pad - 2}" stroke="var(--muted)" stroke-width="1"/>`);
        } else {
          parts.push(`<line x1="${L.left}" x2="${L.w - RIGHT}" y1="${b.y}" y2="${b.y}" stroke="var(--line)" stroke-width="${s.control ? 1.6 : 0.6}"/>`);
          labels.push(`<text x="${L.left - 6}" y="${b.y + 4}" text-anchor="end" font-size="11" fill="${s.control ? "var(--text)" : "var(--muted)"}" font-weight="${s.control ? 600 : 400}">${esc(s.name)}</text>`);
        }
        for (const t of rowTracks) {
          const ry = b.rows[t];
          parts.push(`<line x1="${L.left}" x2="${L.w - RIGHT}" y1="${ry}" y2="${ry}" stroke="var(--line)" stroke-width="0.8"/>`);
          labels.push(`<text x="${L.left - 6}" y="${ry + 3.5}" text-anchor="end" font-size="10" fill="var(--muted)">${esc(rowName(t))}</text>`);
        }
      }
      if (L.day) {
        const lines = parts.splice(1);
        parts.push(`<svg width="${L.left}" height="${L.height}" style="display:block; flex:none; position:sticky; left:0; z-index:1; background:var(--panel, #fff)">${labels.join("")}</svg>`, svgOpen, ...lines);
      } else parts.push(...labels);
      const every = L.minPx >= 40 ? 1 : L.minPx >= 10 ? 5 : 10; // 時刻を書く間隔（分）
      // 目盛りはロープレ時刻のちょうどの分に引く（横軸は実際の時刻なので、ロープレ時刻の1分ぶんの間隔で）
      const g0 = GokkoClock.daySec(L.t0), step = 60000 / GokkoClock.speed();
      const first = L.t0 + (Math.ceil(g0 / 60 - 1e-9) * 60 - g0) / GokkoClock.speed() * 1000;
      for (let t = first; t <= L.t1; t += step) {
        const x = L.x(t), min = Math.round(GokkoClock.daySec(t) / 60) % 60, five = min % 5 === 0;
        if (L.minPx < 8 && !five) continue;
        parts.push(`<line x1="${x}" x2="${x}" y1="${TOP - 6}" y2="${L.height - 18}" stroke="var(--line)" stroke-width="${five ? 1 : 0.4}"/>`);
        if (min % every === 0) parts.push(`<text x="${x}" y="${L.height - 4}" text-anchor="middle" font-size="10" fill="var(--muted)">${hhmm(t)}</text>`);
      }
      const nx = L.x(L.now);
      parts.push(`<line x1="${nx}" x2="${nx}" y1="${TOP - 10}" y2="${L.height - 18}" stroke="var(--danger)" stroke-width="1.2"/>`);
      // 時刻表だけのときは、走っている列車がいないものとして、全部の列車の計画の線を引く
      const simple = simpleOn();
      if (planWanted() && plan) parts.push(planLines(simple ? { ...d, trains: [] } : d, L));

      // 番線の競合（詳細表示のとき、その番線の段に赤い枠つきの帯。短い重なりでも見えるように前後を少し広げ、線と◆の上に描く）
      const found = view.split && !simple ? conflicts(d).filter((c) => c.end >= L.t0) : [];
      const marks = [];
      for (const c of found) {
        if (c.start > L.t1) continue;
        const x1 = Math.max(L.left, L.x(c.start) - 8), x2 = Math.min(L.w - RIGHT, L.x(c.end) + 8);
        const cy = L.y(c.y, c.track), h = Math.max(10, cur().trackPx);
        marks.push(`<rect x="${x1}" y="${cy - h / 2}" width="${Math.max(4, x2 - x1)}" height="${h}" rx="3" fill="rgba(229,57,53,.25)" stroke="#e53935" stroke-width="1.5" pointer-events="none"/>`);
      }

      const obstacles = [], labelQueue = []; // 列番を置くときに避けるもの（◆・ほかの列番）と、置く列番
      d.trains.forEach((tr, ti) => {
        if (simple || !visible(tr)) return;
        const color = TYPE_COLORS[tr.type] || "var(--text)";
        const tracks = trackMap(tr);
        const pts = (arr) => arr.map((p) => `${L.x(p.t).toFixed(1)},${L.y(p.y, p.row ?? tracks[p.y]).toFixed(1)}`).join(" ");
        const { hist, fc } = smoothBetween(tr);
        if (hist.length > 1) parts.push(`<polyline points="${pts(hist)}" fill="none" stroke="${color}" stroke-width="2"/>`);
        if (fc.length > 1) parts.push(`<polyline points="${pts(fc)}" fill="none" stroke="${color}" stroke-width="1.6" stroke-dasharray="5 4"/>`);
        const cur = fc[0];
        labelQueue.push({
          train: tr.train, color,
          at: { x: L.x(cur.t), y: L.y(cur.y, cur.row ?? tracks[cur.y]) },
          pts: [...hist, ...fc].map((p) => ({ x: L.x(p.t), y: L.y(p.y, p.row ?? tracks[p.y]) }))
        });
        tr.markers.forEach((m, mi) => {
          const x = L.x(m.t), y = L.y(m.y, m.track);
          const fill = m.held ? "#c62828" : !m.editable ? "#999" : m.departAt ? "#e0a800" : "#3aa0d8";
          const dep = m.departAt ? `・発車時刻 ${hhmm(new Date(m.departAt).getTime(), true)}` : "";
          const tipText = `${tr.train} ${m.name}: ${m.track == null ? "通過" : m.track + "番線"}（${m.trackSource}）・${m.source} ${hhmm(new Date(m.t).getTime(), true)}${dep}（${m.status}）`
            + "　押すと別ウィンドウで番線・停車／通過・駅扱い・抑止・順序" + (m.draggable ? "　横にずらすと発車時刻（ダブルクリックで解除）" : "") + (view.split && m.changeable ? "　縦にずらすと番線" : "");
          obstacles.push({ x1: x, y1: y, x2: x, y2: y, r: 3.5 }); // 小さい◆の大きさ（線から少し離した列番なら、◆の横を通れる）
          if (readOnly) return; // 読むだけの図は◆を出さない
          // ◆の窓（別ウィンドウ）で選んでいる◆は、丸で囲む
          if (popupOpen() && popup.train === tr.train && popup.stationId === m.stationId)
            parts.push(`<circle cx="${x}" cy="${y}" r="8" fill="none" stroke="var(--accent, #2563eb)" stroke-width="2" pointer-events="none"/>`);
          // ◆は小さく描き、押せる所（透明な◆）は前の大きさのまま。ずらすときは g ごと動かす
          parts.push(`<g data-train="${esc(tr.train)}" data-station="${esc(m.stationId)}" data-ti="${ti}" data-mi="${mi}" data-drag="${m.draggable ? 1 : 0}"
            data-x="${x}" data-y="${y}" data-tip="${esc(tipText)}" style="cursor:${view.split && m.changeable ? "move" : m.draggable ? "ew-resize" : "pointer"}">`
            + `<path d="M${x},${y - 7} L${x + 6},${y} L${x},${y + 7} L${x - 6},${y} Z" fill="transparent"/>`
            + `<path d="M${x},${y - 4.5} L${x + 3.8},${y} L${x},${y + 4.5} L${x - 3.8},${y} Z" fill="${fill}" stroke="var(--panel, #fff)" stroke-width="0.8" pointer-events="none"/></g>`);
        });
      });
      for (const q of labelQueue) parts.push(placeLabel(q, L, obstacles));
      parts.push(...marks);
      parts.push(L.day ? `</svg></div>` : `</svg>`);
      if (!simple && !d.trains.some(visible)) parts.push(`<div class="muted">表示する列車はいません</div>`);
      if (found.length > 0) {
        const name = (y) => (L.band[y] ? L.band[y].s.name : y);
        parts.push(`<div style="color:var(--danger); margin-top:4px"><b>番線の競合</b> ` + found.map((c) =>
          `${esc(name(c.y))} ${esc(rowName(c.track))}: ${esc(c.trains.join("・"))}（${hhmm(c.start, true)}〜${hhmm(c.end, true)}）`).join("／") + `</div>`);
      }
      const html = parts.join("");
      if (box.dataset.html !== html) { box.innerHTML = html; box.dataset.html = html; }
      lastL = L;
      if (jumpNow) { jumpNow = false; if (L.day) scrollToNow(); }
    }

    // 駅間のどこにいるか分からない列車（自動運転の列車など）は、駅間のまん中（奇数のゾーンちょうど）で記録される。
    // そのままだと、駅 → 駅間のまん中 → 次の駅と段々に描かれるので、駅を出た時刻から次の駅に着く時刻まで、まっすぐにつなぐ
    function smoothBetween(tr) {
      const all = [...tr.history.map((p) => ({ ...p, h: true })), ...tr.forecast.map((p) => ({ ...p, h: false }))];
      const odd = (y) => Number.isInteger(y) && Math.abs(y % 2) === 1;
      for (let i = 0; i < all.length;) {
        if (!odd(all[i].y)) { i++; continue; }
        let j = i;
        while (j + 1 < all.length && odd(all[j + 1].y)) j++;
        const p = all[i - 1], n = all[j + 1];
        if (p && n) {
          const t0 = new Date(all[i].t).getTime(), t1 = new Date(n.t).getTime();
          for (let k = i; k <= j; k++) {
            const f = t1 > t0 ? (new Date(all[k].t).getTime() - t0) / (t1 - t0) : 0;
            all[k].y = p.y + (n.y - p.y) * f;
            if (k === i) all[k].row = p.row; // 駅を出たところは、出た番線の段から
          }
        }
        i = j + 1;
      }
      return { hist: all.filter((p) => p.h), fc: all.filter((p) => !p.h) };
    }

    // 列番の置き場所: 線に沿って斜めに、駅と駅の間（駅の線・番線の段にかからない所）。今の時刻に近い所から探し、
    // ほかの列番・◆と重ならない所に置く。どこにも置けなければ、今いる所のそばに置く
    const textW = (t) => [...t].reduce((w, ch) => w + (ch.charCodeAt(0) > 255 ? 11 : 6.8), 0);
    function segDist(a, b) {
      const d = (px, py, s) => {
        const vx = s.x2 - s.x1, vy = s.y2 - s.y1, l2 = vx * vx + vy * vy;
        const k = l2 ? Math.max(0, Math.min(1, ((px - s.x1) * vx + (py - s.y1) * vy) / l2)) : 0;
        return Math.hypot(px - (s.x1 + k * vx), py - (s.y1 + k * vy));
      };
      const cross = (p, q, r) => (q.x - p.x) * (r.y - p.y) - (q.y - p.y) * (r.x - p.x);
      const A = { x: a.x1, y: a.y1 }, B = { x: a.x2, y: a.y2 }, C = { x: b.x1, y: b.y1 }, D = { x: b.x2, y: b.y2 };
      if (cross(A, B, C) * cross(A, B, D) < 0 && cross(C, D, A) * cross(C, D, B) < 0) return 0;
      return Math.min(d(a.x1, a.y1, b), d(a.x2, a.y2, b), d(b.x1, b.y1, a), d(b.x2, b.y2, a));
    }
    function placeLabel(q, L, obstacles) {
      const half = textW(q.train) / 2 + 1, r = 6, nowX = L.x(L.now);
      const text = (x, y, ang) => `<text transform="translate(${x.toFixed(1)},${y.toFixed(1)})${ang ? ` rotate(${ang.toFixed(1)})` : ""}" text-anchor="middle" dominant-baseline="central"`
        + ` font-size="11" font-weight="600" fill="${q.color}" paint-order="stroke" stroke="var(--panel, #fff)" stroke-width="3" pointer-events="none">${esc(q.train)}</text>`;
      const cands = [];
      // ほぼ一直線に続く所（駅を通過するだけの所など）は、つないで1本のまっすぐな所とみなす
      // （通常表示は駅の間隔が狭く、駅と駅の間の1区切りだけでは列番が収まらないことが多いため）
      const src = q.pts, P = src.length ? [src[0]] : [];
      const off = (p, a, b) => {
        const vx = b.x - a.x, vy = b.y - a.y, l = Math.hypot(vx, vy);
        return l ? Math.abs((p.x - a.x) * vy - (p.y - a.y) * vx) / l : Math.hypot(p.x - a.x, p.y - a.y);
      };
      for (let e = 2, s = 0; e < src.length; e++) {
        let straight = e - s <= 40; // 長く続く所は区切る（調べる手間を抑える）
        for (let k = s + 1; straight && k < e; k++) if (off(src[k], src[s], src[e]) > 1.5) straight = false;
        if (!straight) { P.push(src[e - 1]); s = e - 1; }
      }
      if (src.length > 1) P.push(src[src.length - 1]);
      for (let i = 1; i < P.length; i++) {
        const a = P[i - 1], b = P[i];
        if (Math.max(a.x, b.x) < L.left || Math.min(a.x, b.x) > L.w - RIGHT) continue;
        const dx = b.x - a.x, dy = b.y - a.y, len = Math.hypot(dx, dy);
        if (len < 2 * half + 4) continue; // 1本のまっすぐな所に収まるときだけ
        const ux = dx / len, uy = dy / len, nx = uy, ny = -ux; // 線の向きと、線の上側
        for (let k = half + 2; k <= len - half - 2; k += 8) {
          const cx = a.x + ux * k + nx * 10, cy = a.y + uy * k + ny * 10; // 線から少し離す（線の上の◆にかからないように）
          const c = { cx, cy, x1: cx - ux * half, y1: cy - uy * half, x2: cx + ux * half, y2: cy + uy * half, ang: Math.atan2(dy, dx) * 180 / Math.PI };
          if (Math.min(c.x1, c.x2) < L.left + 2 || Math.max(c.x1, c.x2) > L.w - RIGHT - 2) continue;
          const top = Math.min(c.y1, c.y2) - r, bottom = Math.max(c.y1, c.y2) + r;
          const inGap = L.gaps.some(([g1, g2]) => top >= g1 + 1 && bottom <= g2 - 1);
          // 通常表示は駅の間隔が狭く、斜めの列番が駅の間に収まらないことが多いので、駅の線にかかる所にも置く（駅の間に収まる所を先に）
          if (!inGap && view.split) continue;
          c.score = Math.abs(cx - nowX) + (cx < nowX ? 40 : 0) + (inGap ? 0 : 150); // 今より先（見込みの線）を少し優先。駅の線にかかる所は、近くに駅の間がなければ
          cands.push(c);
        }
      }
      cands.sort((p, q2) => p.score - q2.score);
      for (const c of cands) {
        if (obstacles.some((o) => segDist(c, o) < r + o.r)) continue;
        obstacles.push({ x1: c.x1, y1: c.y1, x2: c.x2, y2: c.y2, r });
        return text(c.cx, c.cy, c.ang);
      }
      // 置けなければ、今いる所の右上（重なるなら少しずつ上へ）
      for (let dy = 0; dy < 60; dy += 12) {
        const cx = q.at.x + 6 + half, cy = q.at.y - 9 - dy;
        const c = { x1: cx - half, y1: cy, x2: cx + half, y2: cy };
        if (dy + 12 < 60 && obstacles.some((o) => segDist(c, o) < r + o.r)) continue;
        obstacles.push({ ...c, r });
        return text(cx, cy, 0);
      }
      return "";
    }

    function svgPoint(ev) {
      const svg = box.querySelector("svg[data-dg-main]");
      const pt = svg.createSVGPoint();
      pt.x = ev.clientX; pt.y = ev.clientY;
      return pt.matrixTransform(svg.getScreenCTM().inverse());
    }

    function open(el) {
      const tr = diagram.trains[Number(el.dataset.ti)], m = tr && tr.markers[Number(el.dataset.mi)];
      if (!m) return;
      openPopup({ train: tr.train, marker: m, diagram, act, api, onClose: () => { box.dataset.html = ""; render(diagram); } });
      box.dataset.html = ""; render(diagram); // 選んだ◆に印
    }

    // ◆を縦にずらしたとき: いちばん近い番線の段と、その番線に変えられるか
    function trackTarget(d, y) {
      const L = layout(diagram), b = L.band[d.marker.y];
      if (!b) return null;
      let best = null;
      for (const [label, ry] of Object.entries(b.rows))
        if (best == null || Math.abs(ry - y) < Math.abs(best.y - y)) best = { label, y: ry };
      if (!best) return null;
      const track = /^\d+$/.test(best.label) ? Number(best.label) : null;
      const ok = d.marker.changeable && track != null && (d.marker.options || []).includes(track);
      return { ...best, track, ok, same: track === d.marker.track };
    }

    // 縦にずらしている間、その駅の段を色分けする（変えられる番線は緑、変えられない段は灰色）
    function showRows(d) {
      const L = layout(diagram), b = L.band[d.marker.y], svg = box.querySelector("svg[data-dg-main]");
      if (!b || !svg) return;
      const ns = "http://www.w3.org/2000/svg";
      d.overlay = document.createElementNS(ns, "g");
      d.overlay.setAttribute("pointer-events", "none");
      const h = Math.max(8, cur().trackPx - 2);
      for (const [label, ry] of Object.entries(b.rows)) {
        const track = /^\d+$/.test(label) ? Number(label) : null;
        const ok = track != null && (d.marker.options || []).includes(track);
        const r = document.createElementNS(ns, "rect");
        Object.entries({ x: L.left, y: ry - h / 2, width: L.w - RIGHT - L.left, height: h,
          fill: ok ? "rgba(46,160,67,.18)" : "rgba(128,128,128,.12)" }).forEach(([k, v]) => r.setAttribute(k, v));
        d.overlay.appendChild(r);
      }
      svg.insertBefore(d.overlay, svg.firstChild.nextSibling);
    }

    let panning = null;
    box.addEventListener("pointermove", (ev) => {
      if (panning) { box.scrollLeft = panning.left - (ev.clientX - panning.x); return; }
      if (dragging) {
        const d = dragging, L = layout(diagram), p = svgPoint(ev);
        // 動かし始めの向きで決める: 横 → 発車時刻（スジずらし）、縦 → 番線（詳細表示のときだけ）
        // 押したときから画面で 6px 動くまでは、押しただけ（クリック）とみなす（手ぶれで発車時刻・番線が変わらないように）
        if (!d.mode) {
          const dx = Math.abs(ev.clientX - d.cx), dy = Math.abs(ev.clientY - d.cy);
          if (Math.max(dx, dy) < 6) return;
          d.mode = dy > dx && view.split && L.band[d.marker.y] && Object.keys(L.band[d.marker.y].rows).length > 0 ? "track" : "time";
          if (d.mode === "track") showRows(d);
        }
        if (d.mode === "time") {
          if (!d.canDrag) return;
          const x = Math.max(L.left, Math.min(L.w - RIGHT, p.x));
          d.el.setAttribute("transform", `translate(${x - d.x0},0)`);
          d.x = x;
          if (tip) tip.textContent = `${d.label} → ${hhmm(L.t(x), true)} 発に変更（離すと決定）`;
        } else {
          const t = trackTarget(d, p.y);
          d.target = t;
          d.el.setAttribute("transform", `translate(0,${(t ? t.y : p.y) - d.y0})`);
          if (tip) tip.textContent = !t ? "" : t.same ? `${d.label}: ${rowName(t.label)}のまま`
            : t.ok ? `${d.label} → ${rowName(t.label)}に変更（離すと決定）`
            : !d.marker.changeable ? `${d.label}: 列車がもう進路に入っているので、番線は変えられません`
            : `${d.label}: ${rowName(t.label)}には変えられません`;
        }
      } else if (tip) {
        const m = ev.target.closest("g[data-train]");
        tip.textContent = m ? m.dataset.tip : "";
      }
    });
    box.addEventListener("pointerdown", (ev) => {
      if (ev.button !== undefined && ev.button !== 0) return; // 左ボタンだけ
      const m = ev.target.closest("g[data-train]");
      if (!m) {
        // 終日表示: 図の何もない所をつかんで横に動かす
        if (dayOn() && ev.target.closest("svg[data-dg-main]")) {
          ev.preventDefault();
          box.setPointerCapture(ev.pointerId);
          panning = { x: ev.clientX, left: box.scrollLeft };
          box.style.cursor = "grabbing";
        }
        return;
      }
      ev.preventDefault();
      if (dragging) cancelDrag();
      m.setPointerCapture(ev.pointerId);
      const tr = diagram.trains[Number(m.dataset.ti)];
      dragging = { el: m, train: m.dataset.train, station: m.dataset.station, x0: Number(m.dataset.x), y0: Number(m.dataset.y), x: Number(m.dataset.x),
                   label: m.dataset.tip.split(":")[0], canDrag: m.dataset.drag === "1", marker: tr.markers[Number(m.dataset.mi)], mode: null,
                   cx: ev.clientX, cy: ev.clientY };
    });
    // 押している途中で取り消された（タッチのスクロール・ウィンドウの切り替えなど）: 元に戻す。
    // 前はここで dragging が残り、図が描き直されなくなることがあった
    function cancelDrag() {
      const d = dragging;
      dragging = null;
      panning = null;
      box.style.cursor = "";
      if (!d) return;
      if (d.overlay) d.overlay.remove();
      d.el.removeAttribute("transform");
      if (tip) tip.textContent = "";
      box.dataset.html = "";
      render(diagram);
    }
    box.addEventListener("pointercancel", cancelDrag);
    box.addEventListener("lostpointercapture", () => { if (dragging) cancelDrag(); }); // pointerup のあとにも来るが、そのときはもう dragging がない
    box.addEventListener("pointerup", async () => {
      if (panning) { panning = null; box.style.cursor = ""; return; }
      if (!dragging) return;
      const d = dragging;
      dragging = null;
      if (d.overlay) d.overlay.remove();
      // 縦: 番線を変える（変えられない段で離したら元に戻す）
      if (d.mode === "track") {
        if (d.target && d.target.ok && !d.target.same) {
          box.dataset.html = "";
          await act("POST", "/route/track", { train: d.train, station: d.station, track: d.target.track });
        } else {
          d.el.removeAttribute("transform");
          box.dataset.html = "";
          render(diagram);
        }
        return;
      }
      // 動かさずに離したら、◆の窓（クリック）。ずらせない◆を横に動かしたときも窓
      if (!d.mode || !d.canDrag || Math.abs(d.x - d.x0) < 1) {
        d.el.removeAttribute("transform");
        open(d.el);
        return;
      }
      const time = new Date(layout(diagram).t(d.x)).toISOString();
      box.dataset.html = "";
      await act("POST", "/route/open-at", { train: d.train, station: d.station, time });
    });
    box.addEventListener("dblclick", (ev) => {
      const m = ev.target.closest("g[data-train]");
      if (!m || m.dataset.drag !== "1") return;
      act("POST", "/route/open-at", { train: m.dataset.train, station: m.dataset.station, time: null });
    });

    return { update: render };
  }

  // ---- 進路を取る順番（取れずに待っている場内・出発）と、進路を要求した記録 ----
  function queuePanel({ el, api, help = true }) {
    let showLog = false;
    el.addEventListener("click", async (ev) => {
      const b = ev.target.closest("button[data-log]");
      if (!b) return;
      showLog = !showLog;
      el.dataset.key = "";
      await refreshLog();
    });
    let logLines = [];
    async function refreshLog() {
      if (showLog) { try { logLines = (await api("GET", "/route/log")).lines; } catch (e) { logLines = [e.message]; } }
    }

    return {
      async update(d) {
        await refreshLog();
        const q = (d && d.queue) || [];
        const html = `<div class="row" style="margin-bottom:4px"><b>進路を取る順番</b>
            ${help ? `<span class="muted">（取れずに待っているもの。上から順に取ります）</span>` : ""}
            <button data-log="1" style="margin-left:auto">${showLog ? "記録を閉じる" : "進路の記録を見る"}</button></div>
          ${q.length === 0 ? `<div class="muted">待っている進路はありません</div>` : `<table>${q.map((x, i) =>
            `<tr><td class="muted">${i + 1}</td><td><b>${esc(x.train)}</b></td><td>${esc(x.station)}</td><td>${esc(x.kind)}</td><td class="muted">${esc(x.wait)}</td></tr>`).join("")}</table>`}
          ${showLog ? `<div style="margin-top:8px"><b>進路の記録</b> ${help ? `<span class="muted">（新しい順。サーバーの logs フォルダにも日ごとに残ります）</span>` : ""}
            <pre style="max-height:240px;overflow:auto;font-size:12px;margin:4px 0 0">${esc(logLines.join("\n"))}</pre></div>` : ""}`;
        if (el.dataset.key === html) return;
        el.dataset.key = html;
        el.innerHTML = html;
      }
    };
  }

  // 継続乗務申請（運転画面を出たあとも残している在線。5分で消える）と、出現待ちの列車
  function continuePanel({ el, act, help = true }) {
    // 誤操作を防ぐため、在線を消す・参加者を切断するのどちらも「はい／いいえ」で確かめる
    el.addEventListener("click", async (ev) => {
      const g = ev.target.closest("button[data-ghost]");
      if (g) {
        if (await ask("残している在線を消しますか？", `${g.dataset.train} の継続乗務申請（残している在線）を消します。元に戻せません`))
          await act("POST", "/continue/delete", { id: g.dataset.ghost });
        return;
      }
      const k = ev.target.closest("button[data-kick]");
      if (k && await ask("この参加者を切断しますか？", `${k.dataset.train || "列番なし"} の接続を切ります（その人のクライアントは、つなぎ直すまで切れたままになります）`))
        await act("POST", "/players/kick", { id: k.dataset.kick });
    });
    return {
      update(d) {
        const c = (d && d.continues) || [], a = (d && d.appear) || [], ps = (d && d.players) || [];
        const left = (t) => Math.max(0, Math.round((Date.parse(t) - GokkoClock.now()) / 1000));
        const until = (x) => new Date(x.until).getFullYear() > 9000 ? "消すまで残す（ダイヤ運転中）"
          : `あと${Math.floor(left(x.until) / 60)}分${left(x.until) % 60}秒`;
        const html = `<div class="row" style="margin-bottom:4px"><b>継続乗務申請</b>
            ${help ? `<span class="muted">（運転画面を出たあとも在線を残している列車。同じ番線から次のシナリオを始めると、新しいシナリオの列番で始めます。ダイヤ運転中は必ず残し、消すまで残します）</span>` : ""}</div>
          ${c.length === 0 ? `<div class="muted">継続乗務申請はありません</div>` : `<table>${c.map((x) =>
            `<tr><td><b>${esc(x.train)}</b></td><td class="muted">${esc(x.circuits.join(" "))}</td>
              <td>${hhmm(Date.parse(x.since), true)} から・${until(x)}</td>
              <td><button class="danger" data-ghost="${esc(x.id)}" data-train="${esc(x.train)}">在線を消す</button></td></tr>`).join("")}</table>`}
          <div class="row" style="margin:8px 0 4px"><b>出現待ち</b>
            ${help ? `<span class="muted">（他の列車の在線・進路の上に現れた列車、ダイヤ運転中に列車番号を手入力していない列車。サーバーの列車として扱いません）</span>` : ""}</div>
          ${a.length === 0 ? `<div class="muted">出現待ちの列車はいません</div>` : `<table>${a.map((x) =>
            `<tr><td><b>${esc(x.train)}</b></td><td class="muted">${esc(x.reason)}</td></tr>`).join("")}</table>`}
          <div class="row" style="margin:8px 0 4px"><b>つながっている参加者</b>
            ${help ? `<span class="muted">（切断すると、その人のクライアントの接続を切ります）</span>` : ""}</div>
          ${ps.length === 0 ? `<div class="muted">参加者はいません</div>` : `<table>${ps.map((p) =>
            `<tr><td><b>${esc(p.train || "（列番なし）")}</b>${p.train && !p.typed ? ` <span class="muted">（シナリオの列番）</span>` : ""}</td>
              <td class="muted">${esc(p.mode)}・${p.inGame ? esc(p.position || "位置不明") : "運転画面の外"}${p.appear ? "・出現待ち" : ""}</td>
              <td><button class="danger" data-kick="${esc(p.id)}" data-train="${esc(p.train)}">切断</button></td></tr>`).join("")}</table>`}`;
        if (el.dataset.key === html) return;
        el.dataset.key = html;
        el.innerHTML = html;
      }
    };
  }

  // ---- 閉塞信号機: 停止にしているもの・強制進行、在線の記録 ----
  // サーバーが在線から出している停止（だれの在線か・方向てこ）を見せ、選んだ閉塞信号機を強制進行にする（解除するまで続く）
  function blockSignalPanel({ el, api, act, help = true }) {
    let chosen = "", showLog = false, logLines = [], data = null;
    el.addEventListener("change", (ev) => { if (ev.target.matches("select[data-bs]")) chosen = ev.target.value; });
    el.addEventListener("click", async (ev) => {
      const b = ev.target.closest("button");
      if (!b) return;
      if (b.dataset.bsForce !== undefined) {
        const name = chosen || (el.querySelector("select[data-bs]") || {}).value;
        if (!name) return;
        const s = data && data.signals.find((x) => x.name === name);
        const why = s && s.trains.length ? `防護区間に ${s.trains.join("・")} が在線しています。` : s && s.lever ? "方向てこが逆向きです。" : "";
        if (await ask("強制進行にしますか？", `${name} に、サーバーからの停止を出さなくなります（${why}解除するまで続きます）。ゲームの列車・進路による停止は上げられません`))
          await act("POST", "/signals/force", { name, on: true });
      } else if (b.dataset.bsRelease) {
        await act("POST", "/signals/force", { name: b.dataset.bsRelease, on: false });
      } else if (b.dataset.bsSweep !== undefined) {
        const on = b.dataset.bsSweep === "1";
        if (await ask(on ? "定期解除を入れますか？" : "定期解除を切りますか？",
          on ? `停止を出していない閉塞信号機に、${data ? data.sweepSeconds : 10}秒ごとに解除（None）を送り直します`
             : "閉塞信号機に解除（None）を送り直さなくなります。ゲームとつなぎ直したときなどに、赤が残ったままになることがあります（強制進行は続きます。サーバーを再起動すると入に戻ります）"))
          await act("POST", "/signals/sweep", { on });
      } else if (b.dataset.bsLog !== undefined) {
        showLog = !showLog;
        await draw(true);
      }
    });

    async function draw(force) {
      try { data = await api("GET", "/signals/block"); } catch (e) { return; }
      if (showLog) { try { logLines = (await api("GET", "/occupancy/log")).lines; } catch (e) { logLines = [e.message]; } }
      if (!force && el.contains(document.activeElement) && document.activeElement.tagName === "SELECT") return;
      const sigs = data.signals;
      const forced = sigs.filter((s) => s.forced);
      const stopped = sigs.filter((s) => !s.forced && (s.trains.length || s.lever));
      const label = (s) => s.forced ? "（強制進行中）" : s.trains.length ? `（停止: ${s.trains.join("・")}）` : s.lever ? "（停止: 方向てこ）" : "";
      const html = `<div class="row" style="margin-bottom:4px"><b>閉塞信号機の強制進行</b>
          ${help ? `<span class="muted">（サーバーが在線から出している停止を外します）</span>` : ""}</div>
        <div class="row" style="gap:6px;margin-bottom:6px;flex-wrap:wrap">
          <span>定期解除: <b style="color:${data.sweepEnabled ? "inherit" : "#c62828"}">${data.sweepEnabled ? `入（${data.sweepSeconds}秒ごと）` : "切"}</b></span>
          <button data-bs-sweep="${data.sweepEnabled ? "0" : "1"}">${data.sweepEnabled ? "切る" : "入れる"}</button>
          ${help ? `<span class="muted">（停止を出していない閉塞信号機に解除を送り直して、赤のまま残ったものを戻します。ゲームでおかしな動きが出たら切ってください）</span>` : ""}
        </div>
        <div class="row" style="gap:6px;flex-wrap:wrap">
          <select data-bs="1">${sigs.map((s) => `<option value="${esc(s.name)}"${s.name === chosen ? " selected" : ""}>${esc(s.name)}${esc(label(s))}</option>`).join("")}</select>
          <button class="danger" data-bs-force>強制進行にする</button>
        </div>
        ${forced.length === 0 ? `<div class="muted" style="margin-top:4px">強制進行にしている閉塞信号機はありません</div>` : `<table style="margin-top:4px">${forced.map((s) =>
          `<tr><td><b>${esc(s.name)}</b></td><td>${hhmm(Date.parse(s.forced), true)} から強制進行</td>
            <td class="muted">${s.trains.length ? `防護区間に ${esc(s.trains.join("・"))}` : ""}</td>
            <td><button data-bs-release="${esc(s.name)}">解除</button></td></tr>`).join("")}</table>`}
        <div class="row" style="margin:8px 0 4px"><b>停止にしている閉塞信号機</b>
          <button data-bs-log style="margin-left:auto">${showLog ? "在線の記録を閉じる" : "在線の記録を見る"}</button></div>
        ${stopped.length === 0 ? `<div class="muted">ありません</div>` : `<table>${stopped.map((s) =>
          `<tr><td><b>${esc(s.name)}</b></td><td>${s.trains.length ? esc(s.trains.join("・")) : ""}${s.lever ? `${s.trains.length ? "・" : ""}方向てこ` : ""}</td>
            <td class="muted">${esc(s.protects.join(" "))}</td></tr>`).join("")}</table>`}
        ${showLog ? `<div style="margin-top:8px"><b>在線の記録</b> ${help ? `<span class="muted">（新しい順。回路が変わったときだけ。サーバーの logs フォルダにも occupancy-日付.log で残ります）</span>` : ""}
          <pre style="max-height:240px;overflow:auto;font-size:12px;margin:4px 0 0">${esc(logLines.join("\n"))}</pre></div>` : ""}`;
      if (el.dataset.key === html) return;
      el.dataset.key = html;
      el.innerHTML = html;
    }

    return { update() { return draw(false); } };
  }

  // ---- 運転モード（フリー運転・ダイヤ運転）の切り替え。切り替えるときは「はい／いいえ」で確かめる ----
  function modePanel({ el, act, help = true }) {
    el.addEventListener("click", async (ev) => {
      const b = ev.target.closest("button[data-dia]");
      if (!b) return;
      const dia = b.dataset.dia === "1";
      if (await ask(`${dia ? "ダイヤ運転" : "フリー運転"}に切り替えますか？`,
        dia ? "ダイヤ運転中は、列車番号を手入力していない列車を出現させず、継続乗務は必ず残します" : "列車番号の手入力は要らなくなり、継続乗務は5分で消えるようになります"))
        await act("POST", "/mode", { dia });
    });
    return {
      update(d) {
        const dia = !!(d && d.diaMode);
        const html = `<div class="row" style="margin:0">
            <b>運転モード</b>
            <button data-dia="0" class="${dia ? "" : "primary"}" ${dia ? "" : "disabled"}>フリー運転</button>
            <button data-dia="1" class="${dia ? "primary" : ""}" ${dia ? "disabled" : ""}>ダイヤ運転</button>
            ${help ? `<span class="muted">ダイヤ運転中は、列車番号の手入力が要り、継続乗務は必ず残します</span>` : ""}</div>`;
        if (el.dataset.key === html) return;
        el.dataset.key = html;
        el.innerHTML = html;
      }
    };
  }

  // ---- 駅の扱い（列車扱い・集中扱い・駅扱いの切り替え） ----
  const STATION_MODES = [["train", "列車扱い"], ["central", "集中扱い"], ["local", "駅扱い"]];
  function stationModePanel({ el, act, help }) {
    el.addEventListener("change", (ev) => {
      const s = ev.target.closest("select[data-mode-station]");
      if (s) act("POST", "/route/station-mode", { station: s.dataset.modeStation, mode: s.value, central: s.value === "central" });
    });
    return {
      update(d) {
        const html = (help ? `<div class="muted" style="margin-bottom:8px">集中扱いの駅は、列車ごとに「自動開通」を押すまで自動では開通させません。駅扱いの駅は、信号制御盤で進路を取れます（盤の解放の鍵が R のときだけ）。そのときは自動の進路から切り離し、手動（駅扱いの強制進路）でないと進路を取りません。</div>` : "")
          + `<div class="row">${((d && d.stationModes) || []).map((s) => {
            const cur = s.mode || (s.central ? "central" : "train");
            return `<label style="white-space:nowrap;margin-right:10px">${esc(s.name)}: <select data-mode-station="${esc(s.id)}"${cur !== "train" ? ` style="background:${cur === "local" ? "#ffe9c2" : "#ffd6d6"}"` : ""}>${STATION_MODES.map(([m, label]) =>
              `<option value="${m}"${m === cur ? " selected" : ""}>${label}</option>`).join("")}</select>${s.panel ? ` <span style="color:#b85c00" title="解放の鍵が R。自動の進路から切り離し、手動でないと進路を取りません">（盤で扱い中）</span>` : ""}</label>`;
          }).join("")}</div>`;
        if (el.dataset.key === html) return;
        el.dataset.key = html;
        el.innerHTML = html;
      }
    };
  }

  // ---- 自動退避設定（余裕・許容の遅れ） ----
  function passingPanel({ el, act, help }) {
    let editing = false;
    el.innerHTML = `<div class="row">
        <label>余裕(秒) <input data-p="margin" type="number" min="0" style="width:70px"></label>
        <label>許容の遅れ(秒) <input data-p="tolerance" type="number" min="0" style="width:70px"></label>
        <button data-p-apply="1">反映</button></div>`
      + (help ? `<div class="muted">後ろの特急に「余裕」より近く追いつかれる見込みなら待避させます。特急が見込みより「許容の遅れ」以上遅れたら、待たずに発車させます。</div>` : "");
    const margin = el.querySelector('[data-p="margin"]'), tolerance = el.querySelector('[data-p="tolerance"]');
    el.addEventListener("input", () => { editing = true; });
    el.querySelector("[data-p-apply]").addEventListener("click", async () => {
      await act("POST", "/route/passing", { marginSeconds: Number(margin.value), toleranceSeconds: Number(tolerance.value) });
      editing = false;
    });
    return {
      update(d) {
        if (editing || !d || !d.passing) return;
        margin.value = d.passing.marginSeconds;
        tolerance.value = d.passing.toleranceSeconds;
      }
    };
  }

  // ---- 確認の窓（「はい」「いいえ」）。はいなら true ----
  function ask(message, detail) {
    return new Promise((resolve) => {
      const back = document.createElement("div");
      back.style.cssText = "position:fixed; inset:0; background:rgba(0,0,0,.45); z-index:50; display:flex; align-items:center; justify-content:center; padding:16px";
      back.innerHTML = `<div role="dialog" aria-modal="true" style="background:var(--panel); color:var(--text); border:1px solid var(--line); border-radius:8px; padding:16px 18px; max-width:420px; width:100%; box-shadow:0 4px 16px rgba(0,0,0,.3)">
          ${detail ? `<div class="muted" style="margin-bottom:8px">${esc(detail)}</div>` : ""}
          <div style="font-weight:bold; font-size:16px; margin-bottom:14px">${esc(message)}</div>
          <div class="row" style="justify-content:center; margin:0">
            <button data-ans="yes" class="primary">はい</button><button data-ans="no">いいえ</button></div></div>`;
      const close = (ok) => { back.remove(); document.removeEventListener("keydown", onKey); resolve(ok); };
      const onKey = (ev) => { if (ev.key === "Escape") close(false); };
      back.addEventListener("click", (ev) => {
        const b = ev.target.closest("button[data-ans]");
        if (b) close(b.dataset.ans === "yes");
        else if (ev.target === back) close(false);
      });
      document.addEventListener("keydown", onKey);
      document.body.appendChild(back);
      back.querySelector('[data-ans="no"]').focus(); // うっかり Enter で実行しないように、いいえを選んでおく
    });
  }

  // ---- 手動進路構成（進路名を直接選んで、その列車のゲームに開通・取消させる） ----
  // confirm: true なら、取消のたびに「通告は完了したか？」を聞き、「はい」のときだけ実行する（開通は聞かない）
  function manualRoutePanel({ el, api, after, confirm = false, help = false }) {
    el.innerHTML = (help ? `<div class="muted" style="margin-bottom:8px">進路名を直接選んで、その列車のゲームに開通・取消させます。連動の確認も、列車が接近しているかの確認もしません（運営中に詰んだとき用の強制の操作）。取り消した駅は、自動で開き直さないように抑止になります。</div>` : "")
      + `<div class="row">
        <label>列車 <select data-mr="train"></select></label>
        <label>駅 <select data-mr="station"></select></label>
        <label>進路 <select data-mr="route"></select></label>
        <button data-mr-act="set" class="primary">開通</button>
        <button data-mr-act="cancel" class="danger">取消</button>
        <span data-mr="result" class="muted"></span></div>
      <div data-mr="game" class="muted"></div>`;
    const q = (k) => el.querySelector(`[data-mr="${k}"]`);
    let catalog = null, trains = [];

    function fill(sel, items) {
      const html = items.map((x) => `<option value="${esc(x.value)}">${esc(x.text)}</option>`).join("");
      if (sel.dataset.html === html) return;
      const cur = sel.value;
      sel.innerHTML = html; sel.dataset.html = html;
      if (cur && items.some((x) => x.value === cur)) sel.value = cur;
    }

    function draw() {
      if (!catalog) return;
      fill(q("train"), trains.map((t) => ({ value: t.train, text: `${t.train}（${t.direction}）` })));
      fill(q("station"), catalog.map((s) => ({ value: s.id, text: s.name })));
      const st = catalog.find((s) => s.id === q("station").value);
      fill(q("route"), st ? st.routes.map((r) => ({ value: r.name, text: `${r.name}（${r.from}→${r.to}・${r.kind}）` })) : []);
      const t = trains.find((x) => x.train === q("train").value);
      const mine = t ? t.gameRoutes.filter((g) => !st || g.interlock === st.interlock) : [];
      const html = t ? (mine.length === 0 ? `${esc(t.train)} のゲームに、この駅の進路はありません`
        : `${esc(t.train)} のゲームにある進路: ` + mine.map((g) =>
            `<button data-mr-cancel="${esc(g.route)}" data-mr-il="${esc(g.interlock)}" title="取消">${esc(g.route)} ×</button>`).join(" ")) : "";
      if (q("game").dataset.html !== html) { q("game").innerHTML = html; q("game").dataset.html = html; }
    }

    async function run(interlock, route, set) {
      const train = q("train").value;
      if (!train || !route) return;
      const st = catalog.find((s) => s.interlock === interlock);
      if (confirm && !set && !(await ask("通告は完了したか？", `${train} ${st ? st.name : interlock} ${route} を取消します`))) {
        q("result").textContent = "取りやめました";
        return;
      }
      try {
        const r = await api("POST", "/route/manual-route", { train, interlock, route, set });
        q("result").textContent = r.message;
        after && (await after());
      } catch (e) { q("result").textContent = e.message; }
    }

    el.addEventListener("change", (ev) => { if (ev.target.closest('[data-mr="train"], [data-mr="station"]')) draw(); });
    el.addEventListener("click", (ev) => {
      const b = ev.target.closest("button");
      if (!b || !catalog) return;
      if (b.dataset.mrCancel) { run(b.dataset.mrIl, b.dataset.mrCancel, false); return; }
      if (!b.dataset.mrAct) return;
      const st = catalog.find((s) => s.id === q("station").value);
      if (st) run(st.interlock, q("route").value, b.dataset.mrAct === "set");
    });

    return {
      async update(list) {
        trains = list || [];
        if (!catalog) { try { catalog = await api("GET", "/route/catalog"); } catch (e) { return; } }
        if (el.contains(document.activeElement) && document.activeElement.tagName === "SELECT") return;
        draw();
      }
    };
  }

  // 自動運転列車（にぎやかし。サーバーだけで走る列車）: アリ/ナシの切り替え・手で出す・一覧（管理画面と司令用管理画面で共通）。
  // showSpeed: 走っている速さを出すか（管理画面だけ）。api(method, path, body) はその画面の API（/api/admin か /api/dia）を呼ぶ
  let aiPanelSeq = 0;
  function aiTrainPanel({ el, api, showSpeed = false }) {
    const name = "aiMode" + (++aiPanelSeq);
    el.innerHTML = `<h2>自動運転列車（にぎやかし）</h2>
      <p class="muted">サーバーだけで走る列車です。進路・信号・TID（札の右上に *）・ほかの列車の信号に、ふつうの列車として出ます（ゲームの画面には映りません）。ロープレ時刻が動いている間だけ走ります。</p>
      <div class="row"><b>自動運転列車</b>
        <label><input type="radio" name="${name}" value="on"> アリ</label>
        <label><input type="radio" name="${name}" value="off"> ナシ</label>
        <span class="muted">アリにすると、ダイヤの列車を時刻に合わせて出します。出勤した行路の列車・入力された列番は出しません。ユーザーがその列番で出てきたら、自動運転の列車は消えます。</span>
      </div>
      <div class="row"><b>試運転</b>
        <span data-ai="trialNone"></span>
        <span class="muted">所定の臨時列車です。普段は「なし」（1本も出しません）。走らせる運行番号に印を付けると（いくつでも）、その試運転を、自動運転列車アリのときに時刻に合わせて出します。再起動すると「なし」に戻ります。</span>
      </div>
      <div data-ai="trial" style="margin:0 0 8px 1.5em"></div>
      <p class="muted">ダイヤ運転中に運転画面を出た列車（継続乗務の在線）は、3分たつと、その場所から自動運転の列車が引き継いで走ります（両数は運行ごとの両数）。自動運転列車アリのときは、継続乗務の在線を消したときも引き継ぎます。</p>
      <h3 style="margin:12px 0 4px">手で出す</h3>
      <div class="row">
        <input data-ai="train" placeholder="列番（例: 1651）" size="12" title="打つと、下の一覧をその列番で絞り込みます">
        <input data-ai="cars" type="number" min="1" max="10" placeholder="運行" title="空なら運行ごとの両数（表にない運行は6両）" style="width:4em"><span>両</span>
        <input data-ai="station" placeholder="出す駅（空なら次に発車する駅）" size="22">
        <button data-ai="add" class="primary">手で出す</button>
        <button data-ai="clear">全部消す</button>
      </div>
      <div data-ai="msg" style="min-height:1.4em;margin:2px 0 6px;font-weight:bold;white-space:pre-wrap"></div>
      <div class="muted" data-ai="candsHead"></div>
      <div data-ai="cands" style="max-height:280px;overflow:auto;margin-bottom:8px"></div>
      <div data-ai="carsProblems" class="muted"></div>
      <h3 style="margin:12px 0 4px">走っている自動運転列車</h3>
      <div data-ai="list"></div>`;
    const q = (k) => el.querySelector(`[data-ai="${k}"]`);
    let last = "", lastTrials = "", lastCands = "", cands = [];

    // 結果の表示: 手入力の欄のすぐ下に、次の操作をするまで出したままにする（すぐ消える通知だと読めないため）
    function setMsg(text, ok) {
      const m = q("msg");
      if (!text) { m.textContent = ""; return; }
      const d = new Date(), p = (x) => String(x).padStart(2, "0");
      m.textContent = `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}　${text}`;
      m.style.color = ok ? "var(--ok)" : "var(--danger)";
    }

    // 手で出せる列車（走っていない、これから発車する列車）の一覧。列番の欄に打った文字で絞り込む
    function showCands() {
      const f = q("train").value.trim();
      const shown = f ? cands.filter((c) => c.train.includes(f)) : cands;
      q("candsHead").textContent = cands.length === 0 ? "走らせられる列車はありません（ロープレ時刻が止まっている・これから1時間に発車する列車がない）"
        : `走らせられる列車（走っていない、これから1時間に発車する列車。早い順）${f ? `：「${f}」を含む ${shown.length}本` : `：${cands.length}本`}`;
      const html = shown.length === 0 ? (cands.length && f ? `<div class="muted">一覧にありません。列番を確かめて［手で出す］を押すと、出せない理由が出ます。</div>` : "")
        : `<table>${shown.map((c) => `<tr><td><b>${esc(c.train)}</b></td><td class="muted">${esc(c.type)}</td>`
          + `<td>${esc(c.station)} ${esc(c.dep)}発</td><td class="muted">${esc(c.dest)}行</td><td class="muted">${c.cars}両</td>`
          + `<td class="muted">${c.pending ? "自動で出る予定" : ""}</td>`
          + `<td><button class="primary" data-ai-run="${esc(c.train)}" data-ai-station="${esc(c.station)}">走らせる</button></td></tr>`).join("")}</table>`;
      if (html !== lastCands) { q("cands").innerHTML = html; lastCands = html; }
    }

    // 試運転: 運行番号ごとの印（いくつでも選べる。どれも付いていなければ「なし」）
    let trialBusy = false;
    // 運行番号（"24"・"24S"）の並べ方: 数字の順、同じ数字ならアルファベットなし→あり
    const unyoCmp = (a, b) => (parseInt(a, 10) - parseInt(b, 10)) || (a < b ? -1 : a > b ? 1 : 0);
    function showTrials(trials, on) {
      const box = q("trial");
      const key = JSON.stringify(trials);
      if (key !== lastTrials) {
        box.innerHTML = trials.length === 0 ? `<div class="muted">時刻表に試運転はありません</div>` : trials.map((t) =>
          `<div><label><input type="checkbox" data-trial="${esc(t.unyo)}"> <b>運行${esc(t.unyo)}</b> ${esc(t.trains.join("・"))}`
          + ` <span class="muted">${esc(t.from)} ${esc(t.dep || "")}発〜${esc(t.to)} ${esc(t.arr || "")}着</span></label></div>`).join("");
        lastTrials = key;
      }
      q("trialNone").textContent = on.length === 0 ? "なし" : `運行 ${[...on].map(String).sort(unyoCmp).join("・")}`;
      if (trialBusy) return; // 送っている最中は書き換えない
      for (const c of box.querySelectorAll("input[data-trial]")) c.checked = on.map(String).includes(c.dataset.trial);
    }

    async function refresh() {
      try {
        const d = await api("GET", "/ai-trains");
        for (const r of el.querySelectorAll(`input[name="${name}"]`)) r.checked = (r.value === "on") === !!d.enabled;
        const probs = d.carsProblems || [];
        q("carsProblems").innerHTML = probs.length ? `<b>両数の表（Data/UnyoCars.csv・TrainCars.csv）のおかしな所:</b> ${probs.map(esc).join("　")}` : "";
        showTrials(d.trials || [], d.trialsOn || []);
        cands = d.candidates || [];
        showCands();
        const list = d.trains || [];
        const html = list.length === 0 ? `<div class="muted">走っていません</div>`
          : `<table>${list.map((t) => `<tr><td><b>${esc(t.train)}</b>${t.auto ? ` <span class="muted">自動</span>` : ""}</td><td class="muted">${t.cars}両</td>`
            + `<td>${esc(t.where)}</td>${showSpeed ? `<td style="text-align:right">${Number(t.speedKmh).toFixed(0)}km/h</td>` : ""}`
            + `<td class="muted">${esc(t.status)}</td><td><button data-ai-remove="${esc(t.id)}">消す</button></td></tr>`).join("")}</table>`;
        if (html !== last) { q("list").innerHTML = html; last = html; }
      } catch (e) { /* 接続の失敗は画面の上の表示に出る */ }
    }

    // 送って、結果を手入力の欄の下に出す。うまくいったら true
    async function call(method, path, body) {
      setMsg("");
      let ok = false;
      try { const r = await api(method, path, body); ok = true; setMsg((r && r.message) || "", true); }
      catch (e) { setMsg(e.message || "うまくいきませんでした", false); }
      refresh();
      return ok;
    }

    q("trial").addEventListener("change", async () => {
      const unyos = [...q("trial").querySelectorAll("input[data-trial]:checked")].map((c) => c.dataset.trial);
      trialBusy = true;
      try { await call("POST", "/ai-trains/trial", { unyos }); } finally { trialBusy = false; }
      refresh();
    });
    el.addEventListener("change", async (ev) => {
      if (ev.target.name !== name) return;
      const on = ev.target.value === "on";
      if (!on && !(await ask("自動運転列車をナシにしますか？", "走っている自動運転の列車は、全部すぐに消えます。"))) { refresh(); return; }
      await call("POST", "/ai-trains/mode", { on });
    });
    // 列番の欄は、出せたときだけ空にする（出せなかったら、そのまま直して押し直せる）
    let adding = false;
    async function addTrain(train, station) {
      if (adding) return;
      adding = true;
      try {
        if (await call("POST", "/ai-trains/add", { train, cars: Number(q("cars").value) || 0, station })) {
          if (q("train").value.trim() === train) q("train").value = "";
          showCands();
        }
      } finally { adding = false; }
    }
    q("add").onclick = () => {
      const train = q("train").value.trim();
      if (!train) { setMsg("列番を入れてください", false); return; }
      addTrain(train, q("station").value.trim() || null);
    };
    q("train").addEventListener("keydown", (ev) => { if (ev.key === "Enter") q("add").click(); });
    q("train").addEventListener("input", showCands);
    q("clear").onclick = async () => {
      if (await ask("自動運転の列車を全部消しますか？", "在線もすぐ消えます（アリのままなら、時刻が来た列車はまた出ます）。")) await call("POST", "/ai-trains/remove", {});
    };
    el.addEventListener("click", async (ev) => {
      const run = ev.target.closest("button[data-ai-run]");
      if (run) { addTrain(run.dataset.aiRun, run.dataset.aiStation || null); return; } // 一覧に出ている駅に出す
      const b = ev.target.closest("button[data-ai-remove]");
      if (b) await call("POST", "/ai-trains/remove", { id: b.dataset.aiRemove });
    });

    refresh();
    setInterval(refresh, 1000);
    return { refresh };
  }

  window.GokkoDia = { openPopup, closePopup, isOpen: popupOpen, orderEditor, trainEditor, diagramView, queuePanel, continuePanel, blockSignalPanel, modePanel, stationModePanel, passingPanel, manualRoutePanel, aiTrainPanel, ask, TYPE_COLORS, esc, hhmm };
})();
