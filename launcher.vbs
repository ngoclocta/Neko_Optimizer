Set objShell = CreateObject("WScript.Shell")
Set objFSO = CreateObject("Scripting.FileSystemObject")

' Get script directory
scriptDir = objFSO.GetParentFolderName(WScript.ScriptFullName)
launcherBat = objFSO.BuildPath(scriptDir, "launcher.bat")

' Run launcher hidden if .NET is already installed, visible if installing
Set objProcess = objShell.Exec(launcherBat)
objProcess.Status
