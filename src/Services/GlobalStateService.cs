namespace BackgroundAssistant.Services;

/// <summary>
/// 全域狀態服務，用於追蹤系統是否正在處理任務或說話。
/// </summary>
public class GlobalStateService
{
    private bool _isBusy = false;
    private readonly object _lock = new object();

    /// <summary>
    /// 系統是否正處於忙碌狀態（推論中或播報中）。
    /// </summary>
    public bool IsBusy
    {
        get
        {
            lock (_lock) return _isBusy;
        }
    }

    /// <summary>
    /// 嘗試原子性搶佔系統忙碌狀態鎖。若當前為閒置則設為忙碌並回傳 true；若已是忙碌狀態則回傳 false。
    /// </summary>
    public bool TryAcquire()
    {
        lock (_lock)
        {
            if (_isBusy) return false;
            _isBusy = true;
            return true;
        }
    }

    /// <summary>
    /// 設定系統為忙碌狀態。
    /// </summary>
    public void SetBusy()
    {
        lock (_lock) _isBusy = true;
    }

    /// <summary>
    /// 設定系統為閒置狀態。
    /// </summary>
    public void SetIdle()
    {
        lock (_lock) _isBusy = false;
    }
}
