"use strict";

const element = (id) => document.getElementById(id);
const MAX_SCENES = 5;
const terminal = new Set(["Completed", "Failed", "Canceled", "Cancelled", "NotResponding"]);
let session = null;
let environment = null;
let pollTimer = null;
let submitting = false;
let batch = null;
let selectedJob = null;
let sceneSerial = 0;

const plural = (count, word) => `${count} ${word}${count === 1 ? "" : "s"}`;

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
    const error = new Error((payload.error || `Request failed (${response.status}).`) + reference);
    error.status = response.status;
    throw error;
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

const sceneItems = () => [...element("scene-list").children];
const takes = () => Number(document.querySelector('input[name="takes"]:checked').value);
const batchBusy = () => submitting || (batch && !batch.done);

function updateGenerate() {
  const total = sceneItems().length * takes();
  element("generate").disabled = batchBusy() || !environment?.generationEnabled;
  element("generate").textContent = batchBusy() ? "Submitting…" : `Generate ${plural(total, "video")}`;
}

function updateSummary() {
  const scenes = sceneItems().length;
  const total = scenes * takes();
  element("batch-summary").textContent = `${plural(scenes, "scene")} × ${plural(takes(), "video")} = ${plural(total, "GPU job")}`;
  updateGenerate();
}

function imageChanged(item) {
  if (item.dataset.preview) URL.revokeObjectURL(item.dataset.preview);
  delete item.dataset.preview;
  const file = item.querySelector("input[type=file]").files[0];
  const preview = item.querySelector(".image-preview");
  preview.hidden = !file;
  item.querySelector(".upload-placeholder").hidden = !!file;
  item.querySelector(".remove-image").hidden = !file;
  item.querySelector(".image-name").textContent = file ? `${file.name} · ${(file.size / 1048576).toFixed(1)} MB` : "No image selected";
  if (file) {
    item.dataset.preview = URL.createObjectURL(file);
    preview.src = item.dataset.preview;
  } else {
    preview.removeAttribute("src");
  }
}

function renumberScenes() {
  const scenes = sceneItems();
  scenes.forEach((item, index) => {
    const first = index === 0;
    item.querySelector(".scene-title").textContent = `Scene ${index + 1}`;
    item.querySelector(".remove-scene").hidden = first;
    item.querySelector(".image-requirement").textContent = first ? "Required" : "Optional · reuses Scene 1’s image";
    item.querySelector("input[type=file]").required = first;
  });
  element("scene-list").classList.toggle("single", scenes.length === 1);
  element("add-scene").hidden = scenes.length >= MAX_SCENES;
  element("scene-count").textContent = `${scenes.length} of ${MAX_SCENES}`;
  updateSummary();
}

