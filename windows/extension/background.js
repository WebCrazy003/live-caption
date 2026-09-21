// Local Caption — send to chat. The service worker: asks the app what is new, hands it to
// the chat tab.
//
// Why the worker and not the page: a chat tab you are not looking at is a background tab,
// and Chrome throttles a background tab's timers to once a minute after five minutes. A
// page that polled would be a minute late exactly when the interview is under way. The
// worker is not throttled, and a message from it wakes the tab's script at once.
//
// Why a long poll and not a socket: /next holds the request open until there is something
// to say (or 25 s pass), so news arrives in milliseconds with one quiet request in flight —
// and an in-flight request is also what keeps an MV3 worker alive. If the worker is ever
// stopped anyway, the alarm below starts it again within half a minute.

const DEFAULT_PORT = 17653;
let running = false;

async function port() {
  const { port } = await chrome.storage.local.get({ port: DEFAULT_PORT });
  return Number(port) || DEFAULT_PORT;
}

async function loop() {
  if (running) return;
  running = true;
  let since = -1;          // -1: "tell me where now is". Old questions are never replayed.
  try {
    for (;;) {
      const response = await fetch(`http://127.0.0.1:${await port()}/next?since=${since}`, { cache: "no-store" });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const news = await response.json();
      const first = since < 0;
      since = news.last;
      if (!first) for (const item of news.items || []) await deliver(item);
    }
  } catch (_) {
    // Local Caption is not running, or "Browser extension" is not the chosen target. Not an
    // error worth showing: the alarm will try again.
  } finally {
    running = false;
  }
}

// To the chat tab the user was last in; failing that, any chat tab at all. Tabs announce
// themselves (content.js says hello on load and whenever it is looked at), so this needs no
// permission to read anyone's tab list.
async function deliver(item) {
  const { chats = [] } = await chrome.storage.session.get("chats");
  const alive = [];
  let delivered = false;
  for (const id of chats) {                       // most recently used first
    try {
      const answer = delivered ? { ok: false, skip: true }
        : await chrome.tabs.sendMessage(id, { type: "local-caption", text: item.text, submit: !!item.submit });
      alive.push(id);
      if (answer && answer.ok) delivered = true;
    } catch (_) { /* closed or navigated away: forget it */ }
  }
  await chrome.storage.session.set({ chats: alive });
}

chrome.runtime.onMessage.addListener((message, sender) => {
  if (!message || message.type !== "local-caption-hello" || !sender.tab) return;
  chrome.storage.session.get("chats").then(({ chats = [] }) =>
    chrome.storage.session.set({ chats: [sender.tab.id, ...chats.filter((id) => id !== sender.tab.id)].slice(0, 12) }));
  loop();
});

chrome.alarms.create("keep-listening", { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(loop);
chrome.runtime.onStartup.addListener(loop);
chrome.runtime.onInstalled.addListener(loop);
chrome.storage.onChanged.addListener(loop);
loop();
