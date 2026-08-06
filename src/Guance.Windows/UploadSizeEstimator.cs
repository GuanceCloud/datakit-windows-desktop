namespace Guance.Windows;

internal static class UploadSizeEstimator
{
    // Conservative zlib/deflate upper bound. Charging the bound keeps the
    // request-body rate below the configured budget even for incompressible data.
    public static long EstimateDeflateUpperBound(long uncompressedBytes)
    {
        if (uncompressedBytes <= 0) return 0;
        return checked(
            uncompressedBytes +
            (uncompressedBytes >> 12) +
            (uncompressedBytes >> 14) +
            (uncompressedBytes >> 25) +
            64);
    }
}
