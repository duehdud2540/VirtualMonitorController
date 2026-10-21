using System.Diagnostics;

namespace VirtualMonitor;

// Dispatcher-owned counters. S_OK means submitted, not scanned out by the monitor.
internal sealed class DirectXFrameStatistics
{
    private readonly Func<double> seconds;
    private double started, submitMilliseconds;
    private long received, submitted, redrawn, attempts;

    // 시간 공급 함수를 주입받아 측정을 초기화합니다. 진단에서는 가짜 시계를 넣어 계산 결과를 재현할 수 있습니다.
    internal DirectXFrameStatistics(Func<double>? seconds = null)
    {
        this.seconds = seconds ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        Reset();
    }

    // 현재 시각부터 새 구간을 시작하고 누적값을 비워 정지 시간과 이전 프레임이 섞이지 않게 합니다.
    internal void Reset()
    {
        started = seconds();
        received = submitted = redrawn = attempts = 0;
        submitMilliseconds = 0;
    }

    // 캡처에서 전달받은 프레임을 셉니다. 출력 성공 횟수와는 별개입니다.
    internal void Receive() => received++;

    // 출력 시도 시간은 모두 합산하고 S_OK만 새 프레임 또는 재출력으로 셉니다. 물리 화면의 실제 표시 완료를 측정하는 값은 아닙니다.
    internal void Submit(int result, bool newFrame, double milliseconds)
    {
        attempts++;
        submitMilliseconds += milliseconds;
        if (result != 0) return; // Includes occlusion (a positive DXGI status).
        if (newFrame) submitted++;
        else redrawn++;
    }

    // 구간별 수신·제출·재출력 FPS와 평균 시간을 반환하고 다음 구간을 시작합니다. 경과 시간이 0이면 FPS도 0입니다.
    internal Snapshot Sample()
    {
        double elapsed = seconds() - started;
        var snapshot = new Snapshot(elapsed > 0 ? received / elapsed : 0,
            elapsed > 0 ? submitted / elapsed : 0, elapsed > 0 ? redrawn / elapsed : 0,
            attempts > 0 ? submitMilliseconds / attempts : 0);
        Reset();
        return snapshot;
    }

    internal readonly record struct Snapshot(double ReceivedFps, double SubmittedFps,
        double RedrawFps, double AverageSubmitMilliseconds);
}
