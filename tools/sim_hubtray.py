# -*- coding: utf-8 -*-
"""离线复现综合物流枢纽「多出来一格同样的货」，并验证 1.12.11 的两处改动。

玩家报的症状：给伊卡洛斯供货的那一格，站里会凭空多出一格同样的物品，
方向是「本地仓储 / 行星仓储」。那个方向就是 ELogisticStorage.None(0)——
也就是**只写了 itemId、没写方向**的那一格，全仓库只有 HubCourierPatches.DrainAll
是这么写的（其余写入口要么是面板 SetStationStorage、要么带方向）。

成因是 tick 内的顺序，量出来的（PlanetTransport.GameTick 的调用序列）：

    @01AC  StationComponent.InternalTickLocal   <- 运输机到货，把本地需求格填回满
    @0237  StationComponent.InternalTickRemote
    @03F4  DispenserComponent.InternalTick      <- 我们的 前置摆台 / 后置退货

所以「补货」永远排在「摆台」前面。只要这一 tick **没有从那一格摆过台**
（轮换到别的货，或者一架闲置配送运输机都没有 —— 前置会直接 return），
那一格就还是满的；而这时候要是有货从机甲**回收**回来落到中转台，
退货就找不到空间，旧代码于是另开一格。

两处改动：
  1) 摆台量再按「机甲这一种货还缺多少」夹一道（MechaShortfall）。
     原先是运载量 x 闲置架数 = 5000 x 20 = **每 tick 搬走 10 万件**。
  2) 同物品的格子满了就**留在台上**，不再另开一格；只有台子真的满了
     （再也放不进东西 = 下一 tick 摆不上货 = 配送停摆）才动用最后手段并报警。

  第 2 条靠的是**原版自己的背压**：中转台装不下 X 了，
  InsertIntoStoragePrecalc 算出来的空间就是 0，原版就不再让运输机把 X 送回来。
  和原版储物箱满了的行为一模一样，货一件都不会丢。

跑法： python tools/sim_hubtray.py
"""
from __future__ import print_function

SLOT_MAX = 10005000      # StationCapacityPatches 引导后的单格容量
CARRY = 5000             # logisticCourierCarries（本 mod 放大后）
IDLE = 20                # 闲置配送运输机
TRAY_GRIDS = 30          # bufferCols 6 x bufferRows 5
TRAY_STACK = 10000       # inventoryStackSize
TRAY_CAP = TRAY_GRIDS * TRAY_STACK

REQUIRE = 5000           # 机甲配送清单里这一格的需求量
RECYCLE_PER_TICK = 500   # 机甲每 tick 还回来多少（持有超过回收线）
STAGE_EVERY = 3          # 轮换：平均每 3 个配送 tick 才轮到这一种货


class Sim(object):
    """只模一种货。它在站里有一格，方向是本地需求，物流网供应充足。"""

    def __init__(self, cap_by_need, drain_mode):
        self.cap_by_need = cap_by_need
        self.drain_mode = drain_mode

        self.slots = [SLOT_MAX]   # 每个元素 = 一格的 count，都是同一种货
        self.tray = 0
        self.mecha = REQUIRE      # 机甲已经装满了，所以只会回收、不会要货
        self.overflow_events = 0

    # @01AC：运输机到货，本地需求格被填回满 —— 排在摆台之前
    def network_refill(self):
        self.slots[0] = SLOT_MAX

    # 前置：摆台
    def stage(self, is_my_turn):
        if not is_my_turn:
            return                       # 轮换到别的货了，这一格这一 tick 不摆

        cap = CARRY * IDLE

        if self.cap_by_need:
            cap = min(cap, max(0, REQUIRE - self.mecha))

        want = cap - self.tray

        if want <= 0:
            return

        take = min(want, self.slots[0], TRAY_CAP - self.tray)
        self.slots[0] -= take
        self.tray += take

    # 原版：机甲把多的还回来，**装不下就不还**（InsertIntoStoragePrecalc 的背压）
    def recycle(self):
        room = TRAY_CAP - self.tray
        moved = min(RECYCLE_PER_TICK, room)
        self.tray += moved
        self.mecha -= moved

    # 后置：退货。drain_mode 逐条对着 FindDrainSlot 的三种可能写法
    def drain(self):
        if self.tray <= 0:
            return

        for i, count in enumerate(self.slots):
            left = SLOT_MAX - count

            if left > 0:
                move = min(left, self.tray)
                self.slots[i] += move
                self.tray -= move

                return

        # 同物品的格子全满了 —— 三条路
        if self.drain_mode == 'newslot':          # 1.12.10：另开一格
            self.overflow_events += 1
            self.slots.append(0)
            move = min(SLOT_MAX, self.tray)
            self.slots[-1] += move
            self.tray -= move

            return

        if self.drain_mode == 'tray':             # 留在台上等那一格腾空间
            if self.tray < TRAY_CAP:
                return

            self.overflow_events += 1             # 台子满了，只好还是开一格
            self.slots.append(0)
            move = min(SLOT_MAX, self.tray)
            self.slots[-1] += move
            self.tray -= move

            return

        # 1.12.11：溢进同一格，让它超过 max
        self.overflow_events += 1
        self.slots[0] += self.tray
        self.tray = 0

    def tick(self, n):
        self.network_refill()
        self.stage(n % STAGE_EVERY == 0)
        self.recycle()
        self.drain()


