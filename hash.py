import hashlib
import os
import json
class AutoHasher():
    READ_BYTE_FILES = [".exe", ".dll", ".png"]
    READ_BYTE_REPLACE_FILES = [".xml", ".map", ".xaml", ".log", ".md"]

    # Ignore files
    IGNORED_FILES = [
        "module.json",
        "hash.py",
        "main.cs",
        "Release.zip",
        "README.md",
        "LICENSE",
        ".gitattributes",
        ".gitignore",
        ".git"
    ]

    def __init__(self):
        self.Hashes = {}

    def Hash(self):
        self.GetHash(None)
        self.SaveJson()

    def GetHashDynamicallyBasedOnFileType(self, path: str):
        '''
            For whatever reason, read bytes and read gives a different file content
            so we need to hash the file based on their type.
            .exe, .dll, .png => rb
            .map, .xml => rb and remove \r
            rest => read
        '''
        compareFileEnding = lambda fname, endings : True in [fname.endswith(ending) for ending in endings]
        if (compareFileEnding(path, AutoHasher.READ_BYTE_FILES)):
            with open(path, "rb") as fBytes:
                return hashlib.sha256(fBytes.read()).hexdigest()
        elif (compareFileEnding(path, AutoHasher.READ_BYTE_REPLACE_FILES)):
            with open(path, "rb") as fBytes:
                return hashlib.sha256(fBytes.read().replace(b"\r", b"")).hexdigest()
        else:
            with open(path, "r") as fContent:
                print(path)
                return hashlib.sha256(fContent.read().encode("utf-8")).hexdigest()

    def GetHash(self, path: str):
        for subpath in os.listdir(path):
            # Skip files that shouldn't be hashed
            if (subpath in AutoHasher.IGNORED_FILES):
                continue;

            if "config.json" == subpath:
                self.Hashes[os.path.join(path if path != None else "", subpath)] = "InstallOnly"
                continue

            if (os.path.isdir(os.path.join(path if path != None else "", subpath))):
                self.GetHash(os.path.join(path if path != None else "", subpath))
            else:
                self.Hashes[os.path.join(path if path != None else "", subpath)] = self.GetHashDynamicallyBasedOnFileType(os.path.join(path if path != None else "", subpath))

    def SaveJson(self):
        with open("module.json", "r") as module:
            modInfo = json.load(module)
        modInfo['Update']['FileHashes'] = self.Hashes
        with open("module.json", "w") as HashesFile:
            json.dump(modInfo, HashesFile, indent=4)

if __name__ == "__main__":
    ah = AutoHasher()
    ah.Hash()
