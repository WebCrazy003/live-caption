// Local Caption — send to chat. The page side: put text in the message box, optionally send.
//
// Chat sites rebuild their markup often, so nothing here depends on one site's class names.
// It looks for the message box the way a person would — the visible editable thing near the
// bottom of the page — with a short list of well-known ids tried first.

const KNOWN_BOXES = [
  "#prompt-textarea",                       // ChatGPT (a ProseMirror div)
  "textarea#chat-input",                    // DeepSeek
  "div.ProseMirror[contenteditable='true']",// Claude
  "rich-textarea div[contenteditable='true']", // Gemini
  "textarea#userInput",                     // Copilot
];

const SEND_BUTTONS = [
  "button[data-testid='send-button']",
  "button[data-testid='composer-send-button']",
  "button[aria-label*='Send' i]:not([disabled])",
  "button[type='submit']:not([disabled])",
];

function visible(el) {
  if (!el) return false;
  const box = el.getBoundingClientRect();
  return box.width > 40 && box.height > 10 && getComputedStyle(el).visibility !== "hidden";
}

function findBox() {
  for (const selector of KNOWN_BOXES) {
    const el = document.querySelector(selector);
    if (visible(el)) return el;
  }
  // Otherwise: the lowest visible editable on the page. Message boxes live at the bottom.
  const candidates = [...document.querySelectorAll("textarea, [contenteditable='true'], [role='textbox']")].filter(visible);
  candidates.sort((a, b) => b.getBoundingClientRect().bottom - a.getBoundingClientRect().bottom);
  return candidates[0] || null;
}

function insert(box, text) {
  box.focus();

  if (box instanceof HTMLTextAreaElement || box instanceof HTMLInputElement) {
    // React keeps its own copy of the value and ignores a plain assignment; going through
    // the prototype's setter and then announcing it is what makes the page notice.
    const setter = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(box), "value").set;
    const joined = box.value ? box.value.replace(/\s*$/, "") + "\n" + text : text;
    setter.call(box, joined);
    box.dispatchEvent(new Event("input", { bubbles: true }));
    return true;
  }

  // Rich editors (ProseMirror, Quill): insert as the user would, at the end, so the editor's
  // own model is updated rather than its DOM being rewritten behind its back.
  const selection = window.getSelection();
  const range = document.createRange();
  range.selectNodeContents(box);
  range.collapse(false);
  selection.removeAllRanges();
  selection.addRange(range);

  const lead = box.textContent.trim().length > 0 ? "\n" : "";
  if (document.execCommand("insertText", false, lead + text)) return true;

  // execCommand refused (rare): fall back to a synthetic paste, which editors also honour.
  const data = new DataTransfer();
  data.setData("text/plain", lead + text);
  return box.dispatchEvent(new ClipboardEvent("paste", { clipboardData: data, bubbles: true, cancelable: true }));
}

function submit(box) {
  for (const selector of SEND_BUTTONS) {
    const button = document.querySelector(selector);
    if (visible(button) && !button.disabled) { button.click(); return; }
  }
  const enter = { key: "Enter", code: "Enter", keyCode: 13, which: 13, bubbles: true, cancelable: true };
  box.dispatchEvent(new KeyboardEvent("keydown", enter));
  box.dispatchEvent(new KeyboardEvent("keyup", enter));
}

chrome.runtime.onMessage.addListener((message, _sender, respond) => {
  if (!message || message.type !== "local-caption") return;
  const box = findBox();
  if (!box) { respond({ ok: false, why: "no message box on this page" }); return; }

  const ok = insert(box, message.text);
  // The send button only enables once the page has digested the input event.
  if (ok && message.submit) setTimeout(() => submit(box), 180);
  respond({ ok });
});

// Tell the worker this tab exists, and again whenever it is the one being looked at — the
// question should go to the chat in use, not to one forgotten in another window.
function hello() { try { chrome.runtime.sendMessage({ type: "local-caption-hello" }); } catch (_) {} }
hello();
document.addEventListener("visibilitychange", () => { if (!document.hidden) hello(); });
window.addEventListener("focus", hello);
