using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Android.Content;

namespace IntroBoys.App;

/// <summary>Thrown by <see cref="License.Require"/> when the program is not allowed to act right now.</summary>
public sealed class LicenseException : Exception
{
    public LicenseException(string message) : base(message) { }
}

public enum LicenseState { SignedOut, Checking, Licensed, NoLicense, Expired, Revoked, OtherDevice, NotMember, PhoneOff, Offline, Error }

/// <summary>
/// The license, as the guild site sees it.
///
/// Ported from the PC Assistant (License.cs) for the phone: the same sign-in, tickets and checks; only where the device ID and the
/// saved sign-in come from differ, and every question tells the site it comes from a phone.
///
/// The program never decides by itself whether it may run. It signs in with the member's Discord account (the site shows the
/// member a code and the member approves it in the browser), then asks the site every couple of minutes. The site answers with a
/// TICKET signed with a private key only the site has. This program holds only the matching public key: it can check a ticket
/// but never make one. A ticket names this computer, repeats a random number this program sent with its question (so an old answer
/// can't be played back), and lasts ten minutes at most. Everything that acts on the game (key presses, mouse, screen reading,
/// waiting) calls <see cref="Require"/> first, which checks the ticket again.
///
/// Time is measured with a stopwatch that starts when a ticket arrives, not with the clock, so changing the clock
/// gives no extra time.
/// </summary>
public static class License
{
    // ------------------------------------------------------------------ what the screen shows
    public static LicenseState State { get; private set; } = LicenseState.SignedOut;
    public static string Message { get; private set; } = "";
    public static string UserName { get; private set; } = "";
    /// <summary>The member's Discord picture (https link from the site), "" when there is none.</summary>
    public static string AvatarUrl { get; private set; } = "";
    public static int DaysLeft { get; private set; }
    /// <summary>Raised (from any thread) whenever State, Message or the days change.</summary>
    public static event Action? Changed;

    // ------------------------------------------------------------------ internals
    static readonly object gate = new();
    static readonly HttpClient http = MakeClient();
    static string? token;                     // the app's sign-in (kept in the app's private storage too)
    static string? ticketPayload, ticketSig, ticketNonce;
    static long ticketRecv, ticketIat, ticketTtl, ticketExp;
    static long lastVerify;
    static long nextCheckAt;                  // Stopwatch timestamp of the next question to the site
    static int checkEverySecs = 120;
    static CancellationTokenSource? loop;
    static int checking;                      // 1 while a question to the site is under way
    static ECDsa? verifier;
    static bool keyTried;
    static string? deviceId;

    static HttpClient MakeClient()
    {
        var c = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("IntroBoysAssistantAndroid/" + LicenseConfig.AppVersion);
        return c;
    }

    // ------------------------------------------------------------------ this computer
    /// <summary>
    /// 64 hex characters naming this phone (for this app). Android gives each app its own ID per phone and user, which stays the same
    /// when the app is updated or reinstalled with the same signing key. Only a hash of it ever leaves the phone.
    /// </summary>
    public static string DeviceId
    {
        get
        {
            if (deviceId != null) return deviceId;
            string id = "";
            try { id = Android.Provider.Settings.Secure.GetString(Ctx.ContentResolver, Android.Provider.Settings.Secure.AndroidId) ?? ""; }
            catch { /* handled below */ }
            if (id.Length < 8)
            {
                // no ID from Android: a random one, kept with the app's data
                var prefs = Ctx.GetSharedPreferences("device", FileCreationMode.Private)!;
                id = prefs.GetString("fallback_id", "") ?? "";
                if (id.Length < 8) { id = RandomHex(16); prefs.Edit()!.PutString("fallback_id", id)!.Apply(); }
                id = "fallback|" + id;
            }
            deviceId = Sha256Hex("bi-assistant-device-v1|android|" + id.ToLowerInvariant());
            return deviceId;
        }
    }

    static Context Ctx => Android.App.Application.Context;

    static string Sha256Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    static string RandomHex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    // ------------------------------------------------------------------ starting and stopping
    /// <summary>Starts watching the license: loads the saved sign-in, asks the site, and keeps asking.</summary>
    public static void Begin()
    {
        lock (gate)
        {
            if (loop != null) return;
            loop = new CancellationTokenSource();
            token = LoadToken();
        }
        if (token != null) Set(LicenseState.Checking, "Checking your license...", "", 0);
        var ct = loop!.Token;
        _ = Task.Run(() => RunLoop(ct));
    }

