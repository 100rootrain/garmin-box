using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GarminBox;

sealed record ScheduleOutcome(bool Registered, string Message, DateTime? NextRun);

static class Scheduler
{
    public const string TaskName = "garmin-box";
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// 현재 사용자·일반 권한·로그인 상태에서만 실행. 1시간 제한은 멈춘 실행이 다음 실행을 막지 않게 하는 마지막 안전장치다.
    public static string BuildXml(string exe, TimeOnly at, string userId, DateOnly startDay) => $"""
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>garmin-box 매일 동기화</Description></RegistrationInfo>
          <Triggers><CalendarTrigger><StartBoundary>{startDay:yyyy-MM-dd}T{at:HH:mm}:00</StartBoundary><ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay></CalendarTrigger></Triggers>
          <Principals><Principal id="Author"><UserId>{SecurityElement.Escape(userId)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <StartWhenAvailable>true</StartWhenAvailable>
            <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
            <Enabled>true</Enabled>
          </Settings>
          <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>--sync --scheduled</Arguments></Exec></Actions>
        </Task>
        """;

    /// 등록 후 다시 읽은 정의 검사. Windows는 기본값과 같은 항목을 생략할 수 있어 없으면 기본값으로 읽는다.
    public static List<string> Problems(string xml, string exe, TimeOnly at)
    {
        var task = XDocument.Parse(xml).Root!;
        var settings = task.Element(Ns + "Settings");
        var principal = task.Element(Ns + "Principals")?.Element(Ns + "Principal");
        var problems = new List<string>();
        void Expect(XElement? parent, string name, string fallback, string want)
        {
            string value = parent?.Element(Ns + name)?.Value.Trim() ?? fallback;
            if (!value.Equals(want, StringComparison.OrdinalIgnoreCase)) problems.Add($"{name}={value}");
        }
        Expect(settings, "DisallowStartIfOnBatteries", "true", "false");
        Expect(settings, "StopIfGoingOnBatteries", "true", "false");
        Expect(settings, "StartWhenAvailable", "false", "true");
        Expect(settings, "ExecutionTimeLimit", "PT72H", "PT1H");
        Expect(settings, "MultipleInstancesPolicy", "IgnoreNew", "IgnoreNew");
        Expect(settings, "Enabled", "true", "true");
        Expect(principal, "LogonType", "", "InteractiveToken");
        Expect(principal, "RunLevel", "LeastPrivilege", "LeastPrivilege");
        if (!IsOurs(xml, exe)) problems.Add("실행 파일");
        if (task.Descendants(Ns + "Arguments").FirstOrDefault()?.Value.Trim() != "--sync --scheduled") problems.Add("인수");
        if (task.Descendants(Ns + "StartBoundary").FirstOrDefault()?.Value.Contains($"T{at:HH:mm}") != true) problems.Add("실행 시각");
        if (task.Descendants(Ns + "DaysInterval").FirstOrDefault()?.Value.Trim() != "1") problems.Add("매일 반복");
        return problems;
    }

    static IEnumerable<XElement> Execs(string xml) => XDocument.Parse(xml).Descendants(Ns + "Exec");

    public static bool IsOurs(string xml, string exe) => Execs(xml).Any(exec => string.Equals(
        Environment.ExpandEnvironmentVariables(exec.Element(Ns + "Command")?.Value.Trim().Trim('"') ?? ""), exe, StringComparison.OrdinalIgnoreCase));

    /// 예전 README는 작업 이름을 정하지 않았으므로 실행 동작·인수의 런처 파일명으로 찾는다.
    public static bool IsLegacyBatch(string xml) => Execs(xml).Any(exec =>
        Regex.IsMatch(exec.Value, @"run_garmin_batch(_hidden\.vbs|\.ps1)", RegexOptions.IgnoreCase));

    public static ScheduleOutcome Register(string exe, TimeOnly at)
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        service.Connect();
        dynamic root = service.GetFolder(@"\");
        // root가 dynamic이라 호출 결과도 dynamic이 된다. 정적 타입으로 먼저 받아야 LINQ 람다를 쓸 수 있다 (CS1977).
        List<(string Path, bool Enabled)> tasks = LegacyTasks(root);
        var legacy = tasks.Where(t => t.Enabled).Select(t => t.Path).ToList();
        if (legacy.Count > 0)   // 자동 삭제·이전하지 않고 안내만 한다
            return new(false, "예전 수동 예약 작업이 켜져 있어 새 예약을 켜지 않았습니다. 작업 스케줄러에서 다음 작업을 '사용 안 함'으로 바꾼 뒤 '예약 다시 등록'을 누르세요: "
                + string.Join(", ", legacy), null);
        dynamic? existing = null;
        try { existing = root.GetTask(TaskName); } catch (COMException) { }   // 없으면 0x80070002
        // 같은 이름의 예전 수동 작업은 위 안내대로 꺼져 있으면 덮어쓴다(켜져 있으면 위에서 이미 멈췄다)
        if (existing is not null && !IsOurs((string)existing.Xml, exe) && !IsLegacyBatch((string)existing.Xml))
            return new(false, $"같은 이름({TaskName})의 다른 예약 작업이 있어 덮어쓰지 않았습니다.", null);
        string user = WindowsIdentity.GetCurrent().Name;
        // 오늘 시각이 이미 지났으면 내일부터: 지난 시작 시각 + StartWhenAvailable이면 등록 직후 놓친 실행으로 돌 수 있다
        var first = DateTime.Today + at.ToTimeSpan() > DateTime.Now ? DateTime.Today : DateTime.Today.AddDays(1);
        root.RegisterTask(TaskName, BuildXml(exe, at, user, DateOnly.FromDateTime(first)), 6 /* CREATE_OR_UPDATE */, user, null, 3 /* INTERACTIVE_TOKEN */, null);
        dynamic task = root.GetTask(TaskName);
        var problems = Problems((string)task.Xml, exe, at);
        return problems.Count > 0
            ? new(false, "등록한 예약 작업의 설정이 예상과 다릅니다: " + string.Join(", ", problems), null)
            : new(true, "매일 자동 실행을 등록했습니다.", (DateTime)task.NextRunTime);
    }

    static List<(string Path, bool Enabled)> LegacyTasks(dynamic folder)
    {
        // 읽을 수 없는 작업·폴더 하나 때문에 나머지(켜진 예전 작업이 있을 수 있다)를 건너뛰지 않게 따로 잡는다
        var found = new List<(string, bool)>();
        try
        {
            foreach (dynamic task in folder.GetTasks(1 /* 숨김 포함 */))
            {
                try { if (IsLegacyBatch((string)task.Xml)) found.Add(((string)task.Path, (bool)task.Enabled)); }
                catch (Exception e) when (e is COMException or UnauthorizedAccessException) { }   // 다른 사용자·시스템 작업
            }
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException) { }   // 작업 목록을 못 읽는 폴더
        try
        {
            foreach (dynamic sub in folder.GetFolders(0)) found.AddRange(LegacyTasks(sub));   // 하위 폴더는 재귀 안에서 따로 잡는다
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException) { }
        return found;
    }
}
