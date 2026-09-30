namespace ProjectEden.Patches
{
    // 同时搬运件数和喷涂点数；调用方持有站点库存锁，take 已受库存/备料空间约束。
    internal static class MegaInputTransfer
    {
        internal static void Move(ref StationStore source, ref AssemblerComponent target, int slot, int take)
        {
            int inc = (int)((long)source.inc * take / source.count);
            source.inc -= inc;
            target.incServed[slot] += inc;
            source.count -= take;
            target.served[slot] += take;
        }
    }
}
