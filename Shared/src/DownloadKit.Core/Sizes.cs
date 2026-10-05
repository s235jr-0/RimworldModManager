namespace DownloadKit;

public static class Sizes
{
    // 1.55 GB / 949.8 MB / 12 KB
    public static string Format(long bytes)
    {
        if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.00") + " GB";
        if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0") + " MB";
        return (bytes / 1024.0).ToString("0") + " KB";
    }
}
