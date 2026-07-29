import { datafluxRum } from "@cloudcare/browser-rum";
import "./styles.css";
import type { DesktopBootstrap } from "./contracts";
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

function getStableOperatorId(processUserId: string): string {
  if (processUserId) {
    return processUserId;
  }
  const key = "guance-electron-acceptance-operator";
  const existing = localStorage.getItem(key);
  if (existing) {
    return existing;
  }
  const created = `desktop-${crypto.randomUUID()}`;
  localStorage.setItem(key, created);
  return created;
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
          <span>PHASE 1 · RUM ACCEPTANCE</span>
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
                <span class="panel-kicker">PHASE 1 SCOPE</span>
                <h2>Signal coverage</h2>
              </div>
              <span class="scope-pill">RUM ONLY</span>
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
                <strong>Session Replay 已后移</strong>
                <small>一期配置强制采样率为 0，不启动录制。</small>
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

        <footer class="status-footer">
          <div class="status-left">
            <span class="status-orb" id="rum-orb"></span>
            <span>
              <small>RUM TRANSPORT</small>
              <strong data-testid="rum-status" id="rum-status">正在初始化…</strong>
            </span>
          </div>
          <div class="status-details">
            <span><i></i>${escapeHtml(bootstrap.rum.service)}</span>
            <span><i></i>${escapeHtml(bootstrap.rum.env)}</span>
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

async function initialize(): Promise<void> {
  const bridge = window.guanceDesktop;
  const bootstrap = bridge
    ? await bridge.getBootstrap()
    : await fetch("./bootstrap").then(async (response) => {
        if (!response.ok) {
          throw new Error(`Remote bootstrap failed with HTTP ${response.status}.`);
        }
        return response.json() as Promise<DesktopBootstrap>;
      });
  appRoot.dataset.acceptanceUserId = bootstrap.rum.userId;
  appRoot.dataset.rumInitialized = "false";
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

  const result = buildRumConfig(bootstrap.rum);
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
      datafluxRum.setGlobalContext({
        windows_desktop_platform: "windows",
        windows_desktop_runtime: "electron",
        windows_integration_mode: "hybrid",
        windows_acceptance_phase: "phase_1_rum",
      });
      datafluxRum.setUser({
        id: getStableOperatorId(bootstrap.rum.userId),
        name: "Electron acceptance operator",
      });
      datafluxRum.startView({ name: "electron.operations" });
      rumEnabled = true;
      appRoot.dataset.rumInitialized = "true";
      setRumStatus(`${result.intakeMode === "dataway" ? "Public Dataway" : "Local DataKit"} · 已连接`, true);
    } catch (error) {
      const message = error instanceof Error ? error.message : "unknown initialization error";
      setRumStatus(`初始化失败 · ${message}`, false);
      showToast("RUM 初始化失败，请检查配置。", "danger");
    }
  } else {
    setRumStatus(result.reason || "RUM 未配置", false);
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
