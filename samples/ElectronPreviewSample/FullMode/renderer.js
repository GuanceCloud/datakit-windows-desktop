"use strict";

const status = document.querySelector("#status");
const actionButton = document.querySelector("#preview-action");
const rum = window.DATAFLUX_RUM;

if (!rum || !window.FTWebViewJavascriptBridge) {
  status.textContent = "Browser RUM or the native bridge is unavailable.";
  status.dataset.state = "error";
} else {
  rum.init({
    // Bridge mode supplies application identity and session configuration
    // from the trusted native side. This origin only satisfies Browser RUM's
    // current initialization contract; the bridge does not upload to it.
    datakitOrigin: "http://127.0.0.1",
  });
  status.textContent = "RUM initialized through the Windows native bridge.";
  status.dataset.state = "ready";

  actionButton.addEventListener("click", () => {
    rum.addAction("preview_button_click", { sample: "electron-full-preview" });
    status.textContent = `Action sent at ${new Date().toLocaleTimeString()}.`;
  });
}
