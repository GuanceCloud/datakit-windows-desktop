"use strict";

function safeLabel(value) {
  const text = String(value || "renderer").replace(/[^A-Za-z0-9_.-]/g, "-");
  return text.slice(0, 80) || "renderer";
}

function monitorElectronWindow(window, {
  label,
  sendProcessFailure,
  now = Date.now,
}) {
  const rendererLabel = safeLabel(label);
  let unresponsiveStarted;

  window.on("unresponsive", () => {
    if (unresponsiveStarted === undefined) {
      unresponsiveStarted = now();
    }
  });
  window.on("responsive", () => {
    if (unresponsiveStarted === undefined) {
      return;
    }
    const duration = Math.max(0, now() - unresponsiveStarted);
    unresponsiveStarted = undefined;
    sendProcessFailure({
      type: "ElectronRendererUnresponsive",
      message: `${rendererLabel} was unresponsive for ${duration} ms`,
    });
  });
  window.webContents.on("render-process-gone", (_event, details = {}) => {
    unresponsiveStarted = undefined;
    const reason = safeLabel(details.reason || "unknown");
    const exitCode = Number.isInteger(details.exitCode) ? details.exitCode : 0;
    sendProcessFailure({
      type: "ElectronRendererProcessGone",
      message: `${rendererLabel} process ended: reason=${reason} exit_code=${exitCode}`,
    });
  });
}

module.exports = { monitorElectronWindow };
