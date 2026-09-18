const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

function setup() {
  const nodes = new Map();
  const requests = [];
  let checkResponse = { ok: true, body: { reachable: true } };
  let instagramFlow = { state: "Idle", errorCode: null };
  let intervalCallback;
  const element = id => {
    if (!nodes.has(id)) {
      const listeners = {};
      nodes.set(id, {
        listeners, textContent: "", className: "", value: "", disabled: false, hidden: false,
        addEventListener(name, listener) { listeners[name] = listener; },
      });
    }
    return nodes.get(id);
  };
  const document = {
    getElementById: element,
    querySelectorAll: () => [],
    activeElement: null,
  };
  const fetch = async (url, options) => {
    requests.push({ url, options });
    let result = { ok: true, body: {} };
    if (url === "/api/media/check") result = checkResponse;
    if (url === "/api/media/settings") result = {
      ok: true, body: { owner: "example", repository: "media", releaseTag: "staging", credentialConfigured: true },
    };
    if (url === "/api/instagram/settings") result = {
      ok: true, body: { appId: "123456", appSecretConfigured: true },
    };
    if (url === "/api/instagram/flows/latest" || url === "/api/instagram/flows/test-flow") result = {
      ok: true, body: instagramFlow,
    };
    if (url === "/api/instagram/connect") result = {
      ok: true, body: { flowId: "test-flow", authorizationUrl: "https://www.instagram.com/oauth/authorize?state=test" },
    };
    if (["/api/accounts", "/api/providers", "/api/publications"].includes(url)) result = {
      ok: true, body: [],
    };
    return { ok: result.ok, json: async () => result.body };
  };
  const scriptPath = path.join(__dirname, "..", "src", "PostRouter.Gui", "Web", "app.js");
  // The startup refresh is unrelated to these event handlers and needs a full page.
  const script = fs.readFileSync(scriptPath, "utf8").split("\n(async () => {")[0];
  const sandbox = {
    document, fetch, Headers, FormData, crypto: { randomUUID: () => "test-request" },
    window: {
      setTimeout() {}, setInterval(callback) { intervalCallback = callback; return 1; }, clearInterval() {},
      open() { return { location: { replace() {} }, close() {} }; },
    }, URL,
  };
  vm.runInNewContext(script + "\nglobalThis.testInstagram = { refreshInstagramSettings, startInstagramFlow };", sandbox,
    { filename: scriptPath });
  return {
    element, requests,
    setCheckResponse(value) { checkResponse = value; },
    setInstagramFlow(value) { instagramFlow = value; },
    async refreshInstagram() { await sandbox.testInstagram.refreshInstagramSettings(); },
    async startInstagram() { await sandbox.testInstagram.startInstagramFlow("/api/instagram/connect"); },
    async tickInstagram() { await intervalCallback(); },
    async fire(id, eventName) {
      await element(id).listeners[eventName]({ preventDefault() {} });
    },
    status() { return element("media-check-status").textContent; },
  };
}

test("connection check shows local success and failure without server error or token text", async () => {
  const ui = setup();
  await ui.fire("media-check", "click");
  assert.equal(ui.status(), "接続OK");
  ui.setCheckResponse({ ok: false, body: { message: "secret-token private exception path" } });
  await ui.fire("media-check", "click");
  assert.equal(ui.status(), "接続失敗");
  assert.doesNotMatch(ui.element("media-check-status").textContent + ui.element("notice").textContent,
    /secret-token|private exception path/);
});

test("target inputs and PAT update or deletion reset the connection check", async () => {
  const ui = setup();
  for (const id of ["media-owner", "media-repository", "media-tag"]) {
    await ui.fire("media-check", "click");
    assert.equal(ui.status(), "接続OK");
    await ui.fire(id, "input");
    assert.equal(ui.status(), "未確認");
  }
  await ui.fire("media-check", "click");
  ui.element("media-token").value = "private-pat";
  await ui.fire("media-credential-form", "submit");
  assert.equal(ui.status(), "未確認");
  assert.equal(ui.element("media-token").value, "");
  assert.doesNotMatch(ui.element("media-check-status").textContent + ui.element("notice").textContent,
    /private-pat/);
  await ui.fire("media-check", "click");
  // The delete button registers a confirmation callback; invoke only after confirmation.
  ui.element("confirm-dialog").showModal = () => {};
  ui.element("confirm-dialog").close = () => {};
  await ui.fire("media-clear", "click");
  await ui.element("confirm-ok").onclick();
  assert.equal(ui.status(), "未確認");
  assert.ok(ui.requests.some(request => request.url === "/api/media/credential/clear"));
});

test("an old check response cannot restore success after settings change", async () => {
  const ui = setup();
  let complete;
  ui.setCheckResponse({
    ok: true,
    body: { reachable: true },
  });
  // Delay only the response body, after the request has started.
  ui.setCheckResponse({ ok: true, body: new Promise(resolve => { complete = resolve; }) });
  const checking = ui.fire("media-check", "click");
  await Promise.resolve();
  await ui.fire("media-owner", "input");
  complete({ reachable: true });
  await checking;
  assert.equal(ui.status(), "未確認");
});

test("Instagram failure remains visible in account settings with a safe error code", async () => {
  const ui = setup();
  ui.setInstagramFlow({ state: "Failed", errorCode: "instagram_code_exchange_bad_request" });
  await ui.refreshInstagram();
  assert.equal(ui.element("instagram-settings-state").textContent, "接続失敗");
  assert.equal(ui.element("instagram-flow-error-code").textContent,
    "エラーコード: instagram_code_exchange_bad_request");
  assert.equal(ui.element("instagram-flow-error-code").hidden, false);
  assert.equal(ui.element("instagram-flow-error-hint").hidden, false);
});

test("Instagram flow polling displays only allowlisted safe code characters", async () => {
  const ui = setup();
  ui.setInstagramFlow({ state: "Pending", errorCode: null });
  await ui.startInstagram();
  ui.setInstagramFlow({ state: "Failed", errorCode: "private-token?access_token=secret" });
  await ui.tickInstagram();
  assert.equal(ui.element("instagram-settings-state").textContent, "接続失敗");
  assert.equal(ui.element("instagram-flow-error-code").textContent, "エラーコード: connection_failed");
  assert.doesNotMatch(ui.element("notice").textContent + ui.element("instagram-flow-error-code").textContent,
    /private-token|access_token|secret/);
});
