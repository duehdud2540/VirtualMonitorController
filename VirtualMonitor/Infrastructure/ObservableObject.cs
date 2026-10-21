using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VirtualMonitor;

internal abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // 값이 바뀐 경우에만 필드를 저장하고 바인딩에 알립니다. 반환값은 변경 여부이며 CallerMemberName으로 속성 이름을 자동 전달합니다.
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    // 속성 변경 이벤트를 발생시켜 WPF가 연결된 화면 값을 다시 읽게 합니다.
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
