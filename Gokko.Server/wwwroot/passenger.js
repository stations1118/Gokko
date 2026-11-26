"use strict";
const map = document.querySelector("#map");
const panel = document.querySelector("#detail");
const statusBar = document.querySelector("#status");
let selection = null;
let selectedTrain = null;
let detailRequest = 0;
const schedule = document.querySelector("#schedule");
let timer, busy = false;

function node(tag, className, text) {
  const el = document.createElement(tag);
  el.className = className;
  if (text != null) el.textContent = text;
  return el;
}
const serviceClasses = new Map([
  ["普通", "service-local"], ["特急", "service-limited-express"],
  ["快急", "service-rapid-express"], ["快速急行", "service-rapid-express"],
  ["急行", "service-express"],
  ["区急", "service-section-express"], ["区間急行", "service-section-express"],
  ["準急", "service-semi-express"]
]);
// 種別の色（CSS の service-○○）。種別の札と列車の絵に使う
function serviceClass(kind, label) {
  return serviceClasses.get(kind) || serviceClasses.get(label) || "service-other";
}
function kindBadge(kind, label) {
  return node("span", "kind " + serviceClass(kind, label), label || kind || "列車");
}
function trainLabel(number) {
  return String(number).replace(/列車$/, "") + "列車";
}
const advisoryText = "運転整理中です。今後の運転状況にご注意ください。";
function advisoryIcon() {
  const icon = node("span", "advisory-icon", "⚠");
  icon.title = advisoryText;
  icon.setAttribute("role", "img");
  icon.setAttribute("aria-label", advisoryText);
  return icon;
}
function showTrainSummary(t, dest, position) {
  show(trainLabel(t.train), "");
  panel.querySelector("p").append(kindBadge(t.kind, t.type), "　" +
    [destination(dest, t.direction), t.direction, position, t.delay && t.delay + "分遅れ"].filter(Boolean).join("　"));
  if (t.advisory) panel.querySelector("p").append(node("span","advisory-note","⚠ " + advisoryText));
}
function show(title, message) {
  panel.querySelector("h2").textContent = title;
  panel.querySelector("p").textContent = message;
  if (!panel.open) panel.showModal();
}
function minuteTime(value) {
  return value ? value.split(":").slice(0, 2).join(":") : "―";
}
// 行き先。江原検（車庫）行きは、お客様が乗れる最後の駅で出す（上りは江ノ原、下りは大道寺）
function destination(value, direction) {
  if (value && /^江原検/.test(value)) value = direction === "上り" ? "江ノ原" : "大道寺";
  return value && !/信号場|江原信/.test(value) ? value + "行" : "";
}
async function readBoard(station) {
  try {
    const res = await fetch("/api/passenger/departures/" + encodeURIComponent(station.id), {cache:"no-store"});
    if (selection !== station) return;
    if (res.status === 404) { show(station.name, "この駅の発車案内はありません。"); return; }
    if (!res.ok) throw Error();
    const data = await res.json();
    if (selection !== station) return;
    show(station.name + "駅　発車案内", "発車順　" + minuteTime(data.time) + " 現在");
    const area = document.createDocumentFragment();
    for (const board of data.boards) {
      area.append(node("strong","",board.direction));
      if (!board.trains.length) {
        area.append(node("p","","本日の発車予定はありません"));
        continue;
      }
      const table = node("table","");
      const head = table.createTHead().insertRow();
      for (const label of ["定刻発","種別・行先","のりば"]) head.append(node("th","",label));
      const body = table.createTBody();
      for (const t of board.trains) {
        const row = body.insertRow();
        row.insertCell().append(node("b","",minuteTime(t.time)));
        const service = row.insertCell();
        if (t.advisory) service.append(advisoryIcon(), " ");
        service.append(kindBadge(t.kind,t.kind), " " + destination(t.dest, board.direction));
        if (t.specialStop) service.append("（臨時停車）");
        service.append(node("small","departure-number",trainLabel(t.train)));
        row.insertCell().textContent = t.track ? t.track + "番" : "―";
      }
      area.append(table);
    }
    if (data.boards.some(board => board.trains.some(t => t.advisory)))
      area.prepend(node("span","advisory-note","⚠ の列車は運転整理中です。今後の運転状況にご注意ください。"));
    schedule.replaceChildren(area);
  } catch { if (selection === station) { schedule.replaceChildren(); show(station.name, "発車案内を取得できませんでした。"); } }
}
// 乗車率（編成の平均）。表示は10%きざみ
const crowdLevels = [
  { from: 111, style: "packed", face: "😵", message: "大混雑です。次の電車をご利用ください" },
  { from: 81, style: "tight", face: "😣", message: "かなり窮屈です" },
  { from: 51, style: "moderate", face: "🙂", message: "肩が触れ合う程度です" },
  { from: 0, style: "light", face: "😀", message: "余裕があります" }
];
function crowdCard(percent) {
  const level = Number.isFinite(percent) && crowdLevels.find(item => percent >= item.from);
  const card = node("div", "crowding " + (level ? level.style : "unknown"));
  const icon = node("span", "crowd-face", level ? level.face : "―");
  icon.setAttribute("aria-hidden", "true");
  const info = node("div", "crowd-info");
  const heading = node("div", "crowd-heading");
  heading.append(node("span", "", "乗車率"));
  if (level) {
    const displayPercent = Math.round(Math.ceil(percent) / 10) * 10;
    heading.append(node("b", "crowd-rate", "約" + displayPercent + "%"));
  }
  info.append(heading, node("div", "crowd-message", level ? level.message : "混雑情報は未取得です"));
  card.append(icon, info);
  return card;
}
// 側線に入ったなどで、在線図から消えた列車
function showTrainOffMap() {
  selectedTrain = null;
  schedule.replaceChildren();
  show("列車情報", "この列車は現在、在線図に表示されていません。");
}
async function readTrain(t, position) {
  const request = ++detailRequest;
  const current = () => request === detailRequest && selectedTrain === t.train && panel.open;
  try {
    const res = await fetch("/api/passenger/train?train=" + encodeURIComponent(t.train), {cache:"no-store"});
    if (res.status === 404) {
      if (current()) showTrainOffMap();
      return;
    }
    if (!res.ok) throw Error();
    const data = await res.json();
    if (!current()) return;
    showTrainSummary(t, data.dest || t.destination, position);
    const cars = data.cars || [];
    const formation = cars.length ? cars.length + "両編成　" + cars.map(c => c || "形式不明").join("・") : "編成情報未取得";
    const table = node("table","");
    const head = table.createTHead().insertRow();
    for (const title of ["停車駅","定刻着","到着見込み"]) head.append(node("th","",title));
    const body = table.createTBody();
    for (const stop of data.stops) {
      const row = body.insertRow();
      // 到着見込みが定刻より遅れていれば赤く出す
      row.append(node("td","",stop.name), node("td","",minuteTime(stop.scheduled)),
        node("td",stop.isLate ? "late-estimate" : "",minuteTime(stop.expected)));
    }
    schedule.replaceChildren(node("strong","",formation), crowdCard(data.averageOccupancy), data.stops.length ? table : node("p","","この列車のスタフは未登録です。"));
  } catch {
    if (current()) schedule.textContent = "列車の詳細を取得できませんでした。";
  }
}
panel.addEventListener("close", () => { selection = null; selectedTrain = null; schedule.replaceChildren(); });

