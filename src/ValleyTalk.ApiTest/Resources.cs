using System.Resources;

namespace ValleyTalk.ApiTest;

internal static class Resources
{
    private static readonly ResourceManager Manager =
        new("ValleyTalk.ApiTest.Resources", typeof(Resources).Assembly);

    public static string BannerSeparator
    {
        get => Manager.GetString(nameof(BannerSeparator)) ?? string.Empty;
    }

    public static string BannerTitle
    {
        get => Manager.GetString(nameof(BannerTitle)) ?? string.Empty;
    }

    public static string BannerEnd
    {
        get => Manager.GetString(nameof(BannerEnd)) ?? string.Empty;
    }

    public static string Test1Label
    {
        get => Manager.GetString(nameof(Test1Label)) ?? string.Empty;
    }

    public static string Pass
    {
        get => Manager.GetString(nameof(Pass)) ?? string.Empty;
    }

    public static string FailNoResponse
    {
        get => Manager.GetString(nameof(FailNoResponse)) ?? string.Empty;
    }

    public static string Test2Label
    {
        get => Manager.GetString(nameof(Test2Label)) ?? string.Empty;
    }

    public static string Test3Label
    {
        get => Manager.GetString(nameof(Test3Label)) ?? string.Empty;
    }

    public static string StreamPass
    {
        get => Manager.GetString(nameof(StreamPass)) ?? string.Empty;
    }

    public static string FooterSeparator
    {
        get => Manager.GetString(nameof(FooterSeparator)) ?? string.Empty;
    }

    public static string TestComplete
    {
        get => Manager.GetString(nameof(TestComplete)) ?? string.Empty;
    }

    public static string FailPrefix
    {
        get => Manager.GetString(nameof(FailPrefix)) ?? string.Empty;
    }
}