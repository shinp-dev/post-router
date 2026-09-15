"use strict";

let csrfToken = "";
let accounts = [];
let providers = [];
let pendingRequestId = crypto.randomUUID();

const byId = id => document.getElementById(id);
const escapeState = value => String(value || "Unknown").replace(/[^A-Za-z]/g, "");
const formatTime = value => value ? new Date(value).toLocaleString() : "—";
const badge = value => {
  const span = document.createElement("span");
  span.className = `badge ${escapeState(value)}`;
  span.textContent = value || "—";
  return span;
};

async function request(path, options = {}) {
  const headers = new Headers(options.headers || {});
  if (options.method === "POST") {
    headers.set("X-Post-Router-CSRF", csrfToken);
    if (!(options.body instanceof FormData)) headers.set("Content-Type", "application/json");
  }
  const response = await fetch(path, { ...options, headers, cache: "no-store" });
  const body = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(body.message || "操作を完了できませんでした。");
  return body;
}

function showNotice(message, error = false) {
  const node = byId("notice");
  node.textContent = message;
  node.className = error ? "error" : "";
  window.setTimeout(() => { if (node.textContent === message) node.textContent = ""; }, 7000);
}

function showView(id) {
  document.querySelectorAll(".view").forEach(node => node.classList.toggle("active", node.id === id));
  document.querySelectorAll(".nav").forEach(node => node.classList.toggle("active", node.dataset.view === id));
}

async function refreshAll() {
  try {
    const [dashboard, accountData, providerData, publications] = await Promise.all([
      request("/api/dashboard"), request("/api/accounts"), request("/api/providers"), request("/api/publications")
    ]);
    accounts = accountData;
    providers = providerData;
    renderDashboard(dashboard);
    renderAccounts();
    renderProviders();
    renderPublications(publications);
  } catch (error) { showNotice(error.message, true); }
}

function renderDashboard(data) {
  const labels = [
    ["Scheduled", data.scheduled, ""], ["Pending", data.pending, ""], ["Processing", data.processing, ""],
    ["Published", data.published, ""], ["Failed", data.failed, "alert"], ["Needs attention", data.needsAttention, "alert"], ["Unknown", data.unknown, "unknown"],
    ["Cancelled", data.cancelled, ""], ["Expired", data.expired, ""], ["Auth error", data.authenticationErrors, "alert"]
  ];
  const summary = byId("summary"); summary.replaceChildren();
  labels.forEach(([label, value, style]) => {
    const card = document.createElement("div"); card.className = `metric ${style}`;
    const text = document.createElement("span"); text.textContent = label;
    const count = document.createElement("strong"); count.textContent = value;
    card.append(text, count); summary.append(card);
  });
  const alerts = byId("alerts"); alerts.replaceChildren();
  if (data.unknown > 0) addAlert(alerts, `${data.unknown}件がUnknownです。投稿済みの可能性があるため自動再投稿されていません。`, false);
  if (data.failed > 0 || data.authenticationErrors > 0) addAlert(alerts, "確認が必要な失敗または認証エラーがあります。", true);
  const connected = accounts.filter(item => item.status === "Connected");
  byId("dashboard-accounts").textContent = connected.length ? connected.map(item => `${item.provider} / ${item.displayName}`).join("、") : "接続済みアカウントはありません。";
}

function addAlert(parent, message, error) {
  const node = document.createElement("div"); node.className = `alert-box${error ? " error" : ""}`; node.textContent = message; parent.append(node);
}

function renderProviders() {
  const connect = byId("connect-provider"); connect.replaceChildren();
  providers.filter(item => item.interactiveAuthentication).forEach(item => connect.add(new Option(item.providerKey, item.providerKey)));
  const post = byId("post-account"); post.replaceChildren();
  accounts.filter(item => item.status === "Connected" || item.status === "Ready").forEach(item => {
    const capability = providers.find(provider => provider.providerKey === item.provider);
    if (capability?.contentKinds.includes("TextOnly")) post.add(new Option(`${item.provider} / ${item.displayName || item.alias}`, item.accountId));
  });
  if (!post.options.length) post.add(new Option("接続済みアカウントがありません", ""));
  updateImageCapability();
}

function updateImageCapability() {
  const account = accounts.find(item => item.accountId === byId("post-account").value);
  const capability = providers.find(item => item.providerKey === account?.provider);
  const enabled = capability?.contentKinds.includes("ImageSet") === true;
  byId("image-field").hidden = !enabled;
  if (!enabled) byId("post-images").value = "";
}

