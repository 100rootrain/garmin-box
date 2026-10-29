namespace GarminBox;

/// 파일 배치·설정 저장·지금 동기화·예약 실행·수동 런처를 직렬화하는 하나뿐인 잠금. 획득한 스레드에서 Dispose한다.
sealed class AppLock : IDisposable
{
    // Win32 mutex는 소유 스레드가 다시 기다리면 재진입으로 성공한다. UI 스레드의 두 번째 작업(중첩 메시지 루프,
    // SetBusy에서 빠진 버튼)을 막으려고 이 스레드가 잡은 이름을 따로 기억한다. 다른 스레드·프로세스는 mutex가 막는다.
    [ThreadStatic] static HashSet<string>? mine;
    readonly Mutex mutex;
    readonly string name;
    public bool WasAbandoned { get; }

    AppLock(Mutex mutex, string name, bool abandoned) { this.mutex = mutex; this.name = name; WasAbandoned = abandoned; mine!.Add(name); }

    public static AppLock? TryAcquire(string name = @"Local\garmin-box")
    {
        mine ??= [];
        if (mine.Contains(name)) return null;
        var mutex = new Mutex(false, name);
        try
        {
            if (mutex.WaitOne(0)) return new AppLock(mutex, name, false);
        }
        catch (AbandonedMutexException)
        {
            // 소유권은 얻었다. 이전 실행이 성공했다는 뜻은 아니며, 모든 작업이 설정 완료 표식·런타임 표식을 매번 다시 확인한다.
            return new AppLock(mutex, name, true);
        }
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
        mine!.Remove(name);
    }
}
