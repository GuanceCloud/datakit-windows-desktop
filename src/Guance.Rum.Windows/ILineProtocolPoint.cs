namespace Guance.Rum.Windows;

internal interface ILineProtocolPoint
{
    string Measurement { get; }
    long TimestampNanoseconds { get; }
    Dictionary<string, object?> Tags { get; }
    Dictionary<string, object?> Fields { get; }
}
