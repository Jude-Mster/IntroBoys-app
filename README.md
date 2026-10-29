# BLACKLIST INTROBOYS app (Android)

One app with two icons:

- **BLACKLIST**: the guild hall (blacklistintroboys.com) in Chrome's app mode, like the old app.
- **IntroBoys Assistant**: sign in with the guild account, then auto attack in the game with bubbles placed on the game's buttons.

Built with .NET 10 for Android (Android 10 and newer). GitHub Actions builds the APK on every push
(`.github/workflows/android.yml`); download it from the run's **Artifacts**.

## Repository secrets (Settings → Secrets and variables → Actions)

| Name | What |
|---|---|
| `ANDROID_KEYSTORE_BASE64` | the signing key file (`introboys-release.jks`) as base64 |
| `ANDROID_KEYSTORE_PASSWORD` | its password |
| `ANDROID_KEY_ALIAS` | `introboys` |
| `ANDROID_KEY_PASSWORD` | the key password (the same one if Enter was pressed) |
| `LICENSE_PUBLIC_KEY` | the PUBLIC license key: the text of `PublicKeyB64` in the PC Assistant's `LicenseConfig.cs` |

Never commit the key file or passwords.
