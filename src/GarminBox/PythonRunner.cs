using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace GarminBox;

sealed record RunResult(int ExitCode, bool TimedOut, bool Cancelled, string Stderr);

static class PythonRunner
{
    static readonly UTF8Encoding Utf8 = new(false);

    /// run_batch.py를 Job에 넣어 실행한다. 콜백은 호출자 컨텍스트(GUI에서는 UI 스레드)에서 불린다 — ConfigureAwait(false)를 쓰지 않는다.
    public static async Task<RunResult> RunAsync(string python, string script, IEnumerable<string> args,
        IReadOnlyDictionary<string, string> env, Action<JsonElement> onEvent, Func<CancellationToken, Task<string?>> askMfa,
        TimeSpan timeout, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            // Windows 기본 인코딩이면 한글 메시지가 깨지고 JSON 해석이 실패한다 (실행 검증 기록)
            StandardInputEncoding = Utf8, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8,
        };
        foreach (string arg in new[] { "-I", "-X", "utf8", script, "--await-start" }.Concat(args)) start.ArgumentList.Add(arg);
        foreach (var (key, value) in env) start.Environment[key] = value;   // 비밀값은 인자가 아니라 자식 환경으로만

        using var job = Job.Create();
        using var process = Process.Start(start)!;
        try { job.Assign(process); }
        catch
        {
            process.Kill();            // 시작 신호 전이라 Python은 아무것도 하지 않았다
            process.WaitForExit();
            throw;
        }
        process.StandardInput.NewLine = "\n";
        await process.StandardInput.WriteLineAsync("START");   // Job 연결 성공 후에만
        await process.StandardInput.FlushAsync();
        var stderr = process.StandardError.ReadToEndAsync();   // 파이프가 차지 않게 읽는다. 정제 전이라 파일에 쓰지 않는다.

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        // 동기 파이프 읽기는 취소 토큰으로 끊기지 않을 수 있다. 시간 초과·취소 때 Job 전체(Python과 CLI·업로드·gist 자식)를
        // 끝내면 파이프가 닫혀 EOF로 빠져나온다.
        using (limit.Token.Register(job.Terminate))
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                JsonElement ev;
                try { ev = JsonDocument.Parse(line).RootElement.Clone(); }
                catch (JsonException) { ev = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["event"] = "text", ["line"] = line }); }
                if (ev.TryGetProperty("event", out var name) && name.GetString() == "mfa_required")
                {
                    // 시간 초과·취소면 코드 입력을 기다리지 않는다. 대화상자는 토큰으로 닫히고, Job은 이미 종료 중이라
                    // 아래 쓰기는 IOException으로 넘어가고 EOF 뒤 WaitEmpty로 자식 종료를 확인한다.
                    string? code;
                    try { code = await askMfa(limit.Token).WaitAsync(limit.Token); }
                    catch (OperationCanceledException) { code = null; }
                    try
                    {
                        await process.StandardInput.WriteLineAsync(code ?? "");
                        await process.StandardInput.FlushAsync();
                    }
                    catch (IOException) { }   // 코드를 기다리는 동안 시간 초과로 종료된 경우
                }
                onEvent(ev);
            }
            await process.WaitForExitAsync();
        }
        if (limit.IsCancellationRequested)
        {
            job.WaitEmpty(TimeSpan.FromSeconds(10));   // 자식 종료를 확인한 뒤 돌아가야 호출자가 잠금을 푼다
            return new(-1, !cancel.IsCancellationRequested, cancel.IsCancellationRequested, "");
        }
        return new(process.ExitCode, false, false, await stderr);
        // using job: 마지막 핸들이 닫히면(EXE가 finally 없이 죽어도) OS가 남은 프로세스를 종료한다
    }
}

/// KILL_ON_JOB_CLOSE Job. 핸들은 상속하지 않고(보안 속성 null) breakaway도 허용하지 않아 자식·손자가 모두 같은 Job에 속한다.
sealed class Job : IDisposable
{
    const int ExtendedLimitInformation = 9, BasicAccountingInformation = 1;
    const uint KillOnJobClose = 0x2000;
    readonly SafeFileHandle handle;

    Job(SafeFileHandle handle) => this.handle = handle;

    public static Job Create()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception();
        var info = new ExtendedLimit();
        info.Basic.LimitFlags = KillOnJobClose;
        if (!SetInformationJobObject(handle, ExtendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimit>()))
        {
            var error = new Win32Exception();
            handle.Dispose();
            throw error;
        }
        return new Job(handle);
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception();
    }

    public void Terminate() => TerminateJobObject(handle, 1);

    public void WaitEmpty(TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        while (DateTime.UtcNow < until && QueryInformationJobObject(handle, BasicAccountingInformation, out Accounting info, Marshal.SizeOf<Accounting>(), IntPtr.Zero) && info.ActiveProcesses > 0)
            Thread.Sleep(100);
    }

    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Accounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimit info, int length);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out Accounting info, int length, IntPtr returned);
}
