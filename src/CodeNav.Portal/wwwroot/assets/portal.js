import { createBuildViewModel, formatNumber } from "./portal-model.js";

document.documentElement.classList.add("js");

const elements = {
  body: document.body,
  workspaceSelect: document.querySelector("#workspace-select"),
  connectionLabel: document.querySelector("#connection-label"),
  motionToggle: document.querySelector("#motion-toggle"),
  motionLabel: document.querySelector(".motion-toggle__label"),
  workspaceState: document.querySelector("#workspace-state"),
  workspaceSummary: document.querySelector("#workspace-summary"),
  instanceCount: document.querySelector("#instance-count"),
  semanticState: document.querySelector("#semantic-state"),
  dataState: document.querySelector("#data-state"),
  orbitState: document.querySelector("#orbit-state"),
  buildTitle: document.querySelector("#build-title"),
  buildPanel: document.querySelector("#build-panel"),
  phaseRail: document.querySelector("#phase-rail"),
  progressFiles: document.querySelector("#progress-files"),
  progressPercent: document.querySelector("#progress-percent"),
  progressTrack: document.querySelector("#progress-track"),
  progressFill: document.querySelector("#progress-fill"),
  progressGlow: document.querySelector("#progress-glow"),
  buildRate: document.querySelector("#build-rate"),
  buildElapsed: document.querySelector("#build-elapsed"),
  buildEta: document.querySelector("#build-eta"),
  buildSkipped: document.querySelector("#build-skipped"),
  queryCount: document.querySelector("#query-count"),
  queryP95: document.querySelector("#query-p95"),
  semanticScore: document.querySelector("#semantic-score"),
  semanticLabel: document.querySelector("#semantic-label"),
  semanticDetail: document.querySelector("#semantic-detail"),
  freshnessLabel: document.querySelector("#freshness-label"),
  indexEpoch: document.querySelector("#index-epoch"),
  healthLabel: document.querySelector("#health-label"),
  healthDetail: document.querySelector("#health-detail"),
  activityList: document.querySelector("#activity-list"),
  instancesPanelCount: document.querySelector("#instances-panel-count"),
  instanceIndexId: document.querySelector("#instance-index-id"),
  instanceList: document.querySelector("#instance-list"),
  portalVersion: document.querySelector("#portal-version"),
  dialog: document.querySelector("#operation-dialog"),
  dialogTitle: document.querySelector("#dialog-title"),
  dialogContent: document.querySelector("#dialog-content")
};

const state = {
  bootstrap: null,
  operations: [],
  events: [],
  selectedWorkspaceId: null,
  motionPaused: false,
  reducedMotion: window.matchMedia("(prefers-reduced-motion: reduce)").matches
};

const token = readSessionToken();

initialize().catch((error) => {
  console.error("Portal initialization failed", error);
  elements.body.dataset.connection = "error";
  elements.connectionLabel.textContent = "Offline";
  elements.workspaceState.textContent = "OFFLINE";
  elements.workspaceSummary.textContent = token
    ? "The local portal did not return an operational snapshot."
    : "Open the session URL printed by PhoenixCodeNav.Portal to connect.";
});

async function initialize() {
  bindMotionControl();
  bindNavigation();
  observeReveals();

  const [bootstrap, operations, events] = await Promise.all([
    fetchJson("/api/v1/bootstrap"),
    fetchJson("/api/v1/operations"),
    fetchJson("/api/v1/events")
  ]);

  state.bootstrap = bootstrap;
  state.operations = operations.items ?? [];
  state.events = events.items ?? [];
  state.selectedWorkspaceId = bootstrap.workspaces?.[0]?.workspaceId ?? null;

  renderWorkspacePicker();
  renderSelectedWorkspace();

  elements.portalVersion.textContent = `v${bootstrap.portal.version}`;
  elements.connectionLabel.textContent = "Live";
  elements.body.dataset.connection = "ready";

  window.setTimeout(() => revealVisibleElements(), 60);
}

function readSessionToken() {
  const hash = new URLSearchParams(window.location.hash.slice(1));
  const incoming = hash.get("token");
  if (incoming) {
    window.sessionStorage.setItem("phoenix.portal.token", incoming);
    window.history.replaceState(null, "", `${window.location.pathname}${window.location.search}`);
    return incoming;
  }

  return window.sessionStorage.getItem("phoenix.portal.token");
}

