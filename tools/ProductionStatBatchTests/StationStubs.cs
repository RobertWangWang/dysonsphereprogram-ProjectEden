public struct StationStore { public int itemId, count, max; }
public class StationComponent
{
    public int entityId;
    public bool isCollector, isVeinCollector;
    public int LoopValue;
    public System.Action LoopDuring;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void InternalTickLocal(int n) { LoopValue += n; LoopDuring?.Invoke(); }
    public void InternalTickRemote(int n) { LoopValue += n; }
    public void UpdateCollection() { LoopValue += 11; }
    public void UpdateVeinCollection() { LoopValue += 13; }
    public void SetPCState() { LoopValue += 17; }

    public int id, warperCount, warperMaxCount; public bool isStellar; public int[] needs; public StationStore[] storage;
    // 来自反编译的原版 UpdateNeeds：锁内填写前五格以及曲速器位。
    public System.Action During;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void UpdateNeeds()
    {
        During?.Invoke();
        lock (storage)
        {
            int n = storage.Length;
            needs[0] = 0 < n && storage[0].count < storage[0].max ? storage[0].itemId : 0;
            needs[1] = 1 < n && storage[1].count < storage[1].max ? storage[1].itemId : 0;
            needs[2] = 2 < n && storage[2].count < storage[2].max ? storage[2].itemId : 0;
            needs[3] = 3 < n && storage[3].count < storage[3].max ? storage[3].itemId : 0;
            needs[4] = 4 < n && storage[4].count < storage[4].max ? storage[4].itemId : 0;
            needs[5] = isStellar && warperCount < warperMaxCount ? 1210 : 0;
        }
    }
    public void InputItem(int itemId, int needIdx) { }
}
public static class GameMain { private static long _tick; public static int TickReads; public static long gameTick { get { TickReads++; return _tick; } set { _tick = value; } } public static object data = new object(); }
