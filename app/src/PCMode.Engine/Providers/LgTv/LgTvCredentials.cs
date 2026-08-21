using System.Security.Cryptography;
using System.Text;

namespace PCMode.Engine;

/// <summary>What this machine has to show a television to be let back in.</summary>
/// <param name="ClientKey">
/// The token the television issued when somebody accepted the pairing prompt.
/// Secret: anything holding it can control that television.
/// </param>
/// <param name="CertificateFingerprint">
/// The certificate the television presented at that moment. Not secret, and kept
/// with the key because the two are only meaningful together -- the key is what
/// this machine proves, and the fingerprint is what it demands in return.
/// </param>
public sealed record LgTvCredential(string ClientKey, string CertificateFingerprint);

/// <summary>
/// Where a television's pairing key is kept.
/// </summary>
/// <remarks>
/// A seam, and only a seam, so that the rules about when a key may be written can
/// be tested without a test encrypting anything into the profile of the machine
/// it is running on. The production implementation is
/// <see cref="ProtectedLgTvCredentials"/>.
/// </remarks>
public interface ILgTvCredentials
{
    /// <summary>
    /// What is stored for this television, or null when nothing is.
    /// </summary>
    /// <remarks>
    /// Null for a television never paired with, and null again for a stored
    /// credential that cannot be read back -- a profile copied to another machine,
    /// a truncated file. Both mean the same thing to everything above this: there
    /// is no key, so pairing is the only way in. Failing loudly instead would turn
    /// a recoverable situation into a television that can never be used again
    /// without somebody finding the file.
    /// </remarks>
    LgTvCredential? Read(string host);

    /// <summary>
    /// Records a credential, or says why it could not.
    /// </summary>
    /// <remarks>
    /// Only ever called after a television has actually issued a key, which only
    /// happens after somebody accepted a prompt on the screen. Nothing that reads,
    /// plans or dry-runs reaches this.
    /// </remarks>
    bool TryWrite(string host, LgTvCredential credential, out string? problem);
}

/// <summary>
/// The real thing: one file per television, encrypted to this Windows user.
/// </summary>
/// <remarks>
/// WHY IT IS NOT IN THE CONFIGURATION
///
/// config.json is a file people edit, copy, paste into messages when asking for
/// help, and put in backups. A client key is a credential for a device on their
/// network. It lives apart from the configuration, encrypted, and appears in no
/// message, no status line, no transition report and no diagnostic.
///
/// DPAPI, AND WHAT THAT DOES AND DOES NOT BUY
///
/// <c>CryptProtectData</c> under the current user, with entropy derived from the
/// television's address. It means the file is useless on another machine and
/// useless to another user of this one. It does not defend against code already
/// running as this user -- nothing stored on a machine can -- and it is not
/// pretending to. What it removes is a plaintext credential sitting in a profile
/// folder to be found by anything that goes looking.
///
/// UNDER THE CHOSEN DATA ROOT
///
/// Beside the configuration and state, so that the whole of what this application
/// knows is one folder, and so a test pointed at a temporary root cannot write
/// into the real profile. The entropy binds a file to the television it was
/// issued for, so a file moved between roots or renamed to another television's
/// name does not decrypt.
/// </remarks>
public sealed class ProtectedLgTvCredentials : ILgTvCredentials
{
    private const string FolderName = "lgtv";

    private readonly string _folder;

    public ProtectedLgTvCredentials(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _folder = Path.Combine(root, FolderName);
    }

    public LgTvCredential? Read(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        string path = FileFor(host);

        if (!File.Exists(path))
        {
            return null;
        }

        byte[]? clear = null;

        try
        {
            clear = WindowsDpapi.Unprotect(File.ReadAllBytes(path), Entropy(host));

            string[] parts = Encoding.UTF8.GetString(clear).Split('\n');

            return parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0])
                ? new LgTvCredential(parts[0], parts[1])
                : null;
        }
        catch (Exception)
        {
            // Anything at all: a foreign file, a profile from another machine, a
            // truncated write. See ILgTvCredentials.Read -- all of it means "there
            // is no key", and pairing again is the way out of every one of them.
            return null;
        }
        finally
        {
            if (clear is not null)
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
    }

    public bool TryWrite(string host, LgTvCredential credential, out string? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(credential);

        byte[]? clear = null;
        string path = FileFor(host);
        string temporary = path + ".tmp";

        try
        {
            Directory.CreateDirectory(_folder);

            clear = Encoding.UTF8.GetBytes($"{credential.ClientKey}\n{credential.CertificateFingerprint}");

            // Written aside and moved into place, so that a write interrupted half
            // way leaves the previous credential intact rather than a file that
            // decrypts to nothing and reads as "never paired".
            File.WriteAllBytes(temporary, WindowsDpapi.Protect(clear, Entropy(host)));
            File.Move(temporary, path, overwrite: true);

            problem = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            problem = $"Could not save the television's pairing key in {_folder}. {exception.Message}";
            return false;
        }
        finally
        {
            if (clear is not null)
            {
                CryptographicOperations.ZeroMemory(clear);
            }

            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Tidying up, and nothing more. A leftover temporary file is not
                // worth failing a save that has already succeeded.
            }
        }
    }

    /// <summary>
    /// A stable file name for a television, that is not its address.
    /// </summary>
    /// <remarks>
    /// An address can contain characters a path cannot, and a folder listing that
    /// enumerated the addresses of devices on somebody's network would be telling
    /// anything that could read the folder more than it needs to know.
    /// </remarks>
    private string FileFor(string host) =>
        Path.Combine(_folder, $"{Convert.ToHexString(Entropy(host))[..16]}.cred");

    /// <summary>
    /// Binds a stored credential to the television it was issued for.
    /// </summary>
    /// <remarks>
    /// Additional entropy means a file only decrypts when it is being asked about
    /// the same television, so one renamed or copied between roots fails to open
    /// rather than quietly offering another television's key.
    /// </remarks>
    private static byte[] Entropy(string host) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"PCMode.Native.LgTv|{host.Trim().ToLowerInvariant()}"));
}