async function fetchJson(path) {
  const response = await fetch(path, {
    headers: token ? { Authorization: `Bearer ${token}` } : {},
    cache: "no-store"
  });

  if (!response.ok) {
    const error = await response.json().catch(() => null);
    throw new Error(error?.error?.message ?? `Portal request failed (${response.status})`);
  }

  return response.json();
}

function renderWorkspacePicker() {
  elements.workspaceSelect.replaceChildren();
  for (const workspace of state.bootstrap.workspaces) {
    const option = document.createElement("option");
    option.value = workspace.workspaceId;
    option.textContent = workspace.name;
    elements.workspaceSelect.append(option);
  }

  elements.workspaceSelect.value = state.selectedWorkspaceId;
  elements.workspaceSelect.addEventListener("change", () => {
    state.selectedWorkspaceId = elements.workspaceSelect.value;
    renderSelectedWorkspace();
  });
}

function renderSelectedWorkspace() {
  const workspace = state.bootstrap.workspaces.find((item) => item.workspaceId === state.selectedWorkspaceId);
  if (!workspace)
    return;

  const index = state.bootstrap.indexes.find((item) => item.indexId === workspace.indexId);
  const instances = state.bootstrap.instances.filter((item) => item.workspaceId === workspace.workspaceId);
  const operations = state.operations.filter((item) => item.workspaceId === workspace.workspaceId);
  const dataComplete = Boolean(state.bootstrap.dataComplete);
  const build = index?.currentBuild ?? null;
  const workspaceState = build ? "indexing" : workspace.state;

  elements.body.dataset.workspaceState = workspaceState;
  elements.workspaceState.textContent = workspaceState.toUpperCase();
  elements.workspaceSummary.textContent = build
    ? `${workspace.name} is moving through ${formatToken(build.phase)}. The shared index is live and ${instances.length} agents remain connected.`
    : `${workspace.name} is current and ready. The index is watching for changes and semantic context is available on demand.`;
  elements.instanceCount.textContent = String(instances.length);
  elements.semanticState.textContent = summarizeSemanticState(instances);
  elements.dataState.textContent = dataComplete ? "complete" : "partial";
  elements.orbitState.textContent = build ? "building" : "ready";

  renderBuild(index, build);
  renderSignals(index, instances, operations, dataComplete);
  renderActivity(operations);
  renderInstances(index, instances);
}

function renderBuild(index, build) {
  const phases = build?.phases ?? [
    { id: "scanning", label: "Scan", state: "complete", durationMs: null },
    { id: "parsing_projects", label: "Projects", state: "complete", durationMs: null },
    { id: "indexing_files", label: "Symbols", state: "complete", durationMs: null },
    { id: "finalizing", label: "Publish", state: "complete", durationMs: null }
  ];
  const view = createBuildViewModel(build);

  elements.buildTitle.textContent = build?.phaseLabel ?? "Index ready";
  elements.buildPanel.querySelector(".build-panel__live span").textContent = build ? "LIVE" : "READY";
  elements.phaseRail.replaceChildren(...phases.map(createPhase));
  elements.progressFiles.textContent = view.filesLabel;
  elements.progressPercent.textContent = view.progressLabel;
  elements.progressTrack.setAttribute("aria-valuetext", view.progressAriaLabel);
  elements.progressTrack.classList.toggle("progress-track--indeterminate", !view.determinate);

  if (view.determinate) {
    elements.progressTrack.setAttribute("aria-valuenow", String(Math.round(view.percent)));
    elements.progressFill.setAttribute("width", String(Math.round(view.progress * 1000)));
    elements.progressGlow.style.left = `calc(${Math.min(99, view.percent)}% - 10px)`;
  } else {
    elements.progressTrack.removeAttribute("aria-valuenow");
    elements.progressFill.setAttribute("width", "280");
    elements.progressGlow.style.removeProperty("left");
  }

  elements.buildRate.textContent = view.rateLabel;
  elements.buildElapsed.textContent = view.elapsedLabel;
  elements.buildEta.textContent = view.etaLabel;
  elements.buildSkipped.textContent = view.skippedLabel;

  if (!build) {
    elements.progressGlow.style.left = "calc(100% - 10px)";
    elements.buildPanel.classList.add("is-ready");
  } else {
    elements.buildPanel.classList.remove("is-ready");
  }

  function createPhase(phase) {
    const item = document.createElement("li");
    item.className = `is-${phase.state}`;
    item.textContent = phase.label;

    const duration = document.createElement("small");
    duration.textContent = phase.durationMs == null
      ? phase.state === "pending" ? "waiting" : "complete"
      : formatDuration(phase.durationMs);
    item.append(duration);
    return item;
  }
}