function renderPublications(items) {
  const body = byId("publication-rows"); body.replaceChildren();
  if (!items.length) {
    const row = body.insertRow(); const cell = row.insertCell(); cell.colSpan = 6; cell.textContent = "投稿はまだありません。"; return;
  }
  items.forEach(item => {
    const row = body.insertRow(); row.tabIndex = 0; row.dataset.publicationId = item.publicationId;
    const account = row.insertCell(); account.textContent = `${item.provider} / ${item.accountAlias}`;
    row.insertCell().textContent = item.contentPreview || "—";
    row.insertCell().textContent = `${item.scheduleMode === "AtTime" ? "予約" : "即時"} ${formatTime(item.dueAt)}`;
    row.insertCell().append(badge(item.publicationState));
    const queue = row.insertCell(); queue.textContent = item.jobState ? `${item.jobKind} / ${item.jobState}${item.attemptCount ? ` (${item.attemptCount})` : ""}` : "—";
    row.insertCell().textContent = item.remoteId || "—";
    row.addEventListener("click", () => openDetail(item.publicationId));
    row.addEventListener("keydown", event => { if (event.key === "Enter") openDetail(item.publicationId); });
  });
}

function renderAccounts() {
  const list = byId("account-list"); list.replaceChildren();
  accounts.forEach(account => {
    const card = document.createElement("article"); card.className = "account-card";
    const title = document.createElement("h2"); title.textContent = `${account.provider} / ${account.displayName || account.alias}`;
    const status = badge(account.status);
    const dl = document.createElement("dl");
    addDefinition(dl, "Alias", account.alias); addDefinition(dl, "Provider user ID", account.remoteSubject || "—");
    addDefinition(dl, "Token expiry", formatTime(account.tokenExpiresAt)); addDefinition(dl, "Scope", account.scope || "—");
    addDefinition(dl, "Last auth error", account.lastAuthError || "—");
    const actions = document.createElement("div"); actions.className = "actions";
    if (account.status === "Connected") {
      actions.append(actionButton("Disconnect", () => confirmAction("Disconnect", "ローカル認証情報を削除して未送信jobを停止します。履歴とqueueは保持します。", () => accountAction(account.accountId, "disconnect")), "secondary"));
      const capability = providers.find(item => item.providerKey === account.provider);
      if (capability?.revoke) actions.append(actionButton("Revoke", () => confirmAction("Revoke", "Provider側のtokenも失効し、ローカル認証情報を削除します。", () => accountAction(account.accountId, "revoke")), "danger"));
    } else if (account.remoteSubject) actions.append(actionButton("Reconnect", () => reconnect(account.accountId)));
    card.append(title, status, dl, actions); list.append(card);
  });
  if (!accounts.length) list.textContent = "登録済みアカウントはありません。";
}

function addDefinition(dl, term, value) {
  const dt = document.createElement("dt"); dt.textContent = term; const dd = document.createElement("dd"); dd.textContent = value; dl.append(dt, dd);
}

function actionButton(label, handler, style = "") {
  const button = document.createElement("button"); button.type = "button"; button.textContent = label; button.className = style; button.addEventListener("click", handler); return button;
}

async function openDetail(id) {
  try {
    const [detail, approval] = await Promise.all([
      request(`/api/publications/${id}`),
      request(`/api/publications/${id}/approval`)
    ]);
    const content = byId("detail-content"); content.replaceChildren();
    if (detail.summary.publicationState === "Unknown") addAlert(content, "投稿された可能性があります。自動再投稿は行われず、照合でも確定できない場合があります。", false);
    if (detail.summary.publicationState === "AwaitingApproval") addAlert(content, "公開前で停止しています。内容と公開先を確認してから承認してください。", false);
    const text = document.createElement("p"); text.className = "full-text"; text.textContent = detail.text || "";
    const dl = document.createElement("dl");
    addDefinition(dl, "Provider / Account", `${detail.summary.provider} / ${detail.summary.accountAlias}`);
    addDefinition(dl, "Publication", detail.summary.publicationState);
    addDefinition(dl, "Approval", approval.policy === "RequireApproval" ? (approval.approved ? "Approved" : "Required") : "Automatic");
    addDefinition(dl, "Queue", detail.summary.jobState ? `${detail.summary.jobKind} / ${detail.summary.jobState}` : "—");
    addDefinition(dl, "Scheduled", formatTime(detail.summary.dueAt)); addDefinition(dl, "Attempts", detail.summary.attemptCount);
    addDefinition(dl, "Remote ID", detail.summary.remoteId || "—"); addDefinition(dl, "Provider error", detail.providerError || "—");
    addDefinition(dl, "Normalized error", detail.normalizedError || "—"); addDefinition(dl, "Reconcile queued", detail.reconcileQueued ? "Yes" : "No");
    addDefinition(dl, "Created", formatTime(detail.summary.createdAt)); addDefinition(dl, "Submitted", formatTime(detail.firstSubmittedAt));
    addDefinition(dl, "Confirmed", formatTime(detail.confirmedAt)); addDefinition(dl, "Published", formatTime(detail.summary.publishedAt));
    content.append(text, dl);
    const actions = byId("detail-actions"); actions.replaceChildren();
    if (approval.canApprove) {
      actions.append(actionButton("Approve publish", () => confirmAction(
        "公開を承認",
        "この投稿の公開境界を解除し、workerがProviderへの公開操作を実行できるようにします。",
        () => postAction(`/api/publications/${id}/approve`, "公開を承認しました。"))));
    }
    const cancel = actionButton("Cancel", () => postAction(`/api/posts/${detail.summary.postId}/cancel`, "キャンセルを要求しました。"), "secondary"); cancel.disabled = !detail.canCancel; actions.append(cancel);
    const retry = actionButton("Retry", () => postAction(`/api/publications/${id}/retry`, "安全な再試行をqueueへ登録しました。")); retry.disabled = !detail.canRetry; actions.append(retry);
    const reconcile = actionButton("Reconcile", () => postAction(`/api/publications/${id}/reconcile`, "照合をqueueへ登録しました。再投稿はしていません。")); reconcile.disabled = !detail.canReconcile; actions.append(reconcile);
    byId("detail-dialog").showModal();
  } catch (error) { showNotice(error.message, true); }
}

