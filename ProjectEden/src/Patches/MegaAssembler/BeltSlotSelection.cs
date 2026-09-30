namespace ProjectEden.Patches
{
    // 每次重新识别连接，不缓存beltId或方向，连接/拆带立即生效。
    internal struct BeltSlotSelection
    {
        private uint _inputs, _outputs;
        private SlotData[] _slots;
        internal bool HasInput, HasOutput;
        internal static BeltSlotSelection Prepare(SlotData[] slots)
        {
            var result = new BeltSlotSelection { _slots = slots };
            for (int i = 0; i < slots.Length; i++)
            {
                ref SlotData slot = ref slots[i];
                if (slot.dir != IODir.Input && slot.dir != IODir.Output)
                {
                    // 与旧输出/输入循环一致：无方向的残留引用和计数须清理。
                    slot.beltId = 0; slot.counter = 0;
                    continue;
                }
                if (slot.beltId <= 0) continue;
                if (slot.dir == IODir.Input)
                {
                    result.HasInput = true;
                    if (slots.Length <= 32) result._inputs |= 1u << i;
                }
                else
                {
                    result.HasOutput = true;
                    if (slots.Length <= 32) result._outputs |= 1u << i;
                }
            }
            return result;
        }
        internal Enumerator Inputs => new Enumerator(_slots, _inputs, IODir.Input);
        internal Enumerator Outputs => new Enumerator(_slots, _outputs, IODir.Output);
        internal struct Enumerator
        {
            private readonly SlotData[] _slots;
            private readonly IODir _direction;
            private uint _mask;
            private int _index;
            internal Enumerator(SlotData[] slots, uint mask, IODir direction)
            { _slots = slots; _mask = mask; _direction = direction; _index = -1; }
            public Enumerator GetEnumerator() => this;
            public int Current => _index;
            public bool MoveNext()
            {
                if (_slots.Length > 32)
                {
                    // 兼容旧档/第三方的更长数组，避免位移截断。
                    while (++_index < _slots.Length)
                        if (_slots[_index].dir == _direction && _slots[_index].beltId > 0) return true;
                    return false;
                }
                if (_mask == 0) return false;
                while ((_mask & 1u) == 0) { _mask >>= 1; _index++; }
                _mask >>= 1; _index++;
                return true;
            }
        }
    }
}
