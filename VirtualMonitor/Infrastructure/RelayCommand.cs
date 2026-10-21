using System.Windows.Input;

namespace VirtualMonitor;

internal sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    // 버튼·단축키 실행 조건을 확인합니다. 조건 함수가 없으면 실행을 허용합니다.
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    // 실행 가능 여부를 확인하고 생성자로 전달받은 동기 작업을 호출합니다.
    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) execute();
    }
    public event EventHandler? CanExecuteChanged;
    // WPF가 실행 조건을 다시 평가하여 버튼 활성 상태를 갱신하도록 알립니다.
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
