using System.Security.Cryptography;
using System.Text;

namespace ExtremeEditor.Wpf;

// Only the signed server-side session handle is persisted; never Discord OAuth tokens.
internal static class DiscordDesktopSessionStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExtremeEditor", "discord-desktop-session.dat");

    public static void Save(string session)
    {
        if (session.Length != 64 || !session.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid desktop session.");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        byte[] plain = Encoding.UTF8.GetBytes(session);
        byte[] encrypted = ProtectedData.Protect(
            plain, null, DataProtectionScope.CurrentUser);
        string temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static string? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            byte[] cipher = File.ReadAllBytes(FilePath);
            string session = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                cipher, null, DataProtectionScope.CurrentUser));
            return session.Length == 64 && session.All(Uri.IsHexDigit)
                ? session : null;
        }
        catch (CryptographicException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}
