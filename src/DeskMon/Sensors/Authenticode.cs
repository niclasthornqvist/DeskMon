using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace DeskMon.Sensors;

/// <summary>
/// Whether a program file carries a valid digital signature from a given
/// publisher - for the programs DeskMon starts while it runs as administrator.
///
/// The folder a program is in can't vouch for it here: PresentMon from winget
/// lives under %LOCALAPPDATA%, which anything running as the user can write
/// to, so a program planted there would be started elevated. The signature
/// can't be planted - it has to come from the publisher's own certificate.
/// </summary>
internal static class Authenticode
{
    /// <summary>
    /// True when <paramref name="path"/> is signed, the signature checks out
    /// (the file is unchanged since signing, the chain reaches a trusted root),
    /// and the certificate is issued to <paramref name="publisher"/>.
    ///
    /// Revocation isn't checked online: that can hang for many seconds with no
    /// network, and this runs as a game starts.
    /// </summary>
    public static bool IsSignedBy(string path, string publisher)
    {
        try
        {
            if (!Verify(path))
                return false;

#pragma warning disable SYSLIB0057   // the signer's certificate, not a certificate file
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return string.Equals(signer.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                                 publisher, StringComparison.Ordinal);
        }
        catch
        {
            return false;   // unsigned, unreadable or malformed: not trusted
        }
    }

    private static bool Verify(string path)
    {
        var file = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
        };

        IntPtr filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = filePtr,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
            };

            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            int result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            // The state the verify opened has to be closed again, or it leaks.
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            return result == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
        }
    }

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
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

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);
}
