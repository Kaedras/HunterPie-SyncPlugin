import os
from pathlib import Path

target_path = "D:/mods/mhw/HunterPie/modules/SyncPlugin/"
file_name = "main.cs"

if not os.path.exists(target_path):
    raise Exception

source = Path("./" + file_name)
target = Path(target_path + file_name)

target.write_bytes(source.read_bytes())
