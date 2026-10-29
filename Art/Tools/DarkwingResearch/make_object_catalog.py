"""Generate Art/Research/DarkwingDuck/object_catalog.csv from bank4 type tables + behavior jump tables."""
import json
import sys
from pathlib import Path

ROM = open('Art/Research/DarkwingDuck/extracted/raw/prg.bin', 'rb').read()


def rd(bank, addr, n):
    off = bank * 0x4000 + (addr - 0x8000)
    return list(ROM[off:off + n])


if '--export-types' in sys.argv:
    # $815B-$8177 use the same type*4 Y index for both source pointers.
    payload = bytes(rd(4, 0x8AB3, 105 * 4) +
                    [rd(4, 0x8C57 + t * 4, 1)[0] for t in range(105)])
    Path('Art/Unity/CodeZero/Darkwing/Data/object_types.bytes').write_bytes(payload)
    print('object_types.bytes: 105 four-byte types + 105 strided HP bytes')
    raise SystemExit(0)


BT = json.load(open('Art/Research/DarkwingDuck/extracted/behavior_tables.json'))
b6 = {int(k, 16): v for k, v in BT['bank6'].items()}
b1 = {int(k, 16): v for k, v in BT['bank1'].items()}

beh_names_b6 = {
    0x00: "Noop_RTS", 0x01: "GasProjectile_Travel", 0x02: "GasLobBomb", 0x03: "GasThunderBolt",
    0x04: "GasTimedBomb", 0x05: "EnemyDeathPop", 0x06: "DebrisFall", 0x07: "DebrisShockwave",
    0x08: "GasLinger_WallToPickup", 0x09: "BonusPickupApply", 0x0A: "ExplosionDebris",
    0x0B: "Projectile_Mode8Dir", 0x0C: "ThugWalker", 0x0D: "ThugWalker2", 0x0E: "HopperFish",
    0x0F: "GroundLurker", 0x10: "CeilingGrabberA", 0x11: "CeilingGrabberB", 0x12: "GrabberMineChild",
    0x13: "TimedPatrollerA", 0x14: "TimedPatrollerB", 0x15: "A41E_StateMachine", 0x16: "CeilingGrabberC",
    0x17: "SwayPendulum", 0x18: "SwayPendulum2", 0x19: "TimedPatrollerC", 0x1A: "SwaySwinger",
    0x1B: "SwingerChild", 0x1C: "TimedPatrollerD", 0x1D: "FallingSpike", 0x1E: "FallingSpikeStab",
    0x1F: "ConvertedFriendly", 0x20: "A9A03_Projectile", 0x21: "WalkerPairA", 0x22: "WalkerPairB",
    0x23: "B9BC9_StateMachine", 0x24: "B9C8A_StateMachine", 0x25: "BA6B4_StateMachine",
    0x26: "BA09C_StateMachine", 0x27: "BA7D9_StateMachine", 0x28: "BA8BB_StateMachine",
    0x29: "B9E85_StateMachine", 0x2A: "B9DBD_StateMachine", 0x2B: "BA9BF_StateMachine",
    0x2C: "BAA82_StateMachine", 0x2D: "BA53E_StateMachine", 0x2E: "BA00A_StateMachine",
    0x2F: "BABBE_StateMachine", 0x30: "BAD5B_StateMachine", 0x31: "B883F_StateMachine",
    0x32: "BB007_StateMachine", 0x33: "BAAD1_StateMachine", 0x34: "FallingSpike2",
    0x35: "BAC2C_StateMachine", 0x36: "BA28F_StateMachine", 0x37: "B965D_StateMachine",
    0x38: "A41E_StateMachine", 0x39: "BA164_StateMachine", 0x3A: "B9FCB_StateMachine",
    0x3B: "BA631_StateMachine", 0x3C: "BAEBC_StateMachine", 0x3D: "BB1D3_StateMachine",
    0x3E: "BA28F_StateMachine", 0x3F: "B8A22_StateMachine", 0x40: "BB2FC_StateMachine",
    0x41: "BB2FC_StateMachine", 0x42: "BB483_StateMachine", 0x43: "BB570_StateMachine",
    0x44: "Noop_RTS", 0x45: "Noop_RTS", 0x46: "B9822_StateMachine",
}
beh_names_b1 = {
    0x47: "Noop_RTS", 0x48: "BossHopLift_8989", 0x49: "BossBushroot", 0x4A: "BossMoliarty",
    0x4B: "ForestTeleportShooter", 0x4C: "TowerFlameThrower", 0x4D: "TowerFlameThrower2",
    0x4E: "TowerFlameThrower3", 0x4F: "BossDefeatSequence", 0x50: "BossQuackerjackCore",
    0x51: "BossQuackerjackBall", 0x52: "BossQuackerjackChild", 0x53: "WolfduckController",
    0x54: "BossWolfduck", 0x55: "WolfduckChild", 0x56: "Spawner_8CE6", 0x57: "BossLiquidator",
    0x58: "LiquidatorGeyser", 0x59: "LiquidatorGeyser2", 0x5A: "BossMegavolt", 0x5B: "MegavoltShotA",
    0x5C: "MegavoltShotB", 0x5D: "OrbVolleyChild", 0x5E: "OrbSpawnerCore", 0x5F: "SteelbeakMinionA",
    0x60: "SteelbeakMinionB", 0x61: "SteelbeakMinionC", 0x62: "BossSteelbeak", 0x63: "OrbSpawner63",
    0x64: "SteelbeakFlame", 0x65: "Spawner_8CE6", 0x66: "QuackerjackBallChild", 0x67: "Spawner_8CE6",
    0x68: "StalactiteDrop", 0x69: "GrabberVerifier", 0x6A: "TowerRiser", 0x6B: "TowerRiser2",
    0x6C: "SwingerBossChild", 0x6D: "BossHopLift_8989", 0x6E: "BossHopLift_8989", 0x6F: "BossHopLift_8989",
}
for i in range(0x70, 0x7E):
    beh_names_b1[i] = "MissionCompleteDoor"
