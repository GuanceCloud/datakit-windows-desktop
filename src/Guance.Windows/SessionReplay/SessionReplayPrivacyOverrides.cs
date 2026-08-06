using System.Runtime.CompilerServices;

namespace Guance.Windows.SessionReplay;

internal sealed class SessionReplayPrivacyOverrides
{
    private readonly ConditionalWeakTable<object, OverrideState> states = new();

    public void SetTextAndInputPrivacy(object element, SessionReplayTextAndInputPrivacy? privacy)
    {
        var state = states.GetOrCreateValue(element);
        state.TextAndInputPrivacy = privacy;
    }

    public void SetTouchPrivacy(object element, SessionReplayTouchPrivacy? privacy)
    {
        var state = states.GetOrCreateValue(element);
        state.TouchPrivacy = privacy;
    }

    public void SetImagePrivacy(object element, SessionReplayImagePrivacy? privacy)
    {
        var state = states.GetOrCreateValue(element);
        state.ImagePrivacy = privacy;
    }

    public void SetHidden(object element, bool hidden)
    {
        var state = states.GetOrCreateValue(element);
        state.Hidden = hidden;
    }

    public ResolvedPrivacy Resolve(object? element, ResolvedPrivacy parent, RumSessionReplayConfig config)
    {
        var result = parent;
        if (element is not null && states.TryGetValue(element, out var state))
        {
            result = result with
            {
                TextAndInputPrivacy = state.TextAndInputPrivacy ?? result.TextAndInputPrivacy,
                TouchPrivacy = state.TouchPrivacy ?? result.TouchPrivacy,
                ImagePrivacy = state.ImagePrivacy ?? result.ImagePrivacy,
                Hidden = state.Hidden || result.Hidden
            };
        }

        return result;
    }

    public static ResolvedPrivacy FromConfig(RumSessionReplayConfig config)
    {
        return new ResolvedPrivacy(config.TextAndInputPrivacy, config.TouchPrivacy, config.ImagePrivacy, Hidden: false);
    }

    internal sealed class OverrideState
    {
        public SessionReplayTextAndInputPrivacy? TextAndInputPrivacy { get; set; }
        public SessionReplayTouchPrivacy? TouchPrivacy { get; set; }
        public SessionReplayImagePrivacy? ImagePrivacy { get; set; }
        public bool Hidden { get; set; }
    }
}

internal sealed record ResolvedPrivacy(
    SessionReplayTextAndInputPrivacy TextAndInputPrivacy,
    SessionReplayTouchPrivacy TouchPrivacy,
    SessionReplayImagePrivacy ImagePrivacy,
    bool Hidden);