async function postAction(path, message) {
  try { await request(path, { method: "POST", body: "{}" }); byId("detail-dialog").close(); showNotice(message); await refreshAll(); }
  catch (error) { showNotice(error.message, true); }
}

async function accountAction(id, action) {
  try { await request(`/api/accounts/${id}/${action}`, { method: "POST", body: "{}" }); showNotice(`${action}を完了しました。`); await refreshAll(); }
  catch (error) { showNotice(error.message, true); }
}

async function reconnect(id) {
  const popup = prepareAuthorizationWindow();
  try { const result = await request(`/api/accounts/${id}/reconnect`, { method: "POST", body: "{}" }); if (openAuthorization(popup, result.authorizationUrl)) showNotice("認証画面を開きました。"); }
  catch (error) { if (popup) popup.close(); showNotice(error.message, true); }
}

function prepareAuthorizationWindow() {
  const popup = window.open("about:blank", "post-router-oauth");
  if (popup) popup.opener = null;
  return popup;
}

function openAuthorization(popup, url) {
  if (popup) { popup.location.replace(url); return true; }
  showNotice("ポップアップがブロックされました。再度認証を開始してください。", true);
  return false;
}

function confirmAction(title, message, action) {
  byId("confirm-title").textContent = title; byId("confirm-message").textContent = message;
  const dialog = byId("confirm-dialog"); const ok = byId("confirm-ok");
  ok.onclick = async () => { dialog.close(); await action(); }; dialog.showModal();
}

document.querySelectorAll(".nav").forEach(button => button.addEventListener("click", () => showView(button.dataset.view)));
document.querySelectorAll(".refresh").forEach(button => button.addEventListener("click", refreshAll));
byId("detail-close").addEventListener("click", () => byId("detail-dialog").close());
byId("confirm-cancel").addEventListener("click", () => byId("confirm-dialog").close());
byId("schedule-enabled").addEventListener("change", event => { byId("schedule-field").hidden = !event.target.checked; byId("post-at").required = event.target.checked; });
byId("post-text").addEventListener("input", event => { byId("text-count").textContent = `${[...event.target.value].length} / 280`; pendingRequestId = crypto.randomUUID(); });
byId("post-account").addEventListener("change", updateImageCapability);

byId("post-form").addEventListener("submit", async event => {
  event.preventDefault();
  try {
    const scheduled = byId("schedule-enabled").checked;
    const value = byId("post-at").value;
    const publishAt = scheduled ? new Date(value).toISOString() : null;
    const images = [...byId("post-images").files];
    let result;
    if (images.length) {
      if (images.length > 4 || images.some(file => file.size > 5 * 1024 * 1024 || file.type !== "image/jpeg")) throw new Error("JPEG画像は最大4枚、各5 MBまでです。");
      const body = new FormData(); body.set("accountId", byId("post-account").value); body.set("text", byId("post-text").value); body.set("clientRequestId", pendingRequestId);
      if (publishAt) body.set("publishAt", publishAt); images.forEach(file => body.append("images", file));
      result = await request("/api/posts/images", { method: "POST", body });
    } else {
      const body = { accountId: byId("post-account").value, text: byId("post-text").value, clientRequestId: pendingRequestId, publishAt };
      result = await request("/api/posts", { method: "POST", body: JSON.stringify(body) });
    }
    pendingRequestId = crypto.randomUUID(); byId("post-text").value = ""; byId("text-count").textContent = "0 / 280";
    byId("post-images").value = "";
    showNotice(`Queueへ登録しました: ${result.postId}`); showView("publications"); await refreshAll();
  } catch (error) { showNotice(error.message, true); }
});

byId("connect-form").addEventListener("submit", async event => {
  event.preventDefault();
  const popup = prepareAuthorizationWindow();
  try {
    const body = { provider: byId("connect-provider").value, clientId: byId("client-id").value, alias: byId("account-alias").value || null };
    const result = await request("/api/accounts/connect", { method: "POST", body: JSON.stringify(body) });
    if (openAuthorization(popup, result.authorizationUrl)) showNotice("認証画面を開きました。完了後に更新してください。");
  } catch (error) { if (popup) popup.close(); showNotice(error.message, true); }
});

(async () => {
  try { csrfToken = (await request("/api/session")).csrfToken; await refreshAll(); window.setInterval(refreshAll, 10000); }
  catch (error) { showNotice(error.message, true); }
})();