function renderSignals(index, instances, operations, dataComplete) {
  const completed = operations.filter((item) => item.state === "complete");
  const semanticCompleted = completed.filter((item) => item.category === "semantic");
  const exact = semanticCompleted.filter((item) => item.confidence === "exact");
  const durations = completed.map((item) => item.durationMs).sort((a, b) => a - b);
  const p95 = durations.length ? durations[Math.min(durations.length - 1, Math.floor(durations.length * 0.95))] : null;
  const warmCount = instances.filter((item) => item.semanticState === "warm").length;
  const allConnected = instances.every((item) => item.connectionState === "connected");

  animateNumber(elements.queryCount, operations.length);
  elements.queryP95.textContent = p95 == null ? "—" : formatDuration(p95);
  elements.semanticScore.textContent = semanticCompleted.length ? `${exact.length}/${semanticCompleted.length}` : "—";
  elements.semanticLabel.textContent = warmCount === instances.length ? "Warm" : warmCount ? "Partially warm" : "Cold";
  elements.semanticDetail.textContent = `${warmCount} of ${instances.length} instances have warm semantic context`;
  elements.freshnessLabel.textContent = index?.freshness === "head" ? "HEAD" : "WORKTREE";
  elements.indexEpoch.textContent = index?.epoch == null ? "—" : `#${formatNumber(index.epoch)}`;
  elements.healthLabel.textContent = allConnected && dataComplete ? "All systems nominal" : "Attention needed";
  elements.healthDetail.textContent = allConnected
    ? `${instances.filter((item) => item.role === "writer").length} writer · ${instances.filter((item) => item.role === "follower").length} followers`
    : "One or more instances are stale or disconnected";
}

function renderActivity(operations) {
  elements.activityList.replaceChildren();

  for (const operation of operations.slice(0, 6)) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = `activity-item${operation.state === "running" ? " is-running" : ""}`;
    button.dataset.confidence = operation.confidence;
    button.addEventListener("click", () => openOperation(operation));

    const icon = element("span", "activity-item__icon", abbreviateTool(operation.tool));
    const tool = element("span", "activity-item__tool");
    tool.append(
      element("strong", "", formatToken(operation.tool)),
      element("span", "", `${formatToken(operation.category)} · ${formatToken(operation.coldState)}`)
    );

    const summary = element("span", "activity-item__summary");
    summary.append(
      element("strong", "", operation.summary),
      element("span", "", operation.reason ? formatToken(operation.reason) : "completed without degradation")
    );

    const duration = element("span", "activity-item__duration", formatDuration(operation.durationMs));
    const confidence = element(
      "span",
      `confidence-pill confidence-pill--${operation.confidence}`,
      operation.confidence
    );

    button.append(icon, tool, summary, duration, confidence);
    elements.activityList.append(button);
  }

  if (!operations.length) {
    const empty = element("p", "empty-state", "No recent operations for this workspace.");
    elements.activityList.append(empty);
  }
}

function renderInstances(index, instances) {
  elements.instancesPanelCount.textContent = String(instances.length);
  elements.instanceIndexId.textContent = index?.epoch == null ? "—" : String(index.epoch).slice(-2).padStart(2, "0");
  elements.instanceList.replaceChildren();

  for (const instance of instances) {
    const item = element("article", "instance-item");
    item.dataset.role = instance.role;

    const mark = element("span", "instance-item__mark", instance.role === "writer" ? "W" : "F");
    const copy = element("span", "instance-item__copy");
    copy.append(
      element("strong", "", instance.displayName),
      element("span", "", `${instance.role} · semantics ${instance.semanticState}`)
    );
    const connection = element("span", "instance-item__state");
    connection.append(element("i", ""), document.createTextNode(instance.connectionState));

    item.append(mark, copy, connection);
    elements.instanceList.append(item);
  }
}

