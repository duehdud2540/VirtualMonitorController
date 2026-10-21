using System.Windows.Input;

namespace VirtualMonitor;

// Callbacks handle user-facing errors; concurrent execution is disabled while running.
internal sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool running;
    // 작업 진행 중에는 중복 실행을 막고, 그 외에는 지정된 조건 함수를 평가합니다.
    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke() ?? true);
    // 비동기 작업이 끝날 때까지 재실행을 막고 finally에서 상태를 복구합니다. 오류 안내는 각 작업이 담당합니다.
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        running = true;
        NotifyCanExecuteChanged();
        try { await execute(); }
        finally { running = false; NotifyCanExecuteChanged(); }
    }
    public event EventHandler? CanExecuteChanged;
    // 작업 시작·종료에 맞춰 WPF에 버튼 실행 조건의 변경을 알립니다.
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
