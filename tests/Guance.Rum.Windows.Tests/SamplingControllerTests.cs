using Xunit;

namespace Guance.Rum.Windows.Tests;

public sealed class SamplingControllerTests
{
    [Fact]
    public void ShouldCollect_DropsNonErrorWhenOnlyErrorSamplingHits()
    {
        var controller = new SamplingController(new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            SampleRate = 0,
            SessionErrorSampleRate = 1
        });

        Assert.False(controller.ShouldCollect(RumConstants.MeasurementView));
        Assert.True(controller.ShouldCollect(RumConstants.MeasurementError));
    }
}
