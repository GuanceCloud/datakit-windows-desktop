using System.Text.Json;

namespace Guance.Rum.Windows;

internal static class WebViewBridgeScript
{
    public static string Create(string token, bool sessionReplayEnabled = false, string sessionReplayPrivacyLevel = "mask")
    {
        return Script
            .Replace("__GUANCE_BRIDGE_TOKEN__", JsonSerializer.Serialize(token), StringComparison.Ordinal)
            .Replace("__GUANCE_REPLAY_ENABLED__", JsonSerializer.Serialize(sessionReplayEnabled), StringComparison.Ordinal)
            .Replace("__GUANCE_REPLAY_PRIVACY__", JsonSerializer.Serialize(sessionReplayPrivacyLevel), StringComparison.Ordinal);
    }

    private const string Script = """
(() => {
    'use strict';
    if (window.top !== window) {
        return;
    }

    const previousBridge = window.__guanceRumWebViewBridge;
    if (previousBridge && typeof previousBridge.cleanup === 'function') {
        try {
            previousBridge.cleanup();
        } catch (_) {
        }
    }

    const TOKEN = __GUANCE_BRIDGE_TOKEN__;
    const CHANNEL = 'guance-rum-webview';
    const REPLAY_ENABLED = __GUANCE_REPLAY_ENABLED__;
    const REPLAY_PRIVACY = __GUANCE_REPLAY_PRIVACY__;
    const cleanupCallbacks = [];
    let active = true;
    let officialRumActive = false;
    let pageViewReported = false;
    const pendingResources = [];
    const bridge = {
        version: 1,
        cleanup() {
            if (!active) {
                return;
            }
            active = false;
            while (cleanupCallbacks.length > 0) {
                try {
                    cleanupCallbacks.pop()();
                } catch (_) {
                }
            }
            if (window.__guanceRumWebViewBridge === bridge) {
                delete window.__guanceRumWebViewBridge;
            }
        }
    };
    window.__guanceRumWebViewBridge = bridge;
    const on = (target, eventName, handler, options) => {
        target.addEventListener(eventName, handler, options);
        cleanupCallbacks.push(() => target.removeEventListener(eventName, handler, options));
    };
    const post = (type, data) => {
        try {
            if (!active ||
                (officialRumActive &&
                    (type === 'view' || type === 'action' || type === 'error' || type === 'resource')) ||
                !window.chrome ||
                !window.chrome.webview ||
                typeof window.chrome.webview.postMessage !== 'function') {
                return;
            }
            window.chrome.webview.postMessage(Object.assign({
                channel: CHANNEL,
                version: 1,
                token: TOKEN,
                type
            }, data || {}));
        } catch (_) {
        }
    };
    const previousNativeBridge = window.FTWebViewJavascriptBridge;
    const nativeBridge = {
        getCapabilities() {
            return JSON.stringify(REPLAY_ENABLED ? ['records'] : []);
        },
        getPrivacyLevel() {
            return REPLAY_PRIVACY;
        },
        getAllowedWebViewHosts() {
            return null;
        },
        sendEvent(serializedEvent) {
            try {
                const event = typeof serializedEvent === 'string'
                    ? JSON.parse(serializedEvent)
                    : serializedEvent;
                if (!event || typeof event !== 'object') {
                    return;
                }
                if (event.name === 'rum' && event.data && typeof event.data === 'object') {
                    officialRumActive = true;
                    post('rum', { record: event.data });
                    return;
                }
                if (REPLAY_ENABLED &&
                    event.name === 'session_replay' &&
                    event.data &&
                    typeof event.data === 'object') {
                    post('session_replay', {
                        record: event.data,
                        viewId: event.view && typeof event.view.id === 'string' ? event.view.id : ''
                    });
                }
            } catch (_) {
            }
        }
    };
    if (REPLAY_ENABLED) {
        window.FTWebViewJavascriptBridge = nativeBridge;
        cleanupCallbacks.push(() => {
            if (window.FTWebViewJavascriptBridge === nativeBridge) {
                if (previousNativeBridge === undefined) {
                    delete window.FTWebViewJavascriptBridge;
                } else {
                    window.FTWebViewJavascriptBridge = previousNativeBridge;
                }
            }
        });
        const snapshotTimer = window.setInterval(() => {
            try {
                if (document.visibilityState !== 'hidden' &&
                    window.DATAFLUX_RUM &&
                    typeof window.DATAFLUX_RUM.takeSubsequentFullSnapshot === 'function') {
                    window.DATAFLUX_RUM.takeSubsequentFullSnapshot();
                }
            } catch (_) {
            }
        }, 10000);
        cleanupCallbacks.push(() => window.clearInterval(snapshotTimer));
    }
    const text = value => String(value || '').replace(/\s+/g, ' ').trim().slice(0, 512);
    const absoluteUrl = value => {
        try {
            return new URL(value || location.href, location.href).href;
        } catch (_) {
            return String(value || location.href);
        }
    };
    const targetName = element => {
        if (!element || element.nodeType !== 1) {
            return 'document';
        }
        return text(
            element.getAttribute('aria-label') ||
            element.getAttribute('name') ||
            element.innerText ||
            element.id ||
            element.getAttribute('title') ||
            element.tagName
        ) || String(element.tagName || 'element').toLowerCase();
    };
    const reportView = () => {
        post('view', {
            url: String(location.href),
            title: text(document.title)
        });
        pageViewReported = true;
        while (pendingResources.length > 0) {
            post('resource', pendingResources.shift());
        }
    };
    const reportResource = data => {
        const resource = Object.assign({
            method: 'GET',
            status: 0,
            durationMs: 0,
            responseSize: -1,
            requestSize: -1,
            resourceType: 'web'
        }, data);
        if (!pageViewReported) {
            if (pendingResources.length < 100) {
                pendingResources.push(resource);
            }
            return;
        }
        post('resource', resource);
    };

    if (document.readyState === 'loading') {
        on(document, 'DOMContentLoaded', reportView, { once: true });
    } else {
        queueMicrotask(reportView);
    }

    on(document, 'click', event => {
        const element = event.target && event.target.closest
            ? event.target.closest('button,a,[role="button"],[data-guance-action]')
            : event.target;
        if (!element) {
            return;
        }
        const elementType = element && element.getAttribute
            ? text(element.getAttribute('type')).toLowerCase()
            : '';
        const tagName = element ? String(element.tagName || '').toLowerCase() : '';
        const isSubmitControl = element && element.form && (
            (tagName === 'button' && (!elementType || elementType === 'submit')) ||
            (tagName === 'input' && (elementType === 'submit' || elementType === 'image'))
        );
        const isChangeControl =
            tagName === 'select' ||
            tagName === 'textarea' ||
            (tagName === 'input' &&
                elementType !== 'button' &&
                elementType !== 'reset' &&
                elementType !== 'submit' &&
                elementType !== 'image');
        if (isSubmitControl || isChangeControl) {
            return;
        }
        post('action', {
            name: targetName(element),
            actionType: 'click',
            durationMs: 0
        });
    }, true);
    on(document, 'change', event => post('action', {
        name: targetName(event.target),
        actionType: 'input',
        durationMs: 0
    }), true);
    on(document, 'submit', event => post('action', {
        name: targetName(event.target),
        actionType: 'submit',
        durationMs: 0
    }), true);

    on(window, 'error', event => post('error', {
        message: text(event.message) || 'JavaScript error',
        stack: event.error && event.error.stack ? String(event.error.stack).slice(0, 16384) : '',
        errorType: event.error && event.error.name ? text(event.error.name) : 'JavaScriptError'
    }));
    on(window, 'unhandledrejection', event => {
        const reason = event.reason;
        post('error', {
            message: text(reason && reason.message ? reason.message : reason) || 'Unhandled promise rejection',
            stack: reason && reason.stack ? String(reason.stack).slice(0, 16384) : '',
            errorType: reason && reason.name ? text(reason.name) : 'UnhandledPromiseRejection'
        });
    });

    const originalFetch = window.fetch;
    if (typeof originalFetch === 'function') {
        const instrumentedFetch = async function(input, init) {
            const started = performance.now();
            const url = typeof input === 'string' ? input : input && input.url;
            const method = (init && init.method) || (input && input.method) || 'GET';
            try {
                const response = await originalFetch.apply(this, arguments);
                reportResource({
                    url: absoluteUrl(url),
                    method: String(method),
                    status: Number(response.status) || 0,
                    durationMs: Math.max(0, performance.now() - started),
                    responseSize: Number(response.headers.get('content-length')) || -1,
                    resourceType: 'fetch'
                });
                return response;
            } catch (error) {
                reportResource({
                    url: absoluteUrl(url),
                    method: String(method),
                    status: 0,
                    durationMs: Math.max(0, performance.now() - started),
                    resourceType: 'fetch'
                });
                throw error;
            }
        };
        window.fetch = instrumentedFetch;
        cleanupCallbacks.push(() => {
            if (window.fetch === instrumentedFetch) {
                window.fetch = originalFetch;
            }
        });
    }

    const originalOpen = XMLHttpRequest.prototype.open;
    const originalSend = XMLHttpRequest.prototype.send;
    const instrumentedOpen = function(method, url) {
        this.__guanceRumRequest = {
            method: String(method || 'GET'),
            url: absoluteUrl(url)
        };
        return originalOpen.apply(this, arguments);
    };
    const instrumentedSend = function(body) {
        const metadata = this.__guanceRumRequest || { method: 'GET', url: String(location.href) };
        const started = performance.now();
        this.addEventListener('loadend', () => reportResource({
            url: metadata.url,
            method: metadata.method,
            status: Number(this.status) || 0,
            durationMs: Math.max(0, performance.now() - started),
            responseSize: Number(this.getResponseHeader('content-length')) || -1,
            requestSize: typeof body === 'string' ? new Blob([body]).size : -1,
            resourceType: 'xmlhttprequest'
        }), { once: true });
        return originalSend.apply(this, arguments);
    };
    XMLHttpRequest.prototype.open = instrumentedOpen;
    XMLHttpRequest.prototype.send = instrumentedSend;
    cleanupCallbacks.push(() => {
        if (XMLHttpRequest.prototype.open === instrumentedOpen) {
            XMLHttpRequest.prototype.open = originalOpen;
        }
        if (XMLHttpRequest.prototype.send === instrumentedSend) {
            XMLHttpRequest.prototype.send = originalSend;
        }
    });

    if (typeof PerformanceObserver === 'function') {
        try {
            const observer = new PerformanceObserver(list => {
                list.getEntries().forEach(entry => {
                    if (entry.entryType !== 'resource' ||
                        entry.initiatorType === 'fetch' ||
                        entry.initiatorType === 'xmlhttprequest') {
                        return;
                    }
                    reportResource({
                        url: String(entry.name),
                        method: 'GET',
                        status: Number(entry.responseStatus) || 0,
                        durationMs: Math.max(0, Number(entry.duration) || 0),
                        responseSize: Number(entry.transferSize || entry.encodedBodySize) || -1,
                        resourceType: text(entry.initiatorType) || 'resource'
                    });
                });
            });
            observer.observe({ type: 'resource', buffered: true });
            cleanupCallbacks.push(() => observer.disconnect());
        } catch (_) {
        }
    }

    const wrapHistory = methodName => {
        const original = history[methodName];
        if (typeof original !== 'function') {
            return;
        }
        const instrumented = function() {
            const result = original.apply(this, arguments);
            queueMicrotask(reportView);
            return result;
        };
        history[methodName] = instrumented;
        cleanupCallbacks.push(() => {
            if (history[methodName] === instrumented) {
                history[methodName] = original;
            }
        });
    };
    wrapHistory('pushState');
    wrapHistory('replaceState');
    on(window, 'popstate', reportView);
    on(window, 'hashchange', reportView);
})();
""";
}
