namespace Platform.Web.PlatformHost;

/// <summary>Configuration section <c>Platform</c> of the web host.</summary>
internal sealed class PlatformHostOptions
{
    public const string Section = "Platform";

    /// <summary>
    /// Host name of the platform console (<c>platform.localhost</c> in Development, <c>platform.&lt;domain&gt;</c> in
    /// production). Empty means no host serves the console; platform paths are then a 404 everywhere.
    /// </summary>
    public string? Host { get; set; }
}
