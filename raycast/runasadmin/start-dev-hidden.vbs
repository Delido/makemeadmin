' Startet start-dev.cmd ohne sichtbares Fenster (fuer den Autostart).
Set sh = CreateObject("WScript.Shell")
base = CreateObject("Scripting.FileSystemObject").GetParentFolderName(WScript.ScriptFullName)
sh.CurrentDirectory = base
sh.Run """" & base & "\start-dev.cmd""", 0, False
