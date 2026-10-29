using System.Security.AccessControl;
using System.Security.Principal;

namespace GarminBox.Tests;

public class SecretAclTests
{
    static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);

    [Fact]
    public void BroadFolderAccessIsReplacedAndReplacedTokensInherit()
    {
        using var temp = new TempDir();
        var dir = Directory.CreateDirectory(Path.Combine(temp.Root, ".GarminDb"));
        var security = dir.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.Read,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(security);

        SecretAcl.ProtectTree(dir.FullName);

        string token = Path.Combine(dir.FullName, "garmin_tokens.json");
        File.WriteAllText(token + ".tmp", "{}");
        File.Move(token + ".tmp", token, overwrite: true);   // garminconnect의 임시 파일 교체 흉내
        Assert.True(SecretAcl.IsRestricted(dir.GetAccessControl()));
        Assert.True(SecretAcl.IsRestricted(new FileInfo(token).GetAccessControl()));
    }

    [Fact]
    public void ExplicitBroadAccessOnExistingFileIsRemoved()
    {
        using var temp = new TempDir();
        string file = Path.Combine(temp.Root, "GarminConnectConfig.json.bak");
        File.WriteAllText(file, "{}");
        var security = new FileInfo(file).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(security);

        SecretAcl.ProtectTree(temp.Root);

        Assert.True(SecretAcl.IsRestricted(new FileInfo(file).GetAccessControl()));
    }
}
