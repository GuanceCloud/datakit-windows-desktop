using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Guance.Rum.Windows.Queue;
using Guance.Rum.Windows.SessionReplay;
using Guance.Rum.Windows.Transport;

namespace Guance.Rum.Windows;

public sealed class RumClient : IAsyncDisposable
{
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
        RumConstants.ResourceTtfbEstimated
    };

    private readonly RumConfig config;
    private readonly IRumQueue queue;
    private readonly IDatawayTransport transport;
    private readonly ISessionReplayQueue sessionReplayQueue;
    private readonly ISessionReplayTransport sessionReplayTransport;
    private readonly SessionReplayPrivacyOverrides sessionReplayPrivacy = new();
    private readonly SessionReplayManager sessionReplay;
    private readonly SamplingController sampling;
    private readonly SessionManager session;
    private readonly ConcurrentDictionary<string, ActiveResource> resources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveAction> actions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> globalContext = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> rumGlobalContext = new(StringComparer.Ordinal);
    private readonly object stateGate = new();
    private readonly RetryBackoff rumRetryBackoff;
    private readonly RetryBackoff sessionReplayRetryBackoff;
    private readonly System.Threading.Timer flushTimer;
    private readonly System.Threading.Timer sessionReplayFlushTimer;
    private readonly CancellationTokenSource shutdown = new();
    private AutomaticInstrumentation? automaticInstrumentation;
    private ActiveView? activeView;
    private UserInfo? userInfo;
    private int flushInProgress;
    private int sessionReplayFlushInProgress;
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

    public RumClient(RumConfig config)
        : this(config, new SqliteRumQueue(config), new DatawayTransport(config), new SqliteSessionReplayQueue(config), new SessionReplayTransport(config))
    {
    }

    internal RumClient(RumConfig config, IRumQueue queue, IDatawayTransport transport)
        : this(config, queue, transport, new SqliteSessionReplayQueue(config), new SessionReplayTransport(config))
    {
    }

    internal RumClient(RumConfig config, IRumQueue queue, IDatawayTransport transport, ISessionReplayQueue sessionReplayQueue, ISessionReplayTransport sessionReplayTransport)
    {
        config.Validate();
        this.config = config;
        this.queue = queue;
        this.transport = transport;
        this.sessionReplayQueue = sessionReplayQueue;
        this.sessionReplayTransport = sessionReplayTransport;
        sampling = new SamplingController(config);
        session = new SessionManager(sampling);
        sessionReplay = new SessionReplayManager(config, sessionReplayQueue, sessionReplayPrivacy);
        rumRetryBackoff = new RetryBackoff(config.FlushInterval);
        sessionReplayRetryBackoff = new RetryBackoff(config.SessionReplay.FlushInterval);
        flushTimer = new System.Threading.Timer(_ => _ = FlushRumQueueAsync(shutdown.Token, respectBackoff: true), null, config.FlushInterval, config.FlushInterval);
        sessionReplayFlushTimer = new System.Threading.Timer(_ => _ = FlushSessionReplayAsync(shutdown.Token, respectBackoff: true), null, config.SessionReplay.FlushInterval, config.SessionReplay.FlushInterval);
    }

    public RumConfig Config => config;

    public event EventHandler<RumDiagnosticEvent>? DiagnosticEvent;

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

    public void EnableAutomaticInstrumentation(AutomaticInstrumentationOptions? options = null)
    {
        automaticInstrumentation ??= new AutomaticInstrumentation(this, options ?? new AutomaticInstrumentationOptions());
        automaticInstrumentation.Start();
    }

    public void AttachWinUIWindow(object window, string? viewName = null)
    {
        WinUIReflectionInstrumentation.Attach(this, window, viewName);
    }

    public void StartSessionReplayRecording() => sessionReplay.Start();

    public void StopSessionReplayRecording() => sessionReplay.Stop();

    public void SetSessionReplayTextAndInputPrivacy(object element, SessionReplayTextAndInputPrivacy? privacy)
    {
        sessionReplayPrivacy.SetTextAndInputPrivacy(element, privacy);
    }

    public void SetSessionReplayTouchPrivacy(object element, SessionReplayTouchPrivacy? privacy)
    {
        sessionReplayPrivacy.SetTouchPrivacy(element, privacy);
    }

    public void SetSessionReplayHidden(object element, bool hidden = true)
    {
        sessionReplayPrivacy.SetHidden(element, hidden);
    }

    public void SetUser(string id, string? name = null, string? email = null, IReadOnlyDictionary<string, object?>? extra = null)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("User id is required.", nameof(id));
        }

        userInfo = new UserInfo(id, name, email, extra ?? new Dictionary<string, object?>());
    }

    public void ClearUser()
    {
        userInfo = null;
    }

    public void AddGlobalContext(string key, object? value)
    {
        lock (stateGate)
        {
            globalContext[key] = value;
        }
    }

    public void AddRumGlobalContext(string key, object? value)
    {
        lock (stateGate)
        {
            rumGlobalContext[key] = value;
        }
    }

    public void StartView(string name, IReadOnlyDictionary<string, object?>? properties = null)
    {
        session.Touch();
        ActiveView? viewToClose;
        lock (stateGate)
        {
            viewToClose = activeView;
            activeView = new ActiveView(Guid.NewGuid().ToString("N"), name, viewToClose?.Name, Clock.UnixTimeNanoseconds(), Stopwatch.StartNew());
        }

        if (viewToClose is not null)
        {
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

    public void StopView(IReadOnlyDictionary<string, object?>? properties = null)
    {
        ActiveView? viewToClose;
        lock (stateGate)
        {
            viewToClose = activeView;
            activeView = null;
        }

        if (viewToClose is not null)
        {
            TrackView(viewToClose, isActive: false, properties);
        }
    }

    public RumActionScope StartAction(string name, string type, IReadOnlyDictionary<string, object?>? properties = null)
    {
        session.Touch();
        var action = new ActiveAction(Guid.NewGuid().ToString("N"), name, type, SnapshotView(), Clock.UnixTimeNanoseconds(), Stopwatch.StartNew(), properties);
        actions[action.Id] = action;
        return new RumActionScope(this, action.Id);
    }

    public void AddAction(string name, string type, TimeSpan duration, IReadOnlyDictionary<string, object?>? properties = null)
    {
        session.Touch();
        var action = new ActiveAction(Guid.NewGuid().ToString("N"), name, type, SnapshotView(), Clock.UnixTimeNanoseconds(), Stopwatch.StartNew(), properties);
        TrackAction(action, (long)(duration.TotalMilliseconds * 1_000_000));
    }

    internal void StopAction(string actionId)
    {
        if (actions.TryRemove(actionId, out var action))
        {
            TrackAction(action, Clock.DurationNanoseconds(action.Duration));
        }
    }

    public string StartResource(string url, string method, IReadOnlyDictionary<string, object?>? properties = null)
    {
        session.Touch();
        var resourceId = Guid.NewGuid().ToString("N");
        var redactedUrl = HttpHeaderRedactor.RedactUrl(url, config.Privacy);
        resources[resourceId] = new ActiveResource(resourceId, redactedUrl, method, SnapshotView(), SnapshotAction(), Clock.UnixTimeNanoseconds(), Stopwatch.StartNew(), properties);
        return resourceId;
    }

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

        var duration = Clock.DurationNanoseconds(resource.Duration);
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
                errorMessage ?? $"[{statusCode}][{resource.Url}]",
                "network",
                "network",
                errorProperties);
        }
    }

    public void AddError(Exception exception, IReadOnlyDictionary<string, object?>? properties = null)
    {
        AddError(exception.ToString(), exception.Message, exception.GetType().Name, "logger", properties);
    }

    public void AddError(string stack, string message, string errorType, string source = "logger", IReadOnlyDictionary<string, object?>? properties = null)
    {
        session.Touch();
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
        Enqueue(rumEvent, waitForQueue: IsCrash(properties));
        IncrementError(view, action);
        sessionReplay.NotifyError(CreateSessionReplayContext());
    }

    public void AddLongTask(TimeSpan duration, string? stack = null, IReadOnlyDictionary<string, object?>? properties = null)
    {
        session.Touch();
        var view = SnapshotView();
        var action = SnapshotAction();
        var rumEvent = CreateEvent(RumConstants.MeasurementLongTask, Clock.UnixTimeNanoseconds() - (long)(duration.TotalMilliseconds * 1_000_000))
            .WithTag(RumConstants.ViewId, view?.Id)
            .WithTag(RumConstants.ViewName, view?.Name)
            .WithTag(RumConstants.ViewReferrer, view?.Referrer)
            .WithTag(RumConstants.ActionId, action?.Id)
            .WithTag(RumConstants.ActionName, action?.Name)
            .WithField(RumConstants.LongTaskDuration, (long)(duration.TotalMilliseconds * 1_000_000))
            .WithField(RumConstants.LongTaskStack, stack ?? string.Empty);

        AddFields(rumEvent, properties);
        Enqueue(rumEvent);
        IncrementLongTask(view, action);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await FlushRumQueueAsync(cancellationToken, respectBackoff: false).ConfigureAwait(false);
        await FlushSessionReplayAsync(cancellationToken, respectBackoff: false).ConfigureAwait(false);
    }

    private async Task FlushRumQueueAsync(CancellationToken cancellationToken, bool respectBackoff)
    {
        if (Interlocked.Exchange(ref flushInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            if (respectBackoff && !rumRetryBackoff.CanAttemptNow())
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                await queue.TrimAsync(cancellationToken).ConfigureAwait(false);
                var batch = await queue.PeekAsync(config.BatchSize, cancellationToken).ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    rumRetryBackoff.Reset();
                    break;
                }

                var result = await transport.SendAsync(batch, cancellationToken).ConfigureAwait(false);
                RecordRumUploadResult(result);
                if (config.Debug && result.ErrorMessage is not null)
                {
                    Debug.WriteLine($"[Guance.RUM] upload status={result.StatusCode} retry={result.RetryLater}: {result.ErrorMessage}");
                }

                if (result.DeleteFromQueue)
                {
                    rumRetryBackoff.Reset();
                    await queue.DeleteAsync(batch.Select(item => item.Id).ToArray(), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (result.RetryLater)
                {
                    rumRetryBackoff.ScheduleRetry();
                }

                return;
            }
        }
        finally
        {
            Interlocked.Exchange(ref flushInProgress, 0);
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        StopView();
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        await FlushSessionReplayAsync(cancellationToken).ConfigureAwait(false);
        shutdown.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        flushTimer.Dispose();
        sessionReplayFlushTimer.Dispose();
        automaticInstrumentation?.Dispose();
        await sessionReplay.DisposeAsync().ConfigureAwait(false);
        sessionReplayTransport.Dispose();
        transport.Dispose();
        await sessionReplayQueue.DisposeAsync().ConfigureAwait(false);
        await queue.DisposeAsync().ConfigureAwait(false);
        shutdown.Dispose();
    }

    internal void CaptureSessionReplayFullSnapshot(object root)
    {
        var node = SessionReplayTreeMapper.Map(root, sessionReplayPrivacy, config.SessionReplay);
        if (node is not null)
        {
            sessionReplay.CaptureFullSnapshot(CreateSessionReplayContext(), node);
        }
    }

    internal void CaptureSessionReplayClick(string target, double x, double y)
    {
        sessionReplay.CaptureIncrementalEvent(CreateSessionReplayContext(), "click", target, x, y);
    }

    internal void CaptureSessionReplayClick(object element, string target, double x, double y)
    {
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
        sessionReplay.CaptureIncrementalEvent(CreateSessionReplayContext(), "input", target, 0, 0, value);
    }

    internal void CaptureSessionReplayInteraction(string eventType, string target)
    {
        sessionReplay.CaptureIncrementalEvent(CreateSessionReplayContext(), eventType, target, 0, 0);
    }

    internal void CaptureSessionReplayResize(string target, double width, double height)
    {
        sessionReplay.CaptureResizeEvent(CreateSessionReplayContext(), target, width, height);
    }

    private RumEvent CreateEvent(string measurement, long timestampNanoseconds)
    {
        var rumEvent = new RumEvent(measurement, timestampNanoseconds)
            .WithTag(RumConstants.AppId, config.RumAppId)
            .WithTag(RumConstants.Service, config.ServiceName)
            .WithTag(RumConstants.Env, config.Env)
            .WithTag(RumConstants.SdkName, "guance-rum-windows")
            .WithTag(RumConstants.SdkVersion, typeof(RumClient).Assembly.GetName().Version?.ToString() ?? "0.1.0")
            .WithTag(RumConstants.Os, Environment.OSVersion.Platform.ToString())
            .WithTag(RumConstants.OsVersion, Environment.OSVersion.VersionString)
            .WithTag(RumConstants.SessionId, session.SessionId)
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

        var user = userInfo;
        if (user is not null)
        {
            rumEvent
                .WithTag(RumConstants.IsSignIn, true)
                .WithTag(RumConstants.UserId, user.Id)
                .WithTag(RumConstants.UserName, user.Name)
                .WithTag(RumConstants.UserEmail, user.Email);
            AddTags(rumEvent, user.Extra);
        }

        return rumEvent;
    }

    private void Enqueue(RumEvent rumEvent, bool waitForQueue = false)
    {
        if (!sampling.ShouldCollect(rumEvent.Measurement))
        {
            Interlocked.Increment(ref rumEventsDroppedBySampling);
            EmitDiagnostic(RumDiagnosticLevel.Debug, "sampling", $"Dropped {rumEvent.Measurement} by session sampling.");
            return;
        }

        var line = LineProtocolFormatter.Format(rumEvent);
        Interlocked.Increment(ref rumEventsEnqueued);
        Task enqueueTask;
        try
        {
            enqueueTask = queue.EnqueueAsync(line, shutdown.Token);
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
                FlushRumQueueAsync(crashFlush.Token, respectBackoff: false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                RecordQueueError(ex);
            }

            return;
        }

        _ = enqueueTask
            .ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    RecordQueueError(task.Exception?.GetBaseException() ?? new InvalidOperationException("Queue enqueue failed."));
                    return;
                }

                if (task.IsCanceled)
                {
                    RecordQueueError(new OperationCanceledException("Queue enqueue was canceled."));
                    return;
                }

                _ = FlushRumQueueAsync(shutdown.Token, respectBackoff: true);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task FlushSessionReplayAsync(CancellationToken cancellationToken, bool respectBackoff = false)
    {
        if (Interlocked.Exchange(ref sessionReplayFlushInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            if (respectBackoff && !sessionReplayRetryBackoff.CanAttemptNow())
            {
                return;
            }

            await sessionReplay.FlushPendingRecordsAsync(cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                await sessionReplayQueue.TrimAsync(cancellationToken).ConfigureAwait(false);
                var batch = await sessionReplayQueue.PeekAsync(1, cancellationToken).ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    sessionReplayRetryBackoff.Reset();
                    return;
                }

                var segment = batch[0];
                var result = await sessionReplayTransport.SendAsync(segment, cancellationToken).ConfigureAwait(false);
                RecordReplayUploadResult(result);
                if (config.Debug && result.ErrorMessage is not null)
                {
                    Debug.WriteLine($"[Guance.RUM.SessionReplay] upload status={result.StatusCode} retry={result.RetryLater}: {result.ErrorMessage}");
                }

                if (result.DeleteFromQueue)
                {
                    sessionReplayRetryBackoff.Reset();
                    await sessionReplayQueue.DeleteAsync(new[] { segment.Id }, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (result.RetryLater)
                {
                    sessionReplayRetryBackoff.ScheduleRetry();
                }

                return;
            }
        }
        finally
        {
            Interlocked.Exchange(ref sessionReplayFlushInProgress, 0);
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

    private void EmitDiagnostic(RumDiagnosticLevel level, string source, string message, int? statusCode = null, Exception? exception = null)
    {
        var item = new RumDiagnosticEvent(DateTimeOffset.UtcNow, level, source, message, statusCode, exception);
        try
        {
            config.DiagnosticListener?.Invoke(item);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Guance.RUM] diagnostic listener failed: {ex}");
        }

        try
        {
            DiagnosticEvent?.Invoke(this, item);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Guance.RUM] diagnostic event handler failed: {ex}");
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

    private ActiveViewSnapshot? SnapshotView()
    {
        lock (stateGate)
        {
            return activeView is null ? null : new ActiveViewSnapshot(activeView.Id, activeView.Name, activeView.Referrer);
        }
    }

    private ActiveActionSnapshot? SnapshotAction()
    {
        var action = actions.Values.OrderByDescending(item => item.StartTime).FirstOrDefault();
        return action is null ? null : new ActiveActionSnapshot(action.Id, action.Name);
    }

    private SessionReplayContext CreateSessionReplayContext()
    {
        var view = SnapshotView();
        return new SessionReplayContext(
            config.RumAppId,
            session.SessionId,
            view?.Id,
            config.ServiceName,
            config.Env,
            config.Version,
            "guance-rum-windows",
            typeof(RumClient).Assembly.GetName().Version?.ToString() ?? "0.1.0");
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
    }

    private static void AddTags(RumEvent rumEvent, IReadOnlyDictionary<string, object?> values)
    {
        foreach (var item in values)
        {
            rumEvent.WithTag(item.Key, item.Value);
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
            TimeSpan duration => (long)(duration.TotalMilliseconds * 1_000_000),
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

        return merged;
    }

    private static bool IsCrash(IReadOnlyDictionary<string, object?>? values)
    {
        if (values is null || !values.TryGetValue(RumConstants.IsCrash, out var value) || value is null)
        {
            return false;
        }

        return value switch
        {
            bool crash => crash,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => false
        };
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

    private sealed record ActiveAction(string Id, string Name, string Type, ActiveViewSnapshot? View, long StartTime, Stopwatch Duration, IReadOnlyDictionary<string, object?>? InitialProperties)
    {
        public int ResourceCount;
        public int ErrorCount;
        public int LongTaskCount;
        public Dictionary<string, object?> Properties { get; } = InitialProperties is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(InitialProperties, StringComparer.Ordinal);
    }

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

    private sealed class RetryBackoff
    {
        private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);
        private readonly TimeSpan baseDelay;
        private readonly object gate = new();
        private DateTimeOffset nextAttempt = DateTimeOffset.MinValue;
        private int attempt;

        public RetryBackoff(TimeSpan baseDelay)
        {
            this.baseDelay = baseDelay <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : baseDelay;
        }

        public bool CanAttemptNow()
        {
            lock (gate)
            {
                return DateTimeOffset.UtcNow >= nextAttempt;
            }
        }

        public void Reset()
        {
            lock (gate)
            {
                attempt = 0;
                nextAttempt = DateTimeOffset.MinValue;
            }
        }

        public void ScheduleRetry()
        {
            lock (gate)
            {
                attempt = Math.Min(attempt + 1, 10);
                var delayMilliseconds = Math.Min(MaxDelay.TotalMilliseconds, baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
                nextAttempt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(Math.Max(1, delayMilliseconds * jitter));
            }
        }
    }
}