    public static void Shutdown()
    {
        lock (gate) { loop?.Cancel(); loop = null; }
    }

    /// <summary>Forget this sign-in (here and on the site).</summary>
    public static async Task SignOutAsync()
    {
        string? tok;
        lock (gate) tok = token;
        if (tok != null)
        {
            try { await PostAsync("logout", new Dictionary<string, object?> { ["action"] = "logout" }, tok, CancellationToken.None); } catch { /* the site forgets it by itself after a while */ }
        }
        DropSignIn();
        Set(LicenseState.SignedOut, "Signed out.", "", 0);
    }

    static async Task RunLoop(CancellationToken ct)
    {
        try
        {
            string? first; lock (gate) first = token;
            if (first != null) await CheckNowAsync(ct);
            else Set(LicenseState.SignedOut, "Sign in with your guild Discord account.", "", 0);
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                string? tok; lock (gate) tok = token;
                if (tok == null) continue;
                // the ticket is time limited: when it runs out the program locks itself, whatever the network does
                string? why = Problem();
                if (why != null && State == LicenseState.Licensed) Set(LicenseState.Offline, why, UserName, 0);
                if (Stopwatch.GetTimestamp() >= Interlocked.Read(ref nextCheckAt)) await CheckNowAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Set(LicenseState.Error, "License check stopped: " + ex.Message, UserName, 0); }
    }

    /// <summary>Ask the site right now (the "Check again" button).</summary>
    public static Task RecheckAsync() => CheckNowAsync(CancellationToken.None);

    static void ScheduleNext(int secs) =>
        Interlocked.Exchange(ref nextCheckAt, Stopwatch.GetTimestamp() + (long)(Math.Max(5, secs) * (double)Stopwatch.Frequency));

