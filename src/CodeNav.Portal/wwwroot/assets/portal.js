import {
  countTransitionValue,
  createBuildViewModel,
  createCountTransition,
  formatNumber
} from "./portal-model.js";

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
  buildSymbols: document.querySelector("#build-symbols"),
  buildBytes: document.querySelector("#build-bytes"),
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
  serverStatusList: document.querySelector("#server-status-list"),
  capabilityCount: document.querySelector("#capability-count"),
  capabilityList: document.querySelector("#capability-list"),
  dataMode: document.querySelector("#data-mode"),
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

  await refreshData();
  renderWorkspacePicker();
  elements.workspaceSelect.addEventListener("change", () => {
    state.selectedWorkspaceId = elements.workspaceSelect.value;
    renderSelectedWorkspace();
  });
  elements.body.dataset.connection = "ready";
  window.setInterval(() => refreshData().catch(showRefreshFailure), 2000);
  window.setTimeout(() => revealVisibleElements(), 60);
}

async function refreshData() {
  const [bootstrap, operations, events] = await Promise.all([
    fetchJson("/api/v1/bootstrap"),
    fetchJson("/api/v1/operations"),
    fetchJson("/api/v1/events")
  ]);

  state.bootstrap = bootstrap;
  state.operations = operations.items ?? [];
  state.events = events.items ?? [];
  elements.body.dataset.connection = "ready";
  if (!bootstrap.workspaces?.some((item) => item.workspaceId === state.selectedWorkspaceId))
    state.selectedWorkspaceId = bootstrap.workspaces?.[0]?.workspaceId ?? null;

  if (elements.workspaceSelect.options.length)
    renderWorkspacePicker();
  renderSelectedWorkspace();
  elements.portalVersion.textContent = `v${bootstrap.portal.version}`;
  elements.connectionLabel.textContent = bootstrap.dataSource === "live" ? "Live" : "Fixture";
  const lagMs = bootstrap.telemetry?.lagMs;
  const completeness = bootstrap.dataComplete ? "complete" : "partial";
  elements.dataMode.textContent = bootstrap.dataSource === "live"
    ? `Local · read-only · live · ${formatLag(lagMs)} lag · ${completeness}`
    : "Local · read-only · fixture mode";
}

function showRefreshFailure(error) {
  console.error("Portal refresh failed", error);
  elements.body.dataset.connection = "error";
  elements.connectionLabel.textContent = "Stale";
}

function formatLag(milliseconds) {
  if (typeof milliseconds !== "number" || !Number.isFinite(milliseconds))
    return "unknown";
  if (milliseconds < 1000)
    return `${Math.round(milliseconds)}ms`;
  return `${(milliseconds / 1000).toFixed(milliseconds < 10_000 ? 1 : 0)}s`;
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
  const buildIsLive = build?.live === true;
  const indexState = index?.state ?? workspace.state ?? "unknown";
  const workspaceState = build
    ? buildIsLive ? "indexing" : "degraded"
    : indexState;

  elements.body.dataset.workspaceState = workspaceState;
  elements.workspaceState.textContent = workspaceState.toUpperCase();
  elements.workspaceSummary.textContent = build
    ? buildIsLive
      ? `${workspace.name} is moving through ${formatToken(build.phase)}. The shared index build is live and ${instances.length} recent Phoenix instance${instances.length === 1 ? " is" : "s are"} visible.`
      : `${workspace.name} last reported ${formatToken(build.phase)}, but its build process is no longer running. Progress is stalled until fresh telemetry arrives.`
    : index?.state === "ready"
      ? `${workspace.name} has a committed index ready for queries. ${instances.length} recent Phoenix instance${instances.length === 1 ? "" : "s"} are visible.`
      : index?.state === "queryable"
        ? `${workspace.name} is answering queries through a connected Phoenix instance. Index freshness is not independently verified by the portal.`
      : `${workspace.name} reports ${formatToken(index?.state ?? "unknown")}. Waiting for bounded build or server telemetry.`;
  elements.instanceCount.textContent = String(instances.length);
  elements.semanticState.textContent = summarizeSemanticState(instances);
  elements.dataState.textContent = dataComplete ? "complete" : "partial";
  elements.orbitState.textContent = build
    ? buildIsLive ? "building" : "stalled"
    : indexState === "ready" ? "ready" : formatToken(indexState);

  renderBuild(index, build);
  renderSignals(
    index,
    instances,
    operations,
    dataComplete,
    workspace.recentOperationCount ?? operations.length);
  renderActivity(operations);
  renderInstances(index, instances);
  renderStatus(instances);
}

