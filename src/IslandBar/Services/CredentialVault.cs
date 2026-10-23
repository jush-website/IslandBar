using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace IslandBar.Services
{
    /// <summary>
    /// 讀 Windows 認證管理員（Credential Manager）的「一般認證」。
    /// 只在本機讀取目前使用者自己的項目，用來找 Claude Code 存放的登入憑證；不會寫入或傳出。
    /// </summary>
    internal static class CredentialVault
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredEnumerate(string filter, uint flags, out uint count, out IntPtr credentials);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);

        /// <summary>找名稱含 "claude" 的項目，內容是含 accessToken 的 JSON 就回傳</summary>
        public static QuotaService.ClaudeCred FindClaude(out List<string> claudeTargets)
        {
            claudeTargets = new List<string>();
            if (!CredEnumerate(null, 0, out uint count, out IntPtr list)) return null;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    IntPtr p = Marshal.ReadIntPtr(list, i * IntPtr.Size);
                    var c = Marshal.PtrToStructure<CREDENTIAL>(p);
                    string target = c.TargetName ?? "";
                    if (target.IndexOf("claude", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    claudeTargets.Add(target);
                    if (c.CredentialBlob == IntPtr.Zero || c.CredentialBlobSize == 0) continue;

                    var bytes = new byte[c.CredentialBlobSize];
                    Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
                    foreach (var text in Decode(bytes))
                    {
                        if (text.IndexOf("ccessToken", StringComparison.Ordinal) < 0 && text.IndexOf("ccess_token", StringComparison.Ordinal) < 0) continue;
                        try
                        {
                            var cred = QuotaService.ParseClaudeCredJson(text, out _);
                            if (cred != null) return cred;
                        }
                        catch { }
                    }
                }
            }
            finally
            {
                CredFree(list);
            }
            return null;
        }

        private static IEnumerable<string> Decode(byte[] bytes)
        {
            yield return Encoding.UTF8.GetString(bytes);
            if (bytes.Length % 2 == 0) yield return Encoding.Unicode.GetString(bytes);
        }
    }
}
