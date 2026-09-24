"use strict";

const element = (id) => document.getElementById(id);
let session = null;
let environment = null;
let imageUrl = null;
let pollTimer = null;
let submitting = false;
let activeJob = null;
const terminal = new Set(["Completed", "Failed", "Canceled", "Cancelled", "NotResponding"]);

function showError(message, kind = "operation") {
  element("global-error").textContent = message;
  element("global-error").hidden = !message;
  element("global-error").dataset.kind = kind;
}

function signedOut() {
  session = null;
  environment = null;
  clearTimeout(pollTimer);
  element("workspace").hidden = true;
  element("signin-panel").hidden = false;
  element("signout").hidden = true;
  element("account-name").textContent = "Sign-in required";
}

async function api(path, options = {}) {
  const headers = { ...options.headers };
  if (options.method && options.method !== "GET" && session) {
    headers["X-CSRF-Token"] = session.csrfToken;
  }
  const response = await fetch(path, { ...options, headers, credentials: "same-origin" });
  const payload = await response.json();
  if (!response.ok) {
    if (response.status === 401) signedOut();
    const reference = payload.requestId ? ` Reference: ${payload.requestId}.` : "";
    throw new Error((payload.error || `Request failed (${response.status}).`) + reference);
  }
  return payload;
}

function switchView(view) {
  for (const name of ["create", "library", "environment"]) {
    element(`${name}-view`).hidden = name !== view;
  }
  for (const button of document.querySelectorAll(".nav-item")) {
    const selected = button.dataset.view === view;
    button.classList.toggle("selected", selected);
    if (selected) button.setAttribute("aria-current", "page");
    else button.removeAttribute("aria-current");
  }
  if (view === "library") loadLibrary();
}
document.querySelectorAll("[data-view]").forEach((button) => {
  button.addEventListener("click", () => switchView(button.dataset.view));
});

function updateGenerate() {
  element("generate").disabled = submitting || !environment?.generationEnabled;
  element("generate").textContent = submitting ? "Submitting…" : "Generate video";
}

function renderEnvironment(data) {
  environment = data;
  element("compute-badge").textContent = data.generationEnabled ? "Generation armed" : `Compute: ${data.computeState}`;
  element("compute-badge").className = `status-badge ${data.generationEnabled ? "success" : "neutral"}`;
  element("connection-notice").className = data.generationEnabled ? "notice" : "notice warning";
  element("connection-notice").textContent = data.generationEnabled
    ? "Workspace and private storage connected. The operator-armed cluster accepts jobs and scales from zero when work is queued."
    : data.prepared
      ? (data.computeState === "Not configured"
        ? "Workspace and private storage connected. The GPU cluster has not been configured yet; this is different from a configured cluster with zero running nodes."
        : "Workspace and private storage connected. The GPU cluster is configured, but submission is not armed by the operator.")
      : "Workspace and private storage connected. WAN asset preparation is not complete; generation is disabled.";
  element("generation-help").textContent = data.generationEnabled
    ? "This starts a billed GPU job. No automatic retries."
    : data.prepared
      ? "Generation remains disabled until the operator completes network/egress checks and runs Start -NoPortal with the required approvals."
      : "Complete Prepare and restart Portal to load the verified WAN release. No GPU compute is needed to prepare assets.";
  element("output-format").textContent = data.profile
    ? `${data.profile.width} × ${data.profile.height} · ${data.profile.fps} fps`
    : "Available after preparation";
  if (data.profile && !element("duration").dataset.initialized) {
    element("duration").value = data.profile.durationSeconds;
    element("duration").step = data.profile.durationStep;
    element("duration").dataset.initialized = "true";
  }
  element("job-workspace").textContent = data.workspace;
  const details = [
    ["Azure ML workspace", data.workspace], ["Resource group", data.resourceGroup],
    ["Private Blob Storage", data.storage], ["Compute target", data.compute],
    ["Cluster provisioning", data.computeState], ["Prepared release", data.version || "Not prepared"],
    ["GPU scaling", "Configured for zero minimum / one maximum node; jobs trigger allocation"],
    ["Submission gate", data.armed ? "Operator-armed" : "Not armed"],
    ["Server job timeout", `At most ${data.maxJobMinutes} minutes per job`],
    ["Connections checked", new Date(data.checkedAt).toLocaleString()],
  ];
  element("environment-details").replaceChildren(...details.map(([label, value]) => {
    const row = document.createElement("div");
    const term = document.createElement("dt");
    const description = document.createElement("dd");
    term.textContent = label;
    description.textContent = value;
    row.append(term, description);
    return row;
  }));
  updateGenerate();
}