function draw({stations, trains, updated, gameTime}) {
  const rows = [];
  const fragment = document.createDocumentFragment();
  function row(station, label) {
    const el = node("div", station ? "row station" : "row");
    const name = node(station ? "button" : "span", "name");
    if (station) {
      const id = node("span", "station-id");
      const parts = /^(TH)(\d+)$/.exec(station.id);
      if (parts) id.append(node("small", "", parts[1]), node("b", "", parts[2]));
      else id.textContent = station.id;
      const kinds = node("span", "stop-kinds");
      for (const kind of station.kinds || []) kinds.append(kindBadge(kind, kind));
      name.append(id, node("span", "station-name", station.name), kinds);
    }
    if (station) name.onclick = () => {
      selection = station;
      show(label, "発車案内を読み込み中…");
      readBoard(station);
    };
    const up = node("div","lane"), down = node("div","lane");
    el.append(name, up, down); fragment.append(el); rows.push({label, up, down});
  }
  stations.forEach((station,i) => {
    if (i) row(null, stations[i-1].name + "駅〜" + station.name + "駅");
    row(station, station.name + "駅");
  });
  let activeDetail;
  for (const t of trains) {
    const i = stations.findIndex(s => s.zone >= t.zone);
    const at = rows[i * 2 - (stations[i]?.zone === t.zone ? 0 : 1)];
    if (!at) continue;
    const btn = node("button","train");
    const kind = t.type || "列車";
    const tag = kindBadge(t.kind, kind);
    const face = node("span", "face " + serviceClass(t.kind, kind));
    face.setAttribute("aria-hidden", "true");
    const number = node("span","number",trainLabel(t.train));
    const service = node("span","train-service");
    service.append(tag);
    if (t.delay) service.append(node("span","delay","+" + t.delay + "分"));
    if (t.advisory) number.append(" ", advisoryIcon());
    btn.append(service,face,number);
    btn.setAttribute("aria-label", [kind,trainLabel(t.train),t.direction,at.label,t.advisory ? advisoryText : ""].filter(Boolean).join(" "));

    if (selectedTrain === t.train) activeDetail = [t, at.label];
    btn.onclick = () => {
      selection = null;
      selectedTrain = t.train;
      schedule.textContent = "時刻・編成を読み込み中…";
      showTrainSummary(t, t.destination, at.label);
      readTrain(t, at.label);
    };
    (t.direction === "下り" ? at.down : at.up).append(btn);
  }
  if (selectedTrain && panel.open) {
    if (activeDetail) readTrain(...activeDetail);
    else showTrainOffMap();
  }
  map.replaceChildren(fragment);
  document.querySelector("#range").textContent = stations.length ? stations[0].name + " — " + stations.at(-1).name : "";
  statusBar.textContent = (trains.length ? "在線 " + trains.length + "列車" : "現在、在線列車はありません") + "　｜　最終更新 " + (gameTime ? minuteTime(gameTime) : new Date(updated).toLocaleTimeString("ja-JP", {hour:"2-digit", minute:"2-digit", hourCycle:"h23"}));
}
async function refresh() {
  clearTimeout(timer);
  if (busy || document.hidden) return;
  busy = true;
  try {
    const res = await fetch("/api/passenger/positions", {cache:"no-store"});
    if (!res.ok) throw Error();
    draw(await res.json());
    if (selection && panel.open && !document.hidden) await readBoard(selection);
  } catch { statusBar.textContent = "在線情報の更新が止まっています"; }
  finally { busy = false; if (!document.hidden) timer = setTimeout(refresh, 5000); }
}
document.addEventListener("visibilitychange", refresh);
refresh();