    // ------------------------------------------------------------------ the question
    static async Task CheckNowAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref checking, 1) == 1) return;      // one question at a time
        try { await CheckAsync(ct); }
        finally { Interlocked.Exchange(ref checking, 0); }
    }

    static async Task CheckAsync(CancellationToken ct)
    {
        string? tok; lock (gate) tok = token;
        if (tok == null) { Set(LicenseState.SignedOut, "Sign in with your guild Discord account.", "", 0); return; }
        if (!KeyOk()) { Set(LicenseState.Error, "This build has no license key. Download the official app from the guild site.", "", 0); ScheduleNext(300); return; }
        if (State != LicenseState.Licensed) Set(LicenseState.Checking, "Checking your license...", UserName, DaysLeft);

        string nonce = RandomHex(16);
        Reply r;
        try
        {
            r = await PostAsync("check", new Dictionary<string, object?> { ["action"] = "check", ["hwid"] = DeviceId, ["nonce"] = nonce, ["platform"] = LicenseConfig.Platform, ["app_version"] = LicenseConfig.AppVersion }, tok, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // no connection: a ticket still in force keeps working until it runs out
            ScheduleNext(15);
            string? why = Problem();
            if (why != null) Set(LicenseState.Offline, "Can't reach the guild site (" + Short(ex.Message) + "). " + why, UserName, 0);
            return;
        }

        string code = Str(r.Root, "code");
        if (r.Status == 401 || code == "device_mismatch")
        {
            DropSignIn();
            Set(LicenseState.SignedOut, code == "device_mismatch" ? "This sign-in belonged to another device. Sign in again." : "You were signed out (signed in somewhere else, or the sign-in ran out). Sign in again.", "", 0);
            return;
        }
        if (r.Status == 403)
        {
            DropSignIn();
            Set(LicenseState.NotMember, Str(r.Root, "error") is { Length: > 0 } e ? e : "You are not allowed to use the Assistant.", "", 0);
            return;
        }
        if (r.Status != 200)
        {
            ScheduleNext(30);
            string? why = Problem();
            Set(why == null ? LicenseState.Licensed : LicenseState.Error, Str(r.Root, "error") is { Length: > 0 } e2 ? e2 : "The guild site answered with an error (" + r.Status + ").", UserName, DaysLeft);
            return;
        }

        int every = (int)Math.Clamp(Num(r.Root, "check_every"), 45, 240);
        checkEverySecs = every > 0 ? every : 120;
        string name = Str(r.Root, "name");
        string av = Str(r.Root, "avatar_url");
        AvatarUrl = av.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && av.Length < 500 ? av : "";
        int days = (int)Num(r.Root, "days_left");

        if (!Bool(r.Root, "licensed"))
        {
            ClearTicket();
            string reason = Str(r.Root, "reason");
            switch (reason)
            {
                case "expired": Set(LicenseState.Expired, "Your Assistant time has run out. Buy more days in the guild shop.", name, 0); break;
                case "revoked": Set(LicenseState.Revoked, "Your license was switched off by the Guild Leader.", name, 0); break;
                case "other_device":
                    // a member can have several devices (one license each); this phone has none free
                    Set(LicenseState.OtherDevice, "All your Assistant devices with days are in use on other devices (" + Str(r.Root, "device_tag") + "). To run on this phone too: on the Assistant page of the guild site, move some days to a new device, or buy days for a new device in the shop. Then tap Check again.", name, days); break;
                case "mobile_off":
                    Set(LicenseState.PhoneOff, Str(r.Root, "error") is { Length: > 0 } m ? m : "The phone Assistant is not switched on for you yet. Ask the Guild Leader to turn it on in the Admin Hall.", name, days); break;
                default: Set(LicenseState.NoLicense, "No Assistant time on this account yet. Buy days in the guild shop.", name, 0); break;
            }
            ScheduleNext(Math.Max(30, checkEverySecs / 2));
            return;
        }

        if (!r.Root.TryGetProperty("ticket", out var ticket) || !Accept(ticket, nonce))
        {
            ClearTicket();
            Set(LicenseState.Error, "The guild site's answer could not be verified. Update the app.", name, 0);
            ScheduleNext(30);
            return;
        }
        ScheduleNext(checkEverySecs + Random.Shared.Next(0, 15));
        Set(LicenseState.Licensed, days == 1 ? "1 day left" : days + " days left", name, days);
    }

    static string Short(string s) => s.Length > 60 ? s[..60] : s;

    // ------------------------------------------------------------------ signing in with Discord
    /// <summary>
    /// Asks the site for a sign-in code and waits until the member approves it in the browser.
    /// <paramref name="onCode"/> gets (code, device ID shown on the site, address to open).
    /// </summary>
    public static async Task<bool> SignInAsync(Action<string, string, string> onCode, CancellationToken ct)
    {
        if (!KeyOk()) { Set(LicenseState.Error, "This build has no license key. Download the official app from the guild site.", "", 0); return false; }
        Reply r;
        try { r = await PostAsync("start", new Dictionary<string, object?> { ["action"] = "start", ["hwid"] = DeviceId, ["app_version"] = LicenseConfig.AppVersion, ["platform"] = LicenseConfig.Platform }, null, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Set(LicenseState.SignedOut, "Can't reach the guild site (" + Short(ex.Message) + ").", "", 0);
            return false;
        }
        if (r.Status != 200)
        {
            Set(LicenseState.SignedOut, Str(r.Root, "error") is { Length: > 0 } e ? e : "The guild site refused (" + r.Status + ").", "", 0);
            return false;
        }
        string secret = Str(r.Root, "device_secret"), code = Str(r.Root, "user_code"), tag = Str(r.Root, "device_tag"), url = Str(r.Root, "verify_url");
        int interval = (int)Math.Clamp(Num(r.Root, "interval"), 2, 10);
        double life = Math.Clamp(Num(r.Root, "expires_in"), 60, 900);
        if (secret.Length != 64 || code.Length == 0) { Set(LicenseState.SignedOut, "The guild site's answer was not understood. Update the app.", "", 0); return false; }
        // only ever open the guild's own site, whatever the answer says
        if (!url.StartsWith(LicenseConfig.SiteUrl + "/", StringComparison.OrdinalIgnoreCase)) url = LicenseConfig.SiteUrl + "/link-app?link=" + Uri.EscapeDataString(code);
        onCode(code, tag, url);
        Set(LicenseState.SignedOut, "Waiting for you to approve the code on the guild site...", "", 0);

        long until = Stopwatch.GetTimestamp() + (long)(life * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < until)
        {
            await Task.Delay(interval * 1000, ct);
            Reply p;
            try { p = await PostAsync("poll", new Dictionary<string, object?> { ["action"] = "poll", ["device_secret"] = secret }, null, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { continue; }      // a hiccup: keep waiting
            string state = Str(p.Root, "state");
            if (state == "ok")
            {
                string tok = Str(p.Root, "token");
                if (tok.Length != 64) break;
                lock (gate) token = tok;
                SaveToken(tok);
                ClearTicket();
                Interlocked.Exchange(ref nextCheckAt, 0);
                await CheckNowAsync(ct);
                return true;
            }
            if (state == "denied") { Set(LicenseState.NotMember, "You are not allowed to use the Assistant (no guild member role, or banned).", "", 0); return false; }
            if (state == "expired") break;
        }
        Set(LicenseState.SignedOut, "The code ran out before it was approved. Press Sign in again.", "", 0);
        return false;
    }

    // ------------------------------------------------------------------ tickets
    static bool KeyOk()
    {
        lock (gate)
        {
            if (keyTried) return verifier != null;
            keyTried = true;
            try
            {
                var k = ECDsa.Create();
                k.ImportSubjectPublicKeyInfo(Convert.FromBase64String(LicenseConfig.PublicKeyB64), out _);
                verifier = k;
            }
            catch { verifier = null; }
            return verifier != null;
        }
    }

    static bool VerifySig(string payload, string sigB64)
    {
        try
        {
            if (!KeyOk()) return false;
            byte[] sig = Convert.FromBase64String(sigB64);
            if (sig.Length != 64) return false;
            lock (gate) return verifier!.VerifyData(Encoding.UTF8.GetBytes(payload), sig, HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }

    static bool Accept(JsonElement ticket, string nonce)
    {
        string payload = Str(ticket, "payload"), sig = Str(ticket, "sig");
        if (payload.Length == 0 || !VerifySig(payload, sig)) return false;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var p = doc.RootElement;
            // it must be the answer to THIS question, for THIS computer
            if (Str(p, "hw") != DeviceId || Str(p, "n") != nonce) return false;
            long iat = (long)Num(p, "iat"), ttl = (long)Num(p, "ttl"), exp = (long)Num(p, "exp");
            if (ttl <= 0 || exp <= iat) return false;
            lock (gate)
            {
                ticketPayload = payload; ticketSig = sig; ticketNonce = nonce;
                ticketRecv = Stopwatch.GetTimestamp(); ticketIat = iat; ticketTtl = ttl; ticketExp = exp;
                lastVerify = ticketRecv;
            }
            return true;
        }
        catch { return false; }
    }

    static void ClearTicket()
    {
        lock (gate) { ticketPayload = null; ticketSig = null; ticketNonce = null; ticketRecv = 0; ticketTtl = 0; }
    }

    /// <summary>null when everything is in order, otherwise why the program may not act.</summary>
    static string? Problem()
    {
        string? payload, sig; long recv, iat, ttl, exp, verified;
        lock (gate) { payload = ticketPayload; sig = ticketSig; recv = ticketRecv; iat = ticketIat; ttl = ticketTtl; exp = ticketExp; verified = lastVerify; }
        if (payload == null || sig == null) return "No active license (sign in, and buy Assistant days on the guild site).";
        long now = Stopwatch.GetTimestamp();
        double since = (now - recv) / (double)Stopwatch.Frequency;
        if (since < 0 || since >= ttl) return "The license check is out of date. The app must reach the guild site every few minutes.";
        if (iat + since >= exp) return "Your Assistant time has run out.";
        if ((now - verified) / (double)Stopwatch.Frequency > 5)
        {
            // look at the signature again now and then, not only when the ticket arrived
            if (!VerifySig(payload, sig)) { ClearTicket(); return "The license ticket did not verify."; }
            lock (gate) lastVerify = now;
        }
        return null;
    }

    /// <summary>Throws <see cref="LicenseException"/> unless a valid ticket is in force.</summary>
    public static void Require()
    {
        string? why = Problem();
        if (why != null) throw new LicenseException(why);
    }

    /// <summary>True while a valid ticket is in force.</summary>
    public static bool Allowed() => Problem() == null;

    /// <summary>
    /// Seconds of Assistant time left, counted from the signed ticket with the stopwatch (not the clock, so setting the clock back changes nothing).
    /// -1 when there is no ticket.
    /// </summary>
    public static double SecondsLeft
    {
        get
        {
            string? payload; long recv, iat, exp;
            lock (gate) { payload = ticketPayload; recv = ticketRecv; iat = ticketIat; exp = ticketExp; }
            if (payload == null) return -1;
            double since = (Stopwatch.GetTimestamp() - recv) / (double)Stopwatch.Frequency;
            return Math.Max(0, exp - (iat + since));
        }
    }

    /// <summary>"29d 23h 41m 12s" - the time left as shown in the window.</summary>
    public static string TimeLeftText()
    {
        double s = SecondsLeft;
        if (s < 0) return "-";
        var t = TimeSpan.FromSeconds(Math.Floor(s));
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours:D2}h {t.Minutes:D2}m {t.Seconds:D2}s";
        if (t.TotalHours >= 1) return $"{t.Hours}h {t.Minutes:D2}m {t.Seconds:D2}s";
        return $"{t.Minutes}m {t.Seconds:D2}s";
    }

    /// <summary>Why the program may not act right now (empty when it may).</summary>
    public static string Why() => Problem() ?? "";

    // ------------------------------------------------------------------ state
    static void Set(LicenseState state, string message, string name, int days)
    {
        bool changed;
        lock (gate)
        {
            changed = State != state || Message != message || UserName != name || DaysLeft != days;
            State = state; Message = message; UserName = name; DaysLeft = days;
        }
        if (changed) { try { Changed?.Invoke(); } catch { /* a listener's problem */ } }
    }

    /// <summary>Show a problem on the sign-in screen.</summary>
    public static void Fail(string message) => Set(LicenseState.SignedOut, message, "", 0);

    static void DropSignIn()
    {
        lock (gate) token = null;
        ClearTicket();
        DeleteSaved();
    }

    // ------------------------------------------------------------------ the saved sign-in (the app's private storage: no other app can read it)
    static ISharedPreferences Prefs => Ctx.GetSharedPreferences("license", FileCreationMode.Private)!;

    static void DeleteSaved()
    {
        try { Prefs.Edit()!.Remove("token")!.Apply(); } catch { /* nothing saved */ }
    }

    static string? LoadToken()
    {
        try
        {
            string t = Prefs.GetString("token", "") ?? "";
            return t.Length == 64 ? t : null;
        }
        catch { return null; }
    }

    static void SaveToken(string t)
    {
        try { Prefs.Edit()!.PutString("token", t)!.Apply(); }
        catch { /* the sign-in then lasts until the app is closed */ }
    }

    // ------------------------------------------------------------------ for the updater
    /// <summary>Checks a signature made by the site's key over plain text (used for update manifests).</summary>
    internal static bool VerifyText(string text, string sigB64) => VerifySig(text, sigB64);

    /// <summary>Asks the site something with this program's sign-in. Returns the answer when the site says OK, otherwise null.</summary>
    internal static async Task<JsonElement?> CallSignedInAsync(string action, Dictionary<string, object?> body, CancellationToken ct)
    {
        string? tok;
        lock (gate) tok = token;
        if (tok == null) return null;
        body["action"] = action;
        var r = await PostAsync(action, body, tok, ct);
        return r.Status == 200 ? r.Root : null;
    }

    // ------------------------------------------------------------------ talking to the site
    readonly record struct Reply(int Status, JsonElement Root);

    static async Task<Reply> PostAsync(string action, Dictionary<string, object?> body, string? tok, CancellationToken ct)
    {
        // the sign-in token travels in a header and in the body (the body copy is for any proxy that drops custom headers)
        if (tok != null) body["_bt"] = tok;
        using var req = new HttpRequestMessage(HttpMethod.Post, LicenseConfig.ApiBase + "/" + LicenseConfig.Function)
        {
            Content = new StringContent(ToJson(body), Encoding.UTF8, "application/json")
        };
        if (tok != null) req.Headers.TryAddWithoutValidation("x-bot-token", tok);
        using var res = await http.SendAsync(req, ct);
        string text = await res.Content.ReadAsStringAsync(ct);
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("{}");
            root = empty.RootElement.Clone();
        }
        return new Reply((int)res.StatusCode, root);
    }

    /// <summary>Writes the question by hand (no reflection, so it survives trimming): text, numbers, yes/no, lists and objects.</summary>
    static string ToJson(Dictionary<string, object?> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) Write(w, body);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    static void Write(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case double d: w.WriteNumberValue(d); break;
            case float f: w.WriteNumberValue(f); break;
            case Dictionary<string, object?> map:
                w.WriteStartObject();
                foreach (var (k, x) in map) { w.WritePropertyName(k); Write(w, x); }
                w.WriteEndObject();
                break;
            case System.Collections.IEnumerable list:
                w.WriteStartArray();
                foreach (var x in list) Write(w, x);
                w.WriteEndArray();
                break;
            default: w.WriteStringValue(v.ToString()); break;
        }
    }

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
