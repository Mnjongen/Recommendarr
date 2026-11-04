namespace Recommendarr.Web.Auth;

public sealed class WebOptions
{
    public const string Section = "Recommendarr:Web";

    public string? PublicUrl { get; set; }

    public string[] TrustedProxies { get; set; } = [];
}
