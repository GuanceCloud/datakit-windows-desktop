#if WINDOWS
using Xunit;

namespace Guance.Rum.Windows.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfUiTestCollection
{
    public const string Name = "WPF UI";
}
#endif
