using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace MicMix.Setup;

/// <summary>
/// Fetches VB-Cable from VB-Audio's own site and starts its installer. The driver can't be shipped
/// inside MicMix (VB-Audio doesn't allow redistribution), so this is the "out of the box" path:
/// download, verify the publisher's signature, run elevated, let the user click "Install Driver".
/// </summary>
public static class VbCableInstaller
{
    public const string PageUrl = "https://vb-audio.com/Cable/";
    private const string IndexUrl = "https://vb-audio.com/Cable/index.htm";
    private const string FallbackZip = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";
    private const string ExpectedSigner = "BUREL VINCENT"; // VB-Audio's author; the setup exe is signed by him

    public enum Result { Installed, InstallerClosed, Cancelled, Failed }

    /// <returns>The outcome plus a user-facing message.</returns>
    public static async Task<(Result Result, string Message)> InstallAsync(IProgress<string> progress, CancellationToken ct = default)
    {
        string work = Path.Combine(Path.GetTempPath(), "MicMix", "VBCable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        try
        {
            Directory.CreateDirectory(work);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MicMix/1.0");

            progress.Report("Ищу последнюю версию на vb-audio.com…");
            string zipUrl = FallbackZip;
            try
            {
                string page = await http.GetStringAsync(IndexUrl, ct);
                var m = Regex.Match(page, @"https://download\.vb-audio\.com/Download_CABLE/VBCABLE_Driver_Pack\d+\.zip");
                if (m.Success) zipUrl = m.Value;
            }
            catch (HttpRequestException) { /* use the known version */ }

            progress.Report("Скачиваю VB-Cable…");
            string zip = Path.Combine(work, "vbcable.zip");
            await using (var src = await http.GetStreamAsync(zipUrl, ct))
            await using (var dst = File.Create(zip))
                await src.CopyToAsync(dst, ct);

            progress.Report("Проверяю подпись установщика…");
            string dir = Path.Combine(work, "files");
            ZipFile.ExtractToDirectory(zip, dir);
            string setup = Path.Combine(dir, Environment.Is64BitOperatingSystem ? "VBCABLE_Setup_x64.exe" : "VBCABLE_Setup.exe");
            if (!File.Exists(setup))
                return (Result.Failed, "В архиве VB-Audio не нашёлся установщик. Установи VB-Cable вручную с vb-audio.com.");
            if (!IsTrustedVbAudio(setup))
                return (Result.Failed, "Подпись установщика не прошла проверку, поэтому я его не запускаю. Скачай VB-Cable вручную с vb-audio.com.");

            progress.Report("Жду, пока ты нажмёшь «Install Driver» в окне VB-Cable…");
            Process? proc;
            try
            {
                proc = Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true, Verb = "runas", WorkingDirectory = dir });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC "No"
            {
                return (Result.Cancelled, "Установка отменена. Без VB-Cable MicMix не сможет отдавать звук в Discord и игры.");
            }
            if (proc != null) await proc.WaitForExitAsync(ct);

            return VirtualCableDetector.IsInstalled()
                ? (Result.Installed, "VB-Cable установлен. Если Discord его не видит — перезагрузи компьютер.")
                : (Result.InstallerClosed, "Окно установки закрыто, но VB-Cable пока не виден. Если ты нажал «Install Driver», перезагрузи компьютер.");
        }
        catch (OperationCanceledException)
        {
            return (Result.Cancelled, "Установка отменена.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            return (Result.Failed, "Не получилось скачать VB-Cable: " + ex.Message + " Можно установить вручную с vb-audio.com.");
        }
    }

    /// <summary>Authenticode chain is valid (WinVerifyTrust) and the signer is VB-Audio's author.</summary>
    private static bool IsTrustedVbAudio(string file)
    {
        if (!WinVerifyTrust(file)) return false;
        try
        {
#pragma warning disable SYSLIB0057 // signer extraction from a signed PE, not loading a cert file
            var cert = X509Certificate.CreateFromSignedFile(file);
#pragma warning restore SYSLIB0057
            return cert.Subject.Contains(ExpectedSigner, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    #region WinVerifyTrust

    private static bool WinVerifyTrust(string file)
    {
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = file };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 0,
                dwProvFlags = 0x80,      // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrustNative(new IntPtr(-1), ref action, ref data) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(pFile);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", EntryPoint = "WinVerifyTrust", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrustNative(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    #endregion
}

public static class VirtualCableDetector
{
    public static bool IsInstalled()
    {
        try
        {
            using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
                using (d)
                    if (d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }
}
