using Android.App;
using Android.Content;

namespace IntroBoys.App;

/// <summary>1.3.8: hands the log file to the app the member shares it with (Messenger, Discord, email ...), read-only.</summary>
[ContentProvider(new[] { Authority }, Name = "com.blacklistintroboys.guild.LogProvider", Exported = false, GrantUriPermissions = true)]
[MetaData("android.support.FILE_PROVIDER_PATHS", Resource = "@xml/log_paths")]
public class LogProvider : AndroidX.Core.Content.FileProvider
{
    public const string Authority = "com.blacklistintroboys.guild.logs";
}
