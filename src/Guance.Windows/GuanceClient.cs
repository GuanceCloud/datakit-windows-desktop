using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using Guance.Windows.Queue;
using Guance.Windows.SessionReplay;
using Guance.Windows.Transport;

namespace Guance.Windows;

/// <summary>Owns one configured Windows RUM, logging, tracing, Replay, queue, and upload runtime.</summary>
public sealed class GuanceClient : IAsyncDisposable
{
    private sealed record DefaultClientComponents(
        GuanceConfig Config,
        IRumQueue RumQueue,
        IDatawayTransport RumTransport,
        ISessionReplayQueue ReplayQueue,
        ISessionReplayTransport ReplayTransport,
        ILogQueue LogQueue,
        ILogTransport LogTransport);

    private const string ResourceDurationOverride = "_guance_resource_duration_override";
    private static readonly HashSet<string> ResourcePropertyKeys = new(StringComparer.Ordinal)
    {
        RumConstants.TraceId,
        RumConstants.SpanId,
        RumConstants.ResourceHttpProtocol,
        RumConstants.ResourceDns,
        RumConstants.ResourceTcp,
        RumConstants.ResourceSsl,
        RumConstants.ResourceTtfb,
        RumConstants.ResourceTimingSource,
        RumConstants.ResourceTimingPrecision,
        RumConstants.ResourceTimingDuration,
        RumConstants.ResourceTimingPhase,
        RumConstants.ResourceTtfbEstimated,
        ResourceDurationOverride
    };

    private readonly GuanceConfig config;
    private readonly IRumQueue queue;
    private readonly IDatawayTransport transport;
    private readonly LogPipeline logPipeline;
    private readonly ISessionReplayQueue sessionReplayQueue;
    private readonly ISessionReplayTransport sessionReplayTransport;
    private readonly SessionReplayPrivacyOverrides sessionReplayPrivacy = new();
    private readonly SessionReplayManager sessionReplay;
    private readonly SamplingController sampling;
    private readonly SessionManager session;
    private readonly string anonymousUserId;
    private readonly RumPlatformInfo platformInfo;
    private readonly WebViewInstrumentationManager webViewInstrumentation;
    private readonly ApplicationLaunchTracker applicationLaunch;
    private readonly ConcurrentDictionary<string, ActiveResource> resources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveAction> actions = new(StringComparer.Ordinal);
    private readonly ActionTrackingTiming actionTiming;
    private readonly Dictionary<string, object?> globalContext = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> rumGlobalContext = new(StringComparer.Ordinal);
    private readonly object stateGate = new();
    private readonly object actionGate = new();
    private readonly object rumQueueWriteGate = new();
    private readonly HashSet<Task> pendingRumQueueWrites = new();
    private readonly RetryBackoff rumRetryBackoff;
    private readonly RetryBackoff sessionReplayRetryBackoff;
    private readonly UploadRateLimiter uploadRateLimiter;
    private readonly UploadScheduler uploadScheduler;
    private readonly CancellationTokenSource shutdown = new();
    private long lastReplayPendingFlushAt;
    private AutomaticInstrumentation? automaticInstrumentation;
    private ActiveView? activeView;
    private ActiveAction? activeAction;
    private UserInfo? userInfo;
    private long rumEventsEnqueued;
    private long rumEventsDroppedBySampling;
    private long rumUploadSuccessCount;
    private long rumUploadRetryCount;
    private long rumUploadTerminalFailureCount;
    private long replayUploadSuccessCount;
    private long replayUploadRetryCount;
    private long replayUploadTerminalFailureCount;
    private long queueErrorCount;
    private long lastRumUploadStatusCode;
    private long lastReplayUploadStatusCode;
    private string? lastRumUploadError;
    private string? lastReplayUploadError;
    private string? lastQueueError;
    private volatile bool webViewAutoInstrumentationEnabled = true;

    /// <summary>Creates and starts a client from validated configuration.</summary>
    /// <param name="config">The immutable SDK configuration.</param>
    public GuanceClient(GuanceConfig config)
        : this(CreateDefaultComponents(config))
    {
    }

    private GuanceClient(DefaultClientComponents components)
        : this(
            components.Config,
            components.RumQueue,
            components.RumTransport,
            components.ReplayQueue,
            components.ReplayTransport,
            null,
            components.LogQueue,
            components.LogTransport)
    {
    }

    internal GuanceClient(
        GuanceConfig config,
        IRumQueue queue,
        IDatawayTransport transport,
        ActionTrackingTiming? actionTiming = null)
        : this(
            config,
            queue,
            transport,
            config.SessionReplay.Enabled ? new BatchFileSessionReplayQueue(config) : new DisabledSessionReplayQueue(),
            new SessionReplayTransport(config),
            null,
            config.Logging.EnableCustomLog ? new BatchFileLogQueue(config) : new DisabledLogQueue(),
            new LogTransport(config),
            actionTiming)
    {
    }

    internal GuanceClient(
        GuanceConfig config,
        IRumQueue queue,
        IDatawayTransport transport,
        ISessionReplayQueue sessionReplayQueue,
        ISessionReplayTransport sessionReplayTransport,
        IApplicationLaunchClock? applicationLaunchClock = null,
        ILogQueue? logQueue = null,
        ILogTransport? logTransport = null,
        ActionTrackingTiming? actionTiming = null)
    {
        config.Validate();
        this.config = config;
        this.queue = queue;
        this.transport = transport;
        this.sessionReplayQueue = sessionReplayQueue;
        this.sessionReplayTransport = sessionReplayTransport;
        this.actionTiming = (actionTiming ?? ActionTrackingTiming.Default).Validate();
        sampling = new SamplingController(config);
        session = new SessionManager(sampling);
        var anonymousIdentity = AnonymousUserIdStore.LoadOrCreate(config);
        anonymousUserId = anonymousIdentity.Value;
        platformInfo = RumPlatformInfo.Capture();
        webViewInstrumentation = new WebViewInstrumentationManager(this);
        applicationLaunch = new ApplicationLaunchTracker(TrackApplicationLaunch, applicationLaunchClock);
        sessionReplay = new SessionReplayManager(config, sessionReplayQueue, sessionReplayPrivacy);
        rumRetryBackoff = new RetryBackoff(config.FlushInterval);
        sessionReplayRetryBackoff = new RetryBackoff(config.SessionReplay.FlushInterval);
        uploadRateLimiter = new UploadRateLimiter(config.Upload);
        lastReplayPendingFlushAt = Clock.Timestamp();
        UploadScheduler? scheduler = null;
        logPipeline = new LogPipeline(
            config,
            logQueue ?? (config.Logging.EnableCustomLog ? new BatchFileLogQueue(config) : new DisabledLogQueue()),
            logTransport ?? new LogTransport(config),
            CreateLogEvent,
            PublishDiagnostic,
            shutdown.Token,
            uploadRateLimiter,
            () => scheduler?.Wake());
        scheduler = new UploadScheduler(
            config.Upload,
            new[]
            {
                new UploadChannel(BatchStreamKind.Rum, config.Upload.RumWeight, SealRumQueueAsync, TryUploadRumQueueOnceAsync),
                new UploadChannel(BatchStreamKind.Log, config.Upload.LogWeight, logPipeline.SealAsync, logPipeline.TryUploadOneAsync),
                new UploadChannel(BatchStreamKind.SessionReplay, config.Upload.SessionReplayWeight, SealSessionReplayQueueAsync, TryUploadSessionReplayOnceAsync)
            },
            shutdown.Token);
        uploadScheduler = scheduler;
        if (anonymousIdentity.PersistenceError is not null)
        {
            EmitDiagnostic(
                RumDiagnosticLevel.Warning,
                "identity",
                "Anonymous user identity could not be persisted; this process will use an ephemeral identifier.",
                exception: anonymousIdentity.PersistenceError);
        }
    }

    /// <summary>Gets the configuration used to create this client.</summary>
    public GuanceConfig Config => config;

    /// <summary>Occurs when the SDK emits a transport, queue, or instrumentation diagnostic.</summary>
    public event EventHandler<RumDiagnosticEvent>? DiagnosticEvent;

