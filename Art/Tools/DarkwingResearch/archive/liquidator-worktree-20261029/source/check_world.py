import json
# Let's inspect the world definition or LevelTileMap decoding for World 2
# Where is world data? In world_bank7.bytes or DataLoader?
with open("Assets/Darkwing/Data/world_bank7.bytes", "rb") as f:
    wb7 = f.read()

print("world_bank7 size:", len(wb7))
