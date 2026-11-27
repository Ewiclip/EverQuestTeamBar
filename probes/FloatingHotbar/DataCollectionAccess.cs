namespace TeamBar;

internal static class DataCollectionAccess
{
    internal static bool Enabled => GameDataAccess.IsEnabled(MemoryFeature.DataCollection);
    internal static bool CanCollect => Enabled;
    internal static T Read<T>(Func<T> capture)
    {
        if (!CanCollect) throw new InvalidOperationException(GameDataAccess.DisabledReason(MemoryFeature.DataCollection));
        return capture();
    }
}
