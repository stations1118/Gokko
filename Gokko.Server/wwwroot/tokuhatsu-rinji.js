// 特発臨（運転整理のために、司令員がその場で立てる臨時列車）。司令用管理画面（diasettings.html）の欄と、ダイヤグラムの◆の窓（diapanel.js）で使う。
// 走っている列車を選び、止まっている駅か、これから通る駅を発駅にしてスジ（列番・種別・行先・停車駅・時刻）を立てる。
// 時刻は標準運転時分から引いた案を、表で直してから立てる。元の列車は発駅で打ち切りになる。
// 自動運転の列車は、発駅に着いて止まったら特発臨の列番に変わって走る。ユーザーの列車は、タブレットに特発臨が出る（列番を入れ直してもらう）。
// ほかの欄から列車を選んで開くときは GokkoRinji.pick(列番)。◆の窓では「打ち切り」を選ぶと、後運用の下に出る（GokkoRinji.popupHtml / popupEvent）。
window.GokkoRinji = (() => {
  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const suffix = { 特急: "A", 急行: "B", 準急: "C", 区急: "D", 快急: "K" };
  let picker = null;

  // 列番の案: 元の列車の数字を使い、向きに合わせて最後の桁を奇数（下り）・偶数（上り）にする。回送は「臨回」、ほかは「臨」を頭に付ける
  // 出庫（元の列車がない）は、今のロープレ時刻の時分（13:47 → 1347）を使う
  function suggest(from, type, down) {
    if (!from) from = window.GokkoClock && GokkoClock.isSet() ? GokkoClock.fmt(GokkoClock.now()).replace(/\D/g, "") : "";
    const m = String(from || "").match(/(\d+)(?!.*\d)/);
    if (!m) return "";
    let d = m[1];
    const last = Number(d[d.length - 1]);
    if ((last % 2 === 1) !== down) d = d.slice(0, -1) + (last === 9 ? 8 : last + 1);
    return (type === "回送" ? "臨回" : "臨") + d + (suffix[type] || "");
  }

  const zoneOf = (data, id) => ((data && data.stations || []).find((s) => s.id === id) || {}).zone;

  // 行先の選択肢（発駅を除き、発駅から見た向きを添える）
  function destOptions(data, station, pick) {
    const hz = zoneOf(data, station);
    return [`<option value="">行先</option>`].concat((data.stations || []).filter((s) => s.id !== station).map((s) =>
      `<option value="${esc(s.id)}" ${s.id === pick ? "selected" : ""}>${esc(s.name)}${hz == null ? "" : s.zone > hz ? "（下り）" : "（上り）"}</option>`)).join("");
  }

  const startTrack = (plan) => plan.track != null ? `${plan.track}番線` : "着いた番線";
  const fromLabel = (plan) => plan.depot ? `出庫（${plan.depot}・${plan.cars}両）`
    : plan.split ? `解放（${plan.split} から${plan.cars}両を切り離す）` : `${plan.train}${plan.cars ? `（${plan.cars}両）` : ""}`;

  // スジの案の表（停車駅・時刻・番線を直せる）。attr は欄ごとの印（data-xx-）
  function planTable(plan, number, linkTurn, a, couple) {
    const n = plan.stops.length;
    return `<div style="margin:6px 0">${esc(fromLabel(plan))} → <b>${esc(number || "（列番）")}</b>（${esc(plan.type)}・${esc(plan.dir)}）
        ${esc(plan.stationName)} ${startTrack(plan)}から${plan.arriving ? `<span class="muted">（${esc(plan.train)} をこの番線に入れ、止まったら変わります）</span>` : ""}</div>
      <div style="overflow-x:auto"><table>
      <tr class="muted"><td>駅</td><td>停車</td><td>着</td><td>発・通過</td><td>番線</td></tr>
      ${plan.stops.map((s, i) => {
        const fixed = i === 0 || i === n - 1;
        return `<tr data-${a}i="${i}"><td>${esc(s.name)}</td>
          <td><label><input type="checkbox" data-${a}stop ${s.stop === "停" ? "checked" : ""} ${fixed ? "disabled" : ""}> ${s.stop === "停" ? "停" : "通"}</label></td>
          <td>${i === 0 || s.stop !== "停" ? "" : `<input data-${a}arr size="8" value="${esc(s.arr || "")}">`}</td>
          <td>${i === n - 1 ? "" : `<input data-${a}dep size="8" value="${esc(s.dep || "")}">`}</td>
          <td>${i === 0 ? startTrack(plan) : s.control && s.stop === "停" ? `<input data-${a}track size="3" value="${s.track ?? ""}" placeholder="自動">` : ""}</td></tr>`;
      }).join("")}</table></div>
      <div class="row" style="margin-top:6px">
        ${a === "" ? afterSelect(plan, linkTurn, couple)
          : plan.depot ? "" : `<label><input type="checkbox" data-${a}link ${linkTurn ? "checked" : ""}> 元の列車の後運用につなぐ（終点が同じ駅のとき）</label>`}
        <button data-${a}create class="primary">特発臨を立てる</button>
        <button data-${a}clear>やめる</button></div>`;
  }

  // 終点のあと（司令用管理画面の欄）: なし・元の列車の後運用につなぐ・連結（終点に停車し、あとから着く列車に増結する）
  function afterSelect(plan, linkTurn, couple) {
    const link = !plan.depot && !plan.split;
    return `<select data-after title="終点のあと">
      <option value="" ${!couple && !(link && linkTurn) ? "selected" : ""}>終点のあと: なし</option>
      ${link ? `<option value="link" ${!couple && linkTurn ? "selected" : ""}>終点のあと: 元の列車の後運用につなぐ（終点が同じ駅のとき）</option>` : ""}
      ${(plan.couple || []).map((c) => `<option value="c:${esc(c.train)}" ${couple === c.train ? "selected" : ""}>終点のあと: 連結 ${esc(c.train)}（${esc(c.type)}・${esc((c.time || "").slice(0, 5))}着・${c.track}番線）</option>`).join("")}
    </select>${couple ? ` <span class="muted">列番は「連${esc(couple)}」、種別は回送になります（増結車）</span>` : ""}`;
  }

  // 表で直した時刻・番線を、案に書き戻す
  function readPlan(root, plan, a) {
    if (!plan) return;
    for (const tr of root.querySelectorAll(`tr[data-${a}i]`)) {
      const s = plan.stops[Number(tr.getAttribute(`data-${a}i`))];
      const ar = tr.querySelector(`[data-${a}arr]`), d = tr.querySelector(`[data-${a}dep]`), t = tr.querySelector(`[data-${a}track]`);
      if (ar) s.arr = ar.value.trim() || null;
      if (d) s.dep = d.value.trim() || null;
      if (t) s.track = t.value.trim() === "" ? null : Number(t.value);
    }
  }

  const createBody = (plan, number, linkTurn, couple) => ({
    train: plan.split ? null : plan.train, station: plan.station, number, type: plan.type, linkTurn: !plan.depot && !plan.split && !couple && linkTurn,
    depot: plan.depot || null, split: plan.split || null, couple: couple || null, cars: plan.depot || plan.split ? plan.cars : null,
    stops: plan.stops.map((s) => ({ st: s.st, stop: s.stop, arr: s.arr, dep: s.dep, track: s.track }))
  });

  const confirmText = (plan, number, user) => plan.split
    ? `${plan.split} が ${plan.stationName} ${plan.track}番線に着いたら${plan.cars}両を切り離し、${number}（${plan.type}）として ${plan.stops[0].dep} に ${plan.stops[plan.stops.length - 1].name} へ向けて出します。`
      + `${plan.split} は残りの両数で先へ進みます。`
    : plan.depot
    ? `${plan.depot} の ${plan.track}番線から ${number}（${plan.type}・${plan.cars}両）を出庫させ、${plan.stops[0].dep} に ${plan.stops[plan.stops.length - 1].name} へ向けて出します。`
      + "自動運転の列車は、発車時刻の少し前に車庫の番線に出ます。ユーザーが運転するなら、その列番を入れてもらってください。"
    : `${plan.train} は ${plan.stationName} で打ち切りになり、${number}（${plan.type}）として ${plan.stops[0].dep} に ${plan.stops[plan.stops.length - 1].name} へ向けて出ます。`
    + (plan.arriving ? `${plan.train} が ${plan.stationName} に着いて止まったら変わります。` : "")
    + (user ? "ユーザーの列車なので、運転士に列番の入れ直しを伝えてください。" : "");

  // =========================================================================================
  // 司令用管理画面の欄
  // =========================================================================================
  function panel({ el, api, act, onMessage, help = true }) {
    let data = { candidates: [], list: [], types: [], stations: [] };
    const form = { train: "", depot: "", split: "", couple: "", cars: 6, station: "", type: "普通", number: "", numberTouched: false, dest: "", dep: "", linkTurn: true };
    let plan = null; // スジの案（表で直す）
    const msg = (m) => onMessage && onMessage(m);

    el.innerHTML = `<h2>特発臨（運転整理の臨時列車）</h2>
      <div data-list></div>
      <div class="row" style="margin-top:8px">
        <select data-f="train"></select>
        <select data-f="station" title="発駅（止まっている駅か、これから通る駅）"></select>
        <label data-cars-box style="display:none">両数 <input data-f="cars" type="number" min="1" max="10" value="6" style="width:4em"></label>
        <select data-f="type"></select>
        <select data-f="dest"></select>
        <label>列番 <input data-f="number" size="10" placeholder="臨1347"></label>
        <label>発車 <input data-f="dep" size="8" placeholder="3分後"></label>
        <button data-draft class="primary">時刻を引く</button>
      </div>
      <div data-plan></div>
      ${help ? `<div class="muted" style="margin-top:6px">走っている列車から、止まっている駅か、これから通る駅を発駅にして立てます。時刻は標準運転時分から引いた案です（表で直せます）。途中駅・終点の番線は空けておくと、自動進路構成が決めます。
        立てると、元の列車は発駅で打ち切りになり、元の列車の後運用は時刻表どおりに残ります（「元の列車の後運用につなぐ」を選ぶと、特発臨の終点からつなぎます）。
        自動運転の列車は、発駅に着いて止まったら特発臨の列番に変わって走ります（番線は着いた番線）。ユーザーの列車は、タブレットに特発臨が出るので、運転士に列番を入れ直してもらってください。
        元の列車の代わりに「出庫」を選ぶと、車庫から両数を決めて出せます。元の列車から立てたときは、元の列車の両数を引き継ぎます。
        「解放」を選ぶと、走っている列車がこの先で停車する駅で、両数を決めて切り離し、切り離した車両を特発臨として出します（「解」＋元の列番の車両が特発臨に変わります）。
        「終点のあと」で「連結」を選ぶと、終点にあとから着く列車に増結します（列番は「連」＋その列番、種別は回送）。
        ダイヤグラムの◆の窓で「打ち切り」を選んでも、その駅から立てられます。ロープレ時刻を設定するか、サーバーを再起動すると消えます。</div>` : ""}`;
    const $ = (sel) => el.querySelector(sel);
    const f = (k) => el.querySelector(`[data-f="${k}"]`);
    const cand = () => data.candidates.find((c) => c.train === form.train);

    // 発駅の選択肢: 止まっている駅、これから通る駅（近い順）
    function stationChoices(c) {
      if (!c) return [];
      return (c.here ? [{ id: c.here.id, label: `${c.here.name}（いま ${c.here.track}番線に停車中）` }] : [])
        .concat((c.ahead || []).map((s, i) => ({ id: s.id, label: `${s.name}（${i === 0 ? "次の駅" : "この先"}）` })));
    }

    const depot = () => (data.depots || []).find((d) => d.name === form.depot);
    const splitCand = () => form.split ? data.candidates.find((c) => c.train === form.split) : null;

    function fillSelects() {
      const c = cand();
      const setOpts = (sel, html) => { if (document.activeElement !== sel && sel.dataset.html !== html) { sel.innerHTML = html; sel.dataset.html = html; } };
      // 出庫（車庫から出す）は、元の列車の代わりに選ぶ。値は「@車庫の名前」
      const depotOpts = (data.depots || []).map((d) => `<option value="@${esc(d.name)}" ${d.name === form.depot ? "selected" : ""}>出庫（${esc(d.name)}）</option>`);
      // 解放（走っている列車から切り離して出す）は、値が「~元の列番」
      const splitOpts = data.candidates.filter((x) => (x.split || []).length > 0).map((x) =>
        `<option value="~${esc(x.train)}" ${x.train === form.split ? "selected" : ""}>解放: ${esc(x.train)} から切り離す</option>`);
      const trainOpts = [`<option value="">元の列車を選ぶ（または出庫・解放）</option>`].concat(depotOpts, splitOpts, data.candidates.map((x) =>
        `<option value="${esc(x.train)}" ${x.train === form.train ? "selected" : ""}>${esc(x.train)}（${x.ai ? "自動運転" : "ユーザー"}・${
          x.here ? `${esc(x.here.name)} ${x.here.track}番線に停車中` : `${esc(x.where || "")}を走行中`}${x.rinji ? `・特発臨 ${esc(x.rinji)} 待ち` : ""}）</option>`));
      if (form.train && !c && !form.depot && !form.split) trainOpts.push(`<option value="${esc(form.train)}" selected>${esc(form.train)}（いまは走っていません）</option>`);
      setOpts(f("train"), trainOpts.join(""));
      const dp = depot();
      const sc = splitCand();
      const choices = dp ? [{ id: dp.station, label: `${dp.name}（${dp.stationName}）` }]
        : sc ? (sc.split || []).map((x) => ({ id: x.id, label: `${x.name}（${x.track}番線で切り離す）` }))
        : stationChoices(c);
      if ((c || dp || sc) && !choices.some((s) => s.id === form.station)) form.station = choices.length ? choices[0].id : "";
      el.querySelector("[data-cars-box]").style.display = dp || form.split ? "" : "none";
      setOpts(f("station"), choices.length ? choices.map((s) => `<option value="${esc(s.id)}" ${s.id === form.station ? "selected" : ""}>発駅 ${esc(s.label)}</option>`).join("")
        : `<option value="">発駅</option>`);
      setOpts(f("type"), data.types.map((t) => `<option ${t === form.type ? "selected" : ""}>${esc(t)}</option>`).join(""));
      setOpts(f("dest"), destOptions(data, form.station, form.dest));
    }

    function autoNumber() {
      if (form.numberTouched || !(form.train || form.depot || form.split) || !form.dest || !form.station) return;
      form.number = suggest(form.train || form.split, form.type, zoneOf(data, form.dest) > zoneOf(data, form.station));
      f("number").value = form.number;
    }

    function drawList() {
      const html = data.list.length === 0 ? `<div class="muted">立てた特発臨はありません</div>`
        : `<table><tr class="muted"><td>列番</td><td>種別</td><td>元の列車</td><td>発</td><td>行先</td><td>後運用</td><td>状態</td><td></td></tr>${data.list.map((r) => `
          <tr><td><b>${esc(r.number)}</b></td><td>${esc(r.type)}</td><td>${esc(r.from)}</td>
            <td>${esc(r.station)} ${r.track != null ? `${r.track}番線` : "（着いた番線）"} ${esc(r.dep)}</td><td>${esc(r.dest)} ${esc(r.arr)}着</td>
            <td>${r.couple ? `連結 ${esc(r.couple)}` : r.turn ? esc(r.turn) : `<span class="muted">なし</span>`}</td>
            <td>${r.started ? `<span class="ok">${r.depot ? "出庫しました" : r.split ? "切り離して走り出しました" : "列番を変えて走り出しました"}</span>`
              : r.depot ? `<span class="muted">${r.cars}両・発車時刻の少し前に出ます</span>`
              : r.split ? `<span class="muted">${esc(r.split)} が ${esc(r.station)} で${r.cars}両を切り離すのを待っています</span>`
              : `<span class="muted">${esc(r.from)} が ${esc(r.station)} で変わるのを待っています</span>`}</td>
            <td><button data-cancel="${esc(r.number)}" class="danger">取消</button></td></tr>`).join("")}</table>`;
      const box = $("[data-list]");
      if (box.dataset.html !== html) { box.innerHTML = html; box.dataset.html = html; }
    }

    const drawPlan = () => { $("[data-plan]").innerHTML = plan ? planTable(plan, form.number, form.linkTurn, "", form.couple) : ""; };

    async function draft(stopAt) {
      try {
        plan = await api("POST", "/rinji/draft", { train: form.depot || form.split ? null : form.train, depot: form.depot || null, split: form.split || null,
          cars: form.depot || form.split ? form.cars : null,
          station: form.station, type: form.type, dest: form.dest, dep: form.dep || null, stopAt: stopAt || null });
        msg("");
      } catch (e) { plan = null; msg(e.message); }
      drawPlan();
    }

    el.addEventListener("change", (ev) => {
      const t = ev.target;
      if (t.dataset.f === "train") {
        const v = t.value;
        form.depot = v.startsWith("@") ? v.slice(1) : ""; form.split = v.startsWith("~") ? v.slice(1) : "";
        form.train = form.depot || form.split ? "" : v;
        if (form.depot || form.split) { form.cars = form.depot ? 6 : 2; f("cars").value = form.cars; }
        form.station = ""; form.dest = ""; form.couple = ""; form.numberTouched = false; plan = null; drawPlan(); fillSelects();
      }
      else if (t.dataset.f === "cars") { form.cars = Math.max(1, Math.min(10, Number(t.value) || 6)); t.value = form.cars; if (plan) plan.cars = form.cars; drawPlan(); }
      else if (t.dataset.f === "station") { form.station = t.value; form.dest = ""; plan = null; drawPlan(); fillSelects(); }
      else if (t.dataset.f === "type") { form.type = t.value; autoNumber(); if (plan) { readPlan($("[data-plan]"), plan, ""); draft(); } }
      else if (t.dataset.f === "dest") { form.dest = t.value; form.couple = ""; autoNumber(); plan = null; drawPlan(); }
      else if (t.dataset.f === "number") { form.number = t.value.trim(); form.numberTouched = form.number !== ""; drawPlan(); }
      else if (t.dataset.f === "dep") form.dep = t.value.trim();
      else if (t.dataset.link !== undefined) form.linkTurn = t.checked;
      else if (t.dataset.after !== undefined) {
        // 終点のあと: 連結なら、列番は「連」＋相手、種別は回送（時刻を回送で引き直す）
        const v = t.value;
        form.couple = v.startsWith("c:") ? v.slice(2) : "";
        form.linkTurn = v === "link";
        if (form.couple) {
          form.number = "連" + form.couple; f("number").value = form.number; form.numberTouched = true;
          if (form.type !== "回送") { form.type = "回送"; f("type").value = "回送"; readPlan($("[data-plan]"), plan, ""); draft(); return; }
        }
        drawPlan();
      }
      else if (t.dataset.stop !== undefined && plan) {
        // 停車駅を変えたら、時刻を引き直す（発車時刻は今の表のまま）
        readPlan($("[data-plan]"), plan, "");
        plan.stops[Number(t.closest("tr").dataset.i)].stop = t.checked ? "停" : "通";
        form.dep = plan.stops[0].dep || form.dep;
        f("dep").value = form.dep;
        draft(plan.stops.filter((s) => s.stop === "停").map((s) => s.st));
      }
    });

    el.addEventListener("click", async (ev) => {
      const b = ev.target.closest("button");
      if (!b) return;
      if (b.dataset.draft !== undefined) {
        form.number = f("number").value.trim(); form.dep = f("dep").value.trim();
        if (!form.train && !form.depot && !form.split) { msg("元の列車か、出庫・解放を選んでください"); return; }
        if (!form.dest) { msg("行先を選んでください"); return; }
        await draft();
      } else if (b.dataset.clear !== undefined) {
        plan = null; drawPlan();
      } else if (b.dataset.create !== undefined && plan) {
        readPlan($("[data-plan]"), plan, "");
        form.number = form.couple ? "連" + form.couple : f("number").value.trim();
        if (!form.number) { msg("列番を入れてください"); return; }
        const c = cand();
        if (!(await GokkoDia.ask(`特発臨 ${form.number} を立てますか？`, confirmText(plan, form.number, c && !c.ai)
          + (form.couple ? `終点で ${form.couple} に連結します。` : "")))) return;
        try {
          await api("POST", "/rinji", createBody(plan, form.number, form.linkTurn, form.couple));
          plan = null; form.couple = ""; form.numberTouched = false; form.number = ""; f("number").value = ""; form.dep = ""; f("dep").value = "";
          drawPlan(); msg(""); await refresh();
        } catch (e) { msg(e.message); }
      } else if (b.dataset.cancel) {
        const r = data.list.find((x) => x.number === b.dataset.cancel);
        if (!(await GokkoDia.ask(`特発臨 ${b.dataset.cancel} を取り消しますか？`,
          r && r.started ? "もう走り出しています。自動運転の列車なら消えます。" : r && r.depot ? "出庫をやめます。" : r && r.split ? "切り離しをやめます。" : `${r ? r.from : ""} の打ち切りも外し、元の時刻表に戻します。`))) return;
        await act("POST", "/rinji/cancel", { number: b.dataset.cancel });
        await refresh();
      }
    });

    async function refresh() {
      try { data = await api("GET", "/rinji"); } catch (e) { return; }
      fillSelects();
      drawList();
    }

    picker = (train) => {
      form.train = String(train || "").toUpperCase(); form.depot = ""; form.split = ""; form.couple = ""; form.station = ""; form.dest = ""; form.numberTouched = false; plan = null; drawPlan(); fillSelects();
      el.scrollIntoView({ behavior: "smooth" });
    };

    refresh();
    setInterval(refresh, 2000);
    return { refresh };
  }

  // =========================================================================================
  // ◆の窓（diapanel.js）: 「打ち切り」を選んだ駅を発駅にして立てる。後運用の下に出す
  // p.rinji にこの窓の入力を持つ。redraw() で窓を描き直す
  // =========================================================================================
  function popupState(p, train, station, redraw) {
    let st = p.rinji;
    if (!st || st.train !== train || st.station !== station) {
      st = p.rinji = { train, station, data: null, loading: false, type: "普通", dest: "", number: "", numberTouched: false, dep: "", plan: null, linkTurn: true, err: "" };
    }
    if (!st.data && !st.loading && p.api) {
      st.loading = true;
      p.api("GET", "/rinji").then((d) => { st.data = d; }).catch((e) => { st.err = e.message; }).finally(() => { st.loading = false; redraw(); });
    }
    return st;
  }

  function popupHtml(p, tr, m, redraw) {
    if (!p.api) return "";
    const st = popupState(p, tr.train, m.stationId, redraw);
    const head = `<div class="ttl">特発臨（${esc(m.name)} を発駅にして、運転整理の臨時列車を立てる）</div>`;
    const err = st.err ? `<div style="color:var(--danger)">${esc(st.err)}</div>` : "";
    if (!st.data) return `<div class="grp">${head}${err}<div class="muted">読み込み中…</div></div>`;
    const mine = (st.data.list || []).find((r) => r.from === tr.train && !r.started);
    if (mine) return `<div class="grp">${head}${err}
      <div><b>${esc(mine.number)}</b>（${esc(mine.type)}）${esc(mine.station)} ${esc(mine.dep)}発 → ${esc(mine.dest)} ${esc(mine.arr)}着
        <span class="muted">${esc(tr.train)} が ${esc(mine.station)} で変わるのを待っています</span></div>
      <div class="row" style="margin:4px 0 0"><button data-rj-cancel="${esc(mine.number)}" class="danger">特発臨を取消</button></div></div>`;
    const types = (st.data.types || []).map((t) => `<option ${t === st.type ? "selected" : ""}>${esc(t)}</option>`).join("");
    return `<div class="grp">${head}${err}
      <div class="row" style="margin:0">
        <select data-rj-f="type">${types}</select>
        <select data-rj-f="dest">${destOptions(st.data, m.stationId, st.dest)}</select>
        <input data-rj-f="number" size="9" placeholder="列番" value="${esc(st.number)}">
        <input data-rj-f="dep" size="8" placeholder="発車（3分後）" value="${esc(st.dep)}">
        <button data-rj-draft class="primary">時刻を引く</button></div>
      ${st.plan ? planTable(st.plan, st.number, st.linkTurn, "rj-") : ""}</div>`;
  }

  // ◆の窓の操作（特発臨のものなら true）。ctx: { win, redraw, refresh }
  async function popupEvent(ev, p, ctx) {
    const st = p.rinji;
    if (!st) return false;
    const root = ev.currentTarget;
    const draft = async (stopAt) => {
      try {
        st.plan = await p.api("POST", "/rinji/draft", { train: st.train, station: st.station, type: st.type, dest: st.dest, dep: st.dep || null, stopAt: stopAt || null });
        st.err = "";
      } catch (e) { st.plan = null; st.err = e.message; }
      ctx.redraw();
    };
    const auto = () => {
      if (st.numberTouched || !st.dest) return;
      st.number = suggest(st.train, st.type, zoneOf(st.data, st.dest) > zoneOf(st.data, st.station));
    };
    const t = ev.target;
    if (ev.type === "change") {
      const k = t.getAttribute && t.getAttribute("data-rj-f");
      if (k === "type") { st.type = t.value; auto(); if (st.plan) { readPlan(root, st.plan, "rj-"); await draft(); } else ctx.redraw(); return true; }
      if (k === "dest") { st.dest = t.value; st.plan = null; auto(); ctx.redraw(); return true; }
      if (k === "number") { st.number = t.value.trim(); st.numberTouched = st.number !== ""; return true; }
      if (k === "dep") { st.dep = t.value.trim(); return true; }
      if (t.hasAttribute && t.hasAttribute("data-rj-link")) { st.linkTurn = t.checked; return true; }
      if (t.hasAttribute && t.hasAttribute("data-rj-stop") && st.plan) {
        readPlan(root, st.plan, "rj-");
        st.plan.stops[Number(t.closest("tr").getAttribute("data-rj-i"))].stop = t.checked ? "停" : "通";
        st.dep = st.plan.stops[0].dep || st.dep;
        await draft(st.plan.stops.filter((s) => s.stop === "停").map((s) => s.st));
        return true;
      }
      return false;
    }
    const b = t.closest && t.closest("button");
    if (!b) return false;
    if (b.hasAttribute("data-rj-draft")) {
      const n = root.querySelector("[data-rj-f=number]"), d = root.querySelector("[data-rj-f=dep]");
      if (n) { st.number = n.value.trim(); st.numberTouched = st.numberTouched || (st.number !== "" && st.number !== suggest(st.train, st.type, zoneOf(st.data, st.dest) > zoneOf(st.data, st.station))); }
      if (d) st.dep = d.value.trim();
      if (!st.dest) { st.err = "行先を選んでください"; ctx.redraw(); return true; }
      await draft();
      return true;
    }
    if (b.hasAttribute("data-rj-clear")) { st.plan = null; ctx.redraw(); return true; }
    if (b.hasAttribute("data-rj-create") && st.plan) {
      readPlan(root, st.plan, "rj-");
      const n = root.querySelector("[data-rj-f=number]");
      if (n) st.number = n.value.trim();
      if (!st.number) { st.err = "列番を入れてください"; ctx.redraw(); return true; }
      if (!ctx.win.confirm(`特発臨 ${st.number} を立てますか？\n${confirmText(st.plan, st.number, false)}`)) return true;
      try {
        await p.api("POST", "/rinji", createBody(st.plan, st.number, st.linkTurn));
        st.plan = null; st.err = ""; st.data = null; // 読み直す
      } catch (e) { st.err = e.message; }
      await ctx.refresh();
      return true;
    }
    if (b.hasAttribute("data-rj-cancel")) {
      const no = b.getAttribute("data-rj-cancel");
      if (!ctx.win.confirm(`特発臨 ${no} を取り消しますか？\n打ち切りも外し、元の時刻表に戻します。`)) return true;
      try { await p.api("POST", "/rinji/cancel", { number: no }); st.err = ""; st.data = null; } catch (e) { st.err = e.message; }
      await ctx.refresh();
      return true;
    }
    return false;
  }

  return { panel, suggest, popupHtml, popupEvent, pick: (train) => picker && picker(train) };
})();