function addScene() {
  if (sceneItems().length >= MAX_SCENES) return;
  const item = element("scene-template").content.firstElementChild.cloneNode(true);
  const id = `scene-${++sceneSerial}`;
  const input = item.querySelector("input[type=file]");
  const prompt = item.querySelector("textarea");
  const area = item.querySelector(".upload-area");
  input.id = `${id}-image`;
  area.htmlFor = input.id;
  prompt.id = `${id}-prompt`;
  item.querySelector(".prompt-label").htmlFor = prompt.id;
  input.addEventListener("change", () => imageChanged(item));
  item.querySelector(".remove-image").addEventListener("click", () => {
    input.value = "";
    imageChanged(item);
    input.focus();
  });
  for (const name of ["dragenter", "dragover"]) {
    area.addEventListener(name, (event) => { event.preventDefault(); area.classList.add("dragover"); });
  }
  for (const name of ["dragleave", "drop"]) {
    area.addEventListener(name, () => area.classList.remove("dragover"));
  }
  area.addEventListener("drop", (event) => {
    event.preventDefault();
    if (event.dataTransfer.files.length !== 1) { showError("Drop one reference image per scene."); return; }
    input.files = event.dataTransfer.files;
    imageChanged(item);
  });
  prompt.addEventListener("input", () => {
    item.querySelector(".prompt-count").textContent = `${prompt.value.length} / 4000`;
  });
  item.querySelector(".remove-scene").addEventListener("click", () => {
    if (item.dataset.preview) URL.revokeObjectURL(item.dataset.preview);
    item.remove();
    renumberScenes();
    element("add-scene").hidden ? element("generate").focus() : element("add-scene").focus();
  });
  element("scene-list").append(item);
  renumberScenes();
  if (sceneItems().length > 1) prompt.focus();
}
element("add-scene").addEventListener("click", addScene);
document.querySelectorAll('input[name="takes"]').forEach((input) => input.addEventListener("change", updateSummary));
element("duration").addEventListener("input", () => {
  element("duration-value").textContent = `${element("duration").value} s`;
});

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
    ? "This starts billed GPU jobs. No automatic retries."
    : data.prepared
      ? "Generation remains disabled until the operator completes network/egress checks and runs Start -NoPortal with the required approvals."
      : "Complete Prepare and restart Portal to load the verified WAN release. No GPU compute is needed to prepare assets.";
  const details = [
    ["Azure ML workspace", data.workspace], ["Resource group", data.resourceGroup],
    ["Private Blob Storage", data.storage], ["Compute target", data.compute],
    ["Cluster provisioning", data.computeState], ["Prepared release", data.version || "Not prepared"],
    ["GPU scaling", "Configured for zero minimum / one maximum node; jobs trigger allocation and run one at a time"],
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

function trustedUrl(value, kind) {
  if (!value) return null;
  try {
    const url = new URL(value);
    const host = kind === "studio" ? "ml.azure.com" : `${environment?.storage}.blob.core.windows.net`;
    return url.protocol === "https:" && url.hostname === host ? url.href : null;
  } catch { return null; }
}

function saveBatch() {
  // Signed video URLs stay in memory only; they are fetched again after a reload.
  const jobs = batch.jobs.map(({ video, videoChecks, ...job }) => job);
  sessionStorage.setItem("wan-current-batch", JSON.stringify({ ...batch, jobs }));
}

function showVideo(job) {
  selectedJob = job?.name || null;
  element("output-video").pause();
  element("output-empty").hidden = !!job;
  element("output-video").hidden = !job;
  element("download-video").hidden = !job;
  if (job) {
    element("output-video").src = job.video;
    element("download-video").href = job.video;
  } else {
    element("output-video").removeAttribute("src");
  }
}

function jobBadge(status) {
  const badge = document.createElement("span");
  badge.className = `status-badge ${status === "Completed" ? "success" : terminal.has(status) ? "error" : "neutral"}`;
  badge.textContent = status || "Submitted";
  return badge;
}

function renderJobs() {
  const jobs = batch?.jobs || [];
  element("job-list").hidden = !jobs.length;
  if (!batch) return;
  if (!selectedJob || !jobs.some((job) => job.name === selectedJob && job.video)) {
    showVideo(jobs.find((job) => job.video) || null);
  }
  const completed = jobs.filter((job) => job.status === "Completed").length;
  const failed = jobs.filter((job) => terminal.has(job.status) && job.status !== "Completed").length;
  const finished = batch.done && jobs.every((job) => terminal.has(job.status));
  element("job-badge").textContent = batch.done ? `${completed} of ${batch.total} complete` : `Submitting ${jobs.length} of ${batch.total}`;
  element("job-badge").className = `status-badge ${failed || batch.error ? "error" : finished ? "success" : "neutral"}`;
  element("submit-progress").hidden = batch.done;
  element("job-message").textContent = batch.error
    ? batch.error
    : !batch.done
      ? "The studio is submitting your jobs to Azure ML one at a time. You can leave this page; submission continues on the server."
      : finished
        ? (completed ? "All jobs finished. Select a completed video to preview it." : "No job completed. Review the jobs in Azure ML; they will not be resubmitted.")
        : "Azure ML accepted the jobs. The single GPU node runs them one after another; allow 15–30 minutes per video, including queuing and scale-up.";
  element("job-list").replaceChildren(...jobs.map((job) => {
    const row = document.createElement("li");
    row.className = `job-row${job.name === selectedJob ? " selected" : ""}`;
    const select = document.createElement("button");
    select.type = "button";
    select.className = "job-select";
    select.textContent = `Scene ${job.scene} · Video ${job.take}`;
    select.disabled = !job.video;
    if (job.name === selectedJob) select.setAttribute("aria-current", "true");
    select.addEventListener("click", () => { showVideo(job); renderJobs(); });
    const name = document.createElement("code");
    name.textContent = job.name;
    row.append(select, jobBadge(job.status), name);
    const studio = trustedUrl(job.studio_url, "studio");
    if (studio) {
      const link = document.createElement("a");
      link.href = studio;
      link.target = "_blank";
      link.rel = "noopener noreferrer";
      link.textContent = "Open in Azure ML";
      row.append(link);
    }
    return row;
  }));
  updateGenerate();
}

async function refreshJobs() {
  clearTimeout(pollTimer);
  for (const job of batch.jobs.filter((item) => !terminal.has(item.status))) {
    try {
      Object.assign(job, await api(`/api/jobs/${encodeURIComponent(job.name)}`));
    } catch (error) {
      showError(`${error.message} Job status could not be read; do not submit the same request again.`);
      break;
    }
  }
  const missing = batch.jobs.filter((job) => job.status === "Completed" && !job.video && (job.videoChecks || 0) < 3);
  if (missing.length) {
    try {
      const gallery = await api("/api/gallery?refresh=1");
      for (const job of missing) {
        job.videoChecks = (job.videoChecks || 0) + 1;
        job.video = trustedUrl(gallery.items.find((item) => item.job_name === job.name)?.video_url, "blob");
      }
    } catch (error) { showError(error.message); }
  }
  saveBatch();
  renderJobs();
  if (batch.jobs.some((job) => !terminal.has(job.status) ||
      (job.status === "Completed" && !job.video && (job.videoChecks || 0) < 3))) {
    pollTimer = setTimeout(refreshJobs, 15000);
  }
}

async function pollBatch() {
  clearTimeout(pollTimer);
  if (!batch) return;
  if (!batch.done) {
    try {
      const state = await api(`/api/batches/${encodeURIComponent(batch.id)}`);
      for (const job of state.jobs) {
        if (!batch.jobs.some((known) => known.name === job.name)) batch.jobs.push(job);
      }
      Object.assign(batch, { total: state.total, done: state.done, error: state.error });
    } catch (error) {
      if (error.status === 401) return;
      batch.done = true;
      batch.error = `${error.message} Jobs listed here were submitted; check the video library for any others.`;
    }
    saveBatch();
    renderJobs();
    if (!batch.done) {
      pollTimer = setTimeout(pollBatch, 2000);
      return;
    }
  }
  await refreshJobs();
}

element("create-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (batchBusy() || !environment?.generationEnabled || !event.target.reportValidity()) return;
  const scenes = sceneItems();
  const files = scenes.map((item) => item.querySelector("input[type=file]").files[0]);
  const bytes = files.reduce((sum, file) => sum + (file?.size || 0), 0);
  if (!files[0] || bytes > environment.maxUploadMb * 1048576) {
    showError(`Add a Scene 1 image. All reference images together must be no larger than ${environment.maxUploadMb} MB.`);
    return;
  }
  const form = new FormData();
  form.set("profile", "wan");
  scenes.forEach((item, index) => {
    form.append("prompt", item.querySelector("textarea").value);
    if (files[index]) form.append(`input_image_${index}`, files[index]);
  });
  for (const name of ["aspect_ratio", "duration_seconds", "takes", "negative_prompt"]) {
    form.set(name, event.target.elements[name].value);
  }
  submitting = true;
  updateGenerate();
  showError("");
  element("submit-progress").hidden = false;
  element("job-message").textContent = "Uploading reference images…";
  try {
    const result = await api("/api/submit", { method: "POST", body: form });
    batch = { ...result.batch, jobs: [...result.batch.jobs] };
    showVideo(null);
    saveBatch();
    renderJobs();
    pollBatch();
  } catch (error) {
    showError(error.message);
    element("submit-progress").hidden = true;
    element("job-message").textContent = "Submission was not confirmed. Check Azure ML before retrying; no automatic retry was made.";
  } finally {
    submitting = false;
    updateGenerate();
  }
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
      if (item.width && item.height) video.style.aspectRatio = `${item.width} / ${item.height}`;
      const body = document.createElement("div");
      body.className = "item-body";
      const title = document.createElement("h2");
      title.textContent = item.display_name || item.job_name;
      const meta = document.createElement("span");
      meta.className = "item-meta";
      meta.textContent = [item.width && item.height ? `${item.width} × ${item.height}` : null,
        item.duration_seconds ? `${item.duration_seconds} s` : null].filter(Boolean).join(" · ");
      const prompt = document.createElement("p");
      prompt.textContent = item.prompt || "No prompt was recorded.";
      const link = document.createElement("a");
      link.textContent = "Open video";
      link.href = url;
      link.target = "_blank";
      link.rel = "noopener noreferrer";
      body.append(title, meta, prompt, link);
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
    sessionStorage.removeItem("wan-current-batch");
    signedOut();
  } catch (error) { showError(error.message); }
});

async function initialize() {
  addScene();
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
    const saved = sessionStorage.getItem("wan-current-batch");
    if (saved) {
      batch = JSON.parse(saved);
      renderJobs();
      pollBatch();
    }
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
