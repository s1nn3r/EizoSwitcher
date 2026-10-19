using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace EizoSwitcher.Services;

public sealed record UpdateInfo(Version Version, string Tag, string ZipUrl, string? HashUrl, string PageUrl, string Notes);

/// <summary>
/// Updates from GitHub releases: downloads EizoSwitcher-win-x64.zip, checks its SHA-256 (asset *.sha256)
/// and – if the running exe is Authenticode-signed – requires the same publisher on the new one.
/// The running exe is renamed (allowed by Windows), the new one copied in place and started.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Updater
{
    public const string Repository = "s1nn3r/EizoSwitcher";
    public const string AssetName = "EizoSwitcher-win-x64.zip";
    private static readonly string[] Executables = ["EizoSwitcher.exe", "eizoswitch.exe"];

    private static readonly HttpClient http_ = CreateClient();

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string ReleasesUrl => $"https://github.com/{Repository}/releases";

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await http_.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null; // no release published yet
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || version <= Current) return null;

        string? zip = null, hash = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            string? name = a.GetProperty("name").GetString(), url = a.GetProperty("browser_download_url").GetString();
            if (name == AssetName) zip = url;
            else if (name == AssetName + ".sha256") hash = url;
        }
        if (zip == null) return null;
        return new UpdateInfo(version, tag, zip, hash,
            root.GetProperty("html_url").GetString() ?? ReleasesUrl,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "");
    }

    /// <summary>Downloads, verifies and installs the update, then starts the new version. The caller must exit afterwards.</summary>
    public static async Task InstallAsync(UpdateInfo update, CancellationToken ct = default)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown process path");
        string dir = Path.GetDirectoryName(exe)!;
        string work = Path.Combine(Path.GetTempPath(), $"EizoSwitcher-update-{update.Version}");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);

        string zipPath = Path.Combine(work, AssetName);
        await using (var file = File.Create(zipPath))
        await using (var stream = await http_.GetStreamAsync(update.ZipUrl, ct))
            await stream.CopyToAsync(file, ct);

        if (update.HashUrl != null)
        {
            string expected = (await http_.GetStringAsync(update.HashUrl, ct)).Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0];
            string actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(zipPath, ct)));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L.T("Prüfsumme des Updates stimmt nicht.", "Update checksum mismatch."));
        }

        string extracted = Path.Combine(work, "files");
        ZipFile.ExtractToDirectory(zipPath, extracted);
        foreach (var name in Executables.Where(n => File.Exists(Path.Combine(extracted, n))))
            VerifySignature(exe, Path.Combine(extracted, name));
        if (!File.Exists(Path.Combine(extracted, "EizoSwitcher.exe")))
            throw new InvalidDataException("EizoSwitcher.exe missing in update package");

        // swap files: running executables can be renamed, not overwritten
        var replaced = new List<string>();
        try
        {
            foreach (var src in Directory.EnumerateFiles(extracted))
            {
                string target = Path.Combine(dir, Path.GetFileName(src));
                if (File.Exists(target))
                {
                    File.Delete(target + ".old");
                    File.Move(target, target + ".old");
                    replaced.Add(target);
                }
                File.Copy(src, target, true);
            }
        }
        catch
        {
            foreach (var t in replaced) { File.Delete(t); File.Move(t + ".old", t); }
            throw;
        }

        Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false });
    }

    /// <summary>Removes *.old files left over from the previous update.</summary>
    public static void CleanupOldFiles()
    {
        string? dir = Path.GetDirectoryName(Environment.ProcessPath);
        if (dir == null) return;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            bool pending = false;
            foreach (var f in Directory.EnumerateFiles(dir, "*.old"))
            {
                try { File.Delete(f); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { pending = true; }
            }
            if (!pending) return;
            Thread.Sleep(500); // previous process may still be exiting
        }
    }

    private static void VerifySignature(string current, string candidate)
    {
        string? publisher = Publisher(current);
        if (publisher == null) return; // unsigned (developer) build: checksum is all we have
        if (!Authenticode.IsTrusted(candidate))
            throw new InvalidDataException(L.T("Das Update ist nicht gültig signiert.", "The update is not validly signed."));
        if (Publisher(candidate) != publisher)
            throw new InvalidDataException(L.T("Das Update stammt von einem anderen Herausgeber.", "The update was signed by a different publisher."));
    }

    private static string? Publisher(string file)
    {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is the only managed way to read the Authenticode signer
        try { return X509Certificate.CreateFromSignedFile(file).Subject; }
        catch (CryptographicException) { return null; }
#pragma warning restore SYSLIB0057
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EizoSwitcher", Current.ToString()));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }
}

/// <summary>WinVerifyTrust: is the file's Authenticode signature valid and trusted?</summary>
[SupportedOSPlatform("windows")]
internal static class Authenticode
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile, pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData, pwszURLReference;
        public uint dwProvFlags, dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    public static bool IsTrusted(string path)
    {
        var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = pFile,
            };
            var action = GenericVerifyV2;
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }
}
