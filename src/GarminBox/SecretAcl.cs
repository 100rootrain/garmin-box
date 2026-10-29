using System.Security.AccessControl;
using System.Security.Principal;

namespace GarminBox;

/// 평문 자격증명 파일과 토큰 폴더의 접근을 현재 사용자·SYSTEM·Administrators로 제한한다. 암호화가 아니다.
static class SecretAcl
{
    static readonly SecurityIdentifier[] Allowed =
    [
        WindowsIdentity.GetCurrent().User!,
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
    ];

    public static bool IsRestricted(FileSystemSecurity security) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .All(rule => rule.AccessControlType == AccessControlType.Deny || Allowed.Contains((SecurityIdentifier)rule.IdentityReference));

    /// 토큰 폴더는 프로필 상속에 기대지 않고 명시적 상속 규칙을 둔다. 덕분에 나중에 임시 파일로 교체되는 토큰도 같은 권한을 받는다.
    /// 기존 설정·토큰·백업 파일의 명시적 과도한 권한은 파일별로 따로 정리한다.
    public static void ProtectTree(string directory)
    {
        Protect(directory, always: true);
        foreach (string file in Directory.EnumerateFiles(directory)) Protect(file);
    }

    public static void Protect(string path, bool always = false)
    {
        bool isDirectory = Directory.Exists(path);
        if (!always && IsRestricted(Read(path, isDirectory))) return;   // 프로필의 상속 ACL이 이미 충분하면 그대로 쓴다
        FileSystemSecurity security = isDirectory ? new DirectorySecurity() : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = isDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        foreach (var sid in Allowed)
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (isDirectory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else new FileInfo(path).SetAccessControl((FileSecurity)security);
        if (!IsRestricted(Read(path, isDirectory))) throw new UnauthorizedAccessException($"접근 권한을 제한하지 못했습니다: {path}");
    }

    static FileSystemSecurity Read(string path, bool isDirectory) =>
        isDirectory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
}