function openOperation(operation) {
  elements.dialogTitle.textContent = formatToken(operation.tool);
  elements.dialogContent.replaceChildren();

  const summary = element("section", "drawer-summary");
  summary.append(
    element("span", "", `${operation.confidence} · ${formatDuration(operation.durationMs)}`),
    element("p", "", operation.summary)
  );

  const grid = element("section", "drawer-grid");
  const facts = [
    ["Outcome", formatToken(operation.outcome)],
    ["Cold state", formatToken(operation.coldState)],
    ["Projects", `${operation.counts.loaded} / ${operation.counts.requested} loaded`],
    ["Reason", operation.reason ? formatToken(operation.reason) : "None"]
  ];
  for (const [label, value] of facts) {
    const fact = document.createElement("div");
    fact.append(element("span", "", label), element("strong", "", value));
    grid.append(fact);
  }

  const waterfall = element("section", "waterfall");
  waterfall.append(element("h3", "", "Phase timing"));
  const timings = [
    ["Workspace wait", operation.timings.gateWaitMs],
    ["Fingerprint", operation.timings.fingerprintMs],
    ["Graph topology", operation.timings.topologyMs],
    ["Project load", operation.timings.projectLoadMs]
  ];
  const max = Math.max(1, ...timings.map(([, duration]) => duration));
  for (const [label, duration] of timings) {
    const row = element("div", "waterfall__row");
    const track = element("span", "waterfall__track");
    const bar = document.createElement("i");
    track.append(bar);
    row.append(
      element("span", "", label),
      track,
      element("b", "", formatDuration(duration))
    );
    waterfall.append(row);
    window.requestAnimationFrame(() => {
      bar.style.width = `${Math.max(2, (duration / max) * 100)}%`;
    });
  }

  elements.dialogContent.append(summary, grid, waterfall);
  elements.dialog.showModal();
}

function bindMotionControl() {
  elements.motionToggle.addEventListener("click", () => {
    state.motionPaused = !state.motionPaused;
    elements.body.classList.toggle("motion-paused", state.motionPaused);
    elements.motionToggle.setAttribute("aria-pressed", String(state.motionPaused));
    elements.motionLabel.textContent = state.motionPaused ? "Play motion" : "Pause motion";
  });
}

function bindNavigation() {
  const links = [...document.querySelectorAll(".primary-nav__item")];
  for (const link of links) {
    link.addEventListener("click", () => {
      for (const item of links)
        item.classList.toggle("is-active", item === link);
    });
  }
}

function observeReveals() {
  const revealItems = [...document.querySelectorAll("[data-reveal]")];
  if (state.reducedMotion || !("IntersectionObserver" in window)) {
    for (const item of revealItems)
      item.classList.add("is-revealed");
    return;
  }

  const observer = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting)
        continue;
      entry.target.classList.add("is-revealed");
      observer.unobserve(entry.target);
    }
  }, { threshold: 0.08 });

  for (const item of revealItems)
    observer.observe(item);
}

function revealVisibleElements() {
  for (const item of document.querySelectorAll("[data-reveal]")) {
    if (item.getBoundingClientRect().top < window.innerHeight * 1.06)
      item.classList.add("is-revealed");
  }
}

function animateNumber(target, value) {
  if (state.reducedMotion) {
    target.textContent = formatNumber(value);
    return;
  }

  const started = performance.now();
  const duration = 700;
  const update = (now) => {
    const progress = Math.min(1, (now - started) / duration);
    const eased = 1 - Math.pow(1 - progress, 4);
    target.textContent = formatNumber(Math.round(value * eased));
    if (progress < 1)
      window.requestAnimationFrame(update);
  };
  window.requestAnimationFrame(update);
}

function summarizeSemanticState(instances) {
  if (!instances.length)
    return "unknown";
  if (instances.every((item) => item.semanticState === "warm"))
    return "warm";
  if (instances.some((item) => item.semanticState === "warm"))
    return "mixed";
  return instances[0].semanticState;
}

function abbreviateTool(tool) {
  return tool
    .split("_")
    .map((part) => part[0])
    .join("")
    .slice(0, 3);
}

function formatToken(value) {
  return String(value ?? "unknown")
    .replaceAll("_", " ")
    .replace(/\b\w/g, (character) => character.toUpperCase());
}

function formatDuration(milliseconds) {
  if (milliseconds >= 1000)
    return `${formatNumber(milliseconds / 1000, 2)}s`;
  return `${formatNumber(milliseconds)}ms`;
}

function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className)
    node.className = className;
  if (text != null)
    node.textContent = text;
  return node;
}
