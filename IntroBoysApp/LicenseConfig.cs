namespace IntroBoys.App;

/// <summary>Where the guild site is and which key its license tickets are signed with.</summary>
static class LicenseConfig
{
    /// <summary>The guild site. Sign-in pages are only ever opened on this address.</summary>
    public const string SiteUrl = "https://blacklistintroboys.com";

    /// <summary>Where the site's backend functions answer (the same address the website itself calls).</summary>
    public const string ApiBase = "https://blacklistintroboys.com/api/apps/6abe8bba244ec63c6d6d2855/functions";

    /// <summary>The site's license function.</summary>
    public const string Function = "botAuth";

    /// <summary>
    /// ECDSA P-256 PUBLIC key (base64 SubjectPublicKeyInfo) that checks the site's tickets: the same one the PC Assistant has.
    /// The build fills it in from the repository secret LICENSE_PUBLIC_KEY (.github/workflows/android.yml).
    /// </summary>
    public const string PublicKeyB64 = "__LICENSE_PUBLIC_KEY__";

    /// <summary>Keep in step with ApplicationDisplayVersion in IntroBoysApp.csproj.</summary>
    public const string AppVersion = "1.4.5";

    /// <summary>Tells the site this sign-in is a phone (the Guild Leader switches the phone Assistant on per member).</summary>
    public const string Platform = "android";
}
