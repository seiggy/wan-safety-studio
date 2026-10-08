using System.Runtime.InteropServices;

namespace VideoSafetyStudio.Infrastructure.ComfyUi;

/// <summary>
/// Authenticates this process's logon session to an SMB share so the output folder can be read. Windows uses
/// WNetAddConnection2 (the password never appears on a command line); elsewhere mount the share and point OutputDir at it.
/// </summary>
public static class SmbShare
{
    private static readonly int[] AlreadyConnected = [1219, 85];

    public static string Root(string unc)
    {
        var parts = unc.Replace('/', '\\').TrimStart('\\').Split('\\');
        return unc.StartsWith(@"\\", StringComparison.Ordinal) && parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0
            ? $@"\\{parts[0]}\{parts[1]}"
            : throw new ArgumentException(@"Expected a UNC path like \\server\share[\folder].", nameof(unc));
    }

    public static string Connect(string unc, string user, string password, Func<string, string, string, int>? connector = null)
    {
        var root = Root(unc);
        var code = (connector ?? WindowsConnect)(root, user, password);
        return code == 0 || AlreadyConnected.Contains(code) ? root : throw new IOException($"Could not connect to {root} as {user} (Windows error {code}).");
    }

    private static int WindowsConnect(string root, string user, string password)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("SMB credentials are applied on Windows only; mount the share and use its mount path instead.");
        }

        var resource = new NetResource { Type = 1, RemoteName = root }; // RESOURCETYPE_DISK
        return WNetAddConnection2W(ref resource, password, user, 0);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", EntryPoint = "WNetAddConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2W(ref NetResource resource, string password, string user, int flags);
}
