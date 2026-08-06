import { datafluxRum } from "@cloudcare/browser-rum";
import { datafluxLogs } from "@cloudcare/browser-logs";
import "./styles.css";
import { loadDesktopBootstrap } from "./bootstrap-loader";
import type { DesktopBootstrap } from "./contracts";
import { buildLogConfig } from "./log-config";
import { buildRumConfig } from "./rum-config";

type RumEventPreview = {
  type: string;
  label: string;
  time: string;
  tone: "mint" | "blue" | "amber" | "red";
};

const EVENT_TONES: Record<string, RumEventPreview["tone"]> = {
  view: "blue",
  action: "mint",
  resource: "amber",
  error: "red",
  long_task: "amber",
};

const EVENT_LABELS: Record<string, string> = {
  view: "View 已采集",
  action: "Action 已采集",
  resource: "Resource 已采集",
  error: "Error 已采集",
  long_task: "Long Task 已采集",
};

const INITIAL_EVENTS: RumEventPreview[] = [
  { type: "view", label: "等待 RUM 初始化", time: "--:--:--", tone: "blue" },
  { type: "action", label: "等待界面交互", time: "--:--:--", tone: "mint" },
  { type: "resource", label: "本地 API 已就绪", time: "--:--:--", tone: "amber" },
];

const appRootCandidate = document.querySelector<HTMLDivElement>("#app");
if (!appRootCandidate) {
  throw new Error("Electron renderer root was not found.");
}
const appRoot = appRootCandidate;

