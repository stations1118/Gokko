// 司令員のログイン（diagrammarker.html を単体で開いたとき）。見た目と動きは信号制御盤（relaypanel.html）と同じ:
// 右上の帯の「ログイン」ボタンを押すと、帯の下に小窓が出る。ログイン中はボタンが「ID:…」になり、押すとログアウトを確かめる。
// ログインは乗務タブレットと同じ（社員番号とパスワード。POST /api/tid/login）。合い札はこのタブの中だけに覚える（crewsession.js。先に読んでおく）。
// 乗務メニューから開いたタブや、信号制御盤の小窓で開いたとき（盤と同じタブ）は、そのログインを使うので入り直さなくてよい。URL を直接開いたタブではログインを求める。
// 使えるかどうか（司令員の役職があるか）はサーバーが決める（401・403 で login: true が返る）。
// 使い方: const login = GokkoLogin.create({ who, panel, onLogin, onLogout })。panel の中に #loginId・#loginPw・#loginGo・#loginClose・#loginMsg
(() => {
  function create({ who, panel, onLogin, onLogout }) {
    const $ = (id) => panel.querySelector("#" + id);
    const user = () => GokkoCrew.id();
    const token = () => GokkoCrew.token();
    const showWho = () => { who.textContent = user() ? `ID:${user()}` : "ログイン"; };

    function show(message) {
      panel.classList.add("open");
      $("loginMsg").textContent = message || "";
      if (!$("loginId").value) $("loginId").value = user();
      ($("loginId").value ? $("loginPw") : $("loginId")).focus();
    }
    const hide = () => panel.classList.remove("open");

    async function logout() {
      const t = token();
      if (t) { try { await fetch("/api/tid/logout", { method: "POST", headers: { "X-Crew-Token": t } }); } catch (e) {} }
      GokkoCrew.logout();
      showWho();
      onLogout && onLogout();
    }

    who.onclick = () => {
      if (user()) { if (confirm(`ID:${user()} をログアウトしますか？`)) logout(); return; }
      show("");
    };
    $("loginClose").onclick = hide;
    GokkoCrew.discordButton(panel, $("loginMsg")); // Discord でログイン（戻ってきたら crewsession.js が受け取る）
    async function go() {
      $("loginMsg").textContent = "";
      try {
        const res = await fetch("/api/tid/login", { method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ id: $("loginId").value.trim(), password: $("loginPw").value, passphrase: GokkoCrew.passphrase(panel) }) });
        const j = await res.json().catch(() => ({}));
        if (!res.ok) { $("loginMsg").textContent = j.message || "ログインできません"; return; }
        GokkoCrew.login(j.id, j.token);
        $("loginPw").value = "";
        hide();
        showWho();
        onLogin && onLogin();
      } catch (e) { $("loginMsg").textContent = "サーバーにつながりません"; }
    }
    $("loginGo").onclick = go;
    $("loginPw").addEventListener("keydown", (ev) => { if (ev.key === "Enter") go(); });
    showWho();

    return { token, user, show, hide, logout, showWho, shown: () => panel.classList.contains("open") };
  }

  window.GokkoLogin = { create };
})();
