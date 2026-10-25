namespace Karu;

/// <summary>非表示タブの経過時間と温存順位から、次のライフサイクル操作を決める。</summary>
internal static class TabLifecyclePolicy
{
    internal enum Action
    {
        None,
        Suspend,
        Hibernate,
    }

    static readonly TimeSpan SuspendAfter = TimeSpan.FromSeconds(15);
    static readonly TimeSpan PressureHibernateAfter = TimeSpan.FromSeconds(30);
    static readonly TimeSpan CapacityHibernateAfter = TimeSpan.FromSeconds(60);

    public static Action ForInactiveTab(
        TimeSpan idle,
        int recencyIndex,
        bool underMemoryPressure,
        AppSettings settings)
    {
        var hibernateAfter = underMemoryPressure
            ? PressureHibernateAfter
            : recencyIndex < settings.WarmTabs
                ? TimeSpan.FromMinutes(settings.HibernateWarmMinutes)
                : TimeSpan.FromSeconds(settings.HibernateColdSeconds);

        if (idle >= hibernateAfter) return Action.Hibernate;
        if (idle >= SuspendAfter) return Action.Suspend;
        return Action.None;
    }

    public static bool ShouldSuspendActiveTab(TimeSpan minimizedFor)
        => minimizedFor >= SuspendAfter;

    /// <summary>上限超過分は、変更中の状態を急に失わないよう60秒待ち、
    /// WebView2が安全に停止できたタブだけを休眠させる。</summary>
    public static bool ShouldHibernateForCapacity(TimeSpan idle, bool isSuspended)
        => isSuspended && idle >= CapacityHibernateAfter;

    public static bool IsOverLiveLimit(int retainedLiveTabs, AppSettings settings)
        => settings.MaxLiveTabs > 0 && retainedLiveTabs >= settings.MaxLiveTabs;

    public static bool ShouldPark(TimeSpan minimizedFor, AppSettings settings)
        => minimizedFor >= TimeSpan.FromMinutes(settings.ParkMinutes);
}
