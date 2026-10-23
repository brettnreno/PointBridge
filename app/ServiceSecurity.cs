using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class ServiceSecurity
{
    public static SecurityIdentifier ServiceSid(string name)
    {
        var digest = SHA1.HashData(Encoding.Unicode.GetBytes(name.ToUpperInvariant()));
        var parts = Enumerable.Range(0, 5).Select(index => BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(index * 4, 4)));
        return new SecurityIdentifier("S-1-5-80-" + string.Join("-", parts));
    }

    public static void SecureDirectory(string path, bool isWindowsService, string serviceName = "MetasysPointBridge")
    {
        var directory = Directory.CreateDirectory(path);
        using var identity = WindowsIdentity.GetCurrent();
        var serviceIdentity = isWindowsService ? ServiceSid(serviceName) : identity.User!;
        if (isWindowsService && identity.Groups?.Contains(serviceIdentity) != true)
            throw new InvalidOperationException("The Windows service SID is not enabled. Repair the installation before starting Point Bridge.");
        var permissions = new DirectorySecurity();
        permissions.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { serviceIdentity, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.Distinct())
            permissions.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (isWindowsService)
            permissions.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-3-4"), FileSystemRights.ReadPermissions, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(permissions);
    }
}