def run(cap_by_need, drain_mode, ticks=3600):
    s = Sim(cap_by_need, drain_mode)
    peak = 0

    for n in range(ticks):
        s.tick(n)
        peak = max(peak, s.tray)

    return len(s.slots), peak, s.slots[0]


def main():
    print('3600 个配送 tick；机甲已装满，每 tick 回收 %d 件回枢纽；'
          % RECYCLE_PER_TICK)
    print('轮换使得这一格平均每 %d 个 tick 才被摆台一次。\n' % STAGE_EVERY)

    rows = [
        ('1.12.10 旧：满了另开一格      ', False, 'newslot'),
        ('候选：满了留在中转台上        ', True, 'tray'),
        ('1.12.11 新：满了溢进同一格    ', True, 'spill'),
    ]

    got = {}

    for name, cap, mode in rows:
        slots, peak, first = run(cap, mode)
        got[mode] = (slots, peak, first)
        print('%s  槽位 %d 格   台上峰值 %-7d 首格 %d'
              % (name, slots, peak, first))

    print()
    ok = True

    old_slots = got['newslot'][0]
    tray_slots, tray_peak, _ = got['tray']
    new_slots, new_peak, new_first = got['spill']

    if old_slots <= 1:
        print('FAIL 旧行为没有复现出多余的槽位，模型和玩家报告对不上')
        ok = False
    else:
        print('OK   旧行为复现：槽位长到 %d 格（玩家报的就是这个）' % old_slots)

    # 「留在台上」这条路必须被证伪，否则就该选它（它看起来更干净）
    if tray_peak < TRAY_CAP * 0.5:
        print('FAIL 「留在台上」没有把中转台撑起来，模型太温和了')
        ok = False
    else:
        print('OK   「留在台上」被证伪：中转台涨到 %d / %d，'
              '那正是 1.12.10 修掉的「卡在台上」换了个形态'
              % (tray_peak, TRAY_CAP))

    if new_slots != 1:
        print('FAIL 新行为仍然长出了多余的槽位（%d 格）' % new_slots)
        ok = False
    else:
        print('OK   新行为：始终只有 1 格')

    # 不能要求「tick 之间恒为 0」：DrainAll 每种货每 tick 只搬一次 min(have, room)，
    # 所以 room 小于台上存量时余量本来就会过一夜，下一 tick 才收走。
    # 要断言的是**有界**——和被证伪的那条（涨到 29.95 万）形成对照。
    if new_peak > CARRY * IDLE:
        print('FAIL 新行为下中转台超过了摆台上限 %d（峰值 %d），有增长风险'
              % (CARRY * IDLE, new_peak))
        ok = False
    else:
        print('OK   新行为：中转台峰值 %d，被摆台上限 %d 夹住，不随时间增长'
              % (new_peak, CARRY * IDLE))

    # 回归：摆台量夹小之后，机甲还送得到货吗？这正是改动 1 可能弄坏的东西。
    # 机甲从空的开始、每 tick 消耗，跑完必须被填到需求量。
    for cap_by_need in (False, True):
        s = Sim(cap_by_need, 'spill')
        s.mecha = 0
        low = REQUIRE

        for n in range(600):
            s.network_refill()
            s.mecha = max(0, s.mecha - 20)     # 机甲每 tick 用掉 20 件
            s.stage(n % STAGE_EVERY == 0)
            moved = min(max(0, REQUIRE - s.mecha), s.tray, CARRY * IDLE)
            s.tray -= moved
            s.mecha += moved
            s.drain()
            low = min(low, s.mecha)

        label = '夹到机甲缺口' if cap_by_need else '摆满理论额度'
        if s.mecha < REQUIRE * 0.9:
            print('FAIL %s：机甲只到 %d / %d，送货被饿着了'
                  % (label, s.mecha, REQUIRE))
            ok = False
        else:
            print('OK   %s：机甲 %d / %d（最低谷 %d），送货没有变慢'
                  % (label, s.mecha, REQUIRE, low))

    if new_first <= SLOT_MAX:
        print('注意 首格没有越过 max，这一轮没有真的走到溢出分支')
    else:
        print('OK   首格 %d 越过 max %d —— 面板会显示成超出，那是真的库存'
              % (new_first, SLOT_MAX))

    print('\n' + ('全部通过' if ok else '有失败项'))

    return 0 if ok else 1


if __name__ == '__main__':
    raise SystemExit(main())