    private static DefaultClientComponents CreateDefaultComponents(GuanceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();
        return new DefaultClientComponents(
            config,
            new BatchFileRumQueue(config),
            new DatawayTransport(config),
            config.SessionReplay.Enabled ? new BatchFileSessionReplayQueue(config) : new DisabledSessionReplayQueue(),
            new SessionReplayTransport(config),
            config.Logging.EnableCustomLog ? new BatchFileLogQueue(config) : new DisabledLogQueue(),
            new LogTransport(config));
    }

    /// <summary>Returns current RUM, Replay, queue, and upload diagnostics.</summary>
    public RumDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        lock (stateGate)
        {
            return new RumDiagnosticsSnapshot(
                DateTimeOffset.UtcNow,
                session.SessionId,
                session.IsSampled,
                session.IsErrorSampled,
                activeView is not null,
                actions.Count,
                resources.Count,
                Interlocked.Read(ref rumEventsEnqueued),
                Interlocked.Read(ref rumEventsDroppedBySampling),
                Interlocked.Read(ref rumUploadSuccessCount),
                Interlocked.Read(ref rumUploadRetryCount),
                Interlocked.Read(ref rumUploadTerminalFailureCount),
                Interlocked.Read(ref replayUploadSuccessCount),
                Interlocked.Read(ref replayUploadRetryCount),
                Interlocked.Read(ref replayUploadTerminalFailureCount),
                Interlocked.Read(ref queueErrorCount),
                Interlocked.Read(ref lastRumUploadStatusCode),
                Interlocked.Read(ref lastReplayUploadStatusCode),
                Volatile.Read(ref lastRumUploadError),
                Volatile.Read(ref lastReplayUploadError),
                Volatile.Read(ref lastQueueError));
        }
    }

    /// <summary>
    /// Returns the current shared RUM, log, and Session Replay disk-cache usage.
    /// Allocation-aware bytes are reported so the value tracks actual filesystem
    /// consumption more closely than logical payload length.
    /// </summary>
    public CacheDiagnosticsSnapshot GetCacheDiagnosticsSnapshot()
    {
        var usage = queue.GetUsageSnapshot();
        return new CacheDiagnosticsSnapshot(
            DateTimeOffset.UtcNow,
            usage.AllocatedBytes,
            config.Cache.MaxDiskBytes,
            usage.FileCount,
            config.Cache.MaxFiles);
    }

    /// <summary>Enables the selected automatic UI, HTTP, exception, and launch instrumentation modules.</summary>
    public void EnableAutomaticInstrumentation(AutomaticInstrumentationOptions? options = null)
    {
        if (automaticInstrumentation is null)
        {
            var resolvedOptions = options ?? new AutomaticInstrumentationOptions();
            webViewAutoInstrumentationEnabled = resolvedOptions.EnableWebView;
            if (resolvedOptions.EnableAppLaunch)
            {
                applicationLaunch.Enable();
            }
            automaticInstrumentation = new AutomaticInstrumentation(this, resolvedOptions);
        }
        automaticInstrumentation.Start();
    }

    /// <summary>Attaches a WinUI 3 window for View and Action instrumentation.</summary>
    public void AttachWinUIWindow(object window, string? viewName = null)
    {
        WinUIReflectionInstrumentation.Attach(this, window, viewName);
    }

    /// <summary>Attaches a supported WebView2 control to the native RUM bridge.</summary>
    public void AttachWebView(object webView)
    {
        webViewInstrumentation.Attach(webView);
    }

    /// <summary>Detaches a previously attached WebView2 control.</summary>
    public void DetachWebView(object webView)
    {
        webViewInstrumentation.Detach(webView);
    }

    internal void AttachDiscoveredWebView(object webView)
    {
        if (!webViewAutoInstrumentationEnabled)
        {
            return;
        }

        try
        {
            webViewInstrumentation.Attach(webView);
        }
        catch (ObjectDisposedException)
        {
            // A late UI Loaded/Idle callback may run while the SDK is shutting down.
        }
        catch (ArgumentException ex)
        {
            ReportWebViewDiagnostic(RumDiagnosticLevel.Warning, "Discovered WebView2 control could not be attached.", ex);
        }
    }

    /// <summary>Starts Session Replay recording when Replay is enabled and the session is sampled.</summary>
    public void StartSessionReplayRecording() => sessionReplay.Start();

    /// <summary>Stops Session Replay recording and flushes the active segment.</summary>
    public void StopSessionReplayRecording() => sessionReplay.Stop();

    /// <summary>Overrides text and input privacy for a UI element.</summary>
    public void SetSessionReplayTextAndInputPrivacy(object element, SessionReplayTextAndInputPrivacy? privacy)
    {
        sessionReplayPrivacy.SetTextAndInputPrivacy(element, privacy);
    }

    /// <summary>Overrides pointer and touch privacy for a UI element.</summary>
    public void SetSessionReplayTouchPrivacy(object element, SessionReplayTouchPrivacy? privacy)
    {
        sessionReplayPrivacy.SetTouchPrivacy(element, privacy);
    }

    /// <summary>Overrides image privacy for a UI element.</summary>
    public void SetSessionReplayImagePrivacy(object element, SessionReplayImagePrivacy? privacy)
    {
        sessionReplayPrivacy.SetImagePrivacy(element, privacy);
    }

    /// <summary>Includes or excludes an element subtree from Session Replay.</summary>
    public void SetSessionReplayHidden(object element, bool hidden = true)
    {
        sessionReplayPrivacy.SetHidden(element, hidden);
    }

    /// <summary>Sets user identity and attributes for subsequently created telemetry.</summary>
    public void SetUser(string id, string? name = null, string? email = null, IReadOnlyDictionary<string, object?>? extra = null)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("User id is required.", nameof(id));
        }

        userInfo = new UserInfo(id, name, email, extra ?? new Dictionary<string, object?>());
    }

    /// <summary>Clears the current user identity for subsequent telemetry.</summary>
    public void ClearUser()
    {
        userInfo = null;
    }

    /// <summary>Adds or replaces a context value shared by RUM and logs.</summary>
    public void AddGlobalContext(string key, object? value)
    {
        lock (stateGate)
        {
            globalContext[key] = value;
        }
    }

    /// <summary>Adds or replaces a context value attached only to RUM events.</summary>
    public void AddRumGlobalContext(string key, object? value)
    {
        lock (stateGate)
        {
            rumGlobalContext[key] = value;
        }
    }

    /// <summary>Starts a RUM View and closes the previous active View.</summary>
    public void StartView(string name, IReadOnlyDictionary<string, object?>? properties = null)
    {
        session.Touch();
        CompleteActiveAction();
        ActiveView? viewToClose;
        lock (stateGate)
        {
            viewToClose = activeView;
            activeView = new ActiveView(Guid.NewGuid().ToString("N"), name, viewToClose?.Name, Clock.UnixTimeNanoseconds(), Stopwatch.StartNew());
        }

        if (viewToClose is not null)
        {
            sessionReplay.CaptureViewEnd(CreateSessionReplayContext(viewToClose.Id));
            TrackView(viewToClose, isActive: false, properties: null);
        }

        if (properties is not null)
        {
            foreach (var item in properties)
            {
                activeView?.Properties.TryAdd(item.Key, item.Value);
            }
        }
    }

    /// <summary>Stops the active RUM View.</summary>
    public void StopView(IReadOnlyDictionary<string, object?>? properties = null)
    {
        CompleteActiveAction();
        ActiveView? viewToClose;
        lock (stateGate)
        {
            viewToClose = activeView;
            activeView = null;
        }

        if (viewToClose is not null)
        {
            sessionReplay.CaptureViewEnd(CreateSessionReplayContext(viewToClose.Id));
            TrackView(viewToClose, isActive: false, properties);
        }
    }

    /// <summary>Starts an automatically completed RUM Action with 100 ms frequency protection and a five-second maximum duration.</summary>
    public RumActionScope StartAction(string name, string type, IReadOnlyDictionary<string, object?>? properties = null)
        => StartAction(name, type, needWait: false, properties);

    /// <summary>Starts a RUM Action, optionally requiring an explicit stop. All Actions are limited to five seconds.</summary>
    public RumActionScope StartAction(string name, string type, bool needWait, IReadOnlyDictionary<string, object?>? properties = null)
    {
        session.Touch();
        ActiveAction? actionToClose = null;
        ActiveAction? action;

        lock (actionGate)
        {
            if (activeAction is not null)
            {
                var elapsed = activeAction.Duration.Elapsed;
                var canReplace = elapsed >= actionTiming.MaxDuration ||
                                 (!activeAction.NeedWait && elapsed >= actionTiming.FrequentProtection);
                if (!canReplace)
                {
                    return RumActionScope.Rejected(this);
                }

                actionToClose = RemoveActiveActionLocked(activeAction.Id);
            }

            action = new ActiveAction(
                Guid.NewGuid().ToString("N"),
                name,
                type,
                SnapshotView(),
                Clock.UnixTimeNanoseconds(),
                Stopwatch.StartNew(),
                needWait,
                properties);
            actions[action.Id] = action;
            activeAction = action;
            action.TimeoutTimer = new System.Threading.Timer(
                static state =>
                {
                    var timeout = (ActionTimeoutState)state!;
                    timeout.Client.CompleteAction(timeout.ActionId, requireNeedWait: false);
                },
                new ActionTimeoutState(this, action.Id),
                actionTiming.MaxDuration,
                Timeout.InfiniteTimeSpan);
        }

        if (actionToClose is not null)
        {
            TrackAction(actionToClose, Clock.DurationNanoseconds(actionToClose.Duration));
        }

        return new RumActionScope(this, action.Id);
    }

    /// <summary>Adds a completed RUM Action with a known duration.</summary>
    public void AddAction(string name, string type, TimeSpan duration, IReadOnlyDictionary<string, object?>? properties = null)
    {
        session.Touch();
        var durationNanoseconds = Clock.DurationNanoseconds(duration);
        var action = new ActiveAction(Guid.NewGuid().ToString("N"), name, type, SnapshotView(), Clock.UnixTimeNanosecondsBefore(duration), Stopwatch.StartNew(), false, properties);
        TrackAction(action, durationNanoseconds);
    }

    /// <summary>Stops an active Action that was started with <c>needWait: true</c>.</summary>
    public void StopAction(string actionId)
        => CompleteAction(actionId, requireNeedWait: true);

    private void CompleteAction(string actionId, bool requireNeedWait)
    {
        ActiveAction? action;
        lock (actionGate)
        {
            if (activeAction?.Id != actionId || (requireNeedWait && !activeAction.NeedWait))
            {
                return;
            }

            action = RemoveActiveActionLocked(actionId);
        }

        if (action is not null)
        {
            TrackAction(action, Clock.DurationNanoseconds(action.Duration));
        }
    }

    private ActiveAction? RemoveActiveActionLocked(string actionId)
    {
        if (activeAction?.Id != actionId || !actions.TryRemove(actionId, out var action))
        {
            return null;
        }

        activeAction = null;
        action.TimeoutTimer?.Dispose();
        action.TimeoutTimer = null;
        return action;
    }

    private void CloseNormalActionAfterActivity()
    {
        string? actionId = null;
        lock (actionGate)
        {
            if (activeAction is { NeedWait: false } action &&
                action.Duration.Elapsed >= actionTiming.FrequentProtection)
            {
                actionId = action.Id;
            }
        }

        if (actionId is not null)
        {
            CompleteAction(actionId, requireNeedWait: false);
        }
    }

    private void CompleteActiveAction()
    {
        string? actionId;
        lock (actionGate)
        {
            actionId = activeAction?.Id;
        }

        if (actionId is not null)
        {
            CompleteAction(actionId, requireNeedWait: false);
        }
    }

    internal void MarkApplicationWindowCreated() => applicationLaunch.MarkWindowCreated();

    internal void NotifyApplicationForegrounding() => applicationLaunch.BeginForeground();

    internal void NotifyApplicationBackgrounded() => applicationLaunch.EnterBackground();

    internal void NotifyApplicationFrameRendered() => applicationLaunch.CompleteFrame();

    /// <summary>Starts a manually tracked RUM Resource and returns its identifier.</summary>
    public string StartResource(string url, string method, IReadOnlyDictionary<string, object?>? properties = null)
    {
        session.Touch();
        var resourceId = Guid.NewGuid().ToString("N");
        resources[resourceId] = new ActiveResource(resourceId, url, method, SnapshotView(), SnapshotAction(), Clock.UnixTimeNanoseconds(), Stopwatch.StartNew(), properties);
        return resourceId;
    }

    /// <summary>Stops a RUM Resource using explicit phase timing information.</summary>
    public void StopResource(
        string resourceId,
        int statusCode,
        RumResourceTiming timing,
        long responseSize = -1,
        long requestSize = -1,
        string? resourceType = null,
        string? requestHeader = null,
        string? responseHeader = null,
        string? errorStack = null,
        string? errorMessage = null,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        StopResource(
            resourceId,
            statusCode,
            responseSize,
            requestSize,
            resourceType,
            requestHeader,
            responseHeader,
            errorStack,
            errorMessage,
            MergeResourceTimingProperties(timing, properties));
    }

    /// <summary>Stops a RUM Resource using elapsed time measured since it was started.</summary>
    public void StopResource(
        string resourceId,
        int statusCode,
        long responseSize = -1,
        long requestSize = -1,
        string? resourceType = null,
        string? requestHeader = null,
        string? responseHeader = null,
        string? errorStack = null,
        string? errorMessage = null,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        if (!resources.TryRemove(resourceId, out var resource))
        {
            return;
        }

        var duration = GetInt64(properties, ResourceDurationOverride) ??
                       Clock.DurationNanoseconds(resource.Duration);
        var uri = TryCreateUri(resource.Url);
        var rumEvent = CreateEvent(RumConstants.MeasurementResource, resource.StartTime)
            .WithTag(RumConstants.ResourceId, resource.Id)
            .WithTag(RumConstants.ResourceUrl, resource.Url)
            .WithTag(RumConstants.ResourceUrlHost, uri?.Host)
            .WithTag(RumConstants.ResourceUrlPath, uri?.AbsolutePath)
            .WithTag(RumConstants.ResourceUrlPathGroup, uri is null ? null : GroupUrlPath(uri.AbsolutePath))
            .WithTag(RumConstants.ResourceMethod, resource.Method)
            .WithTag(RumConstants.ResourceStatus, statusCode)
            .WithTag(RumConstants.ResourceStatusGroup, statusCode > 0 ? $"{statusCode / 100}xx" : null)
            .WithTag(RumConstants.ResourceType, resourceType)
            .WithTag(RumConstants.ViewId, resource.View?.Id)
            .WithTag(RumConstants.ViewName, resource.View?.Name)
            .WithTag(RumConstants.ViewReferrer, resource.View?.Referrer)
            .WithTag(RumConstants.ActionId, resource.Action?.Id)
            .WithTag(RumConstants.ActionName, resource.Action?.Name)
            .WithTag(RumConstants.TraceId, GetString(properties, RumConstants.TraceId))
            .WithTag(RumConstants.SpanId, GetString(properties, RumConstants.SpanId))
            .WithTag(RumConstants.ResourceHttpProtocol, GetString(properties, RumConstants.ResourceHttpProtocol))
            .WithField(RumConstants.ResourceDuration, duration)
            .WithField(RumConstants.ResourceSize, responseSize > 0 ? responseSize : null)
            .WithField(RumConstants.ResourceRequestSize, requestSize > 0 ? requestSize : null)
            .WithField(RumConstants.ResourceDns, GetInt64(properties, RumConstants.ResourceDns))
            .WithField(RumConstants.ResourceTcp, GetInt64(properties, RumConstants.ResourceTcp))
            .WithField(RumConstants.ResourceSsl, GetInt64(properties, RumConstants.ResourceSsl))
            .WithField(RumConstants.ResourceTtfb, GetInt64(properties, RumConstants.ResourceTtfb))
            .WithField(RumConstants.ResourceTimingSource, GetString(properties, RumConstants.ResourceTimingSource))
            .WithField(RumConstants.ResourceTimingPrecision, GetString(properties, RumConstants.ResourceTimingPrecision))
            .WithField(RumConstants.ResourceTimingDuration, GetInt64(properties, RumConstants.ResourceTimingDuration))
            .WithField(RumConstants.ResourceTimingPhase, GetString(properties, RumConstants.ResourceTimingPhase))
            .WithField(RumConstants.ResourceTtfbEstimated, GetBool(properties, RumConstants.ResourceTtfbEstimated))
            .WithField(RumConstants.RequestHeader, requestHeader)
            .WithField(RumConstants.ResponseHeader, responseHeader);

        AddFields(rumEvent, resource.Properties);
        AddFieldsExcept(rumEvent, properties, ResourcePropertyKeys);
        Enqueue(rumEvent);
        IncrementResource(resource.View, resource.Action);

        if (statusCode >= 400 || !string.IsNullOrWhiteSpace(errorStack))
        {
            var path = uri?.AbsolutePath;
            var privacySafeUrl = HttpHeaderRedactor.RedactUrl(resource.Url, config.Privacy);
            var errorProperties = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [RumConstants.ResourceId] = resource.Id,
                [RumConstants.ResourceUrl] = resource.Url,
                [RumConstants.ResourceUrlHost] = uri?.Host,
                [RumConstants.ResourceUrlPath] = path,
                [RumConstants.ResourceUrlPathGroup] = path is null ? null : GroupUrlPath(path),
                [RumConstants.ResourceMethod] = resource.Method,
                [RumConstants.ResourceStatus] = statusCode,
                [RumConstants.ResourceStatusGroup] = statusCode > 0 ? $"{statusCode / 100}xx" : null
            };

            if (properties is not null)
            {
                foreach (var item in properties)
                {
                    errorProperties[item.Key] = item.Value;
                }
            }

            AddError(
                errorStack ?? string.Empty,
                errorMessage ?? $"[{statusCode}][{privacySafeUrl}]",
                "network_error",
                "network",
                errorProperties);
        }
    }

    /// <summary>Adds a RUM Error from an exception.</summary>
    public void AddError(Exception exception, IReadOnlyDictionary<string, object?>? properties = null)
    {
        AddError(exception.ToString(), exception.Message, exception.GetType().Name, "logger", properties);
    }

    /// <summary>Adds a RUM Error from explicit stack, message, type, and source values.</summary>
    public void AddError(string stack, string message, string errorType, string source = "logger", IReadOnlyDictionary<string, object?>? properties = null)
    {
        AddError(stack, message, errorType, source, properties, waitForQueue: false);
    }

    internal void AddError(
        string stack,
        string message,
        string errorType,
        string source,
        IReadOnlyDictionary<string, object?>? properties,
        bool waitForQueue)
    {
        session.Touch();
        sessionReplay.NotifyError(CreateSessionReplayContext());
        var view = SnapshotView();
        var action = SnapshotAction();
        var rumEvent = CreateEvent(RumConstants.MeasurementError, Clock.UnixTimeNanoseconds())
            .WithTag(RumConstants.ErrorType, errorType)
            .WithTag(RumConstants.ErrorSource, source)
            .WithTag(RumConstants.ErrorSituation, "run")
            .WithTag(RumConstants.ViewId, view?.Id)
            .WithTag(RumConstants.ViewName, view?.Name)
            .WithTag(RumConstants.ViewReferrer, view?.Referrer)
            .WithTag(RumConstants.ActionId, action?.Id)
            .WithTag(RumConstants.ActionName, action?.Name)
            .WithField(RumConstants.ErrorMessage, message)
            .WithField(RumConstants.ErrorStack, stack);

        AddErrorResourceTags(rumEvent, properties);
        Enqueue(rumEvent, waitForQueue);
        IncrementError(view, action);
    }

    /// <summary>Adds a RUM Long Task.</summary>
    public void AddLongTask(TimeSpan duration, string? stack = null, IReadOnlyDictionary<string, object?>? properties = null)
    {
        session.Touch();
        var view = SnapshotView();
        var action = SnapshotAction();
        var durationNanoseconds = Clock.DurationNanoseconds(duration);
        var rumEvent = CreateEvent(RumConstants.MeasurementLongTask, Clock.UnixTimeNanosecondsBefore(duration))
            .WithTag(RumConstants.ViewId, view?.Id)
            .WithTag(RumConstants.ViewName, view?.Name)
            .WithTag(RumConstants.ViewReferrer, view?.Referrer)
            .WithTag(RumConstants.ActionId, action?.Id)
            .WithTag(RumConstants.ActionName, action?.Name)
            .WithField(RumConstants.LongTaskDuration, durationNanoseconds)
            .WithField(RumConstants.LongTaskStack, stack ?? string.Empty);

        AddFields(rumEvent, properties);
        Enqueue(rumEvent);
        IncrementLongTask(view, action);
    }

    /// <summary>Adds a custom log with a predefined status.</summary>
    public void AddLog(
        string content,
        LogStatus status,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        AddLog(content, LogStatusNames.ToProtocolValue(status), properties);
    }

    /// <summary>Adds a custom log with an application-defined status.</summary>
    public void AddLog(
        string content,
        string status,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        logPipeline.AddLog(content, status, properties);
    }

    /// <summary>Adds a batch of custom logs.</summary>
    public void AddLogs(IEnumerable<LogEntry> logs)
    {
        ArgumentNullException.ThrowIfNull(logs);
        foreach (var log in logs)
        {
            if (log is null)
            {
                throw new ArgumentException("Log collection must not contain null entries.", nameof(logs));
            }

            AddLog(log.Content, log.Status, log.Properties);
        }
    }

    /// <summary>Returns current log queue and upload diagnostics.</summary>
    public LogDiagnosticsSnapshot GetLogDiagnosticsSnapshot()
    {
        return logPipeline.GetDiagnosticsSnapshot();
    }

    /// <summary>Attempts to upload all currently queued telemetry.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await uploadScheduler.DrainAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops instrumentation, flushes queues, and releases client resources.</summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        webViewInstrumentation.Dispose();
        CompleteActiveAction();
        StopView();
        // Shutdown is deliberately bounded. Remaining ready batches stay durable
        // for the next process instead of delaying application exit while the
        // configured bandwidth limit drains the entire historical cache.
        await uploadScheduler.FlushCycleAsync(cancellationToken).ConfigureAwait(false);
        shutdown.Cancel();
    }

    /// <summary>Asynchronously shuts down and releases the client.</summary>
    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        await uploadScheduler.DisposeAsync().ConfigureAwait(false);
        automaticInstrumentation?.Dispose();
        await sessionReplay.DisposeAsync().ConfigureAwait(false);
        sessionReplayTransport.Dispose();
        transport.Dispose();
        await sessionReplayQueue.DisposeAsync().ConfigureAwait(false);
        await logPipeline.DisposeAsync().ConfigureAwait(false);
        await queue.DisposeAsync().ConfigureAwait(false);
        shutdown.Dispose();
    }

    internal void CaptureSessionReplayFullSnapshot(object root)
    {
        if (!sessionReplay.IsRecordingEnabled)
        {
            return;
        }

        try
        {
            var node = SessionReplayTreeMapper.Map(root, sessionReplayPrivacy, config.SessionReplay);
            if (node is not null)
            {
                sessionReplay.CaptureFullSnapshot(CreateSessionReplayContext(), node);
            }
        }
        catch (Exception ex)
        {
            EmitDiagnostic(RumDiagnosticLevel.Warning, "session_replay_capture", "Session Replay snapshot capture failed.", exception: ex);
        }
    }

    internal void CaptureSessionReplayClick(string target, double x, double y)
    {
        if (!sessionReplay.IsRecordingEnabled)
        {
            return;
        }

        sessionReplay.CaptureIncrementalEvent(CreateSessionReplayContext(), "click", target, x, y);
    }

    internal void CaptureSessionReplayClick(object element, string target, double x, double y)
    {
        if (!sessionReplay.IsRecordingEnabled)
        {
            return;
        }

        var privacy = sessionReplayPrivacy.Resolve(element, SessionReplayPrivacyOverrides.FromConfig(config.SessionReplay), config.SessionReplay);
        if (privacy.TouchPrivacy == SessionReplayTouchPrivacy.Hide || privacy.Hidden)
        {
            sessionReplay.CaptureIncrementalEvent(CreateSessionReplayContext(), "click", "hidden", 0, 0);
            return;
        }

        CaptureSessionReplayClick(target, x, y);
    }

    internal void CaptureSessionReplayInput(string target, string? value = null)
    {
        if (!sessionReplay.IsRecordingEnabled)
        {
            return;
        }

        sessionReplay.CaptureIncrementalEvent(CreateSessionReplayContext(), "input", target, 0, 0, value);
    }

    internal void CaptureSessionReplayInteraction(string eventType, string target)
    {
        if (!sessionReplay.IsRecordingEnabled)
        {
            return;
        }

        sessionReplay.CaptureIncrementalEvent(CreateSessionReplayContext(), eventType, target, 0, 0);
    }

    internal void CaptureSessionReplayResize(string target, double width, double height)
    {
        if (!sessionReplay.IsRecordingEnabled)
        {
            return;
        }

        sessionReplay.CaptureResizeEvent(CreateSessionReplayContext(), target, width, height);
    }

    internal void CaptureSessionReplayWebViewRecord(string slotId, JsonElement record, string? webViewId)
    {
        if (!sessionReplay.IsRecordingEnabled || string.IsNullOrWhiteSpace(webViewId))
        {
            return;
        }

        sessionReplay.CaptureWebViewRecord(
            CreateSessionReplayContext(webViewId),
            slotId,
            record);
    }

    internal void TrackWebViewRumRecord(
        JsonElement record,
        string? hostViewId,
        string? hostViewName,
        string sourceUrl)
    {
        if (record.ValueKind != JsonValueKind.Object ||
            !record.TryGetProperty("measurement", out var measurementProperty) ||
            measurementProperty.ValueKind != JsonValueKind.String ||
            !IsSupportedWebViewMeasurement(measurementProperty.GetString()) ||
            !record.TryGetProperty("time", out var timeProperty) ||
            !timeProperty.TryGetInt64(out var timestampMilliseconds) ||
            timestampMilliseconds <= 0 ||
            timestampMilliseconds > long.MaxValue / 1_000_000 ||
            !record.TryGetProperty("tags", out var tags) ||
            tags.ValueKind != JsonValueKind.Object ||
            !record.TryGetProperty("fields", out var fields) ||
            fields.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var measurement = measurementProperty.GetString()!;
        var rumEvent = CreateEvent(measurement, timestampMilliseconds * 1_000_000);
        AddWebViewProperties(rumEvent, tags, asTags: true);
        AddWebViewProperties(rumEvent, fields, asTags: false);
        if (rumEvent.Fields.Count == 0)
        {
            return;
        }

        ApplyTrustedEventContext(rumEvent);
        rumEvent
            .WithTag("is_web_view", true)
            .WithTag("webview_url", sourceUrl)
            .WithTag("webview_host_view_id", hostViewId)
            .WithTag("webview_host_view_name", hostViewName);

        if (!string.IsNullOrWhiteSpace(hostViewId))
        {
            rumEvent.WithTag(
                "container",
                JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["source"] = RumConstants.WindowsSource,
                    ["view_id"] = hostViewId
                }));
        }

        if (string.Equals(measurement, RumConstants.MeasurementView, StringComparison.Ordinal))
        {
            rumEvent.WithField(RumConstants.ViewIsActive, false);
        }
        if (string.Equals(measurement, RumConstants.MeasurementError, StringComparison.Ordinal))
        {
            sessionReplay.NotifyError(CreateSessionReplayContext());
        }

        Enqueue(rumEvent);
    }

    private static bool IsSupportedWebViewMeasurement(string? measurement)
    {
        return measurement is
            RumConstants.MeasurementView or
            RumConstants.MeasurementAction or
            RumConstants.MeasurementResource or
            RumConstants.MeasurementError or
            RumConstants.MeasurementLongTask;
    }

    private void AddWebViewProperties(RumEvent rumEvent, JsonElement properties, bool asTags)
    {
        var count = 0;
        foreach (var property in properties.EnumerateObject())
        {
            if (++count > 256 || !IsSafeWebViewPropertyKey(property.Name))
            {
                continue;
            }

            var value = ConvertWebViewJsonValue(property.Value);
            if (asTags)
            {
                rumEvent.WithTag(property.Name, value);
            }
            else
            {
                rumEvent.WithField(property.Name, value);
            }
        }
    }

    private static bool IsSafeWebViewPropertyKey(string key)
    {
        if (key.Length is < 1 or > 128)
        {
            return false;
        }

        foreach (var character in key)
        {
            if (!IsAsciiLetterOrDigit(character) &&
                character is not '_' and not '-' and not '.')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetterOrDigit(char character)
    {
        return character is >= 'a' and <= 'z' or
            >= 'A' and <= 'Z' or
            >= '0' and <= '9';
    }

    private static object? ConvertWebViewJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when value.TryGetDouble(out var number) && double.IsFinite(number) => number,
            JsonValueKind.Null => null,
            JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
            _ => null
        };
    }

    private RumEvent CreateEvent(string measurement, long timestampNanoseconds)
    {
        return ApplyTrustedEventContext(new RumEvent(measurement, timestampNanoseconds));
    }

    private RumEvent ApplyTrustedEventContext(RumEvent rumEvent)
    {
        var user = userInfo;
        rumEvent
            .WithTag(RumConstants.AppId, config.RumAppId)
            .WithTag(RumConstants.Service, config.ServiceName)
            .WithTag(RumConstants.Env, config.Env.ToLowerInvariant())
            .WithTag(RumConstants.Version, config.Version)
            .WithTag(RumConstants.SdkName, RumConstants.WindowsSdkName)
            .WithTag(RumConstants.SdkVersion, typeof(GuanceClient).Assembly.GetName().Version?.ToString() ?? "0.1.0")
            .WithTag(RumConstants.ApplicationUuid, platformInfo.ApplicationUuid)
            .WithTag(RumConstants.Os, platformInfo.Os)
            .WithTag(RumConstants.OsVersion, platformInfo.OsVersion)
            .WithTag(RumConstants.OsVersionMajor, platformInfo.OsVersionMajor)
            .WithTag(RumConstants.Device, platformInfo.Device)
            .WithTag(RumConstants.Model, platformInfo.Model)
            .WithTag(RumConstants.Arch, platformInfo.Architecture)
            .WithTag(RumConstants.ScreenSize, platformInfo.ScreenSize)
            .WithTag(RumConstants.Locale, platformInfo.Locale)
            .WithTag(RumConstants.NetworkType, RumPlatformInfo.GetNetworkType())
            .WithTag(RumConstants.SessionId, session.SessionId)
            .WithTag(RumConstants.SessionType, "user")
            .WithTag(RumConstants.IsSignIn, user is null ? "F" : "T")
            .WithTag(RumConstants.UserId, user?.Id ?? anonymousUserId)
            .WithField(RumConstants.SessionHasReplay, sessionReplay.HasReplay(session.SessionId))
            .WithField(RumConstants.SessionSampleRate, config.SampleRate)
            .WithField(RumConstants.SessionOnErrorSampleRate, config.SessionErrorSampleRate);

        if (!session.IsSampled && session.IsErrorSampled)
        {
            rumEvent.WithField(RumConstants.SampledForErrorSession, true);
        }

        lock (stateGate)
        {
            AddTags(rumEvent, globalContext);
            AddTags(rumEvent, rumGlobalContext);
        }

        if (user is not null)
        {
            rumEvent
                .WithTag(RumConstants.UserName, user.Name)
                .WithTag(RumConstants.UserEmail, user.Email);
            AddTags(rumEvent, user.Extra);
        }

        return rumEvent;
    }

    private LogEvent CreateLogEvent(
        string content,
        string status,
        IReadOnlyDictionary<string, object?>? properties)
    {
        if (config.Logging.EnableLinkRumData)
        {
            session.Touch();
        }

        var user = userInfo;
        var view = config.Logging.EnableLinkRumData ? SnapshotView() : null;
        var action = config.Logging.EnableLinkRumData ? SnapshotAction() : null;
        var logEvent = new LogEvent(Clock.UnixTimeNanoseconds())
            .WithTag(RumConstants.AppId, config.RumAppId)
            .WithTag(RumConstants.Service, config.ServiceName)
            .WithTag(RumConstants.Env, config.Env.ToLowerInvariant())
            .WithTag(RumConstants.Version, config.Version)
            .WithTag(RumConstants.SdkName, RumConstants.WindowsSdkName)
            .WithTag(RumConstants.SdkVersion, typeof(GuanceClient).Assembly.GetName().Version?.ToString() ?? "0.1.0")
            .WithTag(RumConstants.ApplicationUuid, platformInfo.ApplicationUuid)
            .WithTag(RumConstants.Os, platformInfo.Os)
            .WithTag(RumConstants.OsVersion, platformInfo.OsVersion)
            .WithTag(RumConstants.OsVersionMajor, platformInfo.OsVersionMajor)
            .WithTag(RumConstants.Device, platformInfo.Device)
            .WithTag(RumConstants.Model, platformInfo.Model)
            .WithTag(RumConstants.Arch, platformInfo.Architecture)
            .WithTag(RumConstants.ScreenSize, platformInfo.ScreenSize)
            .WithTag(RumConstants.Locale, platformInfo.Locale)
            .WithTag(RumConstants.NetworkType, RumPlatformInfo.GetNetworkType())
            .WithTag(RumConstants.IsSignIn, user is null ? "F" : "T")
            .WithTag(
                RumConstants.UserId,
                user?.Id ?? (config.Logging.EnableLinkRumData ? anonymousUserId : null))
            .WithTag(RumConstants.UserName, user?.Name)
            .WithTag(RumConstants.UserEmail, user?.Email)
            .WithTag(RumConstants.SessionId, config.Logging.EnableLinkRumData ? session.SessionId : null)
            .WithTag(RumConstants.SessionType, config.Logging.EnableLinkRumData ? "user" : null)
            .WithTag(RumConstants.ViewId, view?.Id)
            .WithTag(RumConstants.ViewName, view?.Name)
            .WithTag(RumConstants.ViewReferrer, view?.Referrer)
            .WithTag(RumConstants.ActionId, action?.Id)
            .WithTag(RumConstants.ActionName, action?.Name);

        lock (stateGate)
        {
            AddTags(logEvent, globalContext);
            AddTags(logEvent, config.Logging.GlobalContext);
        }

        if (user is not null)
        {
            AddTags(logEvent, user.Extra);
        }

        if (properties is not null)
        {
            foreach (var item in properties)
            {
                logEvent.WithField(item.Key, item.Value);
            }
        }

        // Reserved fields are authoritative even when callers pass colliding properties.
        return logEvent
            .WithField(RumConstants.LogMessage, content)
            .WithField(RumConstants.LogStatus, status);
    }

    private void Enqueue(RumEvent rumEvent, bool waitForQueue = false)
    {
        if (!sampling.ShouldCollect(rumEvent.Measurement))
        {
            Interlocked.Increment(ref rumEventsDroppedBySampling);
            EmitDiagnostic(RumDiagnosticLevel.Debug, "sampling", $"Dropped {rumEvent.Measurement} by session sampling.");
            return;
        }

        TelemetryModifierPipeline.Apply(rumEvent, config);
        var line = LineProtocolFormatter.Format(rumEvent);
        Task<BatchAppendResult> enqueueTask;
        try
        {
            enqueueTask = queue.EnqueueAsync(line, shutdown.Token);
            TrackRumQueueWrite(enqueueTask);
        }
        catch (Exception ex)
        {
            RecordQueueError(ex);
            return;
        }

        if (waitForQueue)
        {
            try
            {
                enqueueTask.GetAwaiter().GetResult();
                using var crashFlush = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                crashFlush.CancelAfter(TimeSpan.FromSeconds(2));
                uploadScheduler.DrainAsync(crashFlush.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                RecordQueueError(ex);
            }

            return;
        }

    }

    private void TrackRumQueueWrite(Task<BatchAppendResult> enqueueTask)
    {
        lock (rumQueueWriteGate)
        {
            pendingRumQueueWrites.Add(enqueueTask);
        }

        _ = enqueueTask.ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                RecordQueueError(task.Exception?.GetBaseException() ?? new InvalidOperationException("Queue enqueue failed."));
            }
            else if (task.IsCanceled)
            {
                RecordQueueError(new OperationCanceledException("Queue enqueue was canceled."));
            }
            else if (task.Result.Accepted)
            {
                Interlocked.Increment(ref rumEventsEnqueued);
            }
            else
            {
                EmitDiagnostic(RumDiagnosticLevel.Warning, "queue", "Dropped RUM event because the shared cache is full.");
            }

            lock (rumQueueWriteGate)
            {
                pendingRumQueueWrites.Remove(task);
            }

            if (task.Status == TaskStatus.RanToCompletion && task.Result.BatchReady)
            {
                uploadScheduler.Wake();
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task WaitForPendingRumQueueWritesAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task[] pending;
            lock (rumQueueWriteGate)
            {
                pending = pendingRumQueueWrites.ToArray();
            }

            if (pending.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Enqueue failures are recorded by TrackRumQueueWrite. Continue until
                // all writes observed by this flush have completed or failed.
            }
        }
    }

    private async Task SealRumQueueAsync(bool force, CancellationToken cancellationToken)
    {
        await WaitForPendingRumQueueWritesAsync(cancellationToken).ConfigureAwait(false);
        if (force)
        {
            await queue.SealAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await queue.SealIfOlderAsync(config.FlushInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<UploadAttemptStatus> TryUploadRumQueueOnceAsync(CancellationToken cancellationToken)
    {
        if (!rumRetryBackoff.CanAttemptNow()) return UploadAttemptStatus.RetryLater;
        var batch = await queue.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            rumRetryBackoff.Reset();
            return UploadAttemptStatus.Empty;
        }

        try
        {
            await uploadRateLimiter.WaitAsync(
                config.CompressIntakeRequests
                    ? UploadSizeEstimator.EstimateDeflateUpperBound(batch.PayloadBytes)
                    : batch.PayloadBytes,
                cancellationToken).ConfigureAwait(false);
            var result = await transport.SendAsync(batch.Items, cancellationToken).ConfigureAwait(false);
            RecordRumUploadResult(result);

            if (result.DeleteFromQueue)
            {
                rumRetryBackoff.Reset();
                await queue.CompleteAsync(batch.LeaseId, cancellationToken).ConfigureAwait(false);
                return UploadAttemptStatus.Consumed;
            }

            await queue.AbandonAsync(batch.LeaseId, cancellationToken).ConfigureAwait(false);
            if (result.RetryLater) rumRetryBackoff.ScheduleRetry(result.RetryAfter);
            return UploadAttemptStatus.RetryLater;
        }
        catch
        {
            await queue.AbandonAsync(batch.LeaseId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task SealSessionReplayQueueAsync(bool force, CancellationToken cancellationToken)
    {
        if (force || Clock.ElapsedSince(Volatile.Read(ref lastReplayPendingFlushAt)) >= config.SessionReplay.FlushInterval)
        {
            await sessionReplay.FlushPendingRecordsAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref lastReplayPendingFlushAt, Clock.Timestamp());
        }

        if (force)
        {
            await sessionReplayQueue.SealAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await sessionReplayQueue.SealIfOlderAsync(config.SessionReplay.FlushInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<UploadAttemptStatus> TryUploadSessionReplayOnceAsync(CancellationToken cancellationToken)
    {
        if (!config.SessionReplay.Enabled || !sessionReplayRetryBackoff.CanAttemptNow())
        {
            return config.SessionReplay.Enabled ? UploadAttemptStatus.RetryLater : UploadAttemptStatus.Empty;
        }

        var batch = await sessionReplayQueue.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            sessionReplayRetryBackoff.Reset();
            return UploadAttemptStatus.Empty;
        }

        try
        {
            var segment = batch.Items.Single();
            LogSessionReplayUploadPayload(segment);
            await uploadRateLimiter.WaitAsync(batch.PayloadBytes, cancellationToken).ConfigureAwait(false);
            var result = await sessionReplayTransport.SendAsync(segment, cancellationToken).ConfigureAwait(false);
            RecordReplayUploadResult(result);

            if (result.DeleteFromQueue)
            {
                sessionReplayRetryBackoff.Reset();
                await sessionReplayQueue.CompleteAsync(batch.LeaseId, cancellationToken).ConfigureAwait(false);
                return UploadAttemptStatus.Consumed;
            }

            await sessionReplayQueue.AbandonAsync(batch.LeaseId, cancellationToken).ConfigureAwait(false);
            if (result.RetryLater) sessionReplayRetryBackoff.ScheduleRetry(result.RetryAfter);
            return UploadAttemptStatus.RetryLater;
        }
        catch
        {
            await sessionReplayQueue.AbandonAsync(batch.LeaseId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private void LogSessionReplayUploadPayload(QueuedSessionReplaySegment segment)
    {
        if (!SdkDiagnostics.IsEnabled)
        {
            return;
        }

        try
        {
            var json = SessionReplaySegmentBuilder.TryGetDebugPayload(segment.ContentType, segment.Body);
            if (string.IsNullOrWhiteSpace(json))
            {
                SdkDiagnostics.WriteLine("[Guance.RUM.SessionReplay] upload payload structure unavailable.");
                return;
            }

            var message = "[Guance.RUM.SessionReplay] upload payload structure:\n" + json;
            SdkDiagnostics.WriteLine(message);
        }
        catch (Exception ex)
        {
            SdkDiagnostics.WriteLine($"[Guance.RUM.SessionReplay] failed to print upload payload structure: {ex.Message}");
        }
    }

    private void RecordRumUploadResult(SendResult result)
    {
        Interlocked.Exchange(ref lastRumUploadStatusCode, result.StatusCode ?? 0);
        if (result.RetryLater)
        {
            Volatile.Write(ref lastRumUploadError, result.ErrorMessage);
            Interlocked.Increment(ref rumUploadRetryCount);
            EmitDiagnostic(RumDiagnosticLevel.Warning, "rum_upload", result.ErrorMessage ?? "RUM upload will retry.", result.StatusCode);
        }
        else if (result.StatusCode is >= 200 and < 300)
        {
            Volatile.Write(ref lastRumUploadError, null);
            Interlocked.Increment(ref rumUploadSuccessCount);
            EmitDiagnostic(RumDiagnosticLevel.Debug, "rum_upload", "RUM upload succeeded.", result.StatusCode);
        }
        else if (result.DeleteFromQueue)
        {
            Volatile.Write(ref lastRumUploadError, result.ErrorMessage);
            Interlocked.Increment(ref rumUploadTerminalFailureCount);
            EmitDiagnostic(RumDiagnosticLevel.Error, "rum_upload", result.ErrorMessage ?? "RUM upload terminal failure.", result.StatusCode);
        }
    }

    private void RecordReplayUploadResult(SendResult result)
    {
        Interlocked.Exchange(ref lastReplayUploadStatusCode, result.StatusCode ?? 0);
        if (result.RetryLater)
        {
            Volatile.Write(ref lastReplayUploadError, result.ErrorMessage);
            Interlocked.Increment(ref replayUploadRetryCount);
            EmitDiagnostic(RumDiagnosticLevel.Warning, "session_replay_upload", result.ErrorMessage ?? "Session Replay upload will retry.", result.StatusCode);
        }
        else if (result.StatusCode is >= 200 and < 300)
        {
            Volatile.Write(ref lastReplayUploadError, null);
            Interlocked.Increment(ref replayUploadSuccessCount);
            EmitDiagnostic(RumDiagnosticLevel.Debug, "session_replay_upload", "Session Replay upload succeeded.", result.StatusCode);
        }
        else if (result.DeleteFromQueue)
        {
            Volatile.Write(ref lastReplayUploadError, result.ErrorMessage);
            Interlocked.Increment(ref replayUploadTerminalFailureCount);
            EmitDiagnostic(RumDiagnosticLevel.Error, "session_replay_upload", result.ErrorMessage ?? "Session Replay upload terminal failure.", result.StatusCode);
        }
    }

    private void RecordQueueError(Exception exception)
    {
        Interlocked.Increment(ref queueErrorCount);
        Volatile.Write(ref lastQueueError, exception.Message);
        EmitDiagnostic(RumDiagnosticLevel.Error, "queue", exception.Message, exception: exception);
    }

    internal void ReportWebViewDiagnostic(RumDiagnosticLevel level, string message, Exception? exception = null)
    {
        EmitDiagnostic(level, "webview", message, exception: exception);
    }

    internal void ReportDiagnostic(RumDiagnosticLevel level, string source, string message, Exception? exception = null)
    {
        EmitDiagnostic(level, source, message, exception: exception);
    }

    private void EmitDiagnostic(RumDiagnosticLevel level, string source, string message, int? statusCode = null, Exception? exception = null)
    {
        PublishDiagnostic(new RumDiagnosticEvent(
            DateTimeOffset.UtcNow,
            level,
            source,
            message,
            statusCode,
            exception));
    }

    private void PublishDiagnostic(RumDiagnosticEvent item)
    {
        SdkDiagnostics.Write(item);

        try
        {
            config.DiagnosticListener?.Invoke(item);
        }
        catch (Exception ex)
        {
            SdkDiagnostics.WriteLine($"[Guance.RUM] diagnostic listener failed: {ex}");
        }

        try
        {
            DiagnosticEvent?.Invoke(this, item);
        }
        catch (Exception ex)
        {
            SdkDiagnostics.WriteLine($"[Guance.RUM] diagnostic event handler failed: {ex}");
        }
    }

    private void TrackView(ActiveView view, bool isActive, IReadOnlyDictionary<string, object?>? properties)
    {
        var elapsed = Clock.DurationNanoseconds(view.Duration);
        var rumEvent = CreateEvent(RumConstants.MeasurementView, view.StartTime)
            .WithTag(RumConstants.ViewId, view.Id)
            .WithTag(RumConstants.ViewName, view.Name)
            .WithTag(RumConstants.ViewReferrer, view.Referrer)
            .WithField(RumConstants.ViewTimeSpent, elapsed)
            .WithField(RumConstants.ViewIsActive, isActive)
            .WithField(RumConstants.ViewActionCount, view.ActionCount)
            .WithField(RumConstants.ViewResourceCount, view.ResourceCount)
            .WithField(RumConstants.ViewErrorCount, view.ErrorCount)
            .WithField(RumConstants.ViewLongTaskCount, view.LongTaskCount)
            .WithField(RumConstants.ViewUpdateTime, Clock.UnixTimeNanoseconds());

        AddFields(rumEvent, view.Properties);
        AddFields(rumEvent, properties);
        Enqueue(rumEvent);
    }

    private void TrackAction(ActiveAction action, long durationNanoseconds)
    {
        var rumEvent = CreateEvent(RumConstants.MeasurementAction, action.StartTime)
            .WithTag(RumConstants.ViewId, action.View?.Id)
            .WithTag(RumConstants.ViewName, action.View?.Name)
            .WithTag(RumConstants.ViewReferrer, action.View?.Referrer)
            .WithTag(RumConstants.ActionId, action.Id)
            .WithTag(RumConstants.ActionName, action.Name)
            .WithTag(RumConstants.ActionType, action.Type)
            .WithField(RumConstants.ActionDuration, durationNanoseconds)
            .WithField(RumConstants.ActionResourceCount, action.ResourceCount)
            .WithField(RumConstants.ActionErrorCount, action.ErrorCount)
            .WithField(RumConstants.ActionLongTaskCount, action.LongTaskCount);

        AddFields(rumEvent, action.Properties);
        Enqueue(rumEvent);
        IncrementAction(action.View);
    }

    private void TrackApplicationLaunch(ApplicationLaunchRecord launch)
    {
        session.Touch();
        var action = new ActiveAction(
            Guid.NewGuid().ToString("N"),
            launch.Name,
            launch.Type,
            SnapshotView(),
            launch.StartTimeNanoseconds,
            Stopwatch.StartNew(),
            false,
            launch.Properties);
        TrackAction(action, launch.DurationNanoseconds);
    }

    private ActiveViewSnapshot? SnapshotView()
    {
        lock (stateGate)
        {
            return activeView is null ? null : new ActiveViewSnapshot(activeView.Id, activeView.Name, activeView.Referrer);
        }
    }

    internal (string? Id, string? Name) GetActiveViewCorrelation()
    {
        var view = SnapshotView();
        return (view?.Id, view?.Name);
    }

    private ActiveActionSnapshot? SnapshotAction()
    {
        lock (actionGate)
        {
            return activeAction is null ? null : new ActiveActionSnapshot(activeAction.Id, activeAction.Name);
        }
    }

    private SessionReplayContext CreateSessionReplayContext()
    {
        var view = SnapshotView();
        return CreateSessionReplayContext(view?.Id);
    }

    private SessionReplayContext CreateSessionReplayContext(string? viewId)
    {
        return new SessionReplayContext(
            config.RumAppId,
            session.SessionId,
            viewId,
            config.ServiceName,
            config.Env,
            config.Version,
            RumConstants.WindowsSdkName,
            typeof(GuanceClient).Assembly.GetName().Version?.ToString() ?? "0.1.0");
    }

    private void IncrementAction(ActiveViewSnapshot? view)
    {
        if (view is null)
        {
            return;
        }

        lock (stateGate)
        {
            if (activeView?.Id == view.Id)
            {
                activeView.ActionCount++;
            }
        }
    }

    private void IncrementResource(ActiveViewSnapshot? view, ActiveActionSnapshot? action)
    {
        lock (stateGate)
        {
            if (view is not null && activeView?.Id == view.Id)
            {
                activeView.ResourceCount++;
            }
        }

        if (action is not null && actions.TryGetValue(action.Id, out var activeAction))
        {
            activeAction.ResourceCount++;
        }

        CloseNormalActionAfterActivity();
    }

    private void IncrementError(ActiveViewSnapshot? view, ActiveActionSnapshot? action)
    {
        lock (stateGate)
        {
            if (view is not null && activeView?.Id == view.Id)
            {
                activeView.ErrorCount++;
            }
        }

        if (action is not null && actions.TryGetValue(action.Id, out var activeAction))
        {
            activeAction.ErrorCount++;
        }

        CloseNormalActionAfterActivity();
    }

    private void IncrementLongTask(ActiveViewSnapshot? view, ActiveActionSnapshot? action)
    {
        lock (stateGate)
        {
            if (view is not null && activeView?.Id == view.Id)
            {
                activeView.LongTaskCount++;
            }
        }

        if (action is not null && actions.TryGetValue(action.Id, out var activeAction))
        {
            activeAction.LongTaskCount++;
        }

        CloseNormalActionAfterActivity();
    }

    private static void AddTags(RumEvent rumEvent, IReadOnlyDictionary<string, object?> values)
    {
        foreach (var item in values)
        {
            if (!rumEvent.Tags.ContainsKey(item.Key))
            {
                rumEvent.WithTag(item.Key, item.Value);
            }
        }
    }

    private static void AddTags(LogEvent logEvent, IReadOnlyDictionary<string, object?> values)
    {
        foreach (var item in values)
        {
            if (!logEvent.Tags.ContainsKey(item.Key))
            {
                logEvent.WithTag(item.Key, item.Value);
            }
        }
    }

    private static void AddFields(RumEvent rumEvent, IReadOnlyDictionary<string, object?>? values)
    {
        if (values is null)
        {
            return;
        }

        foreach (var item in values)
        {
            rumEvent.WithField(item.Key, item.Value);
        }
    }

    private static void AddFieldsExcept(RumEvent rumEvent, IReadOnlyDictionary<string, object?>? values, IReadOnlySet<string> excludedKeys)
    {
        if (values is null)
        {
            return;
        }

        foreach (var item in values)
        {
            if (!excludedKeys.Contains(item.Key))
            {
                rumEvent.WithField(item.Key, item.Value);
            }
        }
    }

    private static string? GetString(IReadOnlyDictionary<string, object?>? values, string key)
    {
        if (values is null || !values.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            null => null,
            string text when string.IsNullOrWhiteSpace(text) => null,
            string text => text,
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static long? GetInt64(IReadOnlyDictionary<string, object?>? values, string key)
    {
        if (values is null || !values.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            long number => number,
            int number => number,
            double number => (long)number,
            float number => (long)number,
            TimeSpan duration => Clock.DurationNanoseconds(duration),
            _ when long.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out var parsed) => parsed,
            _ => null
        };
    }

    private static bool? GetBool(IReadOnlyDictionary<string, object?>? values, string key)
    {
        if (values is null || !values.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }

    private static IReadOnlyDictionary<string, object?> MergeResourceTimingProperties(RumResourceTiming timing, IReadOnlyDictionary<string, object?>? properties)
    {
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var item in timing.ToProperties())
        {
            merged[item.Key] = item.Value;
        }

        if (properties is not null)
        {
            foreach (var item in properties)
            {
                merged[item.Key] = item.Value;
            }
        }

        if (timing.TotalDuration is not null)
        {
            merged[ResourceDurationOverride] = Clock.DurationNanoseconds(timing.TotalDuration.Value);
        }

        return merged;
    }

    private static void AddErrorResourceTags(RumEvent rumEvent, IReadOnlyDictionary<string, object?>? values)
    {
        if (values is null)
        {
            return;
        }

        foreach (var item in values)
        {
            switch (item.Key)
            {
                case RumConstants.ResourceId:
                case RumConstants.ResourceUrl:
                case RumConstants.ResourceUrlHost:
                case RumConstants.ResourceUrlPath:
                case RumConstants.ResourceUrlPathGroup:
                case RumConstants.ResourceMethod:
                case RumConstants.ResourceStatus:
                case RumConstants.ResourceStatusGroup:
                case RumConstants.TraceId:
                case RumConstants.SpanId:
                case RumConstants.ResourceHttpProtocol:
                    rumEvent.WithTag(item.Key, item.Value);
                    break;
                default:
                    rumEvent.WithField(item.Key, item.Value);
                    break;
            }
        }
    }

    private static Uri? TryCreateUri(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static string GroupUrlPath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Any(char.IsDigit) ? "?" : part);
        return "/" + string.Join("/", parts);
    }

    private sealed record ActiveView(string Id, string Name, string? Referrer, long StartTime, Stopwatch Duration)
    {
        public int ActionCount;
        public int ResourceCount;
        public int ErrorCount;
        public int LongTaskCount;
        public Dictionary<string, object?> Properties { get; } = new(StringComparer.Ordinal);
    }

    private sealed record ActiveViewSnapshot(string Id, string Name, string? Referrer);

    private sealed record ActiveAction(string Id, string Name, string Type, ActiveViewSnapshot? View, long StartTime, Stopwatch Duration, bool NeedWait, IReadOnlyDictionary<string, object?>? InitialProperties)
    {
        public int ResourceCount;
        public int ErrorCount;
        public int LongTaskCount;
        public System.Threading.Timer? TimeoutTimer;
        public Dictionary<string, object?> Properties { get; } = InitialProperties is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(InitialProperties, StringComparer.Ordinal);
    }

    private sealed record ActionTimeoutState(GuanceClient Client, string ActionId);

    private sealed record ActiveActionSnapshot(string Id, string Name);

    private sealed record ActiveResource(
        string Id,
        string Url,
        string Method,
        ActiveViewSnapshot? View,
        ActiveActionSnapshot? Action,
        long StartTime,
        Stopwatch Duration,
        IReadOnlyDictionary<string, object?>? InitialProperties)
    {
        public Dictionary<string, object?> Properties { get; } = InitialProperties is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(InitialProperties, StringComparer.Ordinal);
    }

}
