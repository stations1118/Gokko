// 乗務員のログインの持ち方（乗務メニュー以外の画面で共通）。各画面の <head> で、ほかのスクリプトより先に読む。
// ・乗務メニュー（menu.html）がログインの元。メニューは合い札をこのブラウザ（localStorage の gokkoTabletId・gokkoTabletToken）に覚える
// ・メニューのタイルから開くと、アドレスに #handoff=引き継ぎ札（30秒・1回だけ）が付いてくる。札を合い札に替えて、このタブの中だけ（sessionStorage）に覚える
//   → 同じタブの中なら、再読み込みや画面の行き来をしてもログインしたまま。URL を直接貼り付けて開いたタブには札がないので、ログインを求める
// ・PC のクライアントから開いたとき（#crew=社員番号&token=合い札）も、このタブに覚える
// ・画面でパスワードを入れてログインしたときは、メニューにも覚える（GokkoCrew.login）。本人が入れたので、メニューに戻っても入り直さなくてよい
// ・Discord でログインして戻ってきたとき（#discord=引き継ぎ札）は、このタブとメニューの両方に覚える。紐づけて戻ってきたときは #discordLinked=1
// ・ログイン（パスワード・Discord）には、参加の合言葉（クライアントの接続と同じ）も要る。ログインの欄に GokkoCrew.passField で欄を足し、
//   GokkoCrew.passphrase(欄の箱) の値をログインのときに送る。ログインしたあとは要らない（ほかのタブは今までどおり引き継ぐ）
// 札の交換は、画面のスクリプトがログインを読む前に済ませたいので、同期の XHR で待つ（同じサーバーへ1回だけ）
(() => {
  const KEY = "gokkoCrew";
  const read = () => { try { const v = JSON.parse(sessionStorage.getItem(KEY) || "null"); return v && v.id && v.token ? v : null; } catch (e) { return null; } };
  const write = (id, token) => {
    try { if (id && token) sessionStorage.setItem(KEY, JSON.stringify({ id, token })); else sessionStorage.removeItem(KEY); } catch (e) {}
  };
  const remember = (id, token) => {
    try {
      if (id && token) { localStorage.setItem("gokkoTabletId", id); localStorage.setItem("gokkoTabletToken", token); }
      else { localStorage.removeItem("gokkoTabletId"); localStorage.removeItem("gokkoTabletToken"); }
    } catch (e) {}
  };

  const h = new URLSearchParams(location.hash.slice(1));
  // Discord でログインして戻ってきたとき（#discord=引き継ぎ札）は、本人が入れたので、メニューにも覚える
  const discord = h.get("discord");
  const code = h.get("handoff") || discord;
  if (code) {
    try {
      const x = new XMLHttpRequest();
      x.open("POST", "/api/tid/login/handoff", false);
      x.setRequestHeader("Content-Type", "application/json");
      x.send(JSON.stringify({ code }));
      if (x.status === 200) { const j = JSON.parse(x.responseText); write(j.id, j.token); if (discord) remember(j.id, j.token); }
    } catch (e) {}
  }
  if (h.get("crew") && h.get("token")) write(h.get("crew"), h.get("token"));
  const linked = h.get("discordLinked") === "1"; // Discord と紐づけて戻ってきた
  if (code || h.get("crew") || linked) history.replaceState(null, "", location.pathname + location.search); // 札・合い札をアドレスに残さない

  // Discord のページへ行く（終わったら、いまの画面に戻る）。mode: "login"（ログイン）・"link"（ログインしている本人に紐づける。token が要る）
  async function goDiscord(mode, token, passphrase) {
    const res = await fetch("/api/auth/discord/begin", {
      method: "POST", headers: { "Content-Type": "application/json", ...(token ? { "X-Crew-Token": token } : {}) },
      body: JSON.stringify({ mode, returnTo: location.pathname + location.search, passphrase: passphrase || "" }),
    });
    const j = await res.json().catch(() => ({}));
    if (!res.ok || !j.url) throw new Error(j.message || "Discord のページを開けませんでした");
    location.href = j.url;
  }
  // このサーバーで Discord ログインが使えるか（パスワードでのログインがもう終わったかも）
  let discordInfo = null;
  async function discordConfig() {
    if (!discordInfo) {
      try { discordInfo = await (await fetch("/api/auth/discord", { cache: "no-store" })).json(); } catch (e) { discordInfo = { enabled: false }; }
    }
    return discordInfo;
  }
  // ログインの欄の中の合言葉の値
  const passphraseOf = (box) => { const i = box && box.querySelector(".gokko-pass"); return i ? i.value : ""; };
  // ログインの欄に「合言葉」の欄を足す（サーバーに合言葉があるときだけ）。before を渡せば、その前に置く（なければ見出しのあと・先頭）
  async function passField(box, before) {
    const c = await discordConfig();
    if (!c.passphrase || !box || box.querySelector(".gokko-pass")) return;
    const label = document.createElement("label");
    label.className = "gokko-pass-label";
    label.append("合言葉");
    const input = document.createElement("input");
    input.className = "gokko-pass";
    input.type = "password";
    input.autocomplete = "off";
    input.placeholder = "クライアントの接続と同じ";
    label.append(input);
    const anchor = before || box.querySelector(".gokko-discord") || box.querySelector("#pwPart");
    if (anchor && anchor.parentNode) anchor.parentNode.insertBefore(label, anchor);
    else { const head = box.querySelector("h1, h2, h3, h5"); if (head) head.after(label); else box.prepend(label); }
  }
  // 紐づけの期限（「11/27 0:00 まで」）
  const until = (t) => {
    if (!t) return "";
    const d = new Date(t);
    return `${d.getMonth() + 1}/${d.getDate()} ${d.getHours()}:${String(d.getMinutes()).padStart(2, "0")} まで`;
  };

  window.GokkoCrew = {
    get: read,
    id: () => (read() ? read().id : ""),
    token: () => (read() ? read().token : ""),
    // このタブの中だけ覚え直す（社員番号が変わったときなど）
    set: write,
    // パスワードでログインしたとき: このタブと、乗務メニューの両方に覚える
    login(id, token) { write(id, token); remember(id, token); },
    // ログアウト: このタブから消す。メニューが同じ合い札を覚えていれば、そちらも消す（サーバーで合い札を捨てるので、もう使えない）
    logout() {
      const c = read();
      write("", "");
      try { if (c && localStorage.getItem("gokkoTabletToken") === c.token) remember("", ""); } catch (e) {}
    },
    // このタブから消すだけ（合い札が使えなくなったとき）
    clear: () => write("", ""),
    // Discord でログインする（Discord のページへ行き、戻ってきたら、このタブとメニューに覚える）。box: 合言葉の欄がある箱
    discordLogin: (box) => goDiscord("login", null, passphraseOf(box)),
    // 合言葉の欄を足す・値を読む
    passField,
    passphrase: passphraseOf,
    // ログインしている本人を Discord と紐づける（紐づけたら、パスワードは使えなくなる）
    discordLink: (token) => goDiscord("link", token),
    discordConfig,
    // ログインの欄に［Discord でログイン］を足す（Discord が使えるサーバーだけ）。パスワードの期限を過ぎたら、欄の中の #pwPart を隠す
    async discordButton(box, msg) {
      const c = await discordConfig();
      if (!c.enabled) await passField(box); // Discord がないサーバーでも、合言葉の欄は足す
      if (!c.enabled || !box || box.querySelector(".gokko-discord")) return;
      const b = document.createElement("button");
      b.type = "button";
      b.className = "gokko-discord";
      b.textContent = "Discord でログイン";
      b.style.cssText = "background:#5865f2;border:1px solid #5865f2;color:#fff;border-radius:6px;padding:6px 12px;font:inherit;cursor:pointer;display:block;margin:4px 0 8px";
      b.onclick = async () => { try { await goDiscord("login", null, passphraseOf(box)); } catch (e) { if (msg) msg.textContent = e.message; } };
      const head = box.querySelector("h1, h2, h3");
      if (head) head.after(b); else box.prepend(b);
      await passField(box, b); // 合言葉は Discord のボタンの前（パスワードでのログインにも使う）
      const pw = box.querySelector("#pwPart");
      if (pw && c.passwordClosed) pw.style.display = "none";
    },
    // Discord と紐づけて戻ってきたところか
    justLinked: linked,
    // Discord でログインして戻ってきたところか
    discordLoggedIn: !!discord && !!read(),
    linkUntil: until,
  };
})();