beh_names_b1[0x7E] = "DoorChild_9083"
beh_names_b1[0x7F] = "FowlCageDroid_8F71"

placed = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 21, 23, 24, 25, 26, 27,
          28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51,
          52, 53, 54, 56, 57, 58, 59, 60, 61, 62, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77,
          78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 98, 99, 101,
          102, 103, 104, 255]
pt = set(placed)
missions = {0: 'bridge', 1: 'city', 2: 'sewers', 3: 'tower', 4: 'forest', 5: 'warehouse', 6: 'fowl'}
lv = json.load(open('Art/Research/DarkwingDuck/extracted/levels.json'))
type_missions = {}
for i, L in enumerate(lv):
    for o in L.get('objects') or []:
        t = o.get('type', o.get('t'))
        if t is not None:
            type_missions.setdefault(t, set()).add(missions[i])

names = {
    0: "Thug walker A", 1: "Thug walker B (variant)", 2: "Heavy gas-immune walker",
    3: "Walker (anim 58)", 4: "Walker (anim 59)", 5: "Walker (anim 58 v2)", 6: "Walker (anim 58 v3)",
    7: "Thug walker A2", 8: "Walker (anim 59 v2)", 9: "Hopper fish", 10: "Sway swinger",
    11: "Walker (anim 58 v4)", 12: "Sway swinger 2", 13: "Grabber (anim 97)", 14: "Heavy ceiling enemy",
    15: "Falling spike (anim 63)", 16: "Pickup weapon 1", 17: "Pickup weapon 2", 18: "Pickup weapon 3",
    19: "Bonus pickup (anim 22)", 20: "Bonus pickup (anim 23)", 21: "Bonus pickup (anim 24)",
    22: "Bonus pickup (anim 25)", 23: "Bonus pickup (anim 26)", 24: "Bonus pickup (anim 27)",
    25: "Bonus pickup (anim 28)", 26: "Forest flyer (anim 35)", 27: "Mission door bridge A",
    28: "Mission door bridge B", 29: "Mission door city A", 30: "Bonus pickup (anim 24 flagB0)",
    31: "Lurker vine", 32: "Sewers walker", 33: "Heavy shooter (anim 76)", 34: "Sewers enemy (anim 69)",
    35: "Vine snapper (anim 30)", 36: "Vine snapper 2 (anim 30)", 37: "Heavy (anim AA)",
    38: "Forest floater (anim A0)", 39: "City droid (anim A4)", 40: "Grabber (anim 8B)",
    41: "Sewers enemy (anim 81)", 42: "Enemy (anim 7A)", 43: "Sewers enemy (anim 32)",
    44: "Pickup emitter (anim 9B)", 45: "Boss-add (anim 51)", 46: "Enemy (anim 8D)",
    47: "Enemy (anim 87)", 48: "Warehouse enemy (anim 3A)", 49: "Forest walker (anim 3C)",
    50: "Enemy (anim 85)", 51: "Enemy (anim 86)", 52: "Falling spike 2 (anim 94)",
    53: "Forest walker 2 (anim 90)", 54: "Walker (anim 59 v5)", 55: "(reserved) anim 34",
    56: "Warehouse enemy (anim 33)", 57: "Enemy (anim 4F)", 58: "Enemy (anim 60)", 59: "Enemy (anim 6D)",
    60: "Fowl heavy (anim 70)", 61: "Fowl hop-lift (beh 48)", 62: "Fowl hop-lift (beh 6D)",
    63: "(reserved) beh 6E", 64: "Fowl hop-lift (beh 6F)", 65: "Fowl spawner (anim 33)",
    66: "Fowl spawner (beh 56)", 67: "Fowl enemy (anim 98)", 68: "Walker (beh 1C)",
    69: "Pickup emitter (beh 2F)", 70: "Moliarty flamethrower 1", 71: "Moliarty flamethrower mount",
    72: "Bushroot add (beh 4B)", 73: "Quackerjack core (anim 92)", 74: "Quackerjack ball (anim 99)",
    75: "Wolfduck controller (anim 6D)", 76: "Wolfduck (anim 6B)", 77: "Liquidator (anim 4C)",
    78: "Megavolt (anim 57)", 79: "Bushroot (anim 4F)", 80: "Steelbeak (anim 60)",
    81: "Steelbeak minion A (anim 61)", 82: "Steelbeak minion C (anim 3E)",
    83: "Steelbeak minion B (anim 61)", 84: "Moliarty flamethrower 2", 85: "Moliarty flamethrower 3",
    86: "Tower add (beh 3E)", 87: "Tower add (beh 6A)", 88: "Tower add (beh 6B)",
    89: "Mission door city B", 90: "Mission door sewers A", 91: "Mission door sewers B",
    92: "Mission door tower A", 93: "Mission door tower B", 94: "Mission door forest A",
    95: "Mission door forest B", 96: "Mission door warehouse A", 97: "Mission door warehouse B",
    98: "Mission door fowl A", 99: "Mission door fowl B", 100: "(never placed) heavy (anim 76)",
    101: "Bonus pickup (anim B7)", 102: "Fowl spawner (beh 65)", 103: "Warehouse spawner (beh 67)",
    104: "Forest orb spawner (anim 1E)",
}

rows = ["type_id,name_or_unknown,behavior_id,behavior_addr,bank,notes"]
for t in range(105):
    flags, beh, anim, par = rd(4, 0x8AB3 + t * 4, 4)
    hp = rd(4, 0x8C57 + t * 4, 1)[0]
    bank = 1 if beh >= 0x47 else 6
    addr = (b1 if bank == 1 else b6).get(beh, 0)
    nm = names.get(t, "Unknown_%02X" % t)
    miss = ",".join(sorted(type_missions.get(t, []))) or "-"
    never = "NEVER_PLACED" if t not in pt else ""
    notes = "flags=$%02X anim=$%02X param=$%02X hp=%d mission=%s %s" % (flags, anim, par, hp, miss, never)
    rows.append("%d,%s,$%02X,$%04X,%d,\"%s\"" % (t, nm, beh, addr, bank, notes))
rows.append("255,end_marker,,,-,\"placed as literal $FF; terminating byte of placement arrays, not a real object\"")
open('Art/Research/DarkwingDuck/object_catalog.csv', 'w').write("\n".join(rows) + "\n")
print("object_catalog.csv written:", len(rows) - 1, "rows")
