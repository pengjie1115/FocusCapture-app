using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FocusCapture.Services.Sync;

/// <summary>
/// Windows DPAPI 封装（P/Invoke crypt32.dll CryptProtectData/CryptUnprotectData，CurrentUser 作用域）。
/// 项目依赖克制（不用第三方 NuGet），且当前环境无外网装不了官方
/// System.Security.Cryptography.ProtectedData 包——用系统原生 API 等价实现（2026-08-13 审查调整）。
/// 用途：WebDAV 授权码本机加密存储（settings.json 中为 DPAPI 密文）。
/// </summary>
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int CbData;
        public IntPtr PbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>加密（CurrentUser 作用域），返回 Base64 密文。</summary>
    public static string Protect(string plaintext)
    {
        var inBlob = ToBlob(Encoding.UTF8.GetBytes(plaintext));
        var outBlob = default(DataBlob);
        try
        {
            if (!CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref outBlob))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var bytes = new byte[outBlob.CbData];
            Marshal.Copy(outBlob.PbData, bytes, 0, outBlob.CbData);
            return Convert.ToBase64String(bytes);
        }
        finally
        {
            FreeBlob(inBlob);
            FreeBlob(outBlob);
        }
    }

    /// <summary>解密失败（换机器/损坏/被篡改）返回 null——调用方提示重新配置，不崩。</summary>
    public static string? Unprotect(string protectedBase64)
    {
        try
        {
            var inBlob = ToBlob(Convert.FromBase64String(protectedBase64));
            var outBlob = default(DataBlob);
            try
            {
                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref outBlob))
                    return null;
                var bytes = new byte[outBlob.CbData];
                Marshal.Copy(outBlob.PbData, bytes, 0, outBlob.CbData);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                FreeBlob(inBlob);
                FreeBlob(outBlob);
            }
        }
        catch
        {
            return null;
        }
    }

    private static DataBlob ToBlob(byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return new DataBlob { CbData = data.Length, PbData = ptr };
    }

    private static void FreeBlob(DataBlob blob)
    {
        if (blob.PbData != IntPtr.Zero)
        {
            LocalFree(blob.PbData);
            blob.PbData = IntPtr.Zero;
        }
    }
}
