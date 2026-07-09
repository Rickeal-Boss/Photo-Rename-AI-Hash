using System;
using System.Runtime.InteropServices;

// 用于改写 exe 内嵌 manifest 中非法 processorArchitecture="x64" -> "amd64"。
// 通过 Win32 资源 API 直接更新 RT_MANIFEST (type=24, id=1) 资源。
public class ResEdit
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr BeginUpdateResource(string pFileName, bool bDeleteExistingResources);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UpdateResource(IntPtr hUpdate, IntPtr lpType, IntPtr lpName, ushort wLanguage, byte[] lpData, uint cbData);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool EndUpdateResource(IntPtr hUpdate, bool fDiscard);
}