async function loadStatus() {
  if (!session) return;
  element("refresh-environment").disabled = true;
  try {
    renderEnvironment(await api("/api/status"));
    if (element("global-error").dataset.kind === "connection") showError("");
  } catch (error) {
    environment = null;
    element("connection-notice").className = "notice error";
    element("connection-notice").textContent = "Live connection checks failed. Generation is disabled until checks succeed.";
    element("compute-badge").textContent = "State unavailable";
    element("compute-badge").className = "status-badge error";
    showError(error.message, "connection");
    updateGenerate();
  } finally {
    element("refresh-environment").disabled = false;
  }
}
element("refresh-environment").addEventListener("click", loadStatus);

function imageChanged() {
  if (imageUrl) URL.revokeObjectURL(imageUrl);
  imageUrl = null;
  const file = element("reference-image").files[0];
  element("image-preview").hidden = !file;
  element("upload-placeholder").hidden = !!file;
  element("remove-image").hidden = !file;
  element("image-name").textContent = file ? `${file.name} · ${(file.size / 1048576).toFixed(1)} MB` : "No image selected";
  if (file) {
    imageUrl = URL.createObjectURL(file);
    element("image-preview").src = imageUrl;
  } else {
    element("image-preview").removeAttribute("src");
  }
}
element("reference-image").addEventListener("change", imageChanged);
element("remove-image").addEventListener("click", () => {
  element("reference-image").value = "";
  imageChanged();
  element("reference-image").focus();
});
for (const event of ["dragenter", "dragover"]) {
  element("drop-area").addEventListener(event, (e) => { e.preventDefault(); element("drop-area").classList.add("dragover"); });
}
for (const event of ["dragleave", "drop"]) {
  element("drop-area").addEventListener(event, () => element("drop-area").classList.remove("dragover"));
}
element("drop-area").addEventListener("drop", (event) => {
  event.preventDefault();
  if (event.dataTransfer.files.length !== 1) { showError("Choose one reference image."); return; }
  element("reference-image").files = event.dataTransfer.files;
  imageChanged();
});
element("prompt").addEventListener("input", () => {
  element("prompt-count").textContent = `${element("prompt").value.length} / 4000`;
});

function trustedUrl(value, kind) {
  if (!value) return null;
  try {
    const url = new URL(value);
    const host = kind === "studio" ? "ml.azure.com" : `${environment?.storage}.blob.core.windows.net`;
    return url.protocol === "https:" && url.hostname === host ? url.href : null;
  } catch { return null; }
}

function showJob(job) {
  activeJob = job;
  sessionStorage.setItem("wan-current-job", job.name);
  element("job-info").hidden = false;
  element("job-name").textContent = job.name;
  element("job-badge").textContent = job.status || "Submitted";
  element("job-badge").className = `status-badge ${job.status === "Completed" ? "success" : terminal.has(job.status) ? "error" : "neutral"}`;
  element("job-message").textContent = terminal.has(job.status)
    ? (job.status === "Completed" ? "Generation completed. Loading the saved video…" : `The job ended ${job.status}. Review it in Azure ML; it will not be resubmitted.`)
    : "Azure ML accepted the job. Queuing, provisioning and generation can take several minutes.";
  const studio = trustedUrl(job.studio_url, "studio");
  element("studio-link").hidden = !studio;
  if (studio) element("studio-link").href = studio;
}

async function pollJob(name) {
  clearTimeout(pollTimer);
  try {
    const job = await api(`/api/jobs/${encodeURIComponent(name)}`);
    showJob(job);
    if (job.status === "Completed") {
      const gallery = await api("/api/gallery?refresh=1");
      const item = gallery.items.find((video) => video.job_name === name);
      const video = trustedUrl(item?.video_url, "blob");
      if (video) {
        element("output-empty").hidden = true;
        element("output-video").hidden = false;
        element("output-video").src = video;
        element("download-video").href = video;
        element("download-video").hidden = false;
        element("job-message").textContent = "Your clip is ready for review.";
      } else {
        element("job-message").textContent = "The job completed, but its video is not available in the library yet. Refresh the library or inspect the job in Azure ML.";
      }
    } else if (!terminal.has(job.status)) {
      pollTimer = setTimeout(() => pollJob(name), 10000);
    }
  } catch (error) {
    showError(error.message);
    element("job-message").textContent = "Job status could not be read. Check connectivity, then reload; do not submit the same request again.";
  }
}

