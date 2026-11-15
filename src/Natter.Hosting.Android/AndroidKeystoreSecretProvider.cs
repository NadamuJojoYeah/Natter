using System.Text;
using Android.Content;
using Android.Security.Keystore;
using Android.Util;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Java.Security;
using Natter.Plugin.Abstractions;

namespace Natter.Hosting.Android;

/// <summary>
/// Android Keystore-backed secret provider. Values are AES-GCM encrypted before being
/// written to app-private SharedPreferences; key material stays in AndroidKeyStore.
/// </summary>
public sealed class AndroidKeystoreSecretProvider : ISecretProvider
{
    private const string PreferenceName = "natter.plugin.secrets";
    private const string KeystoreAlias = "natter.plugin.secrets.key";
    private const string KeystoreProvider = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";

    private readonly ISharedPreferences _preferences;
    private readonly object _gate = new();

    public AndroidKeystoreSecretProvider(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _preferences = context.GetSharedPreferences(PreferenceName, FileCreationMode.Private)!;
    }

    public bool TryGet(string name, out string? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var encoded = _preferences.GetString(name, null);
        if (string.IsNullOrEmpty(encoded))
            return false;

        try
        {
            var separator = encoded.IndexOf(':');
            if (separator <= 0)
                return false;

            var iv = Convert.FromBase64String(encoded[..separator]);
            var cipherText = Convert.FromBase64String(encoded[(separator + 1)..]);
            var key = GetKey();
            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.DecryptMode, key, new GCMParameterSpec(128, iv));
            var plain = cipher.DoFinal(cipherText) ?? [];
            value = Encoding.UTF8.GetString(plain);
            return true;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    /// <summary>Stores an encrypted secret. Intended for management UI/debug provisioning.</summary>
    public void Set(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        lock (_gate)
        {
            var key = GetKey();
            var iv = new byte[12];
            new SecureRandom().NextBytes(iv);

            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.EncryptMode, key, new GCMParameterSpec(128, iv));
            var cipherText = cipher.DoFinal(Encoding.UTF8.GetBytes(value)) ?? [];
            var encoded = Convert.ToBase64String(iv) + ":" + Convert.ToBase64String(cipherText);

            var editor = _preferences.Edit()!;
            editor.PutString(name, encoded);
            editor.Apply();
        }
    }

    public void Remove(string name)
    {
        var editor = _preferences.Edit()!;
        editor.Remove(name);
        editor.Apply();
    }

    private static ISecretKey GetKey()
    {
        var keyStore = KeyStore.GetInstance(KeystoreProvider)!;
        keyStore.Load(null, null);

        if (keyStore.GetKey(KeystoreAlias, null) is ISecretKey existing)
            return existing;

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeystoreProvider)!;
        var spec = new KeyGenParameterSpec.Builder(
                KeystoreAlias,
                KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .Build();

        generator.Init(spec);
        return generator.GenerateKey()!;
    }
}
