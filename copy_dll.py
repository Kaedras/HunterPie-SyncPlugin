import os
from pathlib import Path

source_path = "D:/mods/mhw/HunterPie/modules/SyncPlugin/"
file_name = "SyncPlugin.dll"

if not os.path.exists(source_path):
    raise Exception

source = Path(source_path + file_name)
target = Path("./" + file_name)

target.write_bytes(source.read_bytes())
