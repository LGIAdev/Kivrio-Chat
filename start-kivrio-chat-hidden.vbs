Option Explicit

Dim shell
Dim fso
Dim root
Dim launcher

Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
root = fso.GetParentFolderName(WScript.ScriptFullName)
launcher = """" & fso.BuildPath(root, "start-kivrio-chat.bat") & """"

shell.Run launcher, 0, False
