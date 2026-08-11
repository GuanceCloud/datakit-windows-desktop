namespace Guance.Windows;

internal static class RumConstants
{
    public const string WindowsSdkName = "df_windows_rum_sdk";
    public const string WindowsSource = "windows";
    public const string WindowsLogSource = "df_rum_windows_log";

    public const string RumWritePath = "v1/write/rum";
    public const string LogWritePath = "v1/write/logging";
    public const string RumReplayWritePath = "v1/write/rum/replay";

    public const string MeasurementView = "view";
    public const string MeasurementAction = "action";
    public const string MeasurementResource = "resource";
    public const string MeasurementError = "error";
    public const string MeasurementLongTask = "long_task";

    public const string AppId = "app_id";
    public const string Service = "service";
    public const string Env = "env";
    public const string Version = "version";
    public const string SdkName = "sdk_name";
    public const string SdkVersion = "sdk_version";
    public const string ApplicationUuid = "application_uuid";
    public const string Os = "os";
    public const string OsVersion = "os_version";
    public const string OsVersionMajor = "os_version_major";
    public const string Device = "device";
    public const string Model = "model";
    public const string Arch = "arch";
    public const string ScreenSize = "screen_size";
    public const string Locale = "locale";
    public const string NetworkType = "network_type";
    public const string CustomKeys = "custom_keys";
    public const string LogMessage = "message";
    public const string LogStatus = "status";

    public const string UserId = "userid";
    public const string UserName = "user_name";
    public const string UserEmail = "user_email";
    public const string IsSignIn = "is_signin";

    public const string SessionId = "session_id";
    public const string SessionType = "session_type";
    public const string SessionHasReplay = "session_has_replay";
    public const string SessionSampleRate = "session_sample_rate";
    public const string SessionOnErrorSampleRate = "session_on_error_sample_rate";
    public const string SampledForErrorSession = "sampled_for_error_session";

    public const string ViewId = "view_id";
    public const string ViewName = "view_name";
    public const string ViewReferrer = "view_referrer";
    public const string ViewLoad = "loading_time";
    public const string ViewTimeSpent = "time_spent";
    public const string ViewIsActive = "is_active";
    public const string ViewActionCount = "view_action_count";
    public const string ViewResourceCount = "view_resource_count";
    public const string ViewErrorCount = "view_error_count";
    public const string ViewLongTaskCount = "view_long_task_count";
    public const string ViewUpdateTime = "view_update_time";

    public const string ActionId = "action_id";
    public const string ActionName = "action_name";
    public const string ActionType = "action_type";
    public const string ActionLongTaskCount = "action_long_task_count";
    public const string ActionResourceCount = "action_resource_count";
    public const string ActionErrorCount = "action_error_count";
    public const string ActionDuration = "duration";
    public const string ActionTypeClick = "click";
    public const string ActionTypeKey = "key";
    public const string ActionTypeLaunchCold = "launch_cold";
    public const string ActionTypeLaunchHot = "launch_hot";
    public const string ActionNameLaunchCold = "app cold start";
    public const string ActionNameLaunchHot = "app hot start";
    public const string AppPreApplicationInitTime = "app_pre_application_init_time";
    public const string AppApplicationInitTime = "app_application_init_time";
    public const string AppFirstFrameInitTime = "app_first_frame_init_time";

    public const string ResourceId = "resource_id";
    public const string ResourceUrl = "resource_url";
    public const string ResourceUrlHost = "resource_url_host";
    public const string ResourceUrlPath = "resource_url_path";
    public const string ResourceUrlPathGroup = "resource_url_path_group";
    public const string ResourceMethod = "resource_method";
    public const string ResourceType = "resource_type";
    public const string ResourceStatus = "resource_status";
    public const string ResourceStatusGroup = "resource_status_group";
    public const string ResourceDuration = "duration";
    public const string ResourceSize = "resource_size";
    public const string ResourceRequestSize = "resource_request_size";
    public const string ResourceDns = "resource_dns";
    public const string ResourceTcp = "resource_tcp";
    public const string ResourceSsl = "resource_ssl";
    public const string ResourceTtfb = "resource_ttfb";
    public const string RequestHeader = "request_header";
    public const string ResponseHeader = "response_header";
    public const string ResourceHttpProtocol = "resource_http_protocol";
    public const string ResourceTimingSource = "resource_timing_source";
    public const string ResourceTimingPrecision = "resource_timing_precision";
    public const string ResourceTimingDuration = "resource_timing_duration";
    public const string ResourceTimingPhase = "resource_timing_phase";
    public const string ResourceTtfbEstimated = "resource_ttfb_estimated";
    public const string ResourceConnectionReuse = "resource_connection_reuse";
    public const string ResourceHostIp = "resource_host_ip";
    public const string TraceId = "trace_id";
    public const string SpanId = "span_id";
    public const string HttpRequestVersion = "http_request_version";
    public const string HttpResponseVersion = "http_response_version";
    public const string HttpVersionPolicy = "http_version_policy";
    public const string NetworkInstrumentation = "network_instrumentation";
    public const string NetworkLibrary = "network_library";

    public const string ErrorMessage = "error_message";
    public const string ErrorStack = "error_stack";
    public const string ErrorSource = "error_source";
    public const string ErrorType = "error_type";
    public const string ErrorSituation = "error_situation";

    public const string LongTaskDuration = "duration";
    public const string LongTaskStack = "long_task_stack";
    public const string LongTaskSource = "long_task_source";
    public const string LongTaskDelay = "long_task_delay";
    public const string LongTaskThreshold = "long_task_threshold";
    public const string LongTaskCooldown = "long_task_cooldown";
    public const string LongTaskSuppressedCount = "long_task_suppressed_count";
}