function renderBuild(index, build) {
  const phaseIds = ["scanning", "parsing_projects", "indexing_files", "finalizing"];
  const activeIndex = build ? phaseIds.indexOf(build.phase) : phaseIds.length;
  const indexIsReady = index?.state === "ready";
  const indexIsQueryable = index?.state === "queryable";
  const indexIsAvailable = indexIsReady || indexIsQueryable;
  const buildIsLive = build?.live === true;
  const phases = build?.phases ?? phaseIds.map((id, index) => ({
    id,
    label: id === "parsing_projects"
      ? "Projects"
      : id === "indexing_files" ? "Symbols" : id === "finalizing" ? "Publish" : "Scan",
    state: !build
      ? indexIsAvailable ? "complete" : "pending"
      : index < activeIndex
        ? "complete"
      : index === activeIndex ? "active" : "pending",
    durationMs: index === activeIndex ? build?.phaseElapsedMs ?? null : null
  }));
  const view = build || indexIsAvailable
    ? createBuildViewModel(build)
    : createBuildViewModel({
        progress: null,
        filesProcessed: null,
        filesTotal: null,
        throughputPerSecond: null,
        elapsedMs: null,
        etaSeconds: null,
        filesSkipped: null
      });

  elements.buildTitle.textContent = build
    ? buildIsLive
      ? build.phaseLabel
      : `Stalled · ${build.phaseLabel}`
    : indexIsReady
      ? "Index ready"
      : indexIsQueryable ? "Index queryable" : "Index state unknown";
  elements.buildPanel.querySelector(".build-panel__live span").textContent = build
    ? buildIsLive ? "LIVE" : "STALLED"
    : indexIsReady ? "READY" : indexIsQueryable ? "QUERYABLE" : "UNKNOWN";
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
  elements.buildSymbols.textContent = build?.symbolsWritten == null
    ? "—"
    : formatNumber(build.symbolsWritten);
  elements.buildBytes.textContent = formatBytes(build?.bytesRead);

  if (!build && indexIsAvailable) {
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

function renderSignals(index, instances, operations, dataComplete, recentOperationCount) {
  const completed = operations.filter((item) => item.state === "complete");
  const semanticCompleted = completed.filter((item) => item.category === "semantic");
  const exact = semanticCompleted.filter((item) => item.confidence === "exact");
  const durations = completed
    .map((item) => item.durationMs)
    .filter((value) => typeof value === "number" && Number.isFinite(value))
    .sort((a, b) => a - b);
  const p95 = durations.length ? durations[Math.min(durations.length - 1, Math.floor(durations.length * 0.95))] : null;
  const warmCount = instances.filter((item) => item.semanticState === "warm").length;
  const semanticState = summarizeSemanticState(instances);
  const allConnected = instances.length > 0
    && instances.every((item) => item.connectionState === "connected");

  animateNumber(elements.queryCount, recentOperationCount);
  elements.queryP95.textContent = p95 == null ? "—" : formatDuration(p95);
  elements.semanticScore.textContent = semanticCompleted.length ? `${exact.length}/${semanticCompleted.length}` : "—";
  elements.semanticLabel.textContent = formatToken(semanticState);
  elements.semanticDetail.textContent = semanticState === "warm"
    ? `${warmCount} of ${instances.length} instances have warm semantic context`
    : semanticState === "mixed"
    ? `${warmCount} of ${instances.length} instances are warm; other states remain distinct`
    : semanticState === "warming"
    ? "A cold operation was observed; semantic context is warming"
    : semanticState === "cold"
    ? "Explicit current evidence reports a cold semantic context"
    : "No current warm/cold evidence is available";
  elements.freshnessLabel.textContent = index?.freshness === "head"
    ? "HEAD"
    : index?.freshness === "working_tree" ? "WORKTREE" : "UNKNOWN";
  elements.indexEpoch.textContent = index?.epoch == null ? "—" : `#${formatNumber(index.epoch)}`;
  elements.healthLabel.textContent = allConnected && dataComplete
    ? "All systems nominal"
    : dataComplete ? "Committed data available" : "Telemetry is partial";
  elements.healthDetail.textContent = instances.length === 0
    ? "No recent Phoenix process telemetry is available"
    : allConnected
    ? `${instances.filter((item) => item.role === "writer").length} writer · ${instances.filter((item) => item.role === "follower").length} followers`
    : "One or more instances are stale or disconnected";
}

function renderActivity(operations) {
  elements.activityList.replaceChildren();

  for (const operation of operations.slice(0, 6)) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = `activity-item${operation.state === "running" ? " is-running" : ""}`;
    const visualConfidence = operation.partial ? "partial" : operation.confidence;
    button.dataset.confidence = visualConfidence;
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
      `confidence-pill confidence-pill--${visualConfidence}`,
      operation.partial ? `${operation.confidence} · partial` : operation.confidence
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

    const mark = element(
      "span",
      "instance-item__mark",
      instance.role === "writer" ? "W" : instance.role === "follower" ? "F" : "?"
    );
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

function renderStatus(instances) {
  elements.serverStatusList.replaceChildren();
  elements.capabilityList.replaceChildren();
  const featureIds = [...new Set(
    instances.flatMap((instance) => instance.featureIds ?? [])
  )].sort();
  elements.capabilityCount.textContent = featureIds.length
    ? String(Math.max(
        featureIds.length,
        ...instances.map((instance) => instance.featureCount ?? 0)
      ))
    : "—";

  for (const instance of instances) {
    const item = element("article", "instance-item");
    const mark = element(
      "span",
      "instance-item__mark",
      instance.role === "writer" ? "W" : instance.role === "follower" ? "F" : "?"
    );
    const copy = element("span", "instance-item__copy");
    copy.append(
      element("strong", "", `${instance.version ?? "unknown"} · ${instance.platform ?? "unknown"}`),
      element(
        "span",
        "",
        `${instance.role} · schema ${instance.schemaVersion ?? "unknown"} · ${instance.buildStamp ?? "build unknown"}`
      )
    );
    item.append(mark, copy);
    elements.serverStatusList.append(item);
  }
  if (!instances.length)
    elements.serverStatusList.append(
      element("p", "empty-state", "No serverInfo record has been observed.")
    );

  for (const featureId of featureIds)
    elements.capabilityList.append(element("article", "instance-item", featureId));
  if (!featureIds.length)
    elements.capabilityList.append(
      element("p", "empty-state", "Capability IDs are unavailable until serverInfo arrives.")
    );
}

function openOperation(operation) {
  elements.dialogTitle.textContent = formatToken(operation.tool);
  elements.dialogContent.replaceChildren();

  const summary = element("section", "drawer-summary");
  summary.append(
    element(
      "span",
      "",
      `${operation.confidence}${operation.partial ? " · partial" : ""} · ${formatDuration(operation.durationMs)}`
    ),
    element("p", "", operation.summary)
  );

  const grid = element("section", "drawer-grid");
  const facts = [
    ["Outcome", formatToken(operation.outcome)],
    ["Cold state", formatToken(operation.coldState)],
    ["Projects", operation.counts.loaded == null || operation.counts.requested == null
      ? "Unknown"
      : `${operation.counts.loaded} / ${operation.counts.requested} loaded`],
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
  const measuredTimings = timings.filter(([, duration]) =>
    typeof duration === "number" && Number.isFinite(duration));
  const max = Math.max(1, ...measuredTimings.map(([, duration]) => duration));
  for (const [label, duration] of measuredTimings) {
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
  if (!measuredTimings.length)
    waterfall.append(
      element("p", "empty-state", "Phase timing was not emitted for this operation.")
    );

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
  const previousValue = Number(target.dataset.count);
  const transition = createCountTransition(previousValue, value);
  if (!transition.changed)
    return;

  target.dataset.count = String(transition.end);
  const animationId = String(Number(target.dataset.animationId ?? "0") + 1);
  target.dataset.animationId = animationId;
  if (state.reducedMotion) {
    target.textContent = formatNumber(transition.end);
    return;
  }

  const started = performance.now();
  const duration = 700;
  const update = (now) => {
    if (target.dataset.animationId !== animationId)
      return;
    const progress = Math.min(1, (now - started) / duration);
    target.textContent = formatNumber(countTransitionValue(transition, progress));
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
  if (typeof milliseconds !== "number" || !Number.isFinite(milliseconds))
    return "—";
  if (milliseconds >= 1000)
    return `${formatNumber(milliseconds / 1000, 2)}s`;
  return `${formatNumber(milliseconds)}ms`;
}

function formatBytes(bytes) {
  if (typeof bytes !== "number" || !Number.isFinite(bytes))
    return "—";
  if (bytes < 1024)
    return `${formatNumber(bytes)} B`;
  if (bytes < 1024 * 1024)
    return `${formatNumber(bytes / 1024, 1)} KiB`;
  if (bytes < 1024 * 1024 * 1024)
    return `${formatNumber(bytes / (1024 * 1024), 1)} MiB`;
  return `${formatNumber(bytes / (1024 * 1024 * 1024), 1)} GiB`;
}

function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className)
    node.className = className;
  if (text != null)
    node.textContent = text;
  return node;
}
