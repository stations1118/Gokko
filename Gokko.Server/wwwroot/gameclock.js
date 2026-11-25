// ロープレ時刻（管理画面で設定する時刻）で時刻を出すための共通部品。管理画面・ダイヤグラムの設定画面・勤怠管理で使う。
// サーバーから受け取る時刻（作動した時刻・見込み・発車時刻など）は実際の時刻（UTC）なので、ロープレ時刻に直して出す。
// ロープレ時刻を設定していなければ、この PC の時計のまま出す。
// 使い方: GokkoClock.fmt(ms, sec) → "11:05"（sec なら "11:05:30"）／ GokkoClock.isoFromGame("11:05:30") → 実際の時刻（ISO）
//         GokkoClock.daySec(ms) → その時のロープレ時刻（その日の秒）／ GokkoClock.speed() → 実時間1秒で進む秒数
(() => {
  let clock = null; // { daySec, at（その時刻だった実際の時刻 ms）, speed }。ロープレ時刻がなければ null
  let skew = 0; // サーバーの時計 − この PC の時計（ms）
  let last = null; // 最後に読んだ /api/clock の答え（進んでいるか・止めた時刻を出す画面が使う）

  async function sync() {
    try {
      const before = Date.now();
      const c = await (await fetch("/api/clock", { cache: "no-store" })).json();
      const server = Date.parse(c.serverUtc);
      skew = server - (before + Date.now()) / 2;
      last = c;
      clock = c.daySec == null ? null : { daySec: c.daySec, at: server, speed: c.speed > 0 ? c.speed : 1 };
    } catch (e) { /* 読めなければ前のまま */ }
  }

  const now = () => Date.now() + skew;
  const speed = () => (clock ? clock.speed : 1);
  const wrap = (s) => ((s % 86400) + 86400) % 86400;

  // 実際の時刻 ms のときの、ロープレ時刻（その日の秒）。ロープレ時刻がなければ、この PC の時計の時刻
  function daySec(ms) {
    if (clock) return wrap(clock.daySec + (ms - clock.at) / 1000 * clock.speed);
    const d = new Date(ms);
    return d.getHours() * 3600 + d.getMinutes() * 60 + d.getSeconds() + d.getMilliseconds() / 1000;
  }

  function fmt(ms, sec) {
    if (ms == null || Number.isNaN(ms)) return "";
    const s = Math.floor(daySec(ms) + 0.002) % 86400; // ミリ秒の丸めで 1 秒手前にならないように
    const p = (n) => String(n).padStart(2, "0");
    return `${p(Math.floor(s / 3600))}:${p(Math.floor(s / 60) % 60)}${sec ? ":" + p(s % 60) : ""}`;
  }

  // ロープレ時刻 "HH:MM(:SS)" → 実際の時刻（ISO）。今からいちばん近い（前後12時間以内の）その時刻
  function isoFromGame(v) {
    const m = /^(\d{1,2}):(\d{2})(?::(\d{2}))?$/.exec(v || "");
    if (!m) return null;
    const want = Number(m[1]) * 3600 + Number(m[2]) * 60 + Number(m[3] || 0);
    const n = now();
    let diff = wrap(want - daySec(n));
    if (diff > 43200) diff -= 86400;
    return new Date(n + diff / speed() * 1000).toISOString();
  }

  const ready = sync(); // 最初に読み終わるまで（読み終わってから描くと、実時間の時刻が一瞬出ない）
  // ロープレ時刻（その日の秒）→ 実際の時刻 ms。今からいちばん近い（前後12時間以内の）その時刻（時刻表の時刻をダイヤグラムに置くため）
  function msFromDaySec(want) {
    const n = now();
    let diff = wrap(want - daySec(n));
    if (diff > 43200) diff -= 86400;
    return n + diff / speed() * 1000;
  }

  setInterval(sync, 5000);
  window.GokkoClock = { ready, sync, now, speed, daySec, fmt, isoFromGame, msFromDaySec, isSet: () => clock != null, raw: () => last };
})();
