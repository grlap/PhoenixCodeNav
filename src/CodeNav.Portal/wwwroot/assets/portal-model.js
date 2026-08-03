const UNKNOWN = "—";

export function createBuildViewModel(build) {
  if (!build) {
    return {
      determinate: true,
      progress: 1,
      percent: 100,
      progressLabel: "100%",
      progressAriaLabel: "Index build complete",
      filesLabel: "Published and watching for changes",
      rateLabel: UNKNOWN,
      elapsedLabel: UNKNOWN,
      etaLabel: UNKNOWN,
      skippedLabel: UNKNOWN
    };
  }

  const progress = finiteRatio(build.progress);
  const percent = progress == null ? null : Math.round(progress * 1000) / 10;
  const filesProcessed = finiteNumber(build.filesProcessed);
  const filesTotal = finiteNumber(build.filesTotal);
  const throughput = finiteNumber(build.throughputPerSecond);
  const elapsedMs = finiteNumber(build.elapsedMs);
  const etaSeconds = finiteNumber(build.etaSeconds);
  const filesSkipped = finiteNumber(build.filesSkipped);

  return {
    determinate: progress != null,
    progress,
    percent,
    progressLabel: percent == null ? "Total unknown" : `${formatNumber(percent, 1)}%`,
    progressAriaLabel: percent == null
      ? "Index build progress: total unknown"
      : `Index build progress: ${formatNumber(percent, 1)} percent`,
    filesLabel: formatFileProgress(filesProcessed, filesTotal),
    rateLabel: formatOptionalNumber(throughput),
    elapsedLabel: elapsedMs == null ? UNKNOWN : formatNumber(elapsedMs / 1000, 1),
    etaLabel: etaSeconds == null ? UNKNOWN : `${formatNumber(etaSeconds, 1)}s`,
    skippedLabel: formatOptionalNumber(filesSkipped)
  };
}

export function formatNumber(value, maximumFractionDigits = 0) {
  return new Intl.NumberFormat(undefined, { maximumFractionDigits }).format(value);
}

export function createCountTransition(previousValue, nextValue) {
  const end = nonNegativeInteger(nextValue) ?? 0;
  const start = nonNegativeInteger(previousValue) ?? end;
  return {
    start,
    end,
    changed: start !== end
  };
}

export function countTransitionValue(transition, progress) {
  const boundedProgress = Math.min(1, Math.max(0, finiteNumber(progress) ?? 0));
  const eased = 1 - Math.pow(1 - boundedProgress, 4);
  return Math.round(transition.start + (transition.end - transition.start) * eased);
}

function formatFileProgress(filesProcessed, filesTotal) {
  if (filesProcessed != null && filesTotal != null)
    return `${formatNumber(filesProcessed)} / ${formatNumber(filesTotal)} files`;

  if (filesProcessed != null)
    return `${formatNumber(filesProcessed)} files processed · total unknown`;

  if (filesTotal != null)
    return `Processed count unknown · ${formatNumber(filesTotal)} files total`;

  return "File progress unknown";
}

function formatOptionalNumber(value, maximumFractionDigits = 0) {
  return value == null ? UNKNOWN : formatNumber(value, maximumFractionDigits);
}

function finiteNumber(value) {
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

function finiteRatio(value) {
  return typeof value === "number" && Number.isFinite(value) && value >= 0 && value <= 1
    ? value
    : null;
}

function nonNegativeInteger(value) {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0
    ? value
    : null;
}