function escapeHtml(value: unknown): string {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function nowLabel(): string {
  return new Intl.DateTimeFormat("zh-CN", {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  }).format(new Date());
}

function renderShell(bootstrap: DesktopBootstrap): void {
  document.body.classList.toggle("remote-renderer", bootstrap.hybrid.isRemoteRenderer);
  const isWindows = bootstrap.app.platform === "win32";
  const remoteState = bootstrap.hybrid.remoteUrl ? "外部 RUM 页面已配置" : "内置 RUM 页面已就绪";
  const rendererMode = bootstrap.app.rendererMode === "vite-dev-server"
    ? "Vite 热更新"
    : bootstrap.app.rendererMode === "remote-http"
      ? "HTTP 隔离页面"
      : "file:// 生产页面";
  const rendererLabel = bootstrap.hybrid.isRemoteRenderer ? "REMOTE RENDERER" : "LOCAL RENDERER";
  const replayState = bootstrap.monitoring.rum.sessionReplay.enabled
    ? "EXPERIMENTAL REPLAY ON"
    : "EXPERIMENTAL REPLAY OFF";

  appRoot.innerHTML = `
    <div class="desktop-shell">
      <header class="titlebar">
        <div class="titlebar-brand">
          <div class="brand-mark" aria-hidden="true">
            <span></span><span></span><span></span>
          </div>
          <span>GUANCE</span>
          <span class="title-divider"></span>
          <span class="title-copy">Desktop Control Room</span>
        </div>
        <div class="titlebar-center">
          <span class="pulse-dot"></span>
          <span>WINDOWS NATIVE BRIDGE · RUM + REPLAY</span>
        </div>
        <div class="window-controls" aria-label="窗口控制">
          <button type="button" data-window-action="minimize" aria-label="最小化">—</button>
          <button type="button" data-window-action="maximize" aria-label="最大化">□</button>
          <button type="button" class="window-close" data-window-action="close" aria-label="关闭">×</button>
        </div>
      </header>

      <aside class="sidebar">
        <div class="workspace-switcher">
          <span class="workspace-avatar">WX</span>
          <span>
            <strong>Windows Experience</strong>
            <small>Desktop RUM Lab</small>
          </span>
          <span class="chevron">⌄</span>
        </div>

        <nav class="nav-stack" aria-label="主导航">
          <p class="nav-label">WORKSPACE</p>
          <button class="nav-item active" type="button" data-route="operations">
            <span class="nav-icon">◫</span>
            <span>Operations</span>
            <span class="nav-count">05</span>
          </button>
          <button class="nav-item" type="button" data-route="sessions">
            <span class="nav-icon">◎</span>
            <span>Sessions</span>
          </button>
          <button class="nav-item" type="button" data-route="journeys">
            <span class="nav-icon">⌁</span>
            <span>Journeys</span>
          </button>
          <button class="nav-item" type="button" data-route="resources">
            <span class="nav-icon">↗</span>
            <span>Resources</span>
          </button>

          <p class="nav-label nav-label-spaced">ACCEPTANCE LAB</p>
          <button class="nav-item" type="button" data-route="signal-lab">
            <span class="nav-icon">⚡</span>
            <span>Signal lab</span>
            <span class="nav-beacon"></span>
          </button>
          <button class="nav-item" type="button" data-route="hybrid">
            <span class="nav-icon">◇</span>
            <span>Hybrid runtime</span>
          </button>
        </nav>

        <div class="sidebar-foot">
          <div class="sdk-chip">
            <span class="sdk-chip-icon">G</span>
            <span>
              <small>SDK CHANNEL</small>
              <strong>Browser RUM 3.x</strong>
            </span>
            <span class="sdk-ok">✓</span>
          </div>
          <div class="operator">
            <span class="operator-avatar">LC</span>
            <span>
              <strong>Local operator</strong>
              <small>${escapeHtml(bootstrap.app.arch)} · ${isWindows ? "Windows" : escapeHtml(bootstrap.app.platform)}</small>
            </span>
            <span class="operator-more">•••</span>
          </div>
        </div>
      </aside>

      <main class="main-stage">
        <section class="hero-row">
          <div>
            <p class="eyebrow">LIVE ACCEPTANCE WORKSPACE</p>
            <h1>Operations pulse</h1>
            <p class="hero-copy">在一个真实 Electron 工作台中验证 View、Action、Resource、Error 与 Long Task。</p>
          </div>
          <div class="hero-actions">
            <button class="button button-secondary" type="button" id="choose-workspace" data-guance-action-name="select_native_workspace">
              <span>⌘</span>
              选择本地工作区
            </button>
            <button class="button button-primary" type="button" id="open-remote" data-guance-action-name="open_hybrid_remote">
              <span>↗</span>
              打开混合页面
            </button>
          </div>
        </section>

        <section class="runtime-strip" aria-label="运行时状态">
          <div class="runtime-block">
            <span class="status-orb status-orb-live"></span>
            <span>
              <small>${rendererLabel}</small>
              <strong>${escapeHtml(rendererMode)}</strong>
            </span>
          </div>
          <span class="runtime-connector"><i></i><b>HYBRID</b><i></i></span>
          <div class="runtime-block">
            <span class="status-orb ${bootstrap.hybrid.remoteAvailable ? "status-orb-live" : "status-orb-idle"}"></span>
            <span>
              <small>REMOTE WORKSPACE</small>
              <strong>${escapeHtml(remoteState)}</strong>
            </span>
          </div>
          <div class="runtime-meta">
            <span>Electron ${escapeHtml(bootstrap.app.electronVersion)}</span>
            <span>Chromium ${escapeHtml(bootstrap.app.chromiumVersion)}</span>
            <span>Node ${escapeHtml(bootstrap.app.nodeVersion)}</span>
          </div>
        </section>

        <section class="metrics-grid" aria-label="RUM 指标概览">
          <article class="metric-card">
            <div class="metric-head"><span>ACTIVE VIEWS</span><span class="metric-icon mint">◎</span></div>
            <div class="metric-value"><strong id="metric-views">01</strong><small>window</small></div>
            <div class="metric-foot positive"><span>↑ 100%</span><span>renderer ready</span></div>
          </article>
          <article class="metric-card">
            <div class="metric-head"><span>ACTIONS</span><span class="metric-icon blue">⌁</span></div>
            <div class="metric-value"><strong id="metric-actions">00</strong><small>events</small></div>
            <div class="metric-foot"><span id="action-delta">No activity</span><span>tracked clicks</span></div>
          </article>
          <article class="metric-card">
            <div class="metric-head"><span>RESOURCE P75</span><span class="metric-icon amber">↗</span></div>
            <div class="metric-value"><strong id="metric-resource">—</strong><small>ms</small></div>
            <div class="metric-foot"><span id="resource-delta">Awaiting run</span><span>loopback API</span></div>
          </article>
          <article class="metric-card">
            <div class="metric-head"><span>ERROR RATE</span><span class="metric-icon red">!</span></div>
            <div class="metric-value"><strong id="metric-errors">0.0</strong><small>%</small></div>
            <div class="metric-foot positive"><span id="error-delta">Healthy</span><span>synthetic errors</span></div>
          </article>
        </section>

        <section class="dashboard-grid">
          <article class="panel chart-panel">
            <div class="panel-head">
              <div>
                <span class="panel-kicker">WINDOW PERFORMANCE</span>
                <h2>Interaction latency</h2>
              </div>
              <div class="segmented-control" aria-label="时间范围">
                <button type="button">1H</button>
                <button class="active" type="button">6H</button>
                <button type="button">24H</button>
              </div>
            </div>
            <div class="chart-legend">
              <span><i class="legend-mint"></i>Renderer response</span>
              <span><i class="legend-blue"></i>Native bridge</span>
              <strong>P75 184ms</strong>
            </div>
            <div class="line-chart" role="img" aria-label="交互延迟趋势图">
              <div class="chart-axis"><span>300</span><span>200</span><span>100</span><span>0</span></div>
              <svg viewBox="0 0 760 220" preserveAspectRatio="none" aria-hidden="true">
                <defs>
                  <linearGradient id="mint-fill" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="0%" stop-color="#58f4be" stop-opacity=".25"></stop>
                    <stop offset="100%" stop-color="#58f4be" stop-opacity="0"></stop>
                  </linearGradient>
                  <linearGradient id="blue-fill" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="0%" stop-color="#6097ff" stop-opacity=".14"></stop>
                    <stop offset="100%" stop-color="#6097ff" stop-opacity="0"></stop>
                  </linearGradient>
                </defs>
                <g class="chart-grid-lines">
                  <line x1="0" y1="20" x2="760" y2="20"></line>
                  <line x1="0" y1="80" x2="760" y2="80"></line>
                  <line x1="0" y1="140" x2="760" y2="140"></line>
                  <line x1="0" y1="200" x2="760" y2="200"></line>
                </g>
                <path class="chart-area blue-area" d="M0 155 C80 140 100 165 170 135 S290 145 360 116 S460 136 530 104 S655 112 760 80 L760 220 L0 220 Z"></path>
                <path class="chart-line blue-line" d="M0 155 C80 140 100 165 170 135 S290 145 360 116 S460 136 530 104 S655 112 760 80"></path>
                <path class="chart-area mint-area" d="M0 178 C70 158 116 188 184 154 S292 166 360 145 S476 156 540 126 S650 145 760 102 L760 220 L0 220 Z"></path>
                <path class="chart-line mint-line" d="M0 178 C70 158 116 188 184 154 S292 166 360 145 S476 156 540 126 S650 145 760 102"></path>
                <circle class="chart-point" cx="540" cy="126" r="5"></circle>
              </svg>
              <div class="chart-x-axis"><span>10:00</span><span>11:00</span><span>12:00</span><span>13:00</span><span>14:00</span><span>NOW</span></div>
            </div>
          </article>

          <article class="panel acceptance-panel">
            <div class="panel-head">
              <div>
                <span class="panel-kicker">WINDOWS NATIVE BRIDGE</span>
                <h2>Signal coverage</h2>
              </div>
              <span class="scope-pill">${replayState}</span>
            </div>
            <div class="coverage-list">
              ${["view", "action", "resource", "error", "long_task"]
                .map(
                  (signal, index) => `
                    <div class="coverage-row" data-coverage="${signal}">
                      <span class="coverage-index">0${index + 1}</span>
                      <span class="coverage-name">${signal === "long_task" ? "Long Task" : signal[0].toUpperCase() + signal.slice(1)}</span>
                      <span class="coverage-track"><i></i></span>
                      <span class="coverage-status">READY</span>
                    </div>`,
                )
                .join("")}
            </div>
            <div class="deferred-note">
              <span class="deferred-icon">Ⅱ</span>
              <span>
                <strong>Session Replay（实验性）</strong>
                <small>默认关闭；由原生配置决定采样、会话、持久化和上传。</small>
              </span>
            </div>
          </article>
        </section>

        <section class="lower-grid">
          <article class="panel lab-panel">
            <div class="panel-head">
              <div>
                <span class="panel-kicker">DETERMINISTIC CONTROLS</span>
                <h2>RUM signal lab</h2>
              </div>
              <span class="live-clock" id="live-clock">${nowLabel()}</span>
            </div>
            <div class="lab-controls">
              <button class="lab-action" type="button" id="run-success" data-guance-action-name="load_orders_success">
                <span class="lab-action-icon mint">↗</span>
                <span><strong>成功请求</strong><small>GET /api/orders · 200</small></span>
                <span class="lab-arrow">→</span>
              </button>
              <button class="lab-action" type="button" id="run-failure" data-guance-action-name="load_orders_failure">
                <span class="lab-action-icon amber">↗</span>
                <span><strong>失败请求</strong><small>GET /api/failure · 503</small></span>
                <span class="lab-arrow">→</span>
              </button>
              <button class="lab-action" type="button" id="emit-error" data-guance-action-name="emit_synthetic_error">
                <span class="lab-action-icon red">!</span>
                <span><strong>前端错误</strong><small>Handled exception</small></span>
                <span class="lab-arrow">→</span>
              </button>
              <button class="lab-action" type="button" id="block-thread" data-guance-action-name="block_renderer_thread">
                <span class="lab-action-icon blue">⌁</span>
                <span><strong>长任务</strong><small>Renderer block · 240ms</small></span>
                <span class="lab-arrow">→</span>
              </button>
            </div>
          </article>

          <article class="panel stream-panel">
            <div class="panel-head">
              <div>
                <span class="panel-kicker">BEFORE SEND PREVIEW</span>
                <h2>Local event stream</h2>
              </div>
              <span class="event-counter"><b id="event-count">0</b> captured</span>
            </div>
            <div class="event-stream" id="event-stream" aria-live="polite"></div>
          </article>
        </section>

        <section class="panel replay-panel" id="replay-playground" aria-labelledby="replay-playground-title">
          <div class="panel-head replay-panel-head">
            <div>
              <span class="panel-kicker">EXPERIMENTAL SESSION REPLAY</span>
              <h2 id="replay-playground-title">Session Replay playground</h2>
            </div>
            <div class="replay-panel-meta">
              <span id="replay-interaction-status">0 interactions</span>
              <span class="scope-pill replay-scope-pill">${bootstrap.monitoring.rum.sessionReplay.enabled ? "RECORDING ON" : "RECORDING OFF"}</span>
            </div>
          </div>
          <p class="replay-intro">
            用真实表单、动态 DOM、弹层、拖放、滚动和 Canvas 验证实验性 Session Replay；默认关闭，开启后记录通过原生 Bridge 持久化并上传。
          </p>

          <div class="replay-grid">
            <section class="replay-zone replay-form-zone" aria-labelledby="replay-form-title">
              <div class="replay-zone-head">
                <span>01</span>
                <div><strong id="replay-form-title">Form & privacy states</strong><small>输入、选择与敏感字段</small></div>
              </div>
              <div class="replay-form-grid">
                <label class="replay-field">
                  <span>Operator name</span>
                  <input id="replay-name" data-replay-fixture="input" type="text" placeholder="Type a visible value" autocomplete="off" />
                </label>
                <label class="replay-field">
                  <span>Work email</span>
                  <input id="replay-email" data-replay-fixture="privacy" type="email" placeholder="operator@example.com" autocomplete="off" />
                </label>
                <label class="replay-field">
                  <span>Access token</span>
                  <input id="replay-password" data-replay-fixture="privacy" type="password" value="sensitive-demo-value" autocomplete="off" />
                </label>
                <label class="replay-field">
                  <span>Incident severity</span>
                  <select id="replay-severity" data-replay-fixture="selection">
                    <option value="normal">Normal</option>
                    <option value="warning">Warning</option>
                    <option value="critical">Critical</option>
                  </select>
                </label>
                <label class="replay-field replay-field-wide">
                  <span>Operator notes</span>
                  <textarea id="replay-notes" data-replay-fixture="input" rows="3" placeholder="Describe the reproduction path"></textarea>
                </label>
              </div>
              <div class="replay-choice-row">
                <label><input data-replay-fixture="selection" type="checkbox" checked /> Preserve window state</label>
                <label><input data-replay-fixture="selection" type="checkbox" /> Include diagnostics</label>
              </div>
              <fieldset class="replay-radio-group">
                <legend>Capture mode</legend>
                <label><input data-replay-fixture="selection" type="radio" name="replay-mode" value="guided" checked /> Guided</label>
                <label><input data-replay-fixture="selection" type="radio" name="replay-mode" value="manual" /> Manual</label>
              </fieldset>
              <div class="replay-range-row">
                <label for="replay-range">Interaction intensity</label>
                <input id="replay-range" data-replay-fixture="range" type="range" min="0" max="100" value="48" />
                <output id="replay-range-output" for="replay-range">48%</output>
              </div>
              <button class="replay-toggle" id="replay-toggle" data-replay-fixture="toggle" data-guance-action-name="toggle_replay_fixture" type="button" aria-pressed="false">
                <span></span><strong>Persistent UI state</strong><small>OFF</small>
              </button>
            </section>

            <section class="replay-zone" aria-labelledby="replay-dynamic-title">
              <div class="replay-zone-head">
                <span>02</span>
                <div><strong id="replay-dynamic-title">Dynamic DOM</strong><small>新增、删除与折叠状态</small></div>
              </div>
              <div class="replay-dynamic-list" id="replay-dynamic-list" aria-live="polite">
                <div class="replay-dynamic-row"><i>A1</i><span>Renderer bootstrap</span><b>READY</b></div>
                <div class="replay-dynamic-row"><i>A2</i><span>Hybrid hand-off</span><b>READY</b></div>
                <div class="replay-dynamic-row"><i>A3</i><span>Replay fixture set</span><b>READY</b></div>
              </div>
              <div class="replay-button-row">
                <button id="replay-add-row" data-replay-fixture="dynamic" data-guance-action-name="add_replay_dynamic_row" type="button">＋ Add row</button>
                <button id="replay-remove-row" data-replay-fixture="dynamic" data-guance-action-name="remove_replay_dynamic_row" type="button">− Remove</button>
              </div>
              <details class="replay-details" data-replay-fixture="dynamic">
                <summary>Expandable diagnostic payload</summary>
                <dl>
                  <div><dt>Runtime</dt><dd>Electron renderer</dd></div>
                  <div><dt>Mutation</dt><dd>Incremental DOM snapshot</dd></div>
                  <div><dt>Privacy</dt><dd>Sensitive input fixture</dd></div>
                </dl>
              </details>
              <div class="replay-tabs" role="tablist" aria-label="Replay state tabs">
                <button class="active" data-replay-fixture="selection" data-replay-tab="timeline" type="button" role="tab" aria-selected="true">Timeline</button>
                <button data-replay-fixture="selection" data-replay-tab="snapshot" type="button" role="tab" aria-selected="false">Snapshot</button>
                <button data-replay-fixture="selection" data-replay-tab="mutation" type="button" role="tab" aria-selected="false">Mutation</button>
              </div>
              <div class="replay-tab-content" id="replay-tab-content">Timeline state is visible.</div>
            </section>

            <section class="replay-zone" aria-labelledby="replay-overlay-title">
              <div class="replay-zone-head">
                <span>03</span>
                <div><strong id="replay-overlay-title">Overlay & pointer</strong><small>弹层、提示和拖放轨迹</small></div>
              </div>
              <div class="replay-button-row replay-overlay-actions">
                <button id="replay-dialog-open" data-replay-fixture="overlay" data-guance-action-name="open_replay_dialog" type="button">Open dialog</button>
                <button id="replay-toast" data-replay-fixture="overlay" data-guance-action-name="show_replay_toast" type="button">Show toast</button>
              </div>
              <div class="replay-drag-board">
                <div id="replay-drag-source" class="replay-drag-chip" data-replay-fixture="drag" draggable="true">
                  <span>⋮⋮</span> Drag trace
                </div>
                <div id="replay-drop-zone" class="replay-drop-zone" data-replay-fixture="drag">
                  Drop interaction here
                </div>
              </div>
              <div class="replay-scroll" id="replay-scroll" data-replay-fixture="scroll" tabindex="0">
                ${Array.from(
                  { length: 8 },
                  (_, index) => `
                    <div class="replay-scroll-row">
                      <span>${String(index + 1).padStart(2, "0")}</span>
                      <div><strong>Replay checkpoint ${index + 1}</strong><small>${index % 2 === 0 ? "DOM mutation" : "Pointer movement"}</small></div>
                      <i></i>
                    </div>`,
                ).join("")}
              </div>
            </section>

            <section class="replay-zone replay-canvas-zone" aria-labelledby="replay-canvas-title">
              <div class="replay-zone-head">
                <span>04</span>
                <div><strong id="replay-canvas-title">Canvas trace pad</strong><small>实验性 Canvas 录制验证</small></div>
              </div>
              <canvas id="replay-canvas" data-replay-fixture="canvas" width="560" height="180" aria-label="Replay pointer drawing canvas"></canvas>
              <div class="replay-canvas-foot">
                <span>Press and drag to draw a pointer path.</span>
                <button id="replay-canvas-clear" data-replay-fixture="canvas" data-guance-action-name="clear_replay_canvas" type="button">Clear trace</button>
              </div>
            </section>
          </div>

          <dialog id="replay-dialog" class="replay-dialog" data-replay-fixture="overlay" aria-labelledby="replay-dialog-title">
            <div class="replay-dialog-icon">R</div>
            <p class="panel-kicker">MODAL SNAPSHOT</p>
            <h3 id="replay-dialog-title">Overlay state captured</h3>
            <p>验证打开、聚焦、表单编辑和关闭弹层时的连续 DOM 状态。</p>
            <label class="replay-field">
              <span>Dialog annotation</span>
              <input data-replay-fixture="input" type="text" placeholder="Add a modal note" />
            </label>
            <button id="replay-dialog-close" type="button">Close dialog</button>
          </dialog>
        </section>

        <footer class="status-footer">
          <div class="status-left">
            <span class="status-orb" id="rum-orb"></span>
            <span>
              <small>RUM TRANSPORT</small>
              <strong data-testid="rum-status" id="rum-status">正在初始化…</strong>
            </span>
          </div>
          <div class="status-details">
            <span><i></i>Native-owned identity</span>
            <span><i></i>Native-owned session</span>
            <span><i></i>${escapeHtml(bootstrap.app.rendererMode)}</span>
          </div>
          <div class="status-version">v${escapeHtml(bootstrap.app.version)}</div>
        </footer>
      </main>
    </div>
    <div class="toast-region" id="toast-region" aria-live="polite"></div>
  `;
}

function renderEventStream(events: RumEventPreview[]): void {
  const stream = document.querySelector<HTMLDivElement>("#event-stream");
  if (!stream) {
    return;
  }
  stream.innerHTML = events
    .slice(0, 5)
    .map(
      (event) => `
        <div class="event-row">
          <span class="event-type ${event.tone}">${escapeHtml(event.type.replace("_", " "))}</span>
          <span class="event-copy">${escapeHtml(event.label)}</span>
          <time>${escapeHtml(event.time)}</time>
        </div>`,
    )
    .join("");
}

function showToast(message: string, tone: "success" | "warning" | "danger" = "success"): void {
  const region = document.querySelector<HTMLDivElement>("#toast-region");
  if (!region) {
    return;
  }
  const toast = document.createElement("div");
  toast.className = `toast ${tone}`;
  toast.innerHTML = `<span>${tone === "success" ? "✓" : tone === "warning" ? "↗" : "!"}</span><strong>${escapeHtml(message)}</strong>`;
  region.append(toast);
  window.setTimeout(() => toast.remove(), 3600);
}

function initializeReplayPlayground(onOfflineAction: () => void): void {
  const playground = document.querySelector<HTMLElement>("#replay-playground");
  if (!playground) {
    return;
  }

  let interactionCount = 0;
  const markInteraction = (label: string) => {
    interactionCount += 1;
    appRoot.dataset.replayInteractionCount = String(interactionCount);
    const status = document.querySelector<HTMLElement>("#replay-interaction-status");
    if (status) {
      status.textContent = `${interactionCount} interactions · ${label}`;
    }
  };

  playground.addEventListener("input", () => markInteraction("input"));
  playground.addEventListener("change", () => markInteraction("change"));

  const range = document.querySelector<HTMLInputElement>("#replay-range");
  const rangeOutput = document.querySelector<HTMLOutputElement>("#replay-range-output");
  range?.addEventListener("input", () => {
    if (rangeOutput) {
      rangeOutput.textContent = `${range.value}%`;
    }
  });

  const toggle = document.querySelector<HTMLButtonElement>("#replay-toggle");
  toggle?.addEventListener("click", () => {
    const active = toggle.getAttribute("aria-pressed") !== "true";
    toggle.setAttribute("aria-pressed", String(active));
    toggle.classList.toggle("active", active);
    const label = toggle.querySelector("small");
    if (label) {
      label.textContent = active ? "ON" : "OFF";
    }
    markInteraction(active ? "state on" : "state off");
    onOfflineAction();
  });

  const dynamicList = document.querySelector<HTMLDivElement>("#replay-dynamic-list");
  const addDynamicRow = () => {
    if (!dynamicList) {
      return;
    }
    const index = dynamicList.children.length + 1;
    const row = document.createElement("div");
    row.className = "replay-dynamic-row replay-dynamic-row-new";
    row.innerHTML = `<i>A${index}</i><span>Runtime mutation ${index}</span><b>ADDED</b>`;
    dynamicList.append(row);
    markInteraction("DOM added");
    onOfflineAction();
  };
  document.querySelector("#replay-add-row")?.addEventListener("click", addDynamicRow);
  document.querySelector("#replay-remove-row")?.addEventListener("click", () => {
    if (!dynamicList || dynamicList.children.length <= 1) {
      showToast("至少保留一个动态节点。", "warning");
      return;
    }
    dynamicList.lastElementChild?.remove();
    markInteraction("DOM removed");
    onOfflineAction();
  });

  document.querySelectorAll<HTMLButtonElement>("[data-replay-tab]").forEach((button) => {
    button.addEventListener("click", () => {
      document.querySelectorAll<HTMLButtonElement>("[data-replay-tab]").forEach((item) => {
        const selected = item === button;
        item.classList.toggle("active", selected);
        item.setAttribute("aria-selected", String(selected));
      });
      const tab = button.dataset.replayTab || "timeline";
      const content = document.querySelector("#replay-tab-content");
      if (content) {
        content.textContent = `${tab[0].toUpperCase()}${tab.slice(1)} state is visible.`;
      }
      markInteraction(`${tab} tab`);
      onOfflineAction();
    });
  });

  const dialog = document.querySelector<HTMLDialogElement>("#replay-dialog");
  document.querySelector("#replay-dialog-open")?.addEventListener("click", () => {
    if (dialog && !dialog.open) {
      dialog.showModal();
      markInteraction("dialog opened");
      onOfflineAction();
    }
  });
  document.querySelector("#replay-dialog-close")?.addEventListener("click", () => {
    dialog?.close();
    markInteraction("dialog closed");
    onOfflineAction();
  });
  document.querySelector("#replay-toast")?.addEventListener("click", () => {
    showToast("Replay overlay fixture 已显示。");
    markInteraction("toast");
    onOfflineAction();
  });

  const dragSource = document.querySelector<HTMLElement>("#replay-drag-source");
  const dropZone = document.querySelector<HTMLElement>("#replay-drop-zone");
  dragSource?.addEventListener("dragstart", (event) => {
    event.dataTransfer?.setData("text/plain", "replay-trace");
    dropZone?.classList.add("ready");
    markInteraction("drag started");
  });
  dropZone?.addEventListener("dragover", (event) => {
    event.preventDefault();
  });
  dropZone?.addEventListener("dragleave", () => dropZone.classList.remove("ready"));
  dropZone?.addEventListener("drop", (event) => {
    event.preventDefault();
    dropZone.classList.remove("ready");
    dropZone.classList.add("dropped");
    dropZone.textContent = "Pointer trace received";
    markInteraction("drop completed");
    onOfflineAction();
  });

  const replayScroll = document.querySelector<HTMLElement>("#replay-scroll");
  replayScroll?.addEventListener(
    "scroll",
    () => {
      replayScroll.classList.add("scrolled");
      markInteraction("nested scroll");
    },
    { once: true },
  );

  const canvas = document.querySelector<HTMLCanvasElement>("#replay-canvas");
  const context = canvas?.getContext("2d");
  let drawing = false;
  const canvasPoint = (event: PointerEvent) => {
    if (!canvas) {
      return { x: 0, y: 0 };
    }
    const bounds = canvas.getBoundingClientRect();
    return {
      x: ((event.clientX - bounds.left) / bounds.width) * canvas.width,
      y: ((event.clientY - bounds.top) / bounds.height) * canvas.height,
    };
  };
  const drawCanvasGuide = () => {
    if (!canvas || !context) {
      return;
    }
    context.clearRect(0, 0, canvas.width, canvas.height);
    context.save();
    context.strokeStyle = "rgba(88, 244, 190, 0.16)";
    context.lineWidth = 1;
    context.setLineDash([4, 8]);
    for (let x = 28; x < canvas.width; x += 56) {
      context.beginPath();
      context.moveTo(x, 0);
      context.lineTo(x, canvas.height);
      context.stroke();
    }
    for (let y = 30; y < canvas.height; y += 60) {
      context.beginPath();
      context.moveTo(0, y);
      context.lineTo(canvas.width, y);
      context.stroke();
    }
    context.restore();
  };
  drawCanvasGuide();
  canvas?.addEventListener("pointerdown", (event) => {
    if (!context) {
      return;
    }
    drawing = true;
    canvas.setPointerCapture(event.pointerId);
    const point = canvasPoint(event);
    context.beginPath();
    context.moveTo(point.x, point.y);
    context.strokeStyle = "#58f4be";
    context.lineWidth = 3;
    context.lineCap = "round";
    markInteraction("canvas draw");
    onOfflineAction();
  });
  canvas?.addEventListener("pointermove", (event) => {
    if (!drawing || !context) {
      return;
    }
    const point = canvasPoint(event);
    context.lineTo(point.x, point.y);
    context.stroke();
  });
  const stopDrawing = (event: PointerEvent) => {
    drawing = false;
    if (canvas?.hasPointerCapture(event.pointerId)) {
      canvas.releasePointerCapture(event.pointerId);
    }
  };
  canvas?.addEventListener("pointerup", stopDrawing);
  canvas?.addEventListener("pointercancel", stopDrawing);
  document.querySelector("#replay-canvas-clear")?.addEventListener("click", () => {
    drawCanvasGuide();
    markInteraction("canvas cleared");
    onOfflineAction();
  });

  appRoot.dataset.replayFixturesReady = "true";
}

async function initialize(): Promise<void> {
  const bridge = window.guanceDesktop;
  const bootstrap = await loadDesktopBootstrap(
    bridge,
    window.location,
    window.fetch.bind(window),
  );
  appRoot.dataset.acceptanceUserId = bootstrap.monitoring.userId;
  appRoot.dataset.rumInitialized = "false";
  appRoot.dataset.replayRecording = "false";
  appRoot.dataset.rumSdkEventTypes = "";
  appRoot.dataset.rumSdkResourceCount = "0";
  renderShell(bootstrap);

  const capturedEvents = [...INITIAL_EVENTS];
  let eventCount = 0;
  let actionCount = 0;
  let errorCount = 0;
  let totalSignals = 1;
  let rumEnabled = false;
  const rumSdkEventTypes = new Set<string>();
  let rumSdkResourceCount = 0;
  renderEventStream(capturedEvents);

  const setRumStatus = (message: string, active: boolean) => {
    const status = document.querySelector<HTMLElement>("#rum-status");
    const orb = document.querySelector<HTMLElement>("#rum-orb");
    if (status) {
      status.textContent = message;
    }
    orb?.classList.toggle("status-orb-live", active);
    orb?.classList.toggle("status-orb-idle", !active);
  };

  const markCoverage = (type: string) => {
    document.querySelector(`[data-coverage="${type}"]`)?.classList.add("verified");
  };

  const recordPreview = (type: string) => {
    eventCount += 1;
    totalSignals += 1;
    if (type === "action") {
      actionCount += 1;
      const value = document.querySelector("#metric-actions");
      const delta = document.querySelector("#action-delta");
      if (value) value.textContent = actionCount.toString().padStart(2, "0");
      if (delta) delta.textContent = `+${actionCount} this run`;
    }
    if (type === "error") {
      errorCount += 1;
      const value = document.querySelector("#metric-errors");
      const delta = document.querySelector("#error-delta");
      if (value) value.textContent = ((errorCount / totalSignals) * 100).toFixed(1);
      if (delta) delta.textContent = `${errorCount} synthetic`;
      delta?.parentElement?.classList.remove("positive");
    }

    capturedEvents.unshift({
      type,
      label: EVENT_LABELS[type] || `${type} 已采集`,
      time: nowLabel(),
      tone: EVENT_TONES[type] || "mint",
    });
    document.querySelector("#event-count")!.textContent = String(eventCount);
    renderEventStream(capturedEvents);
    markCoverage(type);
  };

  const logResult = buildLogConfig(
    bootstrap.monitoring.bridgeEnabled,
    bootstrap.monitoring.log,
  );
  if (logResult.enabled && logResult.config) {
    try {
      datafluxLogs.init(
        logResult.config as unknown as Parameters<typeof datafluxLogs.init>[0],
      );
      datafluxLogs.logger.info("Electron Browser Log bridge initialized", {
        source: "electron_renderer",
      });
      appRoot.dataset.logInitialized = "true";
    } catch (error) {
      const message = error instanceof Error ? error.message : "unknown initialization error";
      showToast(`Log 初始化失败 · ${message}`, "danger");
    }
  }

  const result = buildRumConfig(
    bootstrap.monitoring.bridgeEnabled,
    bootstrap.monitoring.rum,
    bootstrap.monitoring.trace,
  );
  if (result.enabled && result.config) {
    try {
      const config = {
        ...result.config,
        beforeSend: (event: { type?: string }) => {
          const type = event.type || "event";
          rumSdkEventTypes.add(type);
          if (type === "resource") {
            rumSdkResourceCount += 1;
          }
          appRoot.dataset.rumSdkEventTypes = [...rumSdkEventTypes].join(",");
          appRoot.dataset.rumSdkResourceCount = String(rumSdkResourceCount);
          recordPreview(type);
          return true;
        },
      } as Parameters<typeof datafluxRum.init>[0];

      datafluxRum.init(config);
      datafluxRum.startView({ name: "electron.operations" });
      if (bootstrap.monitoring.rum.sessionReplay.enabled) {
        datafluxRum.startSessionReplayRecording();
        appRoot.dataset.replayRecording = "true";
      }
      rumEnabled = true;
      appRoot.dataset.rumInitialized = "true";
      appRoot.dataset.traceEnabled = String(bootstrap.monitoring.trace.enabled);
      setRumStatus(
        `Windows Native Bridge · RUM${bootstrap.monitoring.log.enabled ? " + Log" : ""}${bootstrap.monitoring.trace.enabled ? " + Trace" : ""}${bootstrap.monitoring.rum.sessionReplay.enabled ? " + 实验性 Replay" : ""}`,
        true,
      );
    } catch (error) {
      const message = error instanceof Error ? error.message : "unknown initialization error";
      setRumStatus(`初始化失败 · ${message}`, false);
      showToast("RUM 初始化失败，请检查配置。", "danger");
    }
  } else {
    const logInitialized = appRoot.dataset.logInitialized === "true";
    setRumStatus(
      logInitialized ? "Windows Native Bridge · Log" : result.reason || "监控能力未配置",
      logInitialized,
    );
  }

  const previewActionWhenOffline = () => {
    if (!rumEnabled) {
      recordPreview("action");
    }
  };
  const recordProgrammaticAcceptanceAction = (event: MouseEvent, name: string) => {
    if (!rumEnabled) {
      recordPreview("action");
    } else if (!event.isTrusted) {
      datafluxRum.addAction(name, { acceptance_automation: true });
    }
  };

  document.querySelectorAll<HTMLButtonElement>("[data-window-action]").forEach((button) => {
    button.addEventListener("click", (event) => {
      const action = button.dataset.windowAction;
      if (!bridge) return;
      if (action === "minimize") bridge.minimize();
      if (action === "maximize") void bridge.toggleMaximize();
      if (action === "close") bridge.close();
    });
  });

  document.querySelectorAll<HTMLButtonElement>(".nav-item").forEach((button) => {
    button.addEventListener("click", (event) => {
      document.querySelectorAll(".nav-item").forEach((item) => item.classList.remove("active"));
      button.classList.add("active");
      const route = button.dataset.route || "operations";
      if (rumEnabled) {
        datafluxRum.startView({ name: `electron.${route}` });
      } else {
        recordPreview("view");
      }
      recordProgrammaticAcceptanceAction(event, `navigate_${route}`);
      showToast(`已切换到 ${button.textContent?.trim().replace(/\s+/g, " ") || route}`);
    });
  });

  document.querySelector("#choose-workspace")?.addEventListener("click", async () => {
    if (!bridge) {
      showToast("远程 renderer 不具备本地文件访问权限。", "warning");
      return;
    }
    const workspace = await bridge.selectWorkspace();
    if (!workspace) {
      showToast("已取消工作区选择。", "warning");
      return;
    }
    previewActionWhenOffline();
    showToast(`本地工作区 ${workspace.name} 已连接`);
  });

  document.querySelector("#open-remote")?.addEventListener("click", async () => {
    if (!bridge) {
      showToast("当前已经是隔离的远程 renderer。", "warning");
      return;
    }
    const response = await bridge.openRemoteWorkspace();
    if (!response.opened) {
      showToast(response.reason || "未能打开远程工作区。", "warning");
      return;
    }
    previewActionWhenOffline();
    const source = response.instrumentation === "built-in" ? "内置 RUM" : "外部自接入";
    showToast(response.reused ? "已聚焦远程工作区。" : `${source} 远程工作区已打开。`);
  });

  document.querySelector("#run-success")?.addEventListener("click", async () => {
    previewActionWhenOffline();
    const started = performance.now();
    try {
      const response = await fetch(`${bootstrap.hybrid.apiBaseUrl}/api/orders?acceptance=success`);
      if (!response.ok) {
        throw new Error(`Expected HTTP 200, received ${response.status}`);
      }
      await response.json();
      const elapsed = Math.round(performance.now() - started);
      if (!rumEnabled) recordPreview("resource");
      document.querySelector("#metric-resource")!.textContent = String(elapsed);
      document.querySelector("#resource-delta")!.textContent = "HTTP 200";
      showToast(`Resource 成功，耗时 ${elapsed}ms`);
    } catch (error) {
      if (rumEnabled) datafluxRum.addError(error instanceof Error ? error : new Error(String(error)));
      showToast("Resource 请求失败。", "danger");
    }
  });

  document.querySelector("#run-failure")?.addEventListener("click", async () => {
    previewActionWhenOffline();
    const started = performance.now();
    const response = await fetch(`${bootstrap.hybrid.apiBaseUrl}/api/failure?acceptance=failure`);
    await response.json();
    const elapsed = Math.round(performance.now() - started);
    if (!rumEnabled) recordPreview("resource");
    document.querySelector("#metric-resource")!.textContent = String(elapsed);
    document.querySelector("#resource-delta")!.textContent = `HTTP ${response.status}`;
    showToast(`已生成 HTTP ${response.status} Resource`, "warning");
  });

  document.querySelector("#emit-error")?.addEventListener("click", () => {
    previewActionWhenOffline();
    const error = new Error("Synthetic Electron renderer acceptance error");
    if (rumEnabled) {
      datafluxRum.addError(error, {
        acceptance_case: "renderer_handled_exception",
        desktop_runtime: "electron",
      });
    } else {
      recordPreview("error");
    }
    showToast("已生成可控前端错误。", "danger");
  });

  document.querySelector("#block-thread")?.addEventListener("click", () => {
    previewActionWhenOffline();
    const started = performance.now();
    while (performance.now() - started < 240) {
      Math.sqrt(Math.random() * 100_000);
    }
    if (!rumEnabled) {
      recordPreview("long_task");
    }
    if (bridge) {
      void bridge.showNotification("240ms renderer Long Task 已完成。");
    }
    showToast("Long Task 已完成，等待 PerformanceObserver 采集。");
  });

  document.querySelectorAll<HTMLButtonElement>(".segmented-control button").forEach((button) => {
    button.addEventListener("click", () => {
      document.querySelectorAll(".segmented-control button").forEach((item) => item.classList.remove("active"));
      button.classList.add("active");
      previewActionWhenOffline();
    });
  });

  initializeReplayPlayground(previewActionWhenOffline);

  window.setInterval(() => {
    const clock = document.querySelector("#live-clock");
    if (clock) clock.textContent = nowLabel();
  }, 1000);
}

initialize().catch((error) => {
  console.error("[electron-renderer] startup failed", error);
  appRoot.innerHTML = `
    <main class="fatal-state">
      <span>!</span>
      <h1>Electron renderer 启动失败</h1>
      <p>${escapeHtml(error instanceof Error ? error.message : error)}</p>
    </main>
  `;
});
