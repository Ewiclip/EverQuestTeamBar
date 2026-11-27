namespace TeamBar;

internal static class GameDataAccess
{
    private sealed class Permission
    {
        internal readonly ReaderWriterLockSlim Lock = new();
        internal bool Enabled;
    }

    private static readonly Permission screens = new(), spells = new(), collection = new();

    private static Permission For(MemoryFeature feature) => feature switch
    {
        MemoryFeature.ScreenDetection => screens,
        MemoryFeature.SpellBarData => spells,
        MemoryFeature.DataCollection => collection,
        _ => throw new ArgumentOutOfRangeException(nameof(feature))
    };

    internal static string DisabledReason(MemoryFeature feature) => feature switch
    {
        MemoryFeature.ScreenDetection => "Screen Detection is off. Enable it in Settings → Memory Reading to identify characters automatically.",
        MemoryFeature.SpellBarData => "Spell Bar Data is off. Enable it in Settings → Memory Reading to use spell overlays.",
        MemoryFeature.DataCollection => "Data Collection is off. Enable it in Settings → Memory Reading to collect game data.",
        _ => throw new ArgumentOutOfRangeException(nameof(feature))
    };

    internal static bool IsEnabled(MemoryFeature feature) => Volatile.Read(ref For(feature).Enabled);

    internal static void Initialize(IUserSettingsStore store)
    {
        UserSettings settings;
        try { settings = store.Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { settings = new(); }
        SetEnabled(MemoryFeature.ScreenDetection, settings.EnableScreenDetection);
        SetEnabled(MemoryFeature.SpellBarData, settings.EnableSpellBarData);
        SetEnabled(MemoryFeature.DataCollection, settings.EnableDataCollection);
    }

    internal static void SetEnabled(MemoryFeature feature, bool enabled)
    {
        var gate = For(feature);
        gate.Lock.EnterWriteLock();
        try { Volatile.Write(ref gate.Enabled, enabled); }
        finally { gate.Lock.ExitWriteLock(); }
    }

    // One gate per consumer lets an existing reader finish its current OS call
    // before revocation, without waiting on reads for an unrelated feature.
    internal static IDisposable EnterRead(MemoryFeature feature)
    {
        var gate = For(feature);
        gate.Lock.EnterReadLock();
        if (!gate.Enabled) { gate.Lock.ExitReadLock(); throw new InvalidDataException(DisabledReason(feature)); }
        return new ReadLease(gate.Lock);
    }
    private sealed class ReadLease(ReaderWriterLockSlim permission) : IDisposable
    {
        public void Dispose() => permission.ExitReadLock();
    }

#if !TEAMBAR_PUBLIC_RELEASE
    internal static IDisposable FixtureScope(bool enabled)
    {
        var before = Enum.GetValues<MemoryFeature>().ToDictionary(feature => feature, IsEnabled);
        foreach (var feature in before.Keys) SetEnabled(feature, enabled);
        return new RestorePermission(before);
    }
    private sealed class RestorePermission(Dictionary<MemoryFeature, bool> before) : IDisposable
    {
        public void Dispose() { foreach (var pair in before) SetEnabled(pair.Key, pair.Value); }
    }
#endif
}