element("create-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (submitting || !environment?.generationEnabled || !event.target.reportValidity()) return;
  const file = element("reference-image").files[0];
  if (!file || file.size > environment.maxUploadMb * 1048576) {
    showError(`Choose a reference image no larger than ${environment.maxUploadMb} MB.`);
    return;
  }
  submitting = true;
  updateGenerate();
  showError("");
  element("submit-progress").hidden = false;
  element("job-message").textContent = "Uploading the reference image and submitting one job…";
  const form = new FormData(event.target);
  form.set("profile", "wan");
  try {
    const result = await api("/api/submit", { method: "POST", body: form });
    element("output-video").pause();
    element("output-video").removeAttribute("src");
    element("output-video").hidden = true;
    element("output-empty").hidden = false;
    element("download-video").hidden = true;
    showJob(result.job);
    pollJob(result.job.name);
  } catch (error) {
    showError(error.message);
    element("job-message").textContent = "Submission was not confirmed. Check Azure ML before retrying; no automatic retry was made.";
  } finally {
    submitting = false;
    element("submit-progress").hidden = true;
    updateGenerate();
  }
});
element("copy-job").addEventListener("click", async () => {
  if (!activeJob) return;
  try {
    await navigator.clipboard.writeText(activeJob.name);
    element("copy-job").textContent = "Copied";
    setTimeout(() => { element("copy-job").textContent = "Copy ID"; }, 2000);
  } catch { showError("Clipboard access is unavailable. Select the job ID to copy it."); }
});

async function loadLibrary() {
  if (!session) return;
  element("refresh-library").disabled = true;
  element("library-status").textContent = "Loading completed videos from Azure ML and private storage…";
  element("library-empty").hidden = true;
  try {
    const data = await api("/api/gallery?refresh=1");
    element("library-grid").replaceChildren();
    element("library-empty").hidden = data.items.length > 0;
    element("library-status").textContent = `${data.count} completed videos found across ${data.scanned_jobs} recent jobs.`;
    for (const item of data.items) {
      const article = document.createElement("article");
      article.className = "library-item";
      const video = document.createElement("video");
      video.controls = true;
      video.preload = "metadata";
      const url = trustedUrl(item.video_url, "blob");
      if (!url) throw new Error("A video URL does not belong to this workspace's private storage.");
      video.src = url;
      const body = document.createElement("div");
      body.className = "item-body";
      const title = document.createElement("h2");
      title.textContent = item.display_name || item.job_name;
      const prompt = document.createElement("p");
      prompt.textContent = item.prompt || "No prompt was recorded.";
      const link = document.createElement("a");
      link.textContent = "Open video";
      link.href = url;
      link.target = "_blank";
      link.rel = "noopener noreferrer";
      body.append(title, prompt, link);
      article.append(video, body);
      element("library-grid").append(article);
    }
  } catch (error) {
    element("library-status").textContent = "The video library could not be loaded.";
    showError(error.message);
  } finally {
    element("refresh-library").disabled = false;
  }
}
element("refresh-library").addEventListener("click", loadLibrary);
element("signout").addEventListener("click", async () => {
  try {
    await api("/auth/logout", { method: "POST" });
    sessionStorage.removeItem("wan-current-job");
    signedOut();
  } catch (error) { showError(error.message); }
});

async function initialize() {
  if (new URLSearchParams(location.search).has("signin")) {
    element("signin-error").textContent = "Sign-in was not accepted. Confirm your tenant and membership in the approved creator group, then start a new sign-in.";
    element("signin-error").hidden = false;
    history.replaceState(null, "", location.pathname);
  }
  try {
    session = await api("/api/session");
    element("account-name").textContent = session.name;
    element("signin-panel").hidden = true;
    element("workspace").hidden = false;
    element("signout").hidden = false;
    await loadStatus();
    if (location.pathname === "/videos") switchView("library");
    const job = sessionStorage.getItem("wan-current-job");
    if (job) pollJob(job);
  } catch (error) {
    signedOut();
    if (!error.message.startsWith("Sign in with")) {
      element("signin-error").textContent = error.message;
      element("signin-error").hidden = false;
    }
  }
}
initialize();
setInterval(loadStatus, 30000);